using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to report total playtime per owned game, parsed from the profile
/// games tab (no Web API key required). Results are cached
/// (stale-while-revalidate) with a longer default TTL than badges — the games
/// tab is a heavy page and playtime moves slowly; <c>force_refresh</c>
/// bypasses the cache and <c>cache_ttl_seconds: 0</c> disables caching.
/// </summary>
public sealed class GetPlaytimeAction : IAction
{
	// Games-tab pages are large and playtime advances slowly; 30 min keeps a
	// boosting scheduler's polling polite without stale risk.
	private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);
	private static readonly TimeSpan DefaultStaleTtl = TimeSpan.FromMinutes(60);

	private readonly ILogger<GetPlaytimeAction> _logger;
	private readonly Func<SteamWebHandler, SteamProfileGamesClient> _gamesClientFactory;
	private readonly IVaporCache? _cache;

	public GetPlaytimeAction(ILogger<GetPlaytimeAction> logger, IVaporCache? cache = null)
	{
		_logger = logger;
		_cache = cache;
		_gamesClientFactory = webHandler => new SteamProfileGamesClient(webHandler, NullLogger<SteamProfileGamesClient>.Instance);
	}

	// Constructor for testing with custom factory
	internal GetPlaytimeAction(
		ILogger<GetPlaytimeAction> logger,
		Func<SteamWebHandler, SteamProfileGamesClient> gamesClientFactory,
		IVaporCache? cache = null)
	{
		_logger = logger;
		_cache = cache;
		_gamesClientFactory = gamesClientFactory;
	}

	public string Name => "get_playtime";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"List owned games with total playtime (profile games tab)",
		RequiresLogin: true,
		TimeoutSeconds: 120
	);

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

		// steam_id is optional: when omitted, resolve the session's own SteamID
		// from its cookies (what the orchestrator relies on — the control plane
		// only knows account names).
		var steamIdParam = PayloadReader.GetString(payload, "steam_id");
		ulong steamId;
		if (string.IsNullOrEmpty(steamIdParam))
		{
			ulong? resolved = webHandler.TryResolveOwnSteamId();
			if (resolved is null)
			{
				return new ActionResult(false, "steam_id parameter is required when the session cookies do not carry a SteamID (is the session logged on?)", null);
			}

			steamId = resolved.Value;
		}
		else if (!ulong.TryParse(steamIdParam, out steamId))
		{
			return new ActionResult(false, "Invalid steam_id parameter", null);
		}

		// games is optional: "220,620" (PlayGamesPayloadParser formats) filters
		// the report to the queried apps; omitted reports everything.
		var gamesFilter = PlayGamesPayloadParser.ParseGamesInput(PayloadReader.GetString(payload, "games"));

		int? ttlSeconds = PayloadReader.GetInt32(payload, "cache_ttl_seconds");
		bool forceRefresh = PayloadReader.GetBool(payload, "force_refresh") == true;
		bool disableCache = ttlSeconds == 0;
		TimeSpan ttl = ttlSeconds is > 0 ? TimeSpan.FromSeconds(ttlSeconds.Value) : DefaultTtl;
		TimeSpan staleTtl = ttlSeconds is > 0 ? SteamCacheTtl.DefaultStaleWindowFor(ttl) : DefaultStaleTtl;

		try
		{
			var gamesClient = _gamesClientFactory(webHandler);

			async Task<IReadOnlyList<GamePlaytime>?> Fetch(CancellationToken ct)
				=> await gamesClient.GetPlaytimesAsync(steamId, ct).ConfigureAwait(false);

			IReadOnlyList<GamePlaytime>? playtimes;
			if (_cache == null || disableCache)
			{
				playtimes = await Fetch(cancellationToken).ConfigureAwait(false);
			}
			else if (forceRefresh)
			{
				var fresh = await Fetch(cancellationToken).ConfigureAwait(false);
				if (fresh != null)
				{
					await _cache.SetStaleWhileRevalidateAsync(CacheKey(steamId), fresh, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
				}

				playtimes = fresh;
			}
			else
			{
				playtimes = await _cache.GetOrSetStaleWhileRevalidateAsync(CacheKey(steamId), Fetch, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
			}

			// Filter first, then sort by hours descending (appid ascending as
			// the stable tiebreak, matching get_card_drops).
			var ordered = (playtimes ?? [])
				.Where(p => gamesFilter.Count == 0 || gamesFilter.Contains(p.AppId))
				.OrderByDescending(p => p.Hours)
				.ThenBy(p => p.AppId)
				.ToList();
			double totalHours = ordered.Sum(p => p.Hours);

			_logger.LogInformation(
				"Reporting {Count} games with {Total} total hours for SteamID {SteamId}",
				ordered.Count, totalHours, steamId);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["games_count"] = ordered.Count,
				["total_hours"] = totalHours,
				["playtimes"] = ordered.Select(p => new Dictionary<string, object?>
				{
					["app_id"] = p.AppId,
					["name"] = p.Name,
					["hours"] = p.Hours
				}).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get playtime for SteamID {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static string CacheKey(ulong steamId) =>
		$"playtime:{steamId.ToString(CultureInfo.InvariantCulture)}";
}
