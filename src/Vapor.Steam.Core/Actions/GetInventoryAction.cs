using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "get_inventory": reads a Steam account's inventory. The steam id defaults to
/// the session's own (resolved from its cookies), so the control plane only needs
/// account names. Accepts either the classic single app_id/context_id pair (the
/// original output shape, kept back-compat) or an app_ids list — a multi-app scan
/// that reuses the loot context rules (753 → 6, everything else → 2). Optional
/// tradable_only / marketable_only filters keep only items a trade or market
/// feature can act on right now.
/// </summary>
public sealed class GetInventoryAction : IAction
{
	// Defensive cap: how many apps may be scanned per call (mirrors loot).
	internal const int MaxApps = 5;

	private readonly ILogger<GetInventoryAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public GetInventoryAction(ILogger<GetInventoryAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	// Constructor for testing with a custom trade client factory.
	internal GetInventoryAction(
		ILogger<GetInventoryAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "get_inventory";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Get a user's Steam inventory",
		RequiresLogin: true,
		TimeoutSeconds: 60
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var steamIdParam = PayloadReader.GetString(payload, "steam_id");
		var webHandler = session.SteamWebHandler;

		ulong steamId;
		if (!string.IsNullOrEmpty(steamIdParam))
		{
			if (!ulong.TryParse(steamIdParam, out steamId))
			{
				return new ActionResult(false, "Invalid steam_id parameter", null);
			}
		}
		else
		{
			// Default to the session's own SteamID; without a web session there is
			// nothing to resolve it from.
			if (webHandler == null)
			{
				return new ActionResult(false, "steam_id parameter is required when no web session is available", null);
			}

			ulong? resolved = webHandler.TryResolveOwnSteamId();
			if (resolved is null)
			{
				return new ActionResult(false, "steam_id parameter is required when the session cookies do not carry a SteamID (is the session logged on?)", null);
			}

			steamId = resolved.Value;
		}

		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		bool tradableOnly = PayloadReader.GetBool(payload, "tradable_only") == true;
		bool marketableOnly = PayloadReader.GetBool(payload, "marketable_only") == true;

		// A multi-app scan (app_ids) takes precedence over the classic single-app pair.
		List<uint> appIds = ParseAppIds(payload);
		if (appIds.Count > 0)
		{
			if (appIds.Count > MaxApps)
			{
				return new ActionResult(false, $"app_ids is limited to {MaxApps} apps per call", null);
			}

			return await ScanAppsAsync(session, webHandler, steamId, appIds, tradableOnly, marketableOnly, cancellationToken).ConfigureAwait(false);
		}

		// Classic single-app path: app_id (default 730/CS2) + context_id (default 2).
		uint appId = 730;
		var appIdParam = PayloadReader.GetString(payload, "app_id");
		if (!string.IsNullOrEmpty(appIdParam) && uint.TryParse(appIdParam, out var parsedAppId))
		{
			appId = parsedAppId;
		}

		ulong contextId = 2;
		var contextIdParam = PayloadReader.GetString(payload, "context_id");
		if (!string.IsNullOrEmpty(contextIdParam) && ulong.TryParse(contextIdParam, out var parsedContextId))
		{
			contextId = parsedContextId;
		}

		try
		{
			(List<InventoryItem> items, string? error) = await FetchAppInventoryAsync(
				webHandler, steamId, appId, contextId, cancellationToken).ConfigureAwait(false);

			if (error != null)
			{
				return new ActionResult(false, error, null);
			}

			List<InventoryItem> kept = ApplyFilters(items, tradableOnly, marketableOnly);

			_logger.LogInformation(
				"Retrieved {Count} items ({Kept} after filters) from inventory for SteamID {SteamId}, AppID {AppId}",
				items.Count, kept.Count, steamId, appId);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["app_id"] = appId,
				["context_id"] = contextId,
				["total_count"] = kept.Count,
				["items"] = kept.Select(ToItemOutput).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get inventory for {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	/// <summary>Scans several apps in one call and reports per-app and combined counts.</summary>
	private async Task<ActionResult> ScanAppsAsync(
		BotSession session,
		SteamWebHandler webHandler,
		ulong steamId,
		List<uint> appIds,
		bool tradableOnly,
		bool marketableOnly,
		CancellationToken cancellationToken)
	{
		try
		{
			var allItems = new List<InventoryItem>();
			var apps = new List<Dictionary<string, object?>>();

			foreach (uint appId in appIds)
			{
				// The loot context rules: community items (trading cards etc.) live in
				// app 753 context 6; everything else uses the standard context 2.
				ulong contextId = appId == 753 ? 6UL : 2UL;

				(List<InventoryItem> items, string? error) = await FetchAppInventoryAsync(
					webHandler, steamId, appId, contextId, cancellationToken).ConfigureAwait(false);

				if (error != null)
				{
					return new ActionResult(false, $"app {appId}: {error}", null);
				}

				List<InventoryItem> kept = ApplyFilters(items, tradableOnly, marketableOnly);
				apps.Add(new Dictionary<string, object?>
				{
					["app_id"] = appId,
					["context_id"] = contextId.ToString(CultureInfo.InvariantCulture),
					["item_count"] = kept.Count
				});
				allItems.AddRange(kept);
			}

			_logger.LogInformation(
				"Retrieved {Count} items from {AppCount} inventories for SteamID {SteamId} (account {AccountName})",
				allItems.Count, appIds.Count, steamId, session.AccountName);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["total_count"] = allItems.Count,
				["items"] = allItems.Select(ToItemOutput).ToList(),
				["apps"] = apps
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get inventory scan for {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	/// <summary>Pulls one app inventory with pagination, mirroring the safety caps.</summary>
	private async Task<(List<InventoryItem> Items, string? Error)> FetchAppInventoryAsync(
		SteamWebHandler webHandler,
		ulong steamId,
		uint appId,
		ulong contextId,
		CancellationToken cancellationToken)
	{
		var tradeClient = _tradeClientFactory(webHandler);
		var allItems = new List<InventoryItem>();
		ulong? startAssetId = null;
		bool hasMore;

		do
		{
			var response = await tradeClient.GetInventoryAsync(steamId, appId, contextId, startAssetId, cancellationToken).ConfigureAwait(false);

			if (!response.Success)
			{
				return ([], response.Error ?? "Failed to get inventory");
			}

			allItems.AddRange(response.Items);
			hasMore = response.HasMore;
			startAssetId = response.LastAssetId;

			// Safety limit to prevent infinite loops
			if (allItems.Count > 50000)
			{
				_logger.LogWarning("Inventory size exceeded 50000 items, stopping pagination");
				break;
			}
		}
		while (hasMore && startAssetId.HasValue);

		return (allItems, null);
	}

	private static List<InventoryItem> ApplyFilters(List<InventoryItem> items, bool tradableOnly, bool marketableOnly) =>
		items.Where(i => (!tradableOnly || i.Tradable) && (!marketableOnly || i.Marketable)).ToList();

	private static Dictionary<string, object?> ToItemOutput(InventoryItem i) => new()
	{
		["asset_id"] = i.AssetId.ToString(CultureInfo.InvariantCulture),
		["class_id"] = i.ClassId.ToString(CultureInfo.InvariantCulture),
		["instance_id"] = i.InstanceId.ToString(CultureInfo.InvariantCulture),
		["app_id"] = i.AppId,
		["amount"] = i.Amount,
		["name"] = i.Name,
		["market_name"] = i.MarketName,
		["market_hash_name"] = i.MarketHashName,
		["type"] = i.Type,
		["tradable"] = i.Tradable,
		["marketable"] = i.Marketable
	};

	private static List<uint> ParseAppIds(IReadOnlyDictionary<string, object?> payload)
	{
		var result = new List<uint>();

		if (!PayloadReader.TryGetValue(payload, "app_ids", out var raw) || raw is null)
		{
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
			case double d when d > 0 && d <= uint.MaxValue && Math.Floor(d) == d:
				parsed = (uint)d;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetUInt32(out parsed):
				return true;
			case JsonElement { ValueKind: JsonValueKind.String } je:
				return TryParseString(je.GetString()!, out parsed);
			case string s:
				return TryParseString(s, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}

	private static bool TryParseString(string value, out uint parsed)
	{
		// Both call sites pass non-null text (a JSON string element or a CLR string).
		return uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
	}
}
