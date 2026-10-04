using System.Globalization;
using Vapor.Protocol;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.GameAccess;

/// <summary>
/// "get_item_details": per-item metadata and valuation for specific inventory
/// items. Accepts asset_ids (exact items) or class_ids (item kinds), reads the
/// matching inventory entries, and attaches market prices from the community
/// priceoverview endpoint. Read-only: no trade or market call is ever made.
/// </summary>
public sealed class GetItemDetailsAction : IAction
{
	private readonly ILogger<GetItemDetailsAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly Func<SteamWebHandler, IItemPriceClient> _priceClientFactory;

	public GetItemDetailsAction(ILogger<GetItemDetailsAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
		_priceClientFactory = static webHandler => new ItemPriceClient(webHandler);
	}

	// Constructor for testing with injected client factories.
	internal GetItemDetailsAction(
		ILogger<GetItemDetailsAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		Func<SteamWebHandler, IItemPriceClient> priceClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_priceClientFactory = priceClientFactory;
	}

	public string Name => "get_item_details";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Get per-item metadata and market valuation for specific inventory items",
		RequiresLogin: true,
		TimeoutSeconds: 60) { Safety = ActionSafety.ReadOnly };

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		var steamIdParam = PayloadReader.GetString(payload, "steam_id");
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
			ulong? resolved = webHandler.TryResolveOwnSteamId();
			if (resolved is null)
			{
				return new ActionResult(false, "steam_id parameter is required when the session cookies do not carry a SteamID (is the session logged on?)", null);
			}

			steamId = resolved.Value;
		}

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

		List<ulong> assetIds = ParseIdList(payload, "asset_ids");
		List<ulong> classIds = ParseIdList(payload, "class_ids");

		if (assetIds.Count == 0 && classIds.Count == 0)
		{
			return new ActionResult(false, "payload needs at least one asset_id or class_id", null);
		}

		bool withValue = PayloadReader.GetBool(payload, "value") == true;
		uint? currency = null;
		var currencyParam = PayloadReader.GetString(payload, "currency");
		if (!string.IsNullOrEmpty(currencyParam) && uint.TryParse(currencyParam, out var parsedCurrency))
		{
			currency = parsedCurrency;
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			(List<InventoryItem> items, string? error) = await InventoryPaginator.FetchAsync(
				tradeClient, steamId, appId, contextId, _logger, cancellationToken).ConfigureAwait(false);

			if (error != null)
			{
				return new ActionResult(false, error, null);
			}

			List<InventoryItem> matched = items
				.Where(i => assetIds.Contains(i.AssetId) || classIds.Contains(i.ClassId))
				.ToList();

			_logger.LogInformation(
				"Matched {Matched} of {Total} items for app {AppId} (asset_ids={AssetCount}, class_ids={ClassCount})",
				matched.Count, items.Count, appId, assetIds.Count, classIds.Count);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["app_id"] = appId,
				["context_id"] = contextId.ToString(CultureInfo.InvariantCulture),
				["matched_count"] = matched.Count,
				["items"] = matched.Select(static i => new Dictionary<string, object?>
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
				}).ToList()
			};

			if (withValue)
			{
				output["valuation"] = await ValuateAsync(
					webHandler, appId, matched, currency, cancellationToken).ConfigureAwait(false);
			}

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get item details for app {AppId}", appId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private async Task<Dictionary<string, object?>> ValuateAsync(
		SteamWebHandler webHandler,
		uint appId,
		List<InventoryItem> items,
		uint? currency,
		CancellationToken cancellationToken)
	{
		var priceClient = _priceClientFactory(webHandler);
		var valued = new List<Dictionary<string, object?>>();
		int priced = 0;
		int failed = 0;

		foreach (InventoryItem item in items)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (string.IsNullOrEmpty(item.MarketHashName))
			{
				valued.Add(new Dictionary<string, object?>
				{
					["asset_id"] = item.AssetId.ToString(CultureInfo.InvariantCulture),
					["market_hash_name"] = null,
					["error"] = "not marketable"
				});
				continue;
			}

			ItemPriceResult result = await priceClient
				.GetItemPriceAsync(appId, item.MarketHashName, currency, cancellationToken)
				.ConfigureAwait(false);

			if (result.Success)
			{
				priced++;
			}
			else
			{
				failed++;
			}

			valued.Add(new Dictionary<string, object?>
			{
				["asset_id"] = item.AssetId.ToString(CultureInfo.InvariantCulture),
				["market_hash_name"] = item.MarketHashName,
				["lowest_price"] = result.LowestPrice,
				["median_price"] = result.MedianPrice,
				["volume"] = result.Volume,
				["error"] = result.Error
			});
		}

		return new Dictionary<string, object?>
		{
			["currency"] = currency,
			["priced_count"] = priced,
			["failed_count"] = failed,
			["items"] = valued
		};
	}

	private static List<ulong> ParseIdList(IReadOnlyDictionary<string, object?> payload, string key)
	{
		var result = new List<ulong>();

		if (!PayloadReader.TryGetValue(payload, key, out var raw) || raw is null)
		{
			return result;
		}

		if (raw is System.Collections.IEnumerable enumerable and not string)
		{
			foreach (var item in enumerable)
			{
				if (item != null && TryParseUInt64(item, out ulong id) && id > 0)
				{
					result.Add(id);
				}
			}
		}
		else if (TryParseUInt64(raw, out ulong single) && single > 0)
		{
			result.Add(single);
		}

		return result.Distinct().ToList();
	}

	private static bool TryParseUInt64(object? value, out ulong parsed)
	{
		switch (value)
		{
			case ulong u:
				parsed = u;
				return true;
			case long l when l > 0:
				parsed = (ulong)l;
				return true;
			case int i when i > 0:
				parsed = (ulong)i;
				return true;
			case uint u2:
				parsed = u2;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetUInt64(out parsed):
				return true;
			case JsonElement { ValueKind: JsonValueKind.String } je:
				return ulong.TryParse(je.GetString(), out parsed) && parsed > 0;
			case string s:
				return ulong.TryParse(s, out parsed) && parsed > 0;
			default:
				parsed = 0;
				return false;
		}
	}
}
