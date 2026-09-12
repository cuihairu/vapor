using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to list a user's games with remaining card drops, parsed from the
/// community badges overview page (the only source for drop counts). Results
/// are cached (stale-while-revalidate) so farming schedulers can poll without
/// hammering the community site; <c>force_refresh</c> bypasses the cache and
/// <c>cache_ttl_seconds: 0</c> disables caching entirely.
/// </summary>
public sealed class GetCardDropsAction : IAction
{
	private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan DefaultStaleTtl = TimeSpan.FromMinutes(30);

	private readonly ILogger<GetCardDropsAction> _logger;
	private readonly Func<SteamWebHandler, SteamBadgesClient> _badgesClientFactory;
	private readonly IVaporCache? _cache;

	public GetCardDropsAction(ILogger<GetCardDropsAction> logger, IVaporCache? cache = null)
	{
		_logger = logger;
		_cache = cache;
		_badgesClientFactory = webHandler => new SteamBadgesClient(webHandler, NullLogger<SteamBadgesClient>.Instance);
	}

	// Constructor for testing with custom factory
	internal GetCardDropsAction(
		ILogger<GetCardDropsAction> logger,
		Func<SteamWebHandler, SteamBadgesClient> badgesClientFactory,
		IVaporCache? cache = null)
	{
		_logger = logger;
		_cache = cache;
		_badgesClientFactory = badgesClientFactory;
	}

	public string Name => "get_card_drops";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"List games with remaining card drops (community badges page)",
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

		int? ttlSeconds = PayloadReader.GetInt32(payload, "cache_ttl_seconds");
		bool forceRefresh = PayloadReader.GetBool(payload, "force_refresh") == true;
		bool disableCache = ttlSeconds == 0;
		TimeSpan ttl = ttlSeconds is > 0 ? TimeSpan.FromSeconds(ttlSeconds.Value) : DefaultTtl;
		TimeSpan staleTtl = ttlSeconds is > 0 ? SteamCacheTtl.DefaultStaleWindowFor(ttl) : DefaultStaleTtl;

		try
		{
			var badgesClient = _badgesClientFactory(webHandler);

			async Task<IReadOnlyList<CardDropInfo>?> Fetch(CancellationToken ct)
				=> await badgesClient.GetCardDropsAsync(steamId, ct).ConfigureAwait(false);

			IReadOnlyList<CardDropInfo>? drops;
			if (_cache == null || disableCache)
			{
				drops = await Fetch(cancellationToken).ConfigureAwait(false);
			}
			else if (forceRefresh)
			{
				var fresh = await Fetch(cancellationToken).ConfigureAwait(false);
				if (fresh != null)
				{
					await _cache.SetStaleWhileRevalidateAsync(CacheKey(steamId), fresh, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
				}

				drops = fresh;
			}
			else
			{
				drops = await _cache.GetOrSetStaleWhileRevalidateAsync(CacheKey(steamId), Fetch, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
			}

			var ordered = (drops ?? []).OrderByDescending(d => d.DropsRemaining).ThenBy(d => d.AppId).ToList();
			int totalRemaining = ordered.Sum(d => d.DropsRemaining);

			_logger.LogInformation(
				"Found {Count} apps with {Total} remaining card drops for SteamID {SteamId}",
				ordered.Count, totalRemaining, steamId);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["apps_with_drops"] = ordered.Count,
				["total_drops_remaining"] = totalRemaining,
				["drops"] = ordered.Select(d => new Dictionary<string, object?>
				{
					["app_id"] = d.AppId,
					["name"] = d.Name,
					["drops_remaining"] = d.DropsRemaining
				}).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get card drops for SteamID {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static string CacheKey(ulong steamId) =>
		$"card_drops:{steamId.ToString(CultureInfo.InvariantCulture)}";
}
