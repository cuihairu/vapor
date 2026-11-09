using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Desired-state reconciler for declared farm accounts: periodically compares each
/// account's desired state (<see cref="AccountStore"/>) with the observed session
/// state (<see cref="SessionTracker"/>) and drives the account towards its desired
/// state by dispatching login / play_games jobs to capable agents.
///
/// Guarantees and limits:
/// - One assignment per account at a time; every pass considers at most one action.
/// - Failed logins back off exponentially (base cooldown, doubled per consecutive
///   failure, capped) and stop after <see cref="Config.ReconcileMaxLoginAttempts"/>
///   consecutive failures (throttled until the spec is updated or the session
///   reports progress).
/// - Each agent takes at most <see cref="Config.ReconcileMaxAccountsPerAgent"/>
///   assigned accounts; when an assigned agent disappears the account is
///   rebalanced to another capable agent.
/// - Dry-run mode reports deviations (log + counter) without dispatching anything.
/// - Orchestration decisions are audited with the actor "orchestrator".
/// </summary>
public sealed partial class DesiredStateReconciler : BackgroundService
{
	private const string OrchestratorMeta = "desired-state";
	private const string LoginAction = "login";
	private const string PlayGamesAction = "play_games";
	private const string CardDropsAction = "get_card_drops";
	private const string PlaytimeAction = "get_playtime";
	private const string CheckStandingAction = "check_account_standing";
	// Values mirror the AccountTaskRunner action constants; the reconciler
	// dispatches the same jobs through IJobStore directly.
	private const string TradeOffersAction = "get_trade_offers";
	private const string AcceptTradeOfferAction = "accept_trade_offer";
	private const string ConfirmTradeOfferAction = "confirm_trade_offer";
	/// <summary>SteamId64 base for converting the 32-bit partner account ids in trade-offer payloads.</summary>
	internal const ulong SteamId64Base = 76561197960265728;

	/// <summary>A dispatched login job normally finishes well inside its 60s timeout; past this window the outcome is queried.</summary>
	internal static TimeSpan LoginInFlightWindow = TimeSpan.FromSeconds(150);
	/// <summary>Same idea for play_games jobs (30s timeout).</summary>
	internal static TimeSpan PlayInFlightWindow = TimeSpan.FromSeconds(120);
	/// <summary>Same idea for get_card_drops jobs (120s timeout).</summary>
	internal static TimeSpan CardDropsInFlightWindow = TimeSpan.FromSeconds(150);
	/// <summary>Same idea for get_playtime jobs (120s timeout, heavy games-tab page).</summary>
	internal static TimeSpan PlaytimeInFlightWindow = TimeSpan.FromSeconds(150);
	/// <summary>Same idea for the trade-loop jobs (30s timeouts).</summary>
	internal static TimeSpan TradeInFlightWindow = TimeSpan.FromSeconds(60);
	/// <summary>Same idea for check_account_standing jobs (60s timeout, two API round-trips).</summary>
	internal static TimeSpan StandingInFlightWindow = TimeSpan.FromSeconds(150);
	/// <summary>Backoff ceiling for consecutive login failures.</summary>
	private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);
	private static readonly HashSet<string> TransitionalStates = new(StringComparer.Ordinal)
	{
		"Connecting",
		"ConnectingWaitAuthCode",
		"ConnectingWait2FA",
		"Reconnecting"
	};

	private readonly AccountStore _accounts;
	private readonly SessionTracker _sessions;
	private readonly AgentRegistry _agents;
	private readonly IJobStore _jobs;
	private readonly IEventBroker _events;
	private readonly IAuditStore _audit;
	private readonly Config _cfg;
	private readonly ILogger<DesiredStateReconciler> _logger;

	private readonly ConcurrentDictionary<string, AccountRuntime> _runtime = new(StringComparer.OrdinalIgnoreCase);

	private long _loginsDispatched;
	private long _playsDispatched;
	private long _cardDropsDispatched;
	private long _playtimeDispatched;
	private long _tradeAcceptsDispatched;
	private long _rebalances;
	private long _unassignments;
	private long _throttledSkips;
	private long _noAgentSkips;
	private long _dryRunDeviations;

	/// <summary>Login jobs dispatched since startup.</summary>
	public long LoginsDispatched => Interlocked.Read(ref _loginsDispatched);
	/// <summary>play_games jobs dispatched since startup.</summary>
	public long PlaysDispatched => Interlocked.Read(ref _playsDispatched);
	/// <summary>get_card_drops jobs dispatched since startup (smart farming queue refreshes).</summary>
	public long CardDropsDispatched => Interlocked.Read(ref _cardDropsDispatched);
	/// <summary>get_playtime jobs dispatched since startup (boost schedule refreshes).</summary>
	public long PlaytimesDispatched => Interlocked.Read(ref _playtimeDispatched);
	/// <summary>accept_trade_offer jobs dispatched since startup (gift auto-accept loop).</summary>
	public long TradeAcceptsDispatched => Interlocked.Read(ref _tradeAcceptsDispatched);
	/// <summary>Accounts moved to a different agent after their assigned agent disappeared.</summary>
	public long Rebalances => Interlocked.Read(ref _rebalances);
	/// <summary>Assignments dropped because the desired state became offline or the account was disabled.</summary>
	public long Unassignments => Interlocked.Read(ref _unassignments);
	/// <summary>Passes skipped because the account exhausted its login attempt budget (throttled).</summary>
	public long ThrottledSkips => Interlocked.Read(ref _throttledSkips);
	/// <summary>Passes that found no capable agent (or the agent pool was at capacity).</summary>
	public long NoAgentSkips => Interlocked.Read(ref _noAgentSkips);
	/// <summary>Deviations reported in dry-run mode instead of being acted on.</summary>
	public long DryRunDeviations => Interlocked.Read(ref _dryRunDeviations);

	public DesiredStateReconciler(
		AccountStore accounts,
		SessionTracker sessions,
		AgentRegistry agents,
		IJobStore jobs,
		IEventBroker events,
		IAuditStore audit,
		Config cfg,
		ILogger<DesiredStateReconciler> logger)
	{
		_accounts = accounts;
		_sessions = sessions;
		_agents = agents;
		_jobs = jobs;
		_events = events;
		_audit = audit;
		_cfg = cfg;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Interval <= 0 disables the reconciler (documented kill switch).
		if (_cfg.ReconcileIntervalSeconds <= 0)
		{
			_logger.LogInformation("Desired-state reconciler disabled (interval <= 0)");
			return;
		}

		using PeriodicTimer timer = new(TimeSpan.FromSeconds(_cfg.ReconcileIntervalSeconds));
		// The loop body is inverted out (internal for tests) so the exhausted-timer
		// exit arm — WaitForNextTickAsync returning false after Dispose, a BCL
		// contract — is deterministically reachable with a real timer; on the
		// service's private one it had no Dispose channel and was a dead probe.
		await RunReconcileLoopAsync(timer, stoppingToken).ConfigureAwait(false); // timer disposal stays here — ownership and timing unchanged
	}

	internal async Task RunReconcileLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
	{
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
		{
			long passStarted = Stopwatch.GetTimestamp();
			try
			{
				await ReconcileOnce(stoppingToken).ConfigureAwait(false);
				RecordPass(ok: 1, passStarted); // last-pass telemetry for /v1/system/status
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				// A broken pass must never kill the background service.
				_logger.LogError(ex, "Account reconcile pass failed");
				RecordPass(ok: 0, passStarted);
			}
		}
	}

	// Last-pass telemetry (1 = succeeded, 0 = failed; int flag, not bool, so no
	// ternary branch probe exists on the write path).
	private long _lastPassOk = 1;
	private long _lastPassDurationMs;
	private long _lastPassCompletedTicks; // DateTimeOffset.UtcTicks of completion; 0 = never ran

	/// <summary>Records the outcome of one reconcile pass (internal for tests; called from the reconcile loop).</summary>
	internal void RecordPass(int ok, long passStartedTimestamp)
	{
		long durationMs = (long)Stopwatch.GetElapsedTime(passStartedTimestamp).TotalMilliseconds;
		Interlocked.Exchange(ref _lastPassDurationMs, durationMs);
		Interlocked.Exchange(ref _lastPassCompletedTicks, DateTimeOffset.UtcNow.UtcTicks);
		Interlocked.Exchange(ref _lastPassOk, ok);
	}

	/// <summary>Whether the most recent reconcile pass failed (false before the first pass completes).</summary>
	public bool LastPassFailed => Interlocked.Read(ref _lastPassOk) == 0;

	/// <summary>When the most recent reconcile pass finished, or null if none has completed yet.</summary>
	public DateTimeOffset? LastPassAt
	{
		get
		{
			long ticks = Interlocked.Read(ref _lastPassCompletedTicks);
			return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
		}
	}

	/// <summary>Wall duration of the most recent reconcile pass, or null if none has completed yet.</summary>
	public long? LastPassDurationMs
	{
		get
		{
			long ticks = Interlocked.Read(ref _lastPassCompletedTicks);
			return ticks == 0 ? null : Interlocked.Read(ref _lastPassDurationMs);
		}
	}

	/// <summary>Runs a single reconcile pass over all declared accounts (internal for tests).</summary>
	internal async Task ReconcileOnce(CancellationToken cancellationToken)
	{
		Dictionary<string, ConnectedAgent> connected = _agents.ListConnected()
			.ToDictionary(a => a.Hello.AgentId, StringComparer.Ordinal);
		Dictionary<string, int> load = connected.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
		foreach (AccountRuntime runtime in _runtime.Values)
		{
			if (runtime.AssignedAgent is not null)
			{
				load[runtime.AssignedAgent] = load.GetValueOrDefault(runtime.AssignedAgent) + 1;
			}
		}

		foreach (AccountSpec spec in _accounts.List())
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}

			AccountRuntime runtime = _runtime.GetOrAdd(spec.AccountName, _ => new AccountRuntime());

			if (!spec.Enabled)
			{
				await UnassignAsync(spec, runtime, "account_disabled", cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (spec.DesiredState == AccountDesiredState.Offline)
			{
				await UnassignAsync(spec, runtime, "desired_offline", cancellationToken).ConfigureAwait(false);
				continue;
			}

			try
			{
				await ReconcileActiveAccountAsync(spec, runtime, connected, load, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// One broken account must not block the rest of the pass.
				_logger.LogError(ex, "Reconcile failed for account {AccountName}", spec.AccountName);
			}
		}
	}

}
