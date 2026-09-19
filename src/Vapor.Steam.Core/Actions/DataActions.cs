using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Base for store data actions: resolves a per-session store API client,
/// applies tiered caching (fresh TTL + stale-while-revalidate window) and
/// normalizes common payload parameters.
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

	/// <summary>Payload flag forcing a fresh fetch that repopulates the cache entry.</summary>
	protected static bool ResolveForceRefresh(IReadOnlyDictionary<string, object?> payload) =>
		PayloadReader.GetBool(payload, "force_refresh") == true;

	/// <summary>
	/// Unified store fetch through the cache: serves fresh entries, serves stale
	/// entries while a background refresh runs (single-flight), falls back to a
	/// direct fetch when caching is disabled or <c>force_refresh</c> is set.
	/// </summary>
	protected async Task<T?> FetchCachedAsync<T>(
		bool disableCache,
		bool forceRefresh,
		string cacheKey,
		TimeSpan defaultTtl,
		TimeSpan defaultStaleTtl,
		TimeSpan? ttlOverride,
		Func<CancellationToken, Task<T?>> fetch,
		CancellationToken cancellationToken) where T : class
	{
		if (Cache == null || disableCache)
		{
			return await fetch(cancellationToken).ConfigureAwait(false);
		}

		TimeSpan ttl = ttlOverride ?? defaultTtl;
		TimeSpan staleTtl = ttlOverride.HasValue ? SteamCacheTtl.DefaultStaleWindowFor(ttlOverride.Value) : defaultStaleTtl;

		if (forceRefresh)
		{
			T? fresh = await fetch(cancellationToken).ConfigureAwait(false);
			if (fresh != null)
			{
				await Cache.SetStaleWhileRevalidateAsync(cacheKey, fresh, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
			}

			return fresh;
		}

		return await Cache.GetOrSetStaleWhileRevalidateAsync(cacheKey, fetch, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
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
		bool forceRefresh = ResolveForceRefresh(payload);

		try
		{
			var client = CreateClient(session);
			string cacheKey = $"{GameInfo.CacheKey(appId)}:{country.ToLowerInvariant()}";

			GameInfo? game = await FetchCachedAsync(
				disableCache,
				forceRefresh,
				cacheKey,
				SteamCacheTtl.GameInfo,
				SteamCacheTtl.GameInfoStale,
				ttlOverride,
				ct => client.GetGameInfoAsync(appId, country, ct),
				cancellationToken).ConfigureAwait(false);

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
/// Action to fetch Steam store details for a batch of apps in one task.
/// Per-app failures are reported in <c>errors</c> without aborting the rest
/// (confirm_all semantics); the batch succeeds if at least one app resolves.
/// </summary>
public sealed class GetGameInfoBatchAction : StoreDataActionBase, IAction
{
	internal const int MaxAppsPerBatch = 200;
	private const int DefaultIntervalMs = 500;
	private const int MaxIntervalMs = 5000;

	private readonly ILogger<GetGameInfoBatchAction> _logger;
	private readonly Func<TimeSpan, Task> _delay;

	public GetGameInfoBatchAction(ILogger<GetGameInfoBatchAction> logger, IVaporCache? cache = null)
		: this(logger, static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), cache)
	{
	}

	internal GetGameInfoBatchAction(
		ILogger<GetGameInfoBatchAction> logger,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory,
		IVaporCache? cache = null,
		Func<TimeSpan, Task>? delay = null)
		: base(storeClientFactory, cache)
	{
		_logger = logger;
		_delay = delay ?? DefaultDelay;
	}

	private static Task DefaultDelay(TimeSpan span) => Task.Delay(span);

	public string Name => "get_game_info_batch";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Fetch Steam store details for a batch of apps (per-app errors do not abort the batch)",
		RequiresLogin: false,
		TimeoutSeconds: 240
	);

	/// <summary>
	/// Parses <c>app_ids</c> from a CSV string, an in-memory list, or a JSON array
	/// (numbers or strings — payload values become JsonElements after the
	/// WS/SQLite round-trip). Zero or unparsable entries fail with an error.
	/// </summary>
	internal static bool TryParseAppIds(object? raw, out List<uint> appIds, out string? error)
	{
		appIds = new List<uint>();
		error = null;

		IEnumerable<object?>? items = raw switch
		{
			string csv when !string.IsNullOrWhiteSpace(csv) => csv.Split(','),
			JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Cast<object?>().ToList(),
			IEnumerable<object?> list => list.ToList(),
			_ => null
		};

		if (items == null)
		{
			error = "app_ids is required";
			return false;
		}

		foreach (object? item in items)
		{
			string? text = item switch
			{
				string s => s.Trim(),
				JsonElement { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
				JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
				byte or sbyte or short or ushort or int or uint or long or ulong => item.ToString(),
				_ => null
			};

			if (string.IsNullOrEmpty(text) || !uint.TryParse(text, out uint appId) || appId == 0)
			{
				error = $"invalid app_ids entry '{item}'";
				return false;
			}

			appIds.Add(appId);
		}

		if (appIds.Count == 0)
		{
			error = "app_ids is required";
			return false;
		}

		if (appIds.Count > MaxAppsPerBatch)
		{
			error = $"app_ids exceeds the batch limit of {MaxAppsPerBatch}";
			return false;
		}

		return true;
	}

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		PayloadReader.TryGetValue(payload, "app_ids", out object? rawAppIds);
		if (!TryParseAppIds(rawAppIds, out List<uint> appIds, out string? parseError))
		{
			return new ActionResult(false, parseError, null);
		}

		var country = PayloadReader.GetString(payload, "cc") ?? "us";
		int intervalMs = Math.Clamp(PayloadReader.GetInt32(payload, "interval_ms") ?? DefaultIntervalMs, 0, MaxIntervalMs);
		TimeSpan? ttlOverride = ResolveTtlOverride(payload, out bool disableCache);
		bool forceRefresh = ResolveForceRefresh(payload);

		try
		{
			var client = CreateClient(session);
			var games = new List<GameInfo>();
			var errors = new List<Dictionary<string, object?>>();

			for (int i = 0; i < appIds.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				uint appId = appIds[i];
				bool fetchedFromSteam = false;
				string? storeError = null;
				string cacheKey = $"{GameInfo.CacheKey(appId)}:{country.ToLowerInvariant()}";

				GameInfo? game = await FetchCachedAsync(
					disableCache,
					forceRefresh,
					cacheKey,
					SteamCacheTtl.GameInfo,
					SteamCacheTtl.GameInfoStale,
					ttlOverride,
					async ct =>
					{
						fetchedFromSteam = true;
						try
						{
							return await client.GetGameInfoAsync(appId, country, ct).ConfigureAwait(false);
						}
						catch (Exception ex) when (ex is not OperationCanceledException)
						{
							// One unreachable app must not abort the batch.
							storeError = ex.Message;
							return null;
						}
					},
					cancellationToken).ConfigureAwait(false);

				if (game == null)
				{
					errors.Add(new Dictionary<string, object?>
					{
						["app_id"] = appId,
						["error"] = storeError ?? $"Game {appId} not found or store request failed"
					});
				}
				else
				{
					games.Add(game);
				}

				// Pacing only around real store requests: cache hits (including
				// SWR-stale serves) skip the gap entirely.
				if (fetchedFromSteam && i < appIds.Count - 1)
				{
					await _delay(TimeSpan.FromMilliseconds(intervalMs)).ConfigureAwait(false);
				}
			}

			_logger.LogInformation(
				"Batch game info: {Fetched}/{Total} fetched, {Failed} failed (cc={Country})",
				games.Count, appIds.Count, errors.Count, country);

			if (games.Count == 0)
			{
				return new ActionResult(false, $"all {appIds.Count} apps failed", new Dictionary<string, object?>
				{
					["total_count"] = appIds.Count,
					["fetched"] = 0,
					["failed"] = errors.Count,
					["games"] = games,
					["errors"] = errors,
					["cc"] = country
				});
			}

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["total_count"] = appIds.Count,
				["fetched"] = games.Count,
				["failed"] = errors.Count,
				["games"] = games,
				["errors"] = errors,
				["cc"] = country
			});
		}
		catch (InvalidOperationException) when (session.SteamWebHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Batch game info failed with an unexpected error");
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
		bool forceRefresh = ResolveForceRefresh(payload);

		try
		{
			var client = CreateClient(session);
			string cacheKey = $"search:{term.ToLowerInvariant()}:{limit}:{country.ToLowerInvariant()}";

			async Task<IReadOnlyList<GameSearchResult>?> Fetch(CancellationToken ct) =>
				await client.SearchGamesAsync(term, limit, country, ct);

			IReadOnlyList<GameSearchResult> results =
				await FetchCachedAsync(
					disableCache,
					forceRefresh,
					cacheKey,
					SteamCacheTtl.Search,
					SteamCacheTtl.SearchStale,
					ttlOverride,
					Fetch,
					cancellationToken).ConfigureAwait(false) ?? [];

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
		bool forceRefresh = ResolveForceRefresh(payload);

		try
		{
			var client = CreateClient(session);
			string cacheKey = $"price:{appId}:{country.ToLowerInvariant()}";

			PriceOverview? price = await FetchCachedAsync(
				disableCache,
				forceRefresh,
				cacheKey,
				SteamCacheTtl.Price,
				SteamCacheTtl.PriceStale,
				ttlOverride,
				ct => client.GetPriceAsync(appId, country, ct),
				cancellationToken).ConfigureAwait(false);

			if (price == null)
			{
				return new ActionResult(false, $"No price information available for app {appId}", null);
			}

			_logger.LogInformation("Fetched price for app {AppId}: {Final}", appId, price.FinalFormatted ?? price.Final?.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
		bool forceRefresh = ResolveForceRefresh(payload);

		try
		{
			var client = CreateClient(session);
			string cacheKey = MarketListingsPage.CacheKey(appId, Math.Max(start, 0), Math.Clamp(count, 1, 100));

			MarketListingsPage? page = await FetchCachedAsync(
				disableCache,
				forceRefresh,
				cacheKey,
				SteamCacheTtl.MarketListings,
				SteamCacheTtl.MarketListingsStale,
				ttlOverride,
				ct => client.GetMarketListingsAsync(appId, start, count, ct),
				cancellationToken).ConfigureAwait(false);

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

/// <summary>
/// Invalidates cached store data: by key prefix (e.g. "price:730", "market:440:")
/// or the whole cache. Lets jobs and operators expire stale tiers without
/// restarting the agent.
/// </summary>
public sealed class InvalidateCacheAction : IAction
{
	private readonly ILogger<InvalidateCacheAction> _logger;
	private readonly IVaporCache? _cache;

	public InvalidateCacheAction(ILogger<InvalidateCacheAction> logger, IVaporCache? cache = null)
	{
		_logger = logger;
		_cache = cache;
	}

	public string Name => "cache_invalidate";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Invalidate cached store data by key prefix, or clear the whole cache",
		RequiresLogin: false,
		TimeoutSeconds: 10
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		if (_cache is null)
		{
			return Task.FromResult(new ActionResult(false, "No cache is configured for this agent", null));
		}

		var prefix = PayloadReader.GetString(payload, "prefix");
		bool clearAll = PayloadReader.GetBool(payload, "clear_all") == true;

		if (string.IsNullOrEmpty(prefix) && !clearAll)
		{
			return Task.FromResult(new ActionResult(false, "Provide prefix or set clear_all=true", null));
		}

		int removed = clearAll ? ClearAll() : _cache.RemoveByPrefix(prefix!.Trim());
		_logger.LogInformation("Invalidated cache: prefix {Prefix}, {Count} entries removed", clearAll ? "<all>" : prefix, removed);

		return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
		{
			["prefix"] = clearAll ? null : prefix,
			["cleared_all"] = clearAll,
			["removed"] = removed
		}));
	}

	private int ClearAll()
	{
		int before = _cache!.Count;
		_cache.Clear();
		return before;
	}
}
