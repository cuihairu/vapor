using System.Globalization;
using Vapor.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.GameAccess;

/// <summary>
/// "get_game_inventory": the game-economy view of an app's inventory (CS2
/// 730 / Dota 2 570 are the defaults). Pages the same community inventory
/// <c>get_inventory</c> reads, then aggregates it into per-class stacks —
/// counts, tradable/marketable splits — and, when <c>value=true</c>, attaches
/// per-stack market prices from the community priceoverview endpoint plus the
/// host's app-level price (the <c>get_price</c> cache entry). Read-only: no
/// trade or market call is ever made.
/// </summary>
public sealed class GetGameInventoryAction : IAction
{
	/// <summary>Valuation cap: how many stacks get a price lookup per call.</summary>
	internal const int MaxValuationStacks = 50;

	private readonly ILogger<GetGameInventoryAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly Func<SteamWebHandler, IItemPriceClient> _priceClientFactory;
	private readonly Func<SteamWebHandler, ISteamStoreApiClient> _storeClientFactory;
	private readonly IVaporCache? _cache;

	public GetGameInventoryAction(ILogger<GetGameInventoryAction> logger, IVaporCache? cache = null)
	{
		_logger = logger;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
		_priceClientFactory = static webHandler => new ItemPriceClient(webHandler);
		_storeClientFactory = static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance);
		_cache = cache;
	}

	// Constructor for testing with injected client factories.
	internal GetGameInventoryAction(
		ILogger<GetGameInventoryAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		Func<SteamWebHandler, IItemPriceClient> priceClientFactory,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory,
		IVaporCache? cache)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_priceClientFactory = priceClientFactory;
		_storeClientFactory = storeClientFactory;
		_cache = cache;
	}

	public string Name => "get_game_inventory";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Aggregate an app's inventory into per-class stacks with optional market valuation",
		RequiresLogin: true,
		TimeoutSeconds: 60) { Safety = ActionSafety.ReadOnly };

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

		// Game-economy defaults: CS2's inventory, standard context.
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

		bool tradableOnly = PayloadReader.GetBool(payload, "tradable_only") == true;
		bool marketableOnly = PayloadReader.GetBool(payload, "marketable_only") == true;
		bool withValue = PayloadReader.GetBool(payload, "value") == true;

		uint? currency = null;
		var currencyParam = PayloadReader.GetString(payload, "currency");
		if (!string.IsNullOrEmpty(currencyParam) && uint.TryParse(currencyParam, out var parsedCurrency))
		{
			currency = parsedCurrency;
		}

		var country = PayloadReader.GetString(payload, "cc") ?? "us";

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			(List<InventoryItem> items, string? error) = await InventoryPaginator.FetchAsync(
				tradeClient, steamId, appId, contextId, _logger, cancellationToken).ConfigureAwait(false);

			if (error != null)
			{
				return new ActionResult(false, error, null);
			}

			List<InventoryItem> kept = items
				.Where(i => (!tradableOnly || i.Tradable) && (!marketableOnly || i.Marketable))
				.ToList();

			List<StackSummary> stacks = Aggregate(kept);

			_logger.LogInformation(
				"Aggregated {Count} items ({Stacks} stacks) from app {AppId} inventory for SteamID {SteamId} (valuation {ValueState})",
				kept.Count, stacks.Count, appId, steamId, withValue ? "on" : "off");

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["app_id"] = appId,
				["context_id"] = contextId.ToString(CultureInfo.InvariantCulture),
				["total_items"] = kept.Count,
				["stack_count"] = stacks.Count,
				["stacks"] = stacks.Select(static s => new Dictionary<string, object?>
				{
					["class_id"] = s.ClassId.ToString(CultureInfo.InvariantCulture),
					["instance_id"] = s.InstanceId.ToString(CultureInfo.InvariantCulture),
					["name"] = s.Name,
					["market_name"] = s.MarketName,
					["market_hash_name"] = s.MarketHashName,
					["type"] = s.Type,
					["count"] = s.Count,
					["tradable_count"] = s.TradableCount,
					["marketable_count"] = s.MarketableCount,
					["amount_total"] = s.AmountTotal
				}).ToList()
			};

			if (withValue)
			{
				output["valuation"] = await ValuateAsync(
					webHandler, appId, stacks, country, currency, cancellationToken).ConfigureAwait(false);
			}

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get game inventory for app {AppId}", appId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	/// <summary>Folds items into per (class, instance) stacks, largest first.</summary>
	private static List<StackSummary> Aggregate(List<InventoryItem> items) =>
		items
			.GroupBy(i => (i.ClassId, i.InstanceId))
			.Select(g => new StackSummary
			{
				ClassId = g.Key.ClassId,
				InstanceId = g.Key.InstanceId,
				Name = g.Select(i => i.Name).FirstOrDefault(static n => n != null),
				MarketName = g.Select(i => i.MarketName).FirstOrDefault(static n => n != null),
				MarketHashName = g.Select(i => i.MarketHashName).FirstOrDefault(static n => n != null),
				Type = g.Select(i => i.Type).FirstOrDefault(static n => n != null),
				Count = g.Count(),
				TradableCount = g.Count(static i => i.Tradable),
				MarketableCount = g.Count(static i => i.Marketable),
				AmountTotal = g.Sum(static i => Math.Max(i.Amount, 1))
			})
			.OrderByDescending(static s => s.Count)
			.ThenBy(static s => s.Name, StringComparer.Ordinal)
			.ThenBy(static s => s.ClassId)
			.ToList();

	/// <summary>
	/// Prices the largest marketable stacks (capped) through priceoverview and
	/// attaches the app's own store price — the <c>get_price</c> cache entry,
	/// so the two actions share one warm cache.
	/// </summary>
	private async Task<Dictionary<string, object?>> ValuateAsync(
		SteamWebHandler webHandler,
		uint appId,
		List<StackSummary> stacks,
		string country,
		uint? currency,
		CancellationToken cancellationToken)
	{
		List<StackSummary> eligible = stacks
			.Where(static s => s.MarketableCount > 0 && !string.IsNullOrEmpty(s.MarketHashName))
			.ToList();
		bool truncated = eligible.Count > MaxValuationStacks;
		List<StackSummary> priced = [.. eligible.Take(MaxValuationStacks)];

		var priceClient = _priceClientFactory(webHandler);
		var valued = new List<Dictionary<string, object?>>();
		int failedPriced = 0;

		foreach (StackSummary stack in priced)
		{
			cancellationToken.ThrowIfCancellationRequested();

			string cacheKey = ItemPriceClient.CacheKey(appId, stack.MarketHashName!, currency);
			ItemPriceResult? result = null;
			if (_cache != null)
			{
				result = await _cache.GetAsync<ItemPriceResult>(cacheKey, cancellationToken).ConfigureAwait(false);
			}

			if (result == null)
			{
				result = await priceClient
					.GetItemPriceAsync(appId, stack.MarketHashName!, currency, cancellationToken)
					.ConfigureAwait(false);
				if (_cache != null)
				{
					await _cache.SetAsync(cacheKey, result, SteamCacheTtl.Price, cancellationToken).ConfigureAwait(false);
				}
			}

			if (!result.Success)
			{
				failedPriced++;
			}

			valued.Add(new Dictionary<string, object?>
			{
				["market_hash_name"] = stack.MarketHashName,
				["count"] = stack.Count,
				["lowest_price"] = result.LowestPrice,
				["median_price"] = result.MedianPrice,
				["volume"] = result.Volume,
				["error"] = result.Error
			});
		}

		// Stacks past the cap are skipped, not failed — counted separately so
		// priced_stacks + unpriced_stacks still adds up to the eligible set.
		int skipped = eligible.Count - priced.Count;
		int pricedStacks = priced.Count - failedPriced;
		int unpricedStacks = failedPriced + skipped;

		// App-level price, mirroring get_price (same endpoint, same cache key).
		Dictionary<string, object?>? gamePrice = null;
		try
		{
			var storeClient = _storeClientFactory(webHandler);
			string gamePriceKey = $"price:{appId}:{country.ToLowerInvariant()}";
			PriceOverview? price = null;
			if (_cache != null)
			{
				price = await _cache.GetAsync<PriceOverview>(gamePriceKey, cancellationToken).ConfigureAwait(false);
			}

			if (price == null)
			{
				price = await storeClient.GetPriceAsync(appId, country, cancellationToken).ConfigureAwait(false);
				if (price != null && _cache != null)
				{
					await _cache.SetAsync(gamePriceKey, price, SteamCacheTtl.Price, cancellationToken).ConfigureAwait(false);
				}
			}

			if (price != null)
			{
				gamePrice = new Dictionary<string, object?>
				{
					["app_id"] = appId,
					["price"] = price,
					["cache_key"] = gamePriceKey
				};
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// The app price is supplementary: a store failure must not sink the
			// per-item valuation that already succeeded.
			_logger.LogWarning(ex, "App-level price lookup failed for app {AppId}", appId);
			gamePrice = new Dictionary<string, object?>
			{
				["app_id"] = appId,
				["error"] = ex.Message
			};
		}

		return new Dictionary<string, object?>
		{
			["currency"] = currency,
			["priced_stacks"] = pricedStacks,
			["unpriced_stacks"] = unpricedStacks,
			["truncated"] = truncated,
			["items"] = valued,
			["game_price"] = gamePrice
		};
	}

	/// <summary>Per-(class, instance) rollup of one item kind.</summary>
	private sealed class StackSummary
	{
		public ulong ClassId { get; init; }
		public ulong InstanceId { get; init; }
		public string? Name { get; init; }
		public string? MarketName { get; init; }
		public string? MarketHashName { get; init; }
		public string? Type { get; init; }
		public int Count { get; init; }
		public int TradableCount { get; init; }
		public int MarketableCount { get; init; }
		public int AmountTotal { get; init; }
	}
}
