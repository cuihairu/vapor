using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Pass dispatch decisions: per-account action choice, agent picking, in-flight windows and login backoff.</summary>
public sealed partial class DesiredStateReconciler
{
	private async Task ReconcileActiveAccountAsync(
		AccountSpec spec,
		AccountRuntime runtime,
		Dictionary<string, ConnectedAgent> connected,
		Dictionary<string, int> load,
		CancellationToken cancellationToken)
	{
		// A spec update resets the failure budget, giving operators a lever to un-throttle.
		// It also invalidates the farm queue for act purposes by forcing a queue
		// refresh (the exclusion list may have changed — CheckedAt reset below),
		// restarts the per-game budget clock, and drops the trade loop's pending
		// decisions. Completion marks, skip marks and the efficiency counters are
		// facts, not policy: they survive spec changes (the dashboard's stats
		// panel would otherwise lose history on every unrelated edit).
		if (runtime.SpecVersion != spec.Version?.Version)
		{
			runtime.SpecVersion = spec.Version?.Version;
			runtime.LoginAttempts = 0;
			runtime.NextAttemptAt = DateTimeOffset.MinValue;
			runtime.FarmQueueCheckedAt = DateTimeOffset.MinValue;
			runtime.FarmAppStartedAt = null;
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
			runtime.FarmAppStartedAt = null;
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
			ResetFarmPolicyBookkeeping(runtime);
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

}
