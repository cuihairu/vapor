using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Trading;

namespace Vapor.Plugins.GameAccess;

/// <summary>
/// Official plugin carrying the game-access action surface that used to be
/// wired into the agent host directly (play/idle/card-drop farming, playtime,
/// licenses &amp; keys, achievements, inventory reads, duplicate scans/swaps,
/// loot and points shop — fourteen actions in total) plus the game-economy
/// view layered on top (get_game_inventory stack aggregation with optional
/// market valuation, get_item_details per-item metadata and pricing — two
/// more). The split is wire-
/// compatible by construction: action names and payload schemas are untouched,
/// so already-deployed jobs, schedules and control-plane code paths keep
/// dispatching by the same names. The agent's Docker image bundles this plugin
/// into <c>/app/plugins/vapor.game-access</c>, so out-of-the-box capability
/// sets are identical; a host that deliberately omits the plugin simply stops
/// advertising the fourteen names in hello, and tasks for them fail with the
/// regular "action not found" session error.
///
/// Host dependencies stay where they live today: loggers come from the host
/// logger factory, the shared <see cref="IVaporCache"/> (card-drop/playtime
/// caching) and <see cref="TradeRateLimiter"/> (loot/swap throttling) are
/// resolved from the host service provider exactly like the remaining host
/// wiring does. A host that exposes no rate limiter gets a loud warning and
/// the two throttled actions degrade to unthrottled (their constructors accept
/// a null limiter by design) instead of failing the whole plugin load.
/// </summary>
public sealed class GameAccessPlugin : IPlugin, IActionPlugin
{
	private ILogger? _logger;
	private IReadOnlyList<IAction> _actions = [];

	public PluginInfo Info { get; } = new(
		Id: "vapor.game-access",
		Name: "Vapor Game Access",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Official game-access actions (farming, licenses & keys, achievements, inventory, loot, points shop) split out of the agent host. Names and payload schemas unchanged.");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		cancellationToken.ThrowIfCancellationRequested();

		ILoggerFactory loggerFactory = context.Host.LoggerFactory;
		_logger = loggerFactory.CreateLogger<GameAccessPlugin>();

		IVaporCache? cache = context.Host.Services.GetService<IVaporCache>();
		TradeRateLimiter? rateLimiter = context.Host.Services.GetService<TradeRateLimiter>();
		if (rateLimiter is null)
		{
			_logger.LogWarning(
				"Host services expose no {LimiterType}: loot_inventory/swap_duplicates will run without trade rate limiting",
				nameof(TradeRateLimiter));
		}

		_actions =
		[
			new PlayGamesAction(loggerFactory.CreateLogger<PlayGamesAction>()),
			new RedeemKeyAction(loggerFactory.CreateLogger<RedeemKeyAction>()),
			new GetInventoryAction(loggerFactory.CreateLogger<GetInventoryAction>()),
			new GetGameInventoryAction(loggerFactory.CreateLogger<GetGameInventoryAction>(), cache),
			new GetItemDetailsAction(loggerFactory.CreateLogger<GetItemDetailsAction>()),
			new GetAchievementsAction(loggerFactory.CreateLogger<GetAchievementsAction>()),
			new UnlockAchievementsAction(),
			new ResetAchievementsAction(),
			new GetCardDropsAction(loggerFactory.CreateLogger<GetCardDropsAction>(), cache),
			new GetPlaytimeAction(loggerFactory.CreateLogger<GetPlaytimeAction>(), cache),
			new LootInventoryAction(loggerFactory.CreateLogger<LootInventoryAction>(), rateLimiter),
			new FindDuplicatesAction(loggerFactory.CreateLogger<FindDuplicatesAction>()),
			new SwapDuplicatesAction(loggerFactory.CreateLogger<SwapDuplicatesAction>(), rateLimiter),
			new AddLicenseAction(loggerFactory.CreateLogger<AddLicenseAction>()),
			new GetPointsShopSummaryAction(loggerFactory.CreateLogger<GetPointsShopSummaryAction>()),
			new ClaimPointsShopItemsAction(loggerFactory.CreateLogger<ClaimPointsShopItemsAction>()),
		];

		_logger.LogInformation(
			"Game access plugin started: {ActionCount} actions (cache {CacheState}, rate limiter {LimiterState})",
			_actions.Count,
			cache is null ? "absent" : "shared",
			rateLimiter is null ? "absent" : "shared");

		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Every action's logger belongs to the host logger factory and the
		// cache/limiter are host-owned singletons — nothing here is disposable,
		// only the references must drop so the load context can unload.
		_actions = [];
		_logger = null;
		return Task.CompletedTask;
	}

	public IEnumerable<IAction> GetActions() => _actions;
}
