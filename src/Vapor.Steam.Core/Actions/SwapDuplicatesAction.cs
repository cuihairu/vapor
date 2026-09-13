using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "swap_duplicates": the local 1:1 card swap (SteamTradeMatcher style). Loads this
/// account's and the partner's inventories, pairs my duplicate copies against the
/// partner's duplicates of cards I lack entirely (and vice versa) and either reports
/// the matched pairs (dry run, the default) or sends the symmetric trade offer with
/// <c>send=true</c>. Sending still honors the per-account trade rate limiter.
/// </summary>
public sealed class SwapDuplicatesAction : IAction
{
	// Same defensive caps as the loot flow, plus the pair budget for one offer.
	internal const int MaxApps = 5;
	internal const int MaxPagesPerApp = 50;
	internal const int DefaultKeep = 1;
	internal const int DefaultMaxSwaps = 25;
	internal const int MaxMaxSwaps = 100;

	private readonly ILogger<SwapDuplicatesAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly TradeRateLimiter? _rateLimiter;

	public SwapDuplicatesAction(ILogger<SwapDuplicatesAction> logger, TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal SwapDuplicatesAction(
		ILogger<SwapDuplicatesAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_rateLimiter = rateLimiter;
	}

	public string Name => "swap_duplicates";

	public ActionMetadata Metadata => new(
		Name,
		"Match duplicate items against a partner's duplicates and offer a 1:1 swap (dry run unless send=true)",
		RequiresLogin: true,
		TimeoutSeconds: 120);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var partnerSteamIdParam = PayloadReader.GetString(payload, "partner_steam_id");
		var tradeUrl = PayloadReader.GetString(payload, "trade_url");
		var message = PayloadReader.GetString(payload, "message");
		bool send = PayloadReader.GetBool(payload, "send") ?? false;

		ulong partnerSteamId;
		string? token = null;

		if (!string.IsNullOrEmpty(tradeUrl))
		{
			var tradeUrlParams = TradeUrlParams.TryParse(tradeUrl);
			if (tradeUrlParams == null)
			{
				return new ActionResult(false, "Invalid trade URL format", null);
			}

			partnerSteamId = tradeUrlParams.PartnerSteamId;
			token = tradeUrlParams.Token;
		}
		else if (!string.IsNullOrEmpty(partnerSteamIdParam))
		{
			if (!ulong.TryParse(partnerSteamIdParam, out partnerSteamId) || partnerSteamId == 0)
			{
				return new ActionResult(false, "Invalid partner_steam_id parameter", null);
			}
		}
		else
		{
			return new ActionResult(false, "Either partner_steam_id or trade_url is required", null);
		}

		List<uint> appIds = ParseAppIds(payload);
		if (appIds.Count == 0)
		{
			return new ActionResult(false, "app_ids must contain at least one app id when provided", null);
		}

		if (appIds.Count > MaxApps)
		{
			return new ActionResult(false, $"app_ids is limited to {MaxApps} apps per swap", null);
		}

		int keep = DefaultKeep;
		if (PayloadReader.TryGetValue(payload, "keep", out var keepRaw))
		{
			if (!TryParseInt(keepRaw, out keep) || keep < 1 || keep > 100)
			{
				return new ActionResult(false, "keep must be an integer between 1 and 100", null);
			}
		}

		int maxSwaps = DefaultMaxSwaps;
		if (PayloadReader.TryGetValue(payload, "max_swaps", out var maxRaw))
		{
			if (!TryParseInt(maxRaw, out maxSwaps) || maxSwaps < 1 || maxSwaps > MaxMaxSwaps)
			{
				return new ActionResult(false, $"max_swaps must be an integer between 1 and {MaxMaxSwaps}", null);
			}
		}

		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			ulong? ownSteamId = tradeClient.GetOwnSteamId();
			if (ownSteamId == null)
			{
				return new ActionResult(false, "Unable to resolve own SteamID from the web session", null);
			}

			if (ownSteamId.Value == partnerSteamId)
			{
				return new ActionResult(false, "partner_steam_id must differ from this account's own SteamID", null);
			}

			List<InventoryItem> ownItems = await LoadInventoryAsync(tradeClient, ownSteamId.Value, appIds, "own", cancellationToken).ConfigureAwait(false);
			List<InventoryItem> partnerItems = await LoadInventoryAsync(tradeClient, partnerSteamId, appIds, "partner", cancellationToken).ConfigureAwait(false);

			List<SwapMatch> matches = CardSwapMatcher.MatchSwaps(ownItems, partnerItems, keep, maxSwaps);
			if (matches.Count == 0)
			{
				return new ActionResult(false,
					"no complementary duplicates found: each side must hold tradable duplicates of a card the other side lacks entirely", null);
			}

			var matchOutputs = matches
				.Select(m => new Dictionary<string, object?>
				{
					["give"] = DescribeAsset(m.Give, m.GiveItem),
					["receive"] = DescribeAsset(m.Receive, m.ReceiveItem)
				})
				.ToArray();

			var output = new Dictionary<string, object?>
			{
				["partner_steam_id"] = partnerSteamId.ToString(CultureInfo.InvariantCulture),
				["keep"] = keep,
				["matches"] = matchOutputs,
				["give_count"] = matches.Count,
				["receive_count"] = matches.Count,
				["dry_run"] = !send
			};

			if (!send)
			{
				_logger.LogInformation(
					"Swap dry run for {AccountName} with {PartnerSteamId}: {MatchCount} complementary pairs (not sent)",
					session.AccountName, partnerSteamId, matches.Count);
				return new ActionResult(true, null, output);
			}

			using var lease = _rateLimiter == null
				? SendTradeOfferAction.NoopLease
				: await _rateLimiter.AcquireAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
			if (lease == null)
			{
				return new ActionResult(false, "Trade rate limit exceeded for this account, try again later", null);
			}

			var result = await tradeClient.SendTradeOfferAsync(
				partnerSteamId,
				[.. matches.Select(static m => m.Give)],
				[.. matches.Select(static m => m.Receive)],
				token,
				message,
				cancellationToken
			).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to send swap trade offer", null);
			}

			_logger.LogInformation(
				"Sent 1:1 swap offer {TradeOfferId} to {PartnerSteamId} for {AccountName}: {MatchCount} pairs",
				result.TradeOfferId, partnerSteamId, session.AccountName, matches.Count);

			output["trade_offer_id"] = result.TradeOfferId?.ToString(CultureInfo.InvariantCulture);
			output["requires_mobile_confirmation"] = result.RequiresMobileConfirmation;

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to match/send duplicate swap with {PartnerSteamId}", partnerSteamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private async Task<List<InventoryItem>> LoadInventoryAsync(
		ISteamTradeClient tradeClient,
		ulong steamId,
		List<uint> appIds,
		string side,
		CancellationToken cancellationToken)
	{
		var items = new List<InventoryItem>();

		foreach (uint appId in appIds)
		{
			ulong contextId = CardSwapMatcher.ContextIdFor(appId);
			int pages = 0;
			ulong? startAssetId = null;
			bool hasMore;

			do
			{
				var response = await tradeClient.GetInventoryAsync(steamId, appId, contextId, startAssetId, cancellationToken).ConfigureAwait(false);
				if (!response.Success)
				{
					throw new InvalidOperationException($"Failed to load {side} inventory for app {appId}: {response.Error}");
				}

				items.AddRange(response.Items);
				hasMore = response.HasMore;
				startAssetId = response.LastAssetId;
			}
			while (hasMore && startAssetId.HasValue && ++pages < MaxPagesPerApp);
		}

		return items;
	}

	private static Dictionary<string, object?> DescribeAsset(TradeAsset asset, InventoryItem item) => new()
	{
		["app_id"] = asset.AppId,
		["context_id"] = asset.ContextId.ToString(CultureInfo.InvariantCulture),
		["asset_id"] = asset.AssetId,
		["class_id"] = asset.ClassId,
		["instance_id"] = asset.InstanceId,
		["name"] = item.MarketHashName ?? item.Name ?? string.Empty
	};

	// app_ids arrives as a JSON array (a JsonElement after the job store round-trip);
	// also accept .NET collections and a single bare value for direct dispatches.
	private static List<uint> ParseAppIds(IReadOnlyDictionary<string, object?> payload)
	{
		var result = new List<uint>();

		if (!PayloadReader.TryGetValue(payload, "app_ids", out var raw) || raw is null)
		{
			result.Add(753); // Default: Steam community items (trading cards etc.)
			return result;
		}

		if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
		{
			foreach (var element in array.EnumerateArray())
			{
				if (TryParseUInt32(element, out uint appId) && appId > 0)
				{
					result.Add(appId);
				}
			}
		}
		else if (raw is System.Collections.IEnumerable list and not string)
		{
			foreach (var item in list)
			{
				if (item != null && TryParseUInt32(item, out uint appId) && appId > 0)
				{
					result.Add(appId);
				}
			}
		}
		else if (TryParseUInt32(raw, out uint single) && single > 0)
		{
			result.Add(single);
		}

		return result.Distinct().ToList();
	}

	private static bool TryParseUInt32(object? value, out uint parsed)
	{
		switch (value)
		{
			case uint u:
				parsed = u;
				return true;
			case int i when i > 0:
				parsed = (uint)i;
				return true;
			case long l when l > 0 && l <= uint.MaxValue:
				parsed = (uint)l;
				return true;
			case double d when d > 0 && d % 1 == 0 && d <= uint.MaxValue:
				parsed = (uint)d;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } e:
				return e.TryGetUInt32(out parsed);
			case JsonElement { ValueKind: JsonValueKind.String } s:
				return uint.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			case string text:
				return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}

	private static bool TryParseInt(object? value, out int parsed)
	{
		switch (value)
		{
			case int i:
				parsed = i;
				return true;
			case long l when l is >= 1 and <= int.MaxValue:
				parsed = (int)l;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } e:
				return e.TryGetInt32(out parsed);
			case JsonElement { ValueKind: JsonValueKind.String } s:
				return int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			case string text:
				return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}
}
