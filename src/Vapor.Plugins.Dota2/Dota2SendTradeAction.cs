using System.Globalization;
using Vapor.Protocol;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Dota2;

/// <summary>
/// "dota2_send_trade": sends one Dota 2 trade offer per call. The curated,
/// stricter cousin of the core <c>send_trade_offer</c>: assets are pinned to
/// app 570 / context 2 (a foreign app id is rejected, never silently
/// remapped), unparsable asset entries fail the call instead of being dropped,
/// and every give-side asset is validated against a live inventory snapshot —
/// owned, tradable, clear of Steam's 7-day market trade cooldown — before any
/// offer is submitted. Gated by <c>trade.enabled</c> (default off): without
/// it the whole plugin stays read-only. One call moves one offer; batch or
/// automated trading is a documented non-goal.
/// </summary>
public sealed class Dota2SendTradeAction : IAction
{
	/// <summary>Upper bound on items (give + receive) per offer.</summary>
	public const int MaxAssetsPerOffer = 100;

	private readonly Dota2Plugin _plugin;
	private readonly ILogger<Dota2SendTradeAction> _logger;
	private readonly TradeRateLimiter? _rateLimiter;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public Dota2SendTradeAction(Dota2Plugin plugin)
		: this(plugin, NullLogger<Dota2SendTradeAction>.Instance, null)
	{
	}

	// Constructor for testing with a custom trade client factory.
	internal Dota2SendTradeAction(
		Dota2Plugin plugin,
		ILogger<Dota2SendTradeAction> logger,
		TradeRateLimiter? rateLimiter = null,
		Func<SteamWebHandler, ISteamTradeClient>? tradeClientFactory = null)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = tradeClientFactory ?? plugin.TradeClientFactory;
	}

	public string Name => "dota2_send_trade";

	public ActionMetadata Metadata => new(
		Name,
		"Sends one Dota 2 trade offer (app 570 assets only) after validating every given asset is owned, tradable and clear of Steam's 7-day market trade cooldown — before any network call. Gated by trade.enabled (default off).",
		RequiresLogin: true,
		TimeoutSeconds: 60
	)
	{ Safety = ActionSafety.NonIdempotent };

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		// The gate is checked before anything else: a disabled plugin must fail
		// a trade attempt with the knob named (configuration key + environment
		// variable) rather than parse the request at all.
		if (!_plugin.TradeEnabled)
		{
			return new ActionResult(false,
				"dota2 trade sending is disabled: set configuration 'trade.enabled' (or environment " +
				Dota2Plugin.TradeEnabledEnvironmentVariable + ") to true; the action moves Dota 2 items between Steam accounts and stays off by default",
				null);
		}

		string? tradeUrl = PayloadReader.GetString(payload, "trade_url");
		string? partnerParam = PayloadReader.GetString(payload, "partner_steam_id");
		string? token = PayloadReader.GetString(payload, "token");
		string? message = PayloadReader.GetString(payload, "message");

		ulong partnerSteamId;
		if (!string.IsNullOrEmpty(tradeUrl))
		{
			TradeUrlParams? tradeUrlParams = TradeUrlParams.TryParse(tradeUrl);
			if (tradeUrlParams is null)
			{
				return new ActionResult(false, "invalid trade_url", null);
			}

			partnerSteamId = tradeUrlParams.PartnerSteamId;
			token ??= tradeUrlParams.Token;
		}
		else if (!string.IsNullOrEmpty(partnerParam))
		{
			if (!ulong.TryParse(partnerParam, out partnerSteamId))
			{
				return new ActionResult(false, "invalid partner_steam_id parameter", null);
			}
		}
		else
		{
			return new ActionResult(false, "either partner_steam_id or trade_url is required", null);
		}

		// Strict parsing: unparsable entries and foreign app ids are errors, not
		// silent drops — an offer that silently lost an item is the worst
		// failure mode an asset-moving action can have.
		List<string> parseErrors = [];
		List<TradeAsset> itemsToGive = ParseAssets(payload, "items_to_give", parseErrors);
		List<TradeAsset> itemsToReceive = ParseAssets(payload, "items_to_receive", parseErrors);
		if (parseErrors.Count > 0)
		{
			return new ActionResult(false, string.Join("; ", parseErrors), null);
		}

		if (itemsToGive.Count == 0 && itemsToReceive.Count == 0)
		{
			return new ActionResult(false, "the offer needs at least one item in items_to_give or items_to_receive", null);
		}

		if (itemsToGive.Count + itemsToReceive.Count > MaxAssetsPerOffer)
		{
			return new ActionResult(false,
				$"the offer carries {itemsToGive.Count + itemsToReceive.Count} items; at most {MaxAssetsPerOffer} fit in one call " +
				"(batch or automated trading is a non-goal — repeat the call for more offers)", null);
		}

		SteamWebHandler? webHandler = session.SteamWebHandler;
		if (webHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);

			// The shared limiter's AcquireAsync returns null when the account is
			// out of trade budget; hosts without a limiter get the reusable
			// no-op lease (its Dispose is empty, so the plain using is safe).
			TradeRateLease? acquired = _rateLimiter is null
				? SendTradeOfferAction.NoopLease
				: await _rateLimiter.AcquireAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
			if (acquired is null)
			{
				return new ActionResult(false, "dota2 trade rate limit exceeded for this account, try again later", null);
			}

			using var lease = acquired;

			string? validationError = null;
			if (itemsToGive.Count > 0)
			{
				validationError = await ValidateGiveAssetsAsync(tradeClient, itemsToGive, cancellationToken).ConfigureAwait(false);
				if (validationError is not null)
				{
					return new ActionResult(false, validationError, null);
				}
			}

			TradeOfferResult result = await tradeClient.SendTradeOfferAsync(
				partnerSteamId,
				itemsToGive,
				itemsToReceive,
				token,
				message,
				cancellationToken
			).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "failed to send the trade offer", null);
			}

			_logger.LogInformation(
				"Sent Dota 2 trade offer {TradeOfferId} to {PartnerSteamId} ({Give} give / {Receive} receive)",
				result.TradeOfferId, partnerSteamId, itemsToGive.Count, itemsToReceive.Count);

			var output = new Dictionary<string, object?>
			{
				["mode"] = "trade",
				["app_id"] = Dota2Plugin.AppId,
				["trade_offer_id"] = result.TradeOfferId?.ToString(CultureInfo.InvariantCulture),
				["partner_steam_id"] = partnerSteamId.ToString(CultureInfo.InvariantCulture),
				["requires_mobile_confirmation"] = result.RequiresMobileConfirmation,
				["ownership_verified"] = itemsToGive.Count > 0,
				["items_to_give"] = itemsToGive.Count,
				["items_to_receive"] = itemsToReceive.Count
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to send Dota 2 trade offer to {PartnerSteamId}", partnerSteamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	/// <summary>
	/// Reads the Dota 2 inventory of the session's own Steam id and validates
	/// the give-side assets against it. The error string (7-day cooldown and
	/// friends) is the action's failure result.
	/// </summary>
	private async Task<string?> ValidateGiveAssetsAsync(
		ISteamTradeClient tradeClient,
		IReadOnlyList<TradeAsset> assets,
		CancellationToken cancellationToken)
	{
		ulong? ownSteamId = tradeClient.GetOwnSteamId();
		if (ownSteamId is null)
		{
			return "unable to resolve the own SteamID from the web session for ownership validation";
		}

		(List<InventoryItem> items, string? error) = await SteamInventoryPaginator.FetchAsync(
			tradeClient, ownSteamId.Value, Dota2Plugin.AppId, Dota2Plugin.InventoryContextId, _logger, cancellationToken).ConfigureAwait(false);
		if (error is not null)
		{
			return $"failed to load the Dota 2 inventory for validation: {error}";
		}

		Dota2TradeValidation validation = Dota2TradeValidator.Validate(assets, items);
		if (!validation.IsValid)
		{
			_logger.LogWarning("Dota 2 trade validation failed: {Error}", validation.CombinedError);
			return $"trade validation failed: {validation.CombinedError}";
		}

		return null;
	}

	/// <summary>
	/// Parses one payload item list. Strict: every entry must be an object with
	/// a positive asset_id; amounts default to 1; app_id/context_id are optional
	/// but must name Dota 2 (570/2) when present. Failures accumulate in
	/// <paramref name="errors"/> instead of dropping the entry.
	/// </summary>
	internal static List<TradeAsset> ParseAssets(
		IReadOnlyDictionary<string, object?> payload,
		string key,
		List<string> errors)
	{
		var assets = new List<TradeAsset>();
		if (!payload.TryGetValue(key, out object? value) || value is null)
		{
			return assets;
		}

		foreach (object? entry in EnumerateEntries(value))
		{
			TradeAsset? asset = entry switch
			{
				Dictionary<string, object?> fields => FromFields(key, Read(fields, "asset_id"), Read(fields, "amount"), Read(fields, "app_id"), Read(fields, "context_id"), errors),
				JsonElement { ValueKind: JsonValueKind.Object } element => FromFields(
					key,
					ReadElement(element, "asset_id"), ReadElement(element, "amount"),
					ReadElement(element, "app_id"), ReadElement(element, "context_id"),
					errors),
				_ => NotAnObject(key, errors)
			};

			if (asset is not null)
			{
				assets.Add(asset);
			}
		}

		return assets;
	}

	private static TradeAsset? NotAnObject(string key, List<string> errors)
	{
		errors.Add($"'{key}' entries must be item objects");
		return null;
	}

	private static IEnumerable<object?> EnumerateEntries(object? value) => value switch
	{
		JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(entry => (object?)entry),
		IEnumerable<object?> list => list,
		JsonElement { ValueKind: JsonValueKind.Object } single => [single],
		Dictionary<string, object?> single => [single],
		_ => []
	};

	private static TradeAsset? FromFields(
		string key,
		string? assetIdText,
		string? amountText,
		string? appIdText,
		string? contextIdText,
		List<string> errors)
	{
		if (string.IsNullOrWhiteSpace(assetIdText) || !ulong.TryParse(assetIdText, out ulong assetId) || assetId == 0)
		{
			errors.Add($"'{key}' entries need a positive 'asset_id'");
			return null;
		}

		int amount = 1;
		if (!string.IsNullOrWhiteSpace(amountText)
			&& (!int.TryParse(amountText, out amount) || amount <= 0))
		{
			errors.Add($"'{key}' entry {assetId} has an invalid 'amount' {amountText}");
			return null;
		}

		uint appId = Dota2Plugin.AppId;
		if (!string.IsNullOrWhiteSpace(appIdText))
		{
			if (!uint.TryParse(appIdText, out uint parsedAppId) || parsedAppId != Dota2Plugin.AppId)
			{
				errors.Add($"'{key}' entry {assetId} is not a Dota 2 item (app {appIdText}): this action only moves app {Dota2Plugin.AppId} items");
				return null;
			}
		}

		ulong contextId = Dota2Plugin.InventoryContextId;
		if (!string.IsNullOrWhiteSpace(contextIdText))
		{
			if (!ulong.TryParse(contextIdText, out ulong parsedContextId) || parsedContextId != Dota2Plugin.InventoryContextId)
			{
				errors.Add($"'{key}' entry {assetId} names context {contextIdText}: this action only moves app {Dota2Plugin.AppId} context {Dota2Plugin.InventoryContextId} items");
				return null;
			}
		}

		return new TradeAsset
		{
			AppId = appId,
			ContextId = contextId,
			AssetId = assetId,
			Amount = amount
		};
	}

	// Dictionary values render through ToString() like the core parser does
	// (string, number and JsonElement shapes all end up as usable text).
	private static string? Read(IReadOnlyDictionary<string, object?> fields, string name) =>
		fields.TryGetValue(name, out object? value) && value is not null ? value.ToString() : null;

	private static string? ReadElement(JsonElement element, string name)
	{
		if (!element.TryGetProperty(name, out JsonElement property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.String => property.GetString(),
			JsonValueKind.Number => property.GetRawText(),
			_ => null
		};
	}
}
