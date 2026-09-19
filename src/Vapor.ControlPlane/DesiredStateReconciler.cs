using System.Collections.Concurrent;
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
public sealed class DesiredStateReconciler : BackgroundService
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
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
		{
			try
			{
				await ReconcileOnce(stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				// A broken pass must never kill the background service.
				_logger.LogError(ex, "Account reconcile pass failed");
			}
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

	private async Task ReconcileActiveAccountAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		CancellationToken cancellationToken)
	{
		// A spec update resets the failure budget, giving operators a lever to un-throttle.
		// It also invalidates the farm queue — the exclusion list may have changed — and
		// the trade loop: the whitelist may have changed, so pending decisions are
		// dropped and the next pass re-queries instead of acting on stale data.
		if (runtime.SpecVersion != spec.Version?.Version)
		{
			runtime.SpecVersion = spec.Version?.Version;
			runtime.LoginAttempts = 0;
			runtime.NextAttemptAt = DateTimeOffset.MinValue;
			runtime.FarmQueue = null;
			runtime.FarmQueueCheckedAt = DateTimeOffset.MinValue;
			runtime.BoostUnmetApps = null;
			runtime.BoostPlayingApps = null;
			runtime.BoostCheckedAt = DateTimeOffset.MinValue;
			runtime.TradeOffersToAccept = null;
			runtime.TradeOfferCheckedAt = DateTimeOffset.MinValue;
		}

		// 1. Assigned agent disappeared → drop the assignment and rebalance below.
		if (runtime.AssignedAgent is not null && !connected.ContainsKey(runtime.AssignedAgent))
		{
			string lost = runtime.AssignedAgent;
			if (!await GuardDryRunAsync(spec, runtime, $"assigned agent '{lost}' disconnected", cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			await CancelActiveJobAsync(runtime, cancellationToken).ConfigureAwait(false);
			runtime.AssignedAgent = null;
			runtime.ActiveJobId = null;
			runtime.ActiveJobAction = null;
			runtime.Idling = false;
			runtime.FarmingAppId = null;
			runtime.BoostPlayingApps = null;
			Interlocked.Increment(ref _rebalances);
			await RecordActionAsync(spec, null, "rebalanced", $"agent '{lost}' disconnected", cancellationToken: cancellationToken).ConfigureAwait(false);
		}

		SessionSnapshot? snapshot = _sessions.Get(spec.AccountName);
		DateTimeOffset now = DateTimeOffset.UtcNow;
		bool fresh = snapshot is not null && now - snapshot!.UpdatedAt <= TimeSpan.FromSeconds(_cfg.ReconcileSessionStalenessSeconds);

		// 2. Healthy session: converged (and the login job, if any, is done).
		if (fresh && string.Equals(snapshot!.State, "Connected", StringComparison.Ordinal))
		{
			// Success clears login backoff — but leaves a play cooldown untouched.
			bool hadFailures = runtime.LoginAttempts > 0;
			runtime.LoginAttempts = 0;
			if (hadFailures)
			{
				runtime.NextAttemptAt = DateTimeOffset.MinValue;
			}

			runtime.LastDeviation = null;
			if (runtime.ActiveJobAction == LoginAction)
			{
				runtime.ActiveJobId = null;
				runtime.ActiveJobAction = null;
			}
			else if (runtime.ActiveJobId is not null && now - runtime.ActiveJobDispatchedAt >= InFlightWindow(runtime.ActiveJobAction))
			{
				// Settle a finished play_games job (cancelled/failed) before deciding to re-dispatch.
				await SettleActiveJobAsync(spec, runtime, connected, load, cancellationToken).ConfigureAwait(false);
			}

			// Standing health check: a periodic per-account probe of the ban
			// state, ahead of the trade policy (a quarantined account never
			// reaches it — the check below claims the slot and returns).
			if (_cfg.ReconcileStandingRefreshSeconds > 0
				&& now - runtime.StandingCheckedAt >= TimeSpan.FromSeconds(_cfg.ReconcileStandingRefreshSeconds)
				&& runtime.ActiveJobId is null)
			{
				await ReconcileStandingAsync(spec, runtime, connected, load, now, cancellationToken).ConfigureAwait(false);
				if (runtime.ActiveJobId is not null)
				{
					return;
				}
			}

			// Trade-policy evaluation runs for any connected account with the
			// policy enabled, before the desired-state loop claims the single
			// per-account slot; when the trade loop holds the slot this pass,
			// the desired-state work simply happens on the next pass.
			if (spec.TradePolicy is { AutoAcceptGifts: true })
			{
				await ReconcileTradeAsync(spec, runtime, connected, load, now, cancellationToken).ConfigureAwait(false);
				if (runtime.ActiveJobId is not null)
				{
					return;
				}
			}

			if (spec.DesiredState == AccountDesiredState.Farm)
			{
				await ReconcileFarmAsync(spec, runtime, connected, load, now, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (spec.DesiredState == AccountDesiredState.Boost)
			{
				await ReconcileBoostAsync(spec, runtime, connected, load, now, cancellationToken).ConfigureAwait(false);
				return;
			}

			// Leaving the farm/boost state drops its bookkeeping.
			runtime.FarmQueue = null;
			runtime.FarmingAppId = null;
			runtime.FarmQueueCheckedAt = DateTimeOffset.MinValue;
			runtime.BoostUnmetApps = null;
			runtime.BoostPlayingApps = null;
			runtime.BoostCheckedAt = DateTimeOffset.MinValue;

			if (spec.DesiredState == AccountDesiredState.Idle && spec.IdleApps is { Count: > 0 } && !runtime.Idling && now >= runtime.NextAttemptAt)
			{
				await DispatchPlayAsync(spec, runtime, connected, load, stop: false, cancellationToken: cancellationToken).ConfigureAwait(false);
			}
			else if (spec.DesiredState == AccountDesiredState.Online && runtime.Idling)
			{
				await DispatchPlayAsync(spec, runtime, connected, load, stop: true, cancellationToken: cancellationToken).ConfigureAwait(false);
			}

			return;
		}

		// 3. Login in progress (connecting / waiting for a code): leave it alone.
		if (fresh && TransitionalStates.Contains(snapshot!.State))
		{
			return;
		}

		// 4. In-flight orchestration job whose outcome is now queryable.
		if (runtime.ActiveJobId is not null && now - runtime.ActiveJobDispatchedAt >= InFlightWindow(runtime.ActiveJobAction))
		{
			await SettleActiveJobAsync(spec, runtime, connected, load, cancellationToken).ConfigureAwait(false);
		}

		// 5. A play_games job is still being processed — wait for it before deciding again.
		if (runtime.ActiveJobId is not null)
		{
			return;
		}

		// 6. Failure bookkeeping from observed snapshots (counted once per snapshot).
		if (snapshot is not null && string.Equals(snapshot.State, "LoginFailed", StringComparison.Ordinal))
		{
			CountFailure(runtime, $"snapshot:{snapshot.UpdatedAt:O}", "login failed");
		}

		// 7. Throttle and cooldown gates (throttle first so exhausted accounts always report it).
		if (runtime.LoginAttempts >= _cfg.ReconcileMaxLoginAttempts)
		{
			Interlocked.Increment(ref _throttledSkips);
			runtime.LastDeviation = $"throttled: {runtime.LoginAttempts} consecutive failed logins";
			_logger.LogWarning(
				"Account {AccountName} throttled after {Attempts} consecutive failed logins; update the account spec to reset the budget",
				spec.AccountName, runtime.LoginAttempts);
			return;
		}

		if (now < runtime.NextAttemptAt)
		{
			return;
		}

		// 8. Dispatch a login to a capable agent with free capacity.
		ConnectedAgent? agent = PickAgent(spec, connected, load, LoginAction);
		if (agent is null)
		{
			Interlocked.Increment(ref _noAgentSkips);
			runtime.LastDeviation = "no capable agent available";
			_logger.LogWarning("No capable agent available for account {AccountName} (region={Region}, pinned={AgentId})",
				spec.AccountName, spec.Region ?? "<any>", spec.AgentId ?? "<none>");
			return;
		}

		if (!await GuardDryRunAsync(spec, runtime, $"would dispatch login to agent '{agent.Hello.AgentId}'", cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		JobWithTasks job = await _jobs.CreateJob(
			new CreateJobRequest(
				Action: LoginAction,
				Region: spec.Region,
				Targets: [spec.AccountName],
				Payload: new Dictionary<string, object?>(),
				Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
			cancellationToken).ConfigureAwait(false);

		runtime.AssignedAgent = agent.Hello.AgentId;
		runtime.ActiveJobId = job.Job.Id;
		runtime.ActiveJobAction = LoginAction;
		runtime.ActiveJobDispatchedAt = now;
		runtime.Idling = false;
		runtime.NextAttemptAt = now + Cooldown(runtime.LoginAttempts + 1);
		runtime.LastAction = "login_dispatched";
		runtime.LastActionAt = now;
		runtime.LastDeviation = null;
		load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;
		Interlocked.Increment(ref _loginsDispatched);

		await RecordActionAsync(spec, job.Job.Id, "login_dispatched",
			reason: runtime.LoginAttempts > 0 ? $"retry {runtime.LoginAttempts + 1}" : "assign",
			agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Drops the assignment for a disabled/offline account, cancelling its in-flight
	/// orchestration job. No-op when nothing is assigned.
	/// </summary>
	private async Task UnassignAsync(AccountSpec spec, AccountRuntime runtime, string reason, CancellationToken cancellationToken)
	{
		if (runtime.AssignedAgent is null && runtime.ActiveJobId is null && !runtime.Idling)
		{
			return;
		}

		if (!await GuardDryRunAsync(spec, runtime, $"would unassign ({reason})", cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		await CancelActiveJobAsync(runtime, cancellationToken).ConfigureAwait(false);
		runtime.AssignedAgent = null;
		runtime.ActiveJobId = null;
		runtime.ActiveJobAction = null;
		runtime.Idling = false;
		runtime.FarmQueue = null;
		runtime.FarmingAppId = null;
		runtime.FarmQueueCheckedAt = DateTimeOffset.MinValue;
		runtime.BoostUnmetApps = null;
		runtime.BoostPlayingApps = null;
		runtime.BoostCheckedAt = DateTimeOffset.MinValue;
		runtime.LastAction = "unassigned";
		runtime.LastActionAt = DateTimeOffset.UtcNow;
		Interlocked.Increment(ref _unassignments);
		await RecordActionAsync(spec, null, "unassigned", reason, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Smart-farming loop for a connected account in the <see cref="AccountDesiredState.Farm"/>
	/// state: periodically refreshes the card-drop queue by dispatching a
	/// get_card_drops job, then idles the first queued game. When a refresh
	/// shows the current game has run out of drops it rotates to the next one;
	/// with an empty queue it stops idling but keeps the session online.
	/// Farm query failures only mark a deviation and wait for the next refresh
	/// interval — they never consume the login failure budget.
	/// </summary>
	/// <summary>
	/// Dispatches a periodic check_account_standing job. CheckedAt is stamped
	/// on settle (not here), so a failed or lost check retries on the next
	/// refresh interval — the same pattern as the farm-queue refresh.
	/// </summary>
	private async Task ReconcileStandingAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		ConnectedAgent? agent = PickAgent(spec, connected, load, CheckStandingAction);
		if (agent is null)
		{
			Interlocked.Increment(ref _noAgentSkips);
			runtime.LastDeviation = "no capable agent available";
			return;
		}

		if (!await GuardDryRunAsync(spec, runtime, $"would dispatch check_account_standing to agent '{agent.Hello.AgentId}'", cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		JobWithTasks job = await _jobs.CreateJob(
			new CreateJobRequest(
				Action: CheckStandingAction,
				Region: spec.Region,
				Targets: [spec.AccountName],
				Payload: new Dictionary<string, object?>(),
				Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
			cancellationToken).ConfigureAwait(false);

		runtime.AssignedAgent = agent.Hello.AgentId;
		runtime.ActiveJobId = job.Job.Id;
		runtime.ActiveJobAction = CheckStandingAction;
		runtime.ActiveJobDispatchedAt = now;
		runtime.LastAction = "standing_check_dispatched";
		runtime.LastActionAt = now;
		runtime.LastDeviation = null;
		load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;

		await RecordActionAsync(spec, job.Job.Id, "standing_check_dispatched",
			reason: runtime.StandingSummary is null ? "initial standing check" : "standing refresh",
			agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task ReconcileFarmAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		// A card-drops query (or play job) is still in flight — wait for it.
		if (runtime.ActiveJobId is not null)
		{
			return;
		}

		// A failed or unusable report stamps FarmQueueCheckedAt without setting the
		// queue, so the interval — not "queue is null" — gates the retry; this
		// keeps a flapping badges page from being re-polled every reconcile pass.
		bool refreshDue = runtime.FarmQueueCheckedAt == DateTimeOffset.MinValue
			|| now - runtime.FarmQueueCheckedAt >= TimeSpan.FromSeconds(_cfg.ReconcileFarmRefreshSeconds);
		if (refreshDue)
		{
			ConnectedAgent? agent = PickAgent(spec, connected, load, CardDropsAction);
			if (agent is null)
			{
				Interlocked.Increment(ref _noAgentSkips);
				runtime.LastDeviation = "no capable agent available";
				return;
			}

			if (!await GuardDryRunAsync(spec, runtime, $"would dispatch get_card_drops to agent '{agent.Hello.AgentId}'", cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			JobWithTasks job = await _jobs.CreateJob(
				new CreateJobRequest(
					Action: CardDropsAction,
					Region: spec.Region,
					Targets: [spec.AccountName],
					Payload: new Dictionary<string, object?>(),
					Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
				cancellationToken).ConfigureAwait(false);

			runtime.AssignedAgent = agent.Hello.AgentId;
			runtime.ActiveJobId = job.Job.Id;
			runtime.ActiveJobAction = CardDropsAction;
			runtime.ActiveJobDispatchedAt = now;
			runtime.LastAction = "card_drops_dispatched";
			runtime.LastActionAt = now;
			runtime.LastDeviation = null;
			load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;
			Interlocked.Increment(ref _cardDropsDispatched);

			await RecordActionAsync(spec, job.Job.Id, "card_drops_dispatched",
				reason: runtime.FarmQueueCheckedAt == DateTimeOffset.MinValue ? "initial farm queue" : "farm queue refresh",
				agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
			return;
		}

		// The queue is fresh — act on it. No queue means the last report was
		// unusable: change nothing and wait for the next refresh.
		if (runtime.FarmQueue is not { } queue)
		{
			return;
		}
		if (runtime.FarmingAppId is uint farming && queue.Contains(farming))
		{
			return; // current game still has drops
		}

		if (queue.Count > 0)
		{
			await DispatchPlayAsync(spec, runtime, connected, load, stop: false, farmAppId: queue[0], cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		else if (runtime.Idling || runtime.FarmingAppId is not null)
		{
			// Everything is farmed out: stop idling, keep the session online.
			await DispatchPlayAsync(spec, runtime, connected, load, stop: true, cancellationToken: cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Playtime-boosting loop for a connected account in the <see cref="AccountDesiredState.Boost"/>
	/// state: periodically refreshes total playtime per game by dispatching a
	/// get_playtime job, then idles every app that has not reached its target
	/// hours (the account's IdleApps list still acts as an exclusion list, so
	/// an app can be kept out of both farming and boosting). When every target
	/// is met it stops idling but keeps the session online. Playtime query
	/// failures only mark a deviation and wait for the next refresh interval —
	/// they never consume the login failure budget.
	/// </summary>
	private async Task ReconcileBoostAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		// A playtime query (or play job) is still in flight — wait for it.
		if (runtime.ActiveJobId is not null)
		{
			return;
		}

		// A failed or unusable report stamps BoostCheckedAt without setting the
		// unmet set, so the interval — not "set is null" — gates the retry; this
		// keeps a flapping games-tab page from being re-polled every pass.
		bool refreshDue = runtime.BoostCheckedAt == DateTimeOffset.MinValue
			|| now - runtime.BoostCheckedAt >= TimeSpan.FromSeconds(_cfg.ReconcileBoostRefreshSeconds);
		if (refreshDue)
		{
			ConnectedAgent? agent = PickAgent(spec, connected, load, PlaytimeAction);
			if (agent is null)
			{
				Interlocked.Increment(ref _noAgentSkips);
				runtime.LastDeviation = "no capable agent available";
				return;
			}

			if (!await GuardDryRunAsync(spec, runtime, $"would dispatch get_playtime to agent '{agent.Hello.AgentId}'", cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			JobWithTasks job = await _jobs.CreateJob(
				new CreateJobRequest(
					Action: PlaytimeAction,
					Region: spec.Region,
					Targets: [spec.AccountName],
					Payload: new Dictionary<string, object?>(),
					Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
				cancellationToken).ConfigureAwait(false);

			runtime.AssignedAgent = agent.Hello.AgentId;
			runtime.ActiveJobId = job.Job.Id;
			runtime.ActiveJobAction = PlaytimeAction;
			runtime.ActiveJobDispatchedAt = now;
			runtime.LastAction = "playtime_dispatched";
			runtime.LastActionAt = now;
			runtime.LastDeviation = null;
			load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;
			Interlocked.Increment(ref _playtimeDispatched);

			await RecordActionAsync(spec, job.Job.Id, "playtime_dispatched",
				reason: runtime.BoostCheckedAt == DateTimeOffset.MinValue ? "initial playtime query" : "playtime refresh",
				agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
			return;
		}

		// The report is fresh — keep the playing set aligned with the unmet targets.
		// No set means the last report was unusable: change nothing and wait for
		// the next refresh (treating it as "all met" would stop idling).
		if (runtime.BoostUnmetApps is not { } unmet)
		{
			return;
		}
		bool playingTargets = runtime.BoostPlayingApps is not null
			&& runtime.BoostPlayingApps.Count == unmet.Count
			&& runtime.BoostPlayingApps.All(unmet.Contains);
		if (unmet.Count > 0)
		{
			if (playingTargets)
			{
				return; // already idling exactly the unmet apps
			}

			await DispatchPlayAsync(spec, runtime, connected, load, stop: false,
				boostGames: string.Join(",", unmet), cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		else if (runtime.Idling || runtime.BoostPlayingApps is { Count: > 0 })
		{
			// Every target met: stop idling, keep the session online.
			await DispatchPlayAsync(spec, runtime, connected, load, stop: true, cancellationToken: cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Gift auto-accept loop for a connected account with
	/// <see cref="TradePolicy.AutoAcceptGifts"/> enabled (todo §32). Mirrors the
	/// farm/boost pattern: periodically dispatches get_trade_offers (throttled
	/// by <see cref="Config.ReconcileTradeRefreshSeconds"/>), then accepts the
	/// filtered result one offer per pass — an offer only qualifies when its
	/// partner is on the whitelist AND it gives nothing back (gifts-only
	/// semantics; value equivalence is never judged). Query failures only mark
	/// a deviation and wait for the next refresh interval — they never consume
	/// the login failure budget.
	/// </summary>
	private async Task ReconcileTradeAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		// A trade job (query / accept / confirm) is still in flight — wait for it.
		if (runtime.ActiveJobId is not null)
		{
			return;
		}

		// Standing quarantine: a VAC/community/game/economy-banned account
		// never receives trade work. The alert and audit entry happen once at
		// detection (standing check settle); every pass while quarantined just
		// skips silently.
		if (runtime.StandingQuarantined)
		{
			return;
		}

		// Dispatch the next queued accept before refreshing the list: draining
		// stale decisions has priority over querying again.
		if (runtime.TradeOffersToAccept is { Count: > 0 } pending)
		{
			PendingGiftOffer offer = pending[0];
			ConnectedAgent? acceptAgent = PickAgent(spec, connected, load, AcceptTradeOfferAction);
			if (acceptAgent is null)
			{
				Interlocked.Increment(ref _noAgentSkips);
				runtime.LastDeviation = "no capable agent available";
				return;
			}

			if (!await GuardDryRunAsync(spec, runtime, $"would accept gift offer {offer.OfferId} from {offer.PartnerSteamId64}", cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			JobWithTasks job = await _jobs.CreateJob(
				new CreateJobRequest(
					Action: AcceptTradeOfferAction,
					Region: spec.Region,
					Targets: [spec.AccountName],
					Payload: new Dictionary<string, object?>
					{
						// Same payload shape the REST accept endpoint builds: the
						// agent-side state machine re-validates the partner from
						// this id before acting.
						["trade_offer_id"] = offer.OfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
						["partner_steam_id"] = offer.PartnerSteamId64.ToString(System.Globalization.CultureInfo.InvariantCulture),
						["verify_state"] = true
					},
					Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
				cancellationToken).ConfigureAwait(false);

			runtime.AssignedAgent = acceptAgent.Hello.AgentId;
			runtime.ActiveJobId = job.Job.Id;
			runtime.ActiveJobAction = AcceptTradeOfferAction;
			runtime.ActiveJobDispatchedAt = now;
			runtime.AcceptingOffer = offer;
			runtime.LastAction = "trade_accept_dispatched";
			runtime.LastActionAt = now;
			runtime.LastDeviation = null;
			load[acceptAgent.Hello.AgentId] = load.GetValueOrDefault(acceptAgent.Hello.AgentId) + 1;
			Interlocked.Increment(ref _tradeAcceptsDispatched);

			await RecordActionAsync(spec, job.Job.Id, "trade_accept_dispatched",
				reason: $"gift offer {offer.OfferId} from whitelisted partner {offer.PartnerSteamId64} (auto-accept policy)",
				agentId: acceptAgent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
			return;
		}

		bool refreshDue = runtime.TradeOfferCheckedAt == DateTimeOffset.MinValue
			|| now - runtime.TradeOfferCheckedAt >= TimeSpan.FromSeconds(_cfg.ReconcileTradeRefreshSeconds);
		if (!refreshDue)
		{
			return;
		}

		ConnectedAgent? agent = PickAgent(spec, connected, load, TradeOffersAction);
		if (agent is null)
		{
			Interlocked.Increment(ref _noAgentSkips);
			runtime.LastDeviation = "no capable agent available";
			return;
		}

		if (!await GuardDryRunAsync(spec, runtime, $"would dispatch get_trade_offers to agent '{agent.Hello.AgentId}'", cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		JobWithTasks scanJob = await _jobs.CreateJob(
			new CreateJobRequest(
				Action: TradeOffersAction,
				Region: spec.Region,
				Targets: [spec.AccountName],
				Payload: new Dictionary<string, object?> { ["active_only"] = true },
				Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
			cancellationToken).ConfigureAwait(false);

		runtime.AssignedAgent = agent.Hello.AgentId;
		runtime.ActiveJobId = scanJob.Job.Id;
		runtime.ActiveJobAction = TradeOffersAction;
		runtime.ActiveJobDispatchedAt = now;
		runtime.LastAction = "trade_offers_dispatched";
		runtime.LastActionAt = now;
		runtime.LastDeviation = null;
		load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;

		await RecordActionAsync(spec, scanJob.Job.Id, "trade_offers_dispatched",
			reason: runtime.TradeOfferCheckedAt == DateTimeOffset.MinValue ? "initial trade-offer scan" : "trade-offer refresh",
			agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Chains the mobile confirmation after a successful gift-offer accept. The
	/// assigned agent is reused (it just ran the accept); a dry-run pass never
	/// reaches this because the accept itself is gated. The identity secret
	/// never leaves the agent — the confirm action reads it from the
	/// agent-side credential store.
	/// </summary>
	private async Task DispatchTradeConfirmAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		ulong offerId,
		CancellationToken cancellationToken)
	{
		string? agentId = runtime.AssignedAgent;
		if (agentId is null || !connected.ContainsKey(agentId))
		{
			runtime.LastDeviation = $"gift offer {offerId} accepted but its agent is gone for the mobile confirmation";
			return;
		}

		JobWithTasks confirmJob = await _jobs.CreateJob(
			new CreateJobRequest(
				Action: ConfirmTradeOfferAction,
				Region: spec.Region,
				Targets: [spec.AccountName],
				Payload: new Dictionary<string, object?>
				{
					["trade_offer_id"] = offerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
				},
				Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
			cancellationToken).ConfigureAwait(false);

		runtime.ActiveJobId = confirmJob.Job.Id;
		runtime.ActiveJobAction = ConfirmTradeOfferAction;
		runtime.ActiveJobDispatchedAt = DateTimeOffset.UtcNow;
		runtime.LastAction = "trade_confirm_dispatched";
		runtime.LastActionAt = DateTimeOffset.UtcNow;
		load[agentId] = load.GetValueOrDefault(agentId) + 1;

		await RecordActionAsync(spec, confirmJob.Job.Id, "trade_confirm_dispatched",
			reason: $"mobile confirmation for auto-accepted gift offer {offerId}",
			agentId: agentId, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Persists the per-offer decisions of one policy scan as a
	/// trade.policy_evaluated audit entry — including the skips with their
	/// disqualifying reason — so the audit trail explains every offer a scan
	/// saw, not just the ones auto-accepted. Like RecordActionAsync, audit
	/// persistence must never break orchestration. The offer id and partner id
	/// are strings (JS-precision-safe for API consumers).
	/// </summary>
	private async Task RecordTradeEvaluationAsync(
		AccountSpec spec,
		string jobId,
		List<PendingGiftOffer> accepted,
		List<TradeOfferDecision> decisions,
		CancellationToken cancellationToken)
	{
		const int MaxAuditedDecisions = 200;
		bool truncated = decisions.Count > MaxAuditedDecisions;
		var details = new Dictionary<string, object?>
		{
			["policyEnabled"] = true,
			["evaluatedCount"] = decisions.Count,
			["eligibleCount"] = accepted.Count,
			["decisions"] = decisions
				.Take(MaxAuditedDecisions)
				.Select(d => new Dictionary<string, object?>
				{
					["offerId"] = d.OfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
					["partnerSteamId64"] = d.PartnerSteamId64.ToString(System.Globalization.CultureInfo.InvariantCulture),
					["decision"] = d.Decision,
					["reason"] = d.Reason
				})
				.ToList()
		};
		if (truncated)
		{
			details["decisionsTruncated"] = true;
		}

		_logger.LogInformation("Trade policy for {AccountName} evaluated {Evaluated} offer(s): {Eligible} eligible for auto-accept",
			spec.AccountName, decisions.Count, accepted.Count);

		try
		{
			await _audit.RecordAsync(AuditStoreExtensions.CreateEntry(
				"trade.policy_evaluated",
				"orchestrator",
				accountName: spec.AccountName,
				jobId: jobId,
				details: details), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Audit persistence must never break orchestration.
			_logger.LogError(ex, "Failed to persist trade policy audit entry for {AccountName}", spec.AccountName);
		}
	}

	/// <summary>
	/// Audits and announces a completed gift-offer auto-accept: a
	/// trade.auto_accepted audit entry plus a trade.auto_accepted broker event
	/// that the webhook pipeline forwards like any other job event (sinks filter
	/// by type). Payload carries no secret — the offer id, partner and whether a
	/// mobile confirmation is still pending.
	/// </summary>
	private async Task RecordAutoAcceptedAsync(
		AccountSpec spec,
		string jobId,
		PendingGiftOffer offer,
		bool requiresMobileConfirmation,
		CancellationToken cancellationToken)
	{
		string offerId = offer.OfferId.ToString(System.Globalization.CultureInfo.InvariantCulture);
		string partner = offer.PartnerSteamId64.ToString(System.Globalization.CultureInfo.InvariantCulture);

		_events.Publish(jobId, "trade.auto_accepted", new Dictionary<string, object?>
		{
			["accountName"] = spec.AccountName,
			["offerId"] = offerId,
			["partnerSteamId64"] = partner,
			["requiresMobileConfirmation"] = requiresMobileConfirmation
		});

		try
		{
			await _audit.RecordAsync(AuditStoreExtensions.CreateEntry(
				"trade.auto_accepted",
				"orchestrator",
				accountName: spec.AccountName,
				jobId: jobId,
				details: new Dictionary<string, object?>
				{
					["offerId"] = offerId,
					["partnerSteamId64"] = partner,
					["requiresMobileConfirmation"] = requiresMobileConfirmation
				}), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Audit persistence must never break orchestration.
			_logger.LogError(ex, "Failed to persist trade auto-accept audit entry for {AccountName}", spec.AccountName);
		}
	}

	/// <summary>
	/// The accept-queue projection of a policy evaluation (the decisions that
	/// said "accept"); see <see cref="EvaluateTradeOffers"/> for the full rules.
	/// </summary>
	internal static List<PendingGiftOffer> ExtractPendingGiftOffers(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
		=> EvaluateTradeOffers(output, spec).Accepted;

	/// <summary>
	/// Evaluates a get_trade_offers task output against the account's policy,
	/// producing a decision for every received offer — not just the accepted
	/// ones — so the audit trail shows why each skip happened. An offer
	/// qualifies only if it is incoming (not ours), from a whitelisted partner,
	/// and gives nothing back. All three are red lines: a partner outside the
	/// whitelist is skipped even for an empty offer, and any offer with
	/// items_to_give is skipped even for a whitelisted partner. Accepts both
	/// in-memory dictionaries and JsonElement objects (SQLite round-trip); a
	/// missing/invalid output or an absent whitelist yields empty results, never
	/// a query failure. Entries with no usable offer id are not auditable and
	/// produce no decision.
	/// </summary>
	internal static (List<PendingGiftOffer> Accepted, List<TradeOfferDecision> Decisions) EvaluateTradeOffers(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
	{
		var accepted = new List<PendingGiftOffer>();
		var decisions = new List<TradeOfferDecision>();
		if (output is null
			|| !output.TryGetValue("received_offers", out object? raw)
			|| raw is null
			|| spec.TradePolicy?.PartnerWhitelist is not { Count: > 0 } whitelist)
		{
			return (accepted, decisions);
		}

		var whitelistAccounts = whitelist
			.Select(id => id >= SteamId64Base ? (ulong)(id - SteamId64Base) : 0ul)
			.Where(id => id != 0ul)
			.ToHashSet();

		void Collect(object? entry)
		{
			(ulong OfferId, ulong PartnerAccount, int GiveCount, bool IsOurs)? parsed = TryReadOfferEntry(entry);
			if (parsed is not { } offer || offer.OfferId == 0)
			{
				return;
			}

			ulong partner64 = SteamId64Base + offer.PartnerAccount;
			if (offer.PartnerAccount == 0)
			{
				decisions.Add(new TradeOfferDecision(offer.OfferId, partner64, "skip", "unreadable"));
				return;
			}

			// Never act on our own offers (a counter-offer can surface here),
			// and gifts-only: anything the partner asks in return disqualifies
			// the offer, regardless of the whitelist. A give count that cannot
			// be read is treated as non-zero (safe side).
			string? skipReason = offer.IsOurs ? "our_offer"
				: offer.GiveCount == int.MaxValue ? "give_count_unreadable"
				: offer.GiveCount != 0 ? "has_give_items"
				: !whitelistAccounts.Contains(offer.PartnerAccount) ? "partner_not_whitelisted"
				: null;
			if (skipReason is not null)
			{
				decisions.Add(new TradeOfferDecision(offer.OfferId, partner64, "skip", skipReason));
				return;
			}

			decisions.Add(new TradeOfferDecision(offer.OfferId, partner64, "accept", null));
			accepted.Add(new PendingGiftOffer(offer.OfferId, partner64));
		}

		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } jsonArray)
		{
			foreach (System.Text.Json.JsonElement entry in jsonArray.EnumerateArray())
			{
				Collect(entry);
			}
		}
		else if (raw is System.Collections.IEnumerable entries and not string)
		{
			foreach (object? entry in entries)
			{
				Collect(entry);
			}
		}

		return (accepted, decisions);
	}

	/// <summary>Reads one received-offer entry (dictionary or JsonElement) as (offerId, partnerAccount, giveCount, isOurs).</summary>
	private static (ulong OfferId, ulong PartnerAccount, int GiveCount, bool IsOurs)? TryReadOfferEntry(object? entry)
	{
		if (entry is System.Text.Json.JsonElement json)
		{
			if (json.ValueKind != System.Text.Json.JsonValueKind.Object)
			{
				return null;
			}

			ulong offerId = json.TryGetProperty("trade_offer_id", out System.Text.Json.JsonElement idElem)
				&& ulong.TryParse(idElem.GetString(), out ulong parsedId) ? parsedId : 0;
			ulong partnerAccount = json.TryGetProperty("partner_steam_id", out System.Text.Json.JsonElement partnerElem)
				&& ulong.TryParse(partnerElem.GetString(), out ulong parsedPartner) ? parsedPartner : 0;
			int giveCount = json.TryGetProperty("items_to_give_count", out System.Text.Json.JsonElement giveElem)
				&& giveElem.TryGetInt32(out int parsedGive) ? parsedGive : int.MaxValue;
			bool isOurs = json.TryGetProperty("is_our_offer", out System.Text.Json.JsonElement oursElem)
				&& oursElem.ValueKind == System.Text.Json.JsonValueKind.True;
			return (offerId, partnerAccount, giveCount, isOurs);
		}

		if (entry is IReadOnlyDictionary<string, object?> dict)
		{
			ulong offerId = dict.TryGetValue("trade_offer_id", out object? idValue)
				&& ulong.TryParse(idValue?.ToString(), out ulong parsedId) ? parsedId : 0;
			ulong partnerAccount = dict.TryGetValue("partner_steam_id", out object? partnerValue)
				&& ulong.TryParse(partnerValue?.ToString(), out ulong parsedPartner) ? parsedPartner : 0;
			int giveCount = dict.TryGetValue("items_to_give_count", out object? giveValue)
				&& int.TryParse(giveValue?.ToString(), out int parsedGive) ? parsedGive : int.MaxValue;
			bool isOurs = dict.TryGetValue("is_our_offer", out object? oursValue) && oursValue is true;
			return (offerId, partnerAccount, giveCount, isOurs);
		}

		return null;
	}

	/// <summary>Reads a boolean output flag; a missing or unreadable flag is false (never chains a confirmation).</summary>
	private static bool OutputFlagIsTrue(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? value) || value is null)
		{
			return false;
		}

		if (value is System.Text.Json.JsonElement json)
		{
			return json.ValueKind == System.Text.Json.JsonValueKind.True;
		}

		return string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase) || value is true;
	}


	/// <summary>
	/// Rebuilds the farm queue from a get_card_drops task output. Entries are
	/// kept in report order (drops-remaining descending as produced by the
	/// action); the account's IdleApps list is the farm exclusion list. Accepts
	/// both in-memory dictionaries and JsonElement objects (SQLite round-trip).
	/// A missing/invalid output yields an empty queue — treated as "nothing to
	/// farm", never as a query failure.
	/// </summary>
	internal static List<uint> ExtractFarmQueue(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
	{
		var excluded = new HashSet<string>(StringComparer.Ordinal);
		foreach (string app in spec.IdleApps ?? [])
		{
			excluded.Add(app.Trim());
		}

		var queue = new List<uint>();
		if (output is null || !output.TryGetValue("drops", out object? raw) || raw is null)
		{
			return queue;
		}

		void Collect(object? entry)
		{
			if (!TryReadDropEntry(entry, out uint appId, out int remaining))
			{
				return;
			}

			if (remaining > 0 && appId != 0 && !excluded.Contains(appId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
			{
				queue.Add(appId);
			}
		}

		// After the SQLite JSON round-trip the array (and its entries) are JsonElements;
		// freshly dispatched outputs carry in-memory dictionaries.
		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } jsonArray)
		{
			foreach (System.Text.Json.JsonElement entry in jsonArray.EnumerateArray())
			{
				Collect(entry);
			}
		}
		else if (raw is System.Collections.IEnumerable entries and not string)
		{
			foreach (object? entry in entries)
			{
				Collect(entry);
			}
		}

		return queue;
	}

	/// <summary>
	/// Computes the unmet boost targets from a get_playtime task output: target
	/// apps whose reported total hours are below their goal — or that are
	/// missing from the report entirely (conservative: an absent app keeps
	/// idling until it shows up with enough hours; mistaking missing data for
	/// "reached the target" would stop boosting early). The account's IdleApps
	/// list acts as an exclusion list, mirroring farming. Returns null when the
	/// output is unusable (missing/malformed report) — the caller keeps its
	/// previous state, because an empty unmet set would otherwise read as "all
	/// targets met". Accepts both in-memory dictionaries and JsonElement
	/// objects (SQLite round-trip).
	/// </summary>
	internal static List<uint>? ExtractBoostUnmet(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
	{
		if (output is null || !output.TryGetValue("playtimes", out object? raw) || raw is null)
		{
			return null;
		}

		var targets = spec.BoostTargets;
		if (targets is not { Count: > 0 })
		{
			return [];
		}

		var reported = new Dictionary<uint, double>();
		bool valid = true;
		void Collect(object? entry)
		{
			if (!valid || !TryReadPlaytimeEntry(entry, out uint appId, out double hours))
			{
				valid = false;
				return;
			}

			reported[appId] = hours;
		}

		// After the SQLite JSON round-trip the array (and its entries) are
		// JsonElements; freshly dispatched outputs carry in-memory dictionaries.
		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } jsonArray)
		{
			foreach (System.Text.Json.JsonElement entry in jsonArray.EnumerateArray())
			{
				Collect(entry);
			}
		}
		else if (raw is System.Collections.IEnumerable entries and not string)
		{
			foreach (object? entry in entries)
			{
				Collect(entry);
			}
		}
		else
		{
			valid = false;
		}

		if (!valid)
		{
			return null;
		}

		var excluded = new HashSet<string>(StringComparer.Ordinal);
		foreach (string app in spec.IdleApps ?? [])
		{
			excluded.Add(app.Trim());
		}

		// Targets are stored app-id ascending, so the unmet list is too.
		var unmet = new List<uint>();
		foreach (BoostTarget target in targets)
		{
			if (excluded.Contains(target.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
			{
				continue;
			}

			if (!reported.TryGetValue(target.AppId, out double hours) || hours < target.TargetHours)
			{
				unmet.Add(target.AppId);
			}
		}

		return unmet;
	}

	private static bool TryReadPlaytimeEntry(object? entry, out uint appId, out double hours)
	{
		appId = 0;
		hours = 0;

		if (entry is Dictionary<string, object?> dict)
		{
			return TryGetNumber(dict, "app_id", out long id)
				&& NormalizeAppId(id, out appId)
				&& TryGetDouble(dict, "hours", out hours)
				&& hours >= 0;
		}

		if (entry is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element)
		{
			return TryGetNumberFromElement(element, "app_id", out long jsonId)
				&& NormalizeAppId(jsonId, out appId)
				&& TryGetDoubleFromElement(element, "hours", out hours)
				&& hours >= 0;
		}

		return false;
	}

	private static bool NormalizeAppId(long id, out uint appId)
	{
		appId = id is > 0 and <= uint.MaxValue ? (uint)id : 0;
		return appId != 0;
	}

	// internal for tests (property-based invariants), see Vapor.ControlPlane.Tests.
	internal static bool TryGetDouble(Dictionary<string, object?> dict, string key, out double value)
	{
		value = 0;
		if (!dict.TryGetValue(key, out object? raw) || raw is null)
		{
			return false;
		}

		switch (raw)
		{
			// Non-finite values are rejected: a NaN/∞ boost goal would silently
			// never satisfy (NaN comparisons are false) but would still flow into
			// deviation records; conservative refusal keeps the payload honest.
			case double d when double.IsFinite(d):
				value = d;
				return true;
			case long l:
				value = l;
				return true;
			case int i:
				value = i;
				return true;
			case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element:
				return element.TryGetDouble(out value);
			case string s:
				return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
					&& double.IsFinite(value);
			default:
				return false;
		}
	}

	private static bool TryGetDoubleFromElement(System.Text.Json.JsonElement element, string name, out double value)
	{
		value = 0;
		return element.TryGetProperty(name, out System.Text.Json.JsonElement property)
			&& property.ValueKind == System.Text.Json.JsonValueKind.Number
			&& property.TryGetDouble(out value);
	}

	private static bool TryReadDropEntry(object? entry, out uint appId, out int remaining)
	{
		appId = 0;
		remaining = 0;

		if (entry is Dictionary<string, object?> dict)
		{
			return TryGetNumber(dict, "app_id", out long id)
				&& TryGetNumber(dict, "drops_remaining", out long drops)
				&& Normalize(id, drops, out appId, out remaining);
		}

		if (entry is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element)
		{
			return TryGetNumberFromElement(element, "app_id", out long jsonId)
				&& TryGetNumberFromElement(element, "drops_remaining", out long jsonDrops)
				&& Normalize(jsonId, jsonDrops, out appId, out remaining);
		}

		return false;
	}

	private static bool Normalize(long id, long drops, out uint appId, out int remaining)
	{
		appId = 0;
		remaining = 0;
		if (id is > 0 and <= uint.MaxValue && drops is > 0 and <= int.MaxValue)
		{
			appId = (uint)id;
			remaining = (int)drops;
			return true;
		}

		return false;
	}

	private static bool TryGetNumberFromElement(System.Text.Json.JsonElement element, string name, out long value)
	{
		value = 0;
		return element.TryGetProperty(name, out System.Text.Json.JsonElement property)
			&& property.ValueKind == System.Text.Json.JsonValueKind.Number
			&& property.TryGetInt64(out value);
	}

	private static bool TryGetNumber(Dictionary<string, object?> dict, string key, out long value)
	{
		value = 0;
		if (!dict.TryGetValue(key, out object? raw) || raw is null)
		{
			return false;
		}

		switch (raw)
		{
			case long l:
				value = l;
				return true;
			case int i:
				value = i;
				return true;
			case double d:
				value = (long)d;
				return true;
			case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element:
				return element.TryGetInt64(out value);
			case string s:
				return long.TryParse(s, out value);
			default:
				return false;
		}
	}

	private async Task DispatchPlayAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		bool stop,
		uint? farmAppId = null,
		string? boostGames = null,
		CancellationToken cancellationToken = default)
	{
		ConnectedAgent? agent = PickAgent(spec, connected, load, PlayGamesAction);
		if (agent is null)
		{
			Interlocked.Increment(ref _noAgentSkips);
			runtime.LastDeviation = "no capable agent available";
			return;
		}

		if (!await GuardDryRunAsync(spec, runtime, $"would dispatch play_games to agent '{agent.Hello.AgentId}'", cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		var payload = new Dictionary<string, object?>();
		if (!stop && boostGames is { Length: > 0 })
		{
			payload["games"] = boostGames;
		}
		else if (!stop && spec.DesiredState == AccountDesiredState.Farm && farmAppId is uint app)
		{
			payload["games"] = app.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
		else if (!stop && spec.IdleApps is { Count: > 0 })
		{
			payload["games"] = string.Join(",", spec.IdleApps);
		}
		else
		{
			payload["action"] = "stop";
		}

		JobWithTasks job = await _jobs.CreateJob(
			new CreateJobRequest(
				Action: PlayGamesAction,
				Region: spec.Region,
				Targets: [spec.AccountName],
				Payload: payload,
				Meta: new Dictionary<string, string> { ["orchestrator"] = OrchestratorMeta }),
			cancellationToken).ConfigureAwait(false);

		runtime.AssignedAgent = agent.Hello.AgentId;
		runtime.ActiveJobId = job.Job.Id;
		runtime.ActiveJobAction = PlayGamesAction;
		runtime.ActiveJobDispatchedAt = DateTimeOffset.UtcNow;
		runtime.Idling = !stop;
		runtime.FarmingAppId = stop ? null : farmAppId;
		runtime.BoostPlayingApps = stop ? null : (boostGames is { Length: > 0 }
			? boostGames.Split(',').Select(a => uint.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out uint id) ? id : 0).Where(id => id != 0).ToList()
			: runtime.BoostPlayingApps);
		runtime.NextAttemptAt = runtime.ActiveJobDispatchedAt + Cooldown(1);
		runtime.LastAction = stop ? "stop_dispatched" : "idle_dispatched";
		runtime.LastActionAt = runtime.ActiveJobDispatchedAt;
		runtime.LastDeviation = null;
		load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;
		Interlocked.Increment(ref _playsDispatched);

		string reason = stop
			? (spec.DesiredState == AccountDesiredState.Boost ? "all boost targets met"
				: spec.DesiredState == AccountDesiredState.Farm ? "farm queue empty" : "desired online")
			: (boostGames is { Length: > 0 } ? $"boost apps {boostGames}"
				: farmAppId is not null ? $"farm app {farmAppId}" : "desired idle");
		await RecordActionAsync(spec, job.Job.Id, stop ? "stop_dispatched" : "idle_dispatched",
			reason: reason, agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Queries the outcome of a long-outstanding orchestration job and accounts for it
	/// (failure counting, idle flag). The job is cleared afterwards so the next pass can decide freely.
	/// </summary>
	private async Task SettleActiveJobAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		CancellationToken cancellationToken)
	{
		string jobId = runtime.ActiveJobId!;
		string action = runtime.ActiveJobAction ?? LoginAction;

		JobWithTasks outcome;
		try
		{
			outcome = await _jobs.GetJob(jobId, cancellationToken).ConfigureAwait(false);
		}
		catch (NotFoundException)
		{
			runtime.ActiveJobId = null;
			runtime.ActiveJobAction = null;
			return;
		}

		runtime.ActiveJobId = null;
		runtime.ActiveJobAction = null;

		// Card-drops query outcomes feed the farm queue; they never count into
		// the login failure budget. A failed query just waits for the next
		// refresh interval (CheckedAt is stamped regardless of the outcome).
		if (string.Equals(action, CardDropsAction, StringComparison.Ordinal))
		{
			runtime.FarmQueueCheckedAt = DateTimeOffset.UtcNow;
			JobTask? dropsTask = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
				?? outcome.Tasks.FirstOrDefault();
			if (dropsTask is null)
			{
				return;
			}

			if (dropsTask.Status == JobTaskStatus.Finished)
			{
				runtime.FarmQueue = ExtractFarmQueue(dropsTask.Output, spec);
				_logger.LogInformation("Farm queue for {AccountName} refreshed: {Count} app(s) with drops",
					spec.AccountName, runtime.FarmQueue.Count);
			}
			else
			{
				runtime.LastDeviation = $"job {action} outcome: {dropsTask.Status} {dropsTask.Error}".TrimEnd();
			}

			return;
		}

		// Standing-check outcomes feed the quarantine gate. A ban hit flips the
		// runtime into quarantine (blocking all trade work) exactly once, with an
		// alert event and an audit entry; a release back to clean flips it out.
		if (string.Equals(action, CheckStandingAction, StringComparison.Ordinal))
		{
			runtime.StandingCheckedAt = DateTimeOffset.UtcNow;
			JobTask? standingTask = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
				?? outcome.Tasks.FirstOrDefault();
			if (standingTask is null)
			{
				return;
			}

			if (standingTask.Status != JobTaskStatus.Finished)
			{
				runtime.LastDeviation = $"job {action} outcome: {standingTask.Status} {standingTask.Error}".TrimEnd();
				return;
			}

			string? summary = standingTask.Output is { } output
				&& output.TryGetValue("standing", out object? value)
				&& value is string s
					? s
					: null;
			if (summary is null)
			{
				runtime.LastDeviation = "standing check output unreadable";
				return;
			}

			runtime.StandingSummary = summary;
			bool quarantine = !string.Equals(summary, "clean", StringComparison.Ordinal);
			if (quarantine && !runtime.StandingQuarantined)
			{
				runtime.StandingQuarantined = true;
				_logger.LogWarning("Account {AccountName} quarantined from trade work: standing={Standing}", spec.AccountName, summary);
				_events.Publish(jobId, "account.standing_alert", new Dictionary<string, object?>
				{
					["account"] = spec.AccountName,
					["standing"] = summary,
					["economyBan"] = standingTask.Output!.TryGetValue("economyBan", out object? econ) ? econ : null,
					["vacBanned"] = standingTask.Output.TryGetValue("vacBanned", out object? vac) ? vac : null,
				});
				await RecordActionAsync(spec, jobId, "standing_quarantined",
					reason: summary, cancellationToken: cancellationToken).ConfigureAwait(false);
			}
			else if (!quarantine && runtime.StandingQuarantined)
			{
				runtime.StandingQuarantined = false;
				_logger.LogInformation("Account {AccountName} released from trade quarantine: standing=clean", spec.AccountName);
				_events.Publish(jobId, "account.standing_released", new Dictionary<string, object?>
				{
					["account"] = spec.AccountName,
					["standing"] = summary,
				});
				await RecordActionAsync(spec, jobId, "standing_released",
					reason: "clean", cancellationToken: cancellationToken).ConfigureAwait(false);
			}

			return;
		}

		// Playtime query outcomes feed the boost schedule with the same rules:
		// no login-failure accounting, stamped refresh time, deviations instead
		// of retries. An invalid report leaves the previous unmet set untouched
		// — an empty set would read as "all targets met" and stop idling.
		if (string.Equals(action, PlaytimeAction, StringComparison.Ordinal))
		{
			runtime.BoostCheckedAt = DateTimeOffset.UtcNow;
			JobTask? playtimeTask = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
				?? outcome.Tasks.FirstOrDefault();
			if (playtimeTask is null)
			{
				return;
			}

			if (playtimeTask.Status == JobTaskStatus.Finished)
			{
				List<uint>? unmet = ExtractBoostUnmet(playtimeTask.Output, spec);
				if (unmet is not null)
				{
					runtime.BoostUnmetApps = unmet;
					_logger.LogInformation("Boost schedule for {AccountName} refreshed: {Count} app(s) below target",
						spec.AccountName, unmet.Count);
				}
				else
				{
					runtime.LastDeviation = $"job {action} returned no usable playtime report";
				}
			}
			else
			{
				runtime.LastDeviation = $"job {action} outcome: {playtimeTask.Status} {playtimeTask.Error}".TrimEnd();
			}

			return;
		}

		// Trade-loop outcomes follow the same tolerance rules (stamped CheckedAt,
		// deviation instead of login-failure accounting). The query refreshes the
		// pending gift list; the accept dispatches drain it one offer per pass,
		// chaining a mobile confirmation when Steam demands one.
		if (string.Equals(action, TradeOffersAction, StringComparison.Ordinal))
		{
			runtime.TradeOfferCheckedAt = DateTimeOffset.UtcNow;
			JobTask? tradeTask = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
				?? outcome.Tasks.FirstOrDefault();
			if (tradeTask is null)
			{
				return;
			}

			if (tradeTask.Status == JobTaskStatus.Finished)
			{
				(List<PendingGiftOffer> accepted, List<TradeOfferDecision> decisions) = EvaluateTradeOffers(tradeTask.Output, spec);
				runtime.TradeOffersToAccept = accepted;
				_logger.LogInformation("Trade-offer scan for {AccountName}: {Count} gift offer(s) eligible for auto-accept",
					spec.AccountName, accepted.Count);
				await RecordTradeEvaluationAsync(spec, jobId, accepted, decisions, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				runtime.LastDeviation = $"job {action} outcome: {tradeTask.Status} {tradeTask.Error}".TrimEnd();
			}

			return;
		}

		if (string.Equals(action, AcceptTradeOfferAction, StringComparison.Ordinal))
		{
			PendingGiftOffer? accepted = runtime.AcceptingOffer;
			runtime.AcceptingOffer = null;
			if (accepted is not null && runtime.TradeOffersToAccept is { Count: > 0 } queue
				&& queue[0].OfferId == accepted.OfferId)
			{
				// Drain the decision whatever the outcome: a failed accept is
				// recorded and retried only after the next refresh re-detects
				// the offer, never spun on within this cycle.
				queue.RemoveAt(0);
			}

			JobTask? acceptTask = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
				?? outcome.Tasks.FirstOrDefault();
			if (acceptTask is null)
			{
				return;
			}

			if (acceptTask.Status == JobTaskStatus.Finished)
			{
				_logger.LogInformation("Gift offer {OfferId} for {AccountName} accepted", accepted?.OfferId, spec.AccountName);
				bool requiresConfirmation = OutputFlagIsTrue(acceptTask.Output, "requires_mobile_confirmation");
				if (accepted is not null)
				{
					await RecordAutoAcceptedAsync(spec, jobId, accepted, requiresConfirmation, cancellationToken).ConfigureAwait(false);
				}

				if (requiresConfirmation)
				{
					// Steam wants a mobile confirmation to finish the accept —
					// chain confirm_trade_offer now (the identity secret never
					// leaves the agent; the confirm action reads it there).
					await DispatchTradeConfirmAsync(spec, runtime, connected, load, accepted?.OfferId ?? 0, cancellationToken).ConfigureAwait(false);
				}
			}
			else
			{
				runtime.LastDeviation = $"gift offer {accepted?.OfferId} accept outcome: {acceptTask.Status} {acceptTask.Error}".TrimEnd();
			}

			return;
		}

		if (string.Equals(action, ConfirmTradeOfferAction, StringComparison.Ordinal))
		{
			JobTask? confirmTask = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
				?? outcome.Tasks.FirstOrDefault();
			if (confirmTask is not null && confirmTask.Status != JobTaskStatus.Finished)
			{
				runtime.LastDeviation = $"trade mobile confirmation outcome: {confirmTask.Status} {confirmTask.Error}".TrimEnd();
			}

			return;
		}

		JobTask? task = outcome.Tasks.FirstOrDefault(t => string.Equals(t.Target, spec.AccountName, StringComparison.OrdinalIgnoreCase))
			?? outcome.Tasks.FirstOrDefault();

		if (task is null)
		{
			return;
		}

		if (task.Status == JobTaskStatus.Failed)
		{
			CountFailure(runtime, $"task:{task.Id}", $"job {action} failed: {task.Error}");
		}
		else if (task.Status is JobTaskStatus.Queued or JobTaskStatus.Running)
		{
			// Still queued/running past the window: treat as lost and cancel.
			if (string.Equals(action, PlayGamesAction, StringComparison.Ordinal))
			{
				runtime.Idling = false;
				runtime.FarmingAppId = null;
			}

			await CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
			CountFailure(runtime, $"task:{task.Id}", $"job {action} timed out");
		}
		else if (string.Equals(action, PlayGamesAction, StringComparison.Ordinal) && task.Status == JobTaskStatus.Canceled)
		{
			runtime.Idling = false;
			runtime.FarmingAppId = null;
		}
	}

	private void CountFailure(AccountRuntime runtime, string failureKey, string description)
	{
		if (string.Equals(runtime.LastCountedFailureKey, failureKey, StringComparison.Ordinal))
		{
			return;
		}

		runtime.LastCountedFailureKey = failureKey;
		runtime.LoginAttempts++;
		runtime.NextAttemptAt = DateTimeOffset.UtcNow + Cooldown(runtime.LoginAttempts + 1);
		runtime.LastDeviation = description;
		_logger.LogWarning("Account orchestration failure for {Key}: {Description} (attempt {Attempts})", failureKey, description, runtime.LoginAttempts);
	}

	/// <summary>Dry-run gate: reports the deviation and returns false (skip execution) when dry-run is on.</summary>
	private async Task<bool> GuardDryRunAsync(AccountSpec spec, AccountRuntime runtime, string deviation, CancellationToken cancellationToken)
	{
		if (!_cfg.ReconcileDryRun)
		{
			return true;
		}

		Interlocked.Increment(ref _dryRunDeviations);
		runtime.LastDeviation = deviation;
		runtime.LastActionAt = DateTimeOffset.UtcNow;
		_logger.LogInformation("[dry-run] account {AccountName}: {Deviation}", spec.AccountName, deviation);

		try
		{
			await _audit.RecordAsync(AuditStoreExtensions.CreateEntry(
				"account.reconciled.dry_run",
				"orchestrator",
				accountName: spec.AccountName,
				details: new Dictionary<string, object?>
				{
					["deviation"] = deviation
				}), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to persist dry-run audit entry for {AccountName}", spec.AccountName);
		}

		return false;
	}

	private async Task CancelActiveJobAsync(AccountRuntime runtime, CancellationToken cancellationToken)
	{
		if (runtime.ActiveJobId is null)
		{
			return;
		}

		string jobId = runtime.ActiveJobId;
		runtime.ActiveJobId = null;
		runtime.ActiveJobAction = null;
		await CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
	}

	private async Task CancelJobAsync(string jobId, CancellationToken cancellationToken)
	{
		try
		{
			await _jobs.CancelJob(jobId, cancellationToken).ConfigureAwait(false);
		}
		catch (NotFoundException)
		{
			// Already gone.
		}
	}

	private ConnectedAgent? PickAgent(AccountSpec spec, Dictionary<string, ConnectedAgent> connected, Dictionary<string, int> load, string action)
	{
		if (!string.IsNullOrWhiteSpace(spec.AgentId)
			&& connected.TryGetValue(spec.AgentId, out ConnectedAgent? pinned)
			&& pinned.SupportsAction(action)
			&& load.GetValueOrDefault(pinned.Hello.AgentId) < _cfg.ReconcileMaxAccountsPerAgent)
		{
			return pinned;
		}

		IEnumerable<ConnectedAgent> candidates = connected.Values;
		if (!string.IsNullOrWhiteSpace(spec.Region))
		{
			candidates = candidates.Where(a => string.Equals(a.Hello.Region, spec.Region, StringComparison.OrdinalIgnoreCase));
		}

		return candidates
			.Where(a => a.SupportsAction(action))
			.Where(a => load.GetValueOrDefault(a.Hello.AgentId) < _cfg.ReconcileMaxAccountsPerAgent)
			.OrderBy(a => load.GetValueOrDefault(a.Hello.AgentId))
			.ThenBy(a => a.Hello.AgentId, StringComparer.Ordinal)
			.FirstOrDefault();
	}

	private TimeSpan InFlightWindow(string? action)
	{
		if (string.Equals(action, PlayGamesAction, StringComparison.Ordinal))
		{
			return PlayInFlightWindow;
		}

		if (string.Equals(action, PlaytimeAction, StringComparison.Ordinal))
		{
			return PlaytimeInFlightWindow;
		}

		if (string.Equals(action, CardDropsAction, StringComparison.Ordinal))
		{
			return CardDropsInFlightWindow;
		}

		if (string.Equals(action, CheckStandingAction, StringComparison.Ordinal))
		{
			return StandingInFlightWindow;
		}

		if (string.Equals(action, TradeOffersAction, StringComparison.Ordinal)
			|| string.Equals(action, AcceptTradeOfferAction, StringComparison.Ordinal)
			|| string.Equals(action, ConfirmTradeOfferAction, StringComparison.Ordinal))
		{
			return TradeInFlightWindow;
		}

		return LoginInFlightWindow;
	}

	/// <summary>Exponential backoff: base, 2x, 4x … capped at 15 minutes. Zero base disables cooldowns.</summary>
	private TimeSpan Cooldown(int attempt)
	{
		if (_cfg.ReconcileLoginCooldownSeconds <= 0)
		{
			return TimeSpan.Zero;
		}

		double seconds = _cfg.ReconcileLoginCooldownSeconds * Math.Pow(2, attempt - 1);
		return TimeSpan.FromSeconds(Math.Min(seconds, MaxCooldown.TotalSeconds));
	}

	private async Task RecordActionAsync(AccountSpec spec, string? jobId, string orchestrationAction, string reason, string? agentId = null, CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Account {AccountName} orchestration: {Action} ({Reason}) agent={AgentId} job={JobId}",
			spec.AccountName, orchestrationAction, reason, agentId ?? "<none>", jobId ?? "<none>");

		var details = new Dictionary<string, object?>
		{
			["orchestrationAction"] = orchestrationAction,
			["reason"] = reason,
			["desiredState"] = spec.DesiredState.ToString()
		};
		if (agentId is not null)
		{
			details["agentId"] = agentId;
		}

		_events.Publish(jobId, "account.reconciled", new Dictionary<string, object?>
		{
			["accountName"] = spec.AccountName,
			["orchestrationAction"] = orchestrationAction,
			["reason"] = reason,
			["agentId"] = agentId
		});

		try
		{
			await _audit.RecordAsync(AuditStoreExtensions.CreateEntry(
				"account.reconciled",
				"orchestrator",
				accountName: spec.AccountName,
				jobId: jobId,
				details: details), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Audit persistence must never break orchestration.
			_logger.LogError(ex, "Failed to persist orchestration audit entry for {AccountName}", spec.AccountName);
		}
	}

	/// <summary>Exposes orchestration state for the account aggregate view (GET /v1/accounts/{name}).</summary>
	internal AccountOrchestrationView? GetOrchestrationView(string accountName)
	{
		if (!_runtime.TryGetValue(accountName, out AccountRuntime? runtime))
		{
			return null;
		}

		return new AccountOrchestrationView(
			AssignedAgent: runtime.AssignedAgent,
			ActiveJobId: runtime.ActiveJobId,
			ActiveJobAction: runtime.ActiveJobAction,
			LoginAttempts: runtime.LoginAttempts,
			NextAttemptAt: runtime.NextAttemptAt == DateTimeOffset.MinValue ? null : runtime.NextAttemptAt,
			Idling: runtime.Idling,
			FarmingAppId: runtime.FarmingAppId,
			FarmQueue: runtime.FarmQueue,
			FarmQueueCheckedAt: runtime.FarmQueueCheckedAt == DateTimeOffset.MinValue ? null : runtime.FarmQueueCheckedAt,
			BoostUnmetApps: runtime.BoostUnmetApps,
			BoostCheckedAt: runtime.BoostCheckedAt == DateTimeOffset.MinValue ? null : runtime.BoostCheckedAt,
			TradeOffersToAccept: runtime.TradeOffersToAccept?.Select(o => o.OfferId).ToList(),
			TradeOfferCheckedAt: runtime.TradeOfferCheckedAt == DateTimeOffset.MinValue ? null : runtime.TradeOfferCheckedAt,
			LastAction: runtime.LastAction,
			LastActionAt: runtime.LastActionAt == DateTimeOffset.MinValue ? null : runtime.LastActionAt,
			LastDeviation: runtime.LastDeviation,
			Standing: runtime.StandingSummary,
			StandingQuarantined: runtime.StandingQuarantined,
			StandingCheckedAt: runtime.StandingCheckedAt == DateTimeOffset.MinValue ? null : runtime.StandingCheckedAt
		);
	}

	/// <summary>
	/// Forces the next reconcile pass to re-run the standing check for one
	/// account (dashboard "run check now"). Honors the single-writer slot:
	/// refused while a job is in flight, when the account is unknown to the
	/// orchestrator, or when standing checks are disabled.
	/// </summary>
	internal bool RequestStandingCheck(string accountName)
	{
		if (_cfg.ReconcileStandingRefreshSeconds <= 0
			|| !_runtime.TryGetValue(accountName, out AccountRuntime? runtime)
			|| runtime.ActiveJobId is not null)
		{
			return false;
		}

		runtime.StandingCheckedAt = DateTimeOffset.MinValue;
		return true;
	}

	/// <summary>Standing snapshot for every tracked account (dashboard account list).</summary>
	internal IReadOnlyList<AccountStandingView> GetStandingSummaries() =>
		_runtime.OrderBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv => new AccountStandingView(
				kv.Key,
				kv.Value.StandingSummary,
				kv.Value.StandingQuarantined,
				kv.Value.StandingCheckedAt == DateTimeOffset.MinValue ? null : kv.Value.StandingCheckedAt))
			.ToList();

	private sealed class AccountRuntime
	{
		public string? AssignedAgent;
		public string? ActiveJobId;
		public string? ActiveJobAction;
		public DateTimeOffset ActiveJobDispatchedAt;
		public int LoginAttempts;
		public DateTimeOffset NextAttemptAt = DateTimeOffset.MinValue;
		public bool Idling;
		public List<uint>? FarmQueue;
		public uint? FarmingAppId;
		public DateTimeOffset FarmQueueCheckedAt = DateTimeOffset.MinValue;
		public List<uint>? BoostUnmetApps;
		public List<uint>? BoostPlayingApps;
		public DateTimeOffset BoostCheckedAt = DateTimeOffset.MinValue;
		public List<PendingGiftOffer>? TradeOffersToAccept;
		public DateTimeOffset TradeOfferCheckedAt = DateTimeOffset.MinValue;
		public PendingGiftOffer? AcceptingOffer;
		public string? StandingSummary;
		public DateTimeOffset StandingCheckedAt = DateTimeOffset.MinValue;
		public bool StandingQuarantined;
		public string? LastCountedFailureKey;
		public int? SpecVersion;
		public DateTimeOffset LastActionAt = DateTimeOffset.MinValue;
		public string? LastAction;
		public string? LastDeviation;
	}

	/// <summary>An incoming gifts-only offer from a whitelisted partner, awaiting its accept dispatch.</summary>
	internal sealed record PendingGiftOffer(ulong OfferId, ulong PartnerSteamId64);

	/// <summary>
	/// One evaluated received offer from a policy scan: "accept" (queued for the
	/// auto-accept dispatch) or "skip" with the red line that disqualified it
	/// (partner_not_whitelisted / has_give_items / our_offer / give_count_unreadable).
	/// </summary>
	internal sealed record TradeOfferDecision(ulong OfferId, ulong PartnerSteamId64, string Decision, string? Reason);
}

/// <summary>Read-only orchestration state for one account (aggregate view payload).</summary>
public sealed record AccountOrchestrationView(
	string? AssignedAgent,
	string? ActiveJobId,
	string? ActiveJobAction,
	int LoginAttempts,
	DateTimeOffset? NextAttemptAt,
	bool Idling,
	uint? FarmingAppId = null,
	IReadOnlyList<uint>? FarmQueue = null,
	DateTimeOffset? FarmQueueCheckedAt = null,
	IReadOnlyList<uint>? BoostUnmetApps = null,
	DateTimeOffset? BoostCheckedAt = null,
	IReadOnlyList<ulong>? TradeOffersToAccept = null,
	DateTimeOffset? TradeOfferCheckedAt = null,
	string? LastAction = null,
	DateTimeOffset? LastActionAt = null,
	string? LastDeviation = null,
	string? Standing = null,
	bool StandingQuarantined = false,
	DateTimeOffset? StandingCheckedAt = null
);

/// <summary>Read-only standing snapshot for one account (dashboard list payload).</summary>
public sealed record AccountStandingView(
	string AccountName,
	string? Standing,
	bool Quarantined,
	DateTimeOffset? CheckedAt
);
