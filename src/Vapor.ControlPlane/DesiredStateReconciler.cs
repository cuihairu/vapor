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

	/// <summary>A dispatched login job normally finishes well inside its 60s timeout; past this window the outcome is queried.</summary>
	internal static TimeSpan LoginInFlightWindow = TimeSpan.FromSeconds(150);
	/// <summary>Same idea for play_games jobs (30s timeout).</summary>
	internal static TimeSpan PlayInFlightWindow = TimeSpan.FromSeconds(120);
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
	private long _rebalances;
	private long _unassignments;
	private long _throttledSkips;
	private long _noAgentSkips;
	private long _dryRunDeviations;

	/// <summary>Login jobs dispatched since startup.</summary>
	public long LoginsDispatched => Interlocked.Read(ref _loginsDispatched);
	/// <summary>play_games jobs dispatched since startup.</summary>
	public long PlaysDispatched => Interlocked.Read(ref _playsDispatched);
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
		if (runtime.SpecVersion != spec.Version?.Version)
		{
			runtime.SpecVersion = spec.Version?.Version;
			runtime.LoginAttempts = 0;
			runtime.NextAttemptAt = DateTimeOffset.MinValue;
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
				await SettleActiveJobAsync(spec, runtime, cancellationToken).ConfigureAwait(false);
			}

			if (spec.DesiredState == AccountDesiredState.Idle && spec.IdleApps is { Count: > 0 } && !runtime.Idling && now >= runtime.NextAttemptAt)
			{
				await DispatchPlayAsync(spec, runtime, connected, load, stop: false, cancellationToken).ConfigureAwait(false);
			}
			else if (spec.DesiredState == AccountDesiredState.Online && runtime.Idling)
			{
				await DispatchPlayAsync(spec, runtime, connected, load, stop: true, cancellationToken).ConfigureAwait(false);
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
			await SettleActiveJobAsync(spec, runtime, cancellationToken).ConfigureAwait(false);
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
		runtime.LastAction = "unassigned";
		runtime.LastActionAt = DateTimeOffset.UtcNow;
		Interlocked.Increment(ref _unassignments);
		await RecordActionAsync(spec, null, "unassigned", reason, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task DispatchPlayAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		bool stop,
		CancellationToken cancellationToken)
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
		if (!stop && spec.IdleApps is { Count: > 0 })
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
		runtime.NextAttemptAt = runtime.ActiveJobDispatchedAt + Cooldown(1);
		runtime.LastAction = stop ? "stop_dispatched" : "idle_dispatched";
		runtime.LastActionAt = runtime.ActiveJobDispatchedAt;
		runtime.LastDeviation = null;
		load[agent.Hello.AgentId] = load.GetValueOrDefault(agent.Hello.AgentId) + 1;
		Interlocked.Increment(ref _playsDispatched);

		await RecordActionAsync(spec, job.Job.Id, stop ? "stop_dispatched" : "idle_dispatched",
			reason: stop ? "desired online" : "desired idle", agentId: agent.Hello.AgentId, cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Queries the outcome of a long-outstanding orchestration job and accounts for it
	/// (failure counting, idle flag). The job is cleared afterwards so the next pass can decide freely.
	/// </summary>
	private async Task SettleActiveJobAsync(AccountSpec spec, AccountRuntime runtime, CancellationToken cancellationToken)
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
			}

			await CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
			CountFailure(runtime, $"task:{task.Id}", $"job {action} timed out");
		}
		else if (string.Equals(action, PlayGamesAction, StringComparison.Ordinal) && task.Status == JobTaskStatus.Canceled)
		{
			runtime.Idling = false;
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
		return string.Equals(action, PlayGamesAction, StringComparison.Ordinal) ? PlayInFlightWindow : LoginInFlightWindow;
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
			LastAction: runtime.LastAction,
			LastActionAt: runtime.LastActionAt == DateTimeOffset.MinValue ? null : runtime.LastActionAt,
			LastDeviation: runtime.LastDeviation
		);
	}

	private sealed class AccountRuntime
	{
		public string? AssignedAgent;
		public string? ActiveJobId;
		public string? ActiveJobAction;
		public DateTimeOffset ActiveJobDispatchedAt;
		public int LoginAttempts;
		public DateTimeOffset NextAttemptAt = DateTimeOffset.MinValue;
		public bool Idling;
		public string? LastCountedFailureKey;
		public int? SpecVersion;
		public DateTimeOffset LastActionAt = DateTimeOffset.MinValue;
		public string? LastAction;
		public string? LastDeviation;
	}
}

/// <summary>Read-only orchestration state for one account (aggregate view payload).</summary>
public sealed record AccountOrchestrationView(
	string? AssignedAgent,
	string? ActiveJobId,
	string? ActiveJobAction,
	int LoginAttempts,
	DateTimeOffset? NextAttemptAt,
	bool Idling,
	string? LastAction,
	DateTimeOffset? LastActionAt,
	string? LastDeviation
);
