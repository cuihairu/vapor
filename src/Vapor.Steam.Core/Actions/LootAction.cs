using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "loot_inventory": sends all of this account's tradable items to another Steam user
/// (the ASF/Watt "loot" flow). Scans the configured apps' inventories (default: app 753 —
/// Steam community items, where trading cards land), keeps everything tradable right now,
/// and offers the lot to the partner. The partner is given as partner_steam_id or
/// trade_url (a token-bearing URL lets Steam deliver the offer to a non-friend).
/// </summary>
public sealed class LootInventoryAction : IAction
{
	// Defensive caps: how many app inventories may be scanned per loot and how many
	// inventory pages per app (50 pages ≈ 5000 items/app) before giving up.
	internal const int MaxApps = 5;
	internal const int MaxPagesPerApp = 50;

	private readonly ILogger<LootInventoryAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly TradeRateLimiter? _rateLimiter;

	public LootInventoryAction(ILogger<LootInventoryAction> logger, TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal LootInventoryAction(
		ILogger<LootInventoryAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_rateLimiter = rateLimiter;
	}

	public string Name => "loot_inventory";

	public ActionMetadata Metadata => new(
		Name,
		"Send all tradable inventory items to another Steam user (loot)",
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
			return new ActionResult(false, $"app_ids is limited to {MaxApps} apps per loot", null);
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

			// Scan each app's inventory (community app uses context 6; everything else
			// uses the game inventory context 2) and keep what is tradable right now.
			var assets = new List<TradeAsset>();
			var scanned = new List<Dictionary<string, object?>>();

			foreach (uint appId in appIds)
			{
				ulong contextId = appId == 753 ? 6UL : 2UL;
				int tradableForApp = 0;
				int pages = 0;
				ulong? startAssetId = null;
				bool hasMore;

				do
				{
					var response = await tradeClient.GetInventoryAsync(ownSteamId.Value, appId, contextId, startAssetId, cancellationToken).ConfigureAwait(false);
					if (!response.Success)
					{
						return new ActionResult(false, $"Failed to load inventory for app {appId}: {response.Error}", null);
					}

					foreach (var item in response.Items)
					{
						if (!IsTradableNow(item))
						{
							continue;
						}

						assets.Add(new TradeAsset
						{
							AppId = appId,
							ContextId = contextId,
							AssetId = item.AssetId,
							Amount = item.Amount > 0 ? item.Amount : 1
						});
						tradableForApp++;
					}

					hasMore = response.HasMore;
					startAssetId = response.LastAssetId;
				}
				while (hasMore && startAssetId.HasValue && ++pages < MaxPagesPerApp);

				scanned.Add(new Dictionary<string, object?>
				{
					["app_id"] = appId,
					["context_id"] = contextId.ToString(CultureInfo.InvariantCulture),
					["tradable_items"] = tradableForApp
				});
			}

			if (assets.Count == 0)
			{
				return new ActionResult(false, "no tradable items to loot in the scanned apps", null);
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
				assets,
				[],
				token,
				message,
				cancellationToken
			).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to send trade offer", null);
			}

			_logger.LogInformation(
				"Looted {ItemCount} items to {PartnerSteamId} for {AccountName}",
				assets.Count, partnerSteamId, session.AccountName);

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["trade_offer_id"] = result.TradeOfferId?.ToString(CultureInfo.InvariantCulture),
				["partner_steam_id"] = partnerSteamId.ToString(CultureInfo.InvariantCulture),
				["item_count"] = assets.Count,
				["apps_scanned"] = scanned,
				["requires_mobile_confirmation"] = result.RequiresMobileConfirmation
			});
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to loot inventory to {PartnerSteamId}", partnerSteamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static bool IsTradableNow(InventoryItem item)
	{
		if (!item.Tradable)
		{
			return false;
		}

		// .Value is safe here: the is-null arm already ruled out a missing date, and
		// the non-lifted comparison keeps coverlet from minting an unreachable
		// HasValue-false probe for a nullable that cannot be null on this path.
		return item.TradabilityDate is null || item.TradabilityDate.Value <= DateTimeOffset.UtcNow;
	}

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
		else if (raw is IEnumerable<object?> list)
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
}
