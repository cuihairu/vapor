using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Dota2;

/// <summary>
/// Dota 2 (app 570) trading shell — the third official per-title plugin, built
/// on the same template as <c>vapor.csgo</c> but carrying no game-coordinator
/// protocol of its own: every Steam surface it needs (trade offers, mobile
/// confirmations, inventory pagination, per-account rate limiting) is already
/// shipped game-agnostic in <c>Vapor.Steam.Core</c>, so the plugin only scopes
/// it to Dota 2. Three actions:
/// <list type="bullet">
/// <item><c>dota2_inventory</c> — read-only inventory read with the tradability
/// split (ready / under Steam's market trade cooldown / locked), unlock times
/// surfaced when Steam reports them;</item>
/// <item><c>dota2_trades</c> — read-only trade-offer listing flagged for offers
/// that involve Dota 2 items;</item>
/// <item><c>dota2_send_trade</c> — one offer per call, assets pinned to app 570,
/// validated (owned, tradable, off cooldown) <em>before</em> any network call,
/// and gated by <c>trade.enabled</c> (default off — the plugin ships read-only).</item>
/// </list>
/// The 7-day market trade cooldown (Steam holds market-bought items untradable
/// for seven days) is stated in the validation errors rather than only implied
/// by a bare "not tradable" flag. Crafting, battle-pass/event automation and
/// in-match automation remain documented non-goals (docs/game-plugins.md), and
/// batch/auto trading is deliberately absent: one call moves one offer.
/// Mobile confirmations apply through the game-agnostic
/// <c>confirm_trade_offer</c>/<c>respond_trade_confirmation</c> actions.
/// </summary>
public sealed class Dota2Plugin : IPlugin, IActionPlugin
{
	/// <summary>Steam AppID the whole plugin is scoped to (Dota 2).</summary>
	public const uint AppId = 570;

	/// <summary>Steam inventory context for tradable Dota 2 items.</summary>
	public const ulong InventoryContextId = 2;

	/// <summary>Configuration key for the trade-sending gate (default off).</summary>
	public const string TradeEnabledConfigKey = "trade.enabled";

	/// <summary>Environment override for <see cref="TradeEnabledConfigKey"/>.</summary>
	public const string TradeEnabledEnvironmentVariable = "VAPOR_DOTA2_TRADE_ENABLED";

	private readonly Func<SteamWebHandler, ISteamTradeClient>? _tradeClientFactory;

	private ILogger? _logger;
	private IReadOnlyList<IAction> _actions = [];
	private bool _tradeEnabled;
	private TradeRateLimiter? _rateLimiter;

	/// <summary>Creates the plugin with default wiring.</summary>
	public Dota2Plugin()
	{
	}

	/// <summary>Test hook: injects a trade client factory instead of the default wiring.</summary>
	internal Dota2Plugin(Func<SteamWebHandler, ISteamTradeClient>? tradeClientFactory)
	{
		_tradeClientFactory = tradeClientFactory;
	}

	public PluginInfo Info { get; } = new(
		Id: "vapor.dota2",
		Name: "Vapor Dota 2",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Dota 2 trading plugin: scoped inventory read with cooldown split, Dota-flagged trade-offer listing, and a gated one-offer-per-call send (trade.enabled, default off) with Steam's 7-day market trade cooldown in validation errors.");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		cancellationToken.ThrowIfCancellationRequested();

		ILoggerFactory loggerFactory = context.Host.LoggerFactory;
		_logger = loggerFactory.CreateLogger<Dota2Plugin>();

		// The send gate defaults to off and is the only switch whose "on" state
		// moves items between Steam accounts. The plugin is read-only without it.
		_tradeEnabled = context.Configuration.GetBool(
			TradeEnabledConfigKey, fallback: false, TradeEnabledEnvironmentVariable);

		// Shared per-account trade rate limiter, resolved exactly like the
		// game-access plugin resolves its host dependencies. A host without one
		// logs loud and the send action degrades to unthrottled (its constructor
		// accepts a null limiter by design) instead of failing the plugin load.
		_rateLimiter = context.Host.Services.GetService<TradeRateLimiter>();
		if (_rateLimiter is null)
		{
			_logger.LogWarning(
				"Host services expose no {LimiterType}: dota2_send_trade will run without trade rate limiting",
				nameof(TradeRateLimiter));
		}

		_actions =
		[
			new Dota2InventoryAction(this, loggerFactory.CreateLogger<Dota2InventoryAction>()),
			new Dota2TradesAction(this, loggerFactory.CreateLogger<Dota2TradesAction>()),
			new Dota2SendTradeAction(this, loggerFactory.CreateLogger<Dota2SendTradeAction>(), _rateLimiter),
		];

		_logger.LogInformation(
			"Dota 2 plugin started: {ActionCount} action(s), trade sending {TradeState}",
			_actions.Count, _tradeEnabled ? "enabled" : "disabled");

		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Nothing here is disposable: the rate limiter is host-owned, the trade
		// client is built per call, and the plugin holds no background work.
		_actions = [];
		_logger = null;
		_rateLimiter = null;

		return Task.CompletedTask;
	}

	public IEnumerable<IAction> GetActions() => _actions;

	internal bool TradeEnabled => _tradeEnabled;

	internal TradeRateLimiter? RateLimiter => _rateLimiter;

	/// <summary>Builds a trade client for a session (injectable for tests).</summary>
	internal Func<SteamWebHandler, ISteamTradeClient> TradeClientFactory =>
		_tradeClientFactory ?? (webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance));
}
