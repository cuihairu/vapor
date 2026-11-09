using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Orchestration job lifecycle: dispatch, settle outcomes, failure counting, dry-run guard, cancellation and audit recording.</summary>
public sealed partial class DesiredStateReconciler
{
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
		if (stop)
		{
			runtime.FarmAppStartedAt = null;
		}
		else if (farmAppId is not null)
		{
			// The per-game budget clock starts when the farm loop starts idling the app.
			runtime.FarmAppStartedAt = DateTimeOffset.UtcNow;
		}
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
				List<uint> fresh = ExtractFarmQueue(dropsTask.Output, spec);

				// Completion marking by queue diff: the report only contains apps
				// that still have drops, so an app that was queued last round and
				// is gone now has finished farming. Marked once, with an audit
				// entry and a farm_progress event per app.
				if (runtime.FarmQueue is { } previous)
				{
					foreach (uint completedApp in previous.Where(a => !fresh.Contains(a)))
					{
						// Copy-on-write (see the budget-exhausted path): never mutate
						// the list the dashboard may be serializing concurrently.
						runtime.FarmCompletedApps = [.. (runtime.FarmCompletedApps ?? []), completedApp];
						_logger.LogInformation(
							"Farm app {AppId} for {AccountName} finished: no drops remaining",
							completedApp, spec.AccountName);
						await RecordActionAsync(spec, jobId, "farm_app_completed",
							reason: $"app {completedApp} card drops exhausted",
							cancellationToken: cancellationToken).ConfigureAwait(false);
						_events.Publish(jobId, "account.farm_progress", new Dictionary<string, object?>
						{
							["account"] = spec.AccountName,
							["kind"] = "app_completed",
							["appId"] = completedApp,
						});
					}
				}

				// A new round after a full drain (Steam granted fresh drops for a
				// finished game) restarts the bookkeeping: previously completed or
				// budget-skipped apps may farm again.
				if (fresh.Count > 0 && runtime.FarmCompletedNotified)
				{
					ResetFarmPolicyBookkeeping(runtime);
				}

				// Done and budget-skipped apps never re-enter the queue, however
				// often the report lags behind the decisions.
				fresh.RemoveAll(a => runtime.FarmCompletedApps?.Contains(a) == true
					|| runtime.FarmSkippedApps?.Contains(a) == true);

				// Efficiency counters: the collected total only ever grows, by the
				// drop counts that disappeared between two reports — a newly queued
				// game raising the remaining total must not read as negative
				// progress.
				if (TryReadTotalDropsRemaining(dropsTask.Output, out int totalRemaining))
				{
					if (runtime.FarmCardsRemaining is { } previousTotal && totalRemaining < previousTotal)
					{
						runtime.FarmCardsCollected += previousTotal - totalRemaining;
					}

					runtime.FarmCardsRemaining = totalRemaining;
					runtime.FarmStatsStartedAt ??= DateTimeOffset.UtcNow;
				}

				runtime.FarmQueue = fresh;
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
				// Copy-on-write: never mutate the list in place — the orchestration
				// view serializes it concurrently for dashboard polls.
				runtime.TradeOffersToAccept = queue.Skip(1).ToList();
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
				agentId: agentId,
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

}
