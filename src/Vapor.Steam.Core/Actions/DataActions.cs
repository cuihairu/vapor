using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Base for store data actions: resolves a per-session store API client,
/// applies optional caching and normalizes common payload parameters.
/// </summary>
public abstract class StoreDataActionBase
{
	protected readonly Func<SteamWebHandler, ISteamStoreApiClient> StoreClientFactory;
	protected readonly IVaporCache? Cache;

	protected StoreDataActionBase(Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory, IVaporCache? cache)
	{
		StoreClientFactory = storeClientFactory;
		Cache = cache;
	}

	/// <summary>
	/// Resolves the cache TTL override from payload: null keeps the cache default,
	/// zero disables caching entirely.
	/// </summary>
	protected static TimeSpan? ResolveTtlOverride(IReadOnlyDictionary<string, object?> payload, out bool disableCache)
	{
		int? seconds = PayloadReader.GetInt32(payload, "cache_ttl_seconds");
		disableCache = seconds == 0;
		return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
	}

	protected ISteamStoreApiClient CreateClient(BotSession session)
	{
		var webHandler = session.SteamWebHandler ?? throw new InvalidOperationException("Steam web handler not available");
		return StoreClientFactory(webHandler);
	}
}

/// <summary>
/// Action to fetch full game details from the Steam store.
/// </summary>
public sealed class GetGameInfoAction : StoreDataActionBase, IAction
{
	private readonly ILogger<GetGameInfoAction> _logger;

	public GetGameInfoAction(ILogger<GetGameInfoAction> logger, IVaporCache? cache = null)
		: this(logger, static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), cache)
	{
	}

	internal GetGameInfoAction(
		ILogger<GetGameInfoAction> logger,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory,
		IVaporCache? cache = null)
		: base(storeClientFactory, cache)
	{
		_logger = logger;
	}

	public string Name => "get_game_info";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Get Steam store details for a game",
		RequiresLogin: false,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var appIdParam = PayloadReader.GetString(payload, "app_id");
		if (string.IsNullOrEmpty(appIdParam) || !uint.TryParse(appIdParam, out uint appId) || appId == 0)
		{
			return new ActionResult(false, "Valid app_id is required", null);
		}

		var country = PayloadReader.GetString(payload, "cc") ?? "us";
		TimeSpan? ttlOverride = ResolveTtlOverride(payload, out bool disableCache);

		try
		{
			var client = CreateClient(session);
			string cacheKey = $"{GameInfo.CacheKey(appId)}:{country.ToLowerInvariant()}";

			GameInfo? game;
			if (disableCache || Cache == null)
			{
				game = await client.GetGameInfoAsync(appId, country, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				game = await Cache.GetOrSetAsync(
					cacheKey,
					ct => client.GetGameInfoAsync(appId, country, ct),
					ttlOverride,
					cancellationToken).ConfigureAwait(false);
			}

			if (game == null)
			{
				return new ActionResult(false, $"Game {appId} not found or store request failed", null);
			}

			_logger.LogInformation("Fetched game info for app {AppId} ({Name})", appId, game.Name);

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["game"] = game,
				["cache_key"] = cacheKey
			});
		}
		catch (InvalidOperationException) when (session.SteamWebHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get game info for app {AppId}", appId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}

/// <summary>
/// Action to search the Steam store catalog.
/// </summary>
public sealed class SearchGamesAction : StoreDataActionBase, IAction
{
	private readonly ILogger<SearchGamesAction> _logger;

	public SearchGamesAction(ILogger<SearchGamesAction> logger, IVaporCache? cache = null)
		: this(logger, static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), cache)
	{
	}

	internal SearchGamesAction(
		ILogger<SearchGamesAction> logger,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory,
		IVaporCache? cache = null)
		: base(storeClientFactory, cache)
	{
		_logger = logger;
	}

	public string Name => "search_games";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Search the Steam store catalog",
		RequiresLogin: false,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var term = PayloadReader.GetString(payload, "term");
		if (string.IsNullOrWhiteSpace(term))
		{
			return new ActionResult(false, "term is required", null);
		}

		term = term.Trim();
		int limit = PayloadReader.GetInt32(payload, "limit") ?? 20;
		var country = PayloadReader.GetString(payload, "cc") ?? "us";
		TimeSpan? ttlOverride = ResolveTtlOverride(payload, out bool disableCache);

		try
		{
			var client = CreateClient(session);
			string cacheKey = $"search:{term.ToLowerInvariant()}:{limit}:{country.ToLowerInvariant()}";

			async Task<IReadOnlyList<GameSearchResult>?> Fetch(CancellationToken ct) =>
				await client.SearchGamesAsync(term, limit, country, ct);

			IReadOnlyList<GameSearchResult> results;
			if (disableCache || Cache == null)
			{
				results = await client.SearchGamesAsync(term, limit, country, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				results = await Cache.GetOrSetAsync(cacheKey, Fetch, ttlOverride, cancellationToken).ConfigureAwait(false) ?? [];
			}

			_logger.LogInformation("Searched games for term {Term}, {Count} results", term, results.Count);

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["term"] = term,
				["total_count"] = results.Count,
				["results"] = results
			});
		}
		catch (InvalidOperationException) when (session.SteamWebHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to search games for term {Term}", term);
			return new ActionResult(false, ex.Message, null);
		}
	}
}

/// <summary>
/// Action to fetch the current price overview for a game.
/// </summary>
public sealed class GetPriceAction : StoreDataActionBase, IAction
{
	private readonly ILogger<GetPriceAction> _logger;

	public GetPriceAction(ILogger<GetPriceAction> logger, IVaporCache? cache = null)
		: this(logger, static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), cache)
	{
	}

	internal GetPriceAction(
		ILogger<GetPriceAction> logger,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory,
		IVaporCache? cache = null)
		: base(storeClientFactory, cache)
	{
		_logger = logger;
	}

	public string Name => "get_price";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Get current price information for a game",
		RequiresLogin: false,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var appIdParam = PayloadReader.GetString(payload, "app_id");
		if (string.IsNullOrEmpty(appIdParam) || !uint.TryParse(appIdParam, out uint appId) || appId == 0)
		{
			return new ActionResult(false, "Valid app_id is required", null);
		}

		var country = PayloadReader.GetString(payload, "cc") ?? "us";
		TimeSpan? ttlOverride = ResolveTtlOverride(payload, out bool disableCache);

		try
		{
			var client = CreateClient(session);
			string cacheKey = $"price:{appId}:{country.ToLowerInvariant()}";

			PriceOverview? price;
			if (disableCache || Cache == null)
			{
				price = await client.GetPriceAsync(appId, country, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				price = await Cache.GetOrSetAsync(
					cacheKey,
					ct => client.GetPriceAsync(appId, country, ct),
					ttlOverride,
					cancellationToken).ConfigureAwait(false);
			}

			if (price == null)
			{
				return new ActionResult(false, $"No price information available for app {appId}", null);
			}

			_logger.LogInformation("Fetched price for app {AppId}: {Final}", appId, price.FinalFormatted ?? price.Final?.ToString());

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["app_id"] = appId,
				["price"] = price,
				["cache_key"] = cacheKey
			});
		}
		catch (InvalidOperationException) when (session.SteamWebHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get price for app {AppId}", appId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}

/// <summary>
/// Action to fetch Community Market listings for a game.
/// </summary>
public sealed class GetMarketListingsAction : StoreDataActionBase, IAction
{
	private readonly ILogger<GetMarketListingsAction> _logger;

	public GetMarketListingsAction(ILogger<GetMarketListingsAction> logger, IVaporCache? cache = null)
		: this(logger, static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), cache)
	{
	}

	internal GetMarketListingsAction(
		ILogger<GetMarketListingsAction> logger,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory,
		IVaporCache? cache = null)
		: base(storeClientFactory, cache)
	{
		_logger = logger;
	}

	public string Name => "get_market_listings";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Get Steam Community Market listings for a game",
		RequiresLogin: false,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var appIdParam = PayloadReader.GetString(payload, "app_id");
		if (string.IsNullOrEmpty(appIdParam) || !uint.TryParse(appIdParam, out uint appId) || appId == 0)
		{
			return new ActionResult(false, "Valid app_id is required", null);
		}

		int start = PayloadReader.GetInt32(payload, "start") ?? 0;
		int count = PayloadReader.GetInt32(payload, "count") ?? 20;
		TimeSpan? ttlOverride = ResolveTtlOverride(payload, out bool disableCache);

		try
		{
			var client = CreateClient(session);
			string cacheKey = MarketListingsPage.CacheKey(appId, Math.Max(start, 0), Math.Clamp(count, 1, 100));

			MarketListingsPage? page;
			if (disableCache || Cache == null)
			{
				page = await client.GetMarketListingsAsync(appId, start, count, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				page = await Cache.GetOrSetAsync(
					cacheKey,
					ct => client.GetMarketListingsAsync(appId, start, count, ct),
					ttlOverride,
					cancellationToken).ConfigureAwait(false);
			}

			if (page == null)
			{
				return new ActionResult(false, $"Market listings request failed for app {appId}", null);
			}

			_logger.LogInformation("Fetched {Count} market listings for app {AppId} (total {Total})",
				page.Listings.Count, appId, page.TotalCount);

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["app_id"] = appId,
				["total_count"] = page.TotalCount,
				["start"] = page.Start,
				["page_size"] = page.PageSize,
				["has_more"] = page.HasMore,
				["listings"] = page.Listings
			});
		}
		catch (InvalidOperationException) when (session.SteamWebHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get market listings for app {AppId}", appId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}
