using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Per-intent reconcile flows: standing, farm (idle and card drops), boost (playtime) and trade offers.</summary>
public sealed partial class DesiredStateReconciler
{
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
			// The per-game budget (if declared) is a fuse against dead farming:
			// once the app has been idled for the whole budget it is marked
			// budget-exhausted and pulled from the queue even though the report
			// still shows drops — the flow below then rotates to the next game
			// or stops the idling. Done/failed-never checks are settled at
			// report time, so this is the only decision this loop makes per pass.
			double? budget = spec.FarmPolicy?.PerGameHourBudget;
			if (budget is not { } hours)
			{
				return; // no budget declared — idle the current game until its drops run out
			}
			if (runtime.FarmAppStartedAt is not { } startedAt)
			{
				// The clock was reset by a policy edit (spec bump) or an agent
				// rebalance, but the game keeps idling — restart the clock from
				// now instead of disarming the fuse forever (nothing re-dispatches
				// a play job for the app that is already running).
				runtime.FarmAppStartedAt = now;
				return;
			}
			if (now - startedAt < TimeSpan.FromHours(hours))
			{
				return; // current game still has drops and budget to spare
			}

			// Copy-on-write: the reconcile thread is the single writer, but REST
			// dashboard polls serialize these lists concurrently. Never mutate in
			// place — publish a fresh list so a concurrent view snapshot is stable.
			queue = queue.Where(a => a != farming).ToList();
			runtime.FarmQueue = queue;
			runtime.FarmSkippedApps = [.. (runtime.FarmSkippedApps ?? []), farming];
			_logger.LogWarning(
				"Farm app {AppId} for {AccountName} exhausted its {Hours:0.#}h budget after {Elapsed:0.#}h — skipped",
				farming, spec.AccountName, hours, (now - startedAt).TotalHours);
			await RecordActionAsync(spec, null, "farm_app_budget_exhausted",
				reason: $"app {farming} idled {(now - startedAt).TotalHours:0.#}h without finishing its drops (budget {hours:0.#}h)",
				cancellationToken: cancellationToken).ConfigureAwait(false);
			_events.Publish(null, "account.farm_progress", new Dictionary<string, object?>
			{
				["account"] = spec.AccountName,
				["kind"] = "app_budget_exhausted",
				["appId"] = farming,
			});
			// Fall through: rotate to the next queued game, or stop if none is left.
		}

		if (queue.Count > 0)
		{
			await DispatchPlayAsync(spec, runtime, connected, load, stop: false, farmAppId: queue[0], cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		else if (runtime.Idling || runtime.FarmingAppId is not null)
		{
			// Everything is farmed out (drained, budget-skipped or excluded):
			// stop idling, keep the session online. The completion notice fires
			// exactly once per drain; a later report with fresh drops resets it.
			if (!runtime.FarmCompletedNotified)
			{
				runtime.FarmCompletedNotified = true;
				int completed = runtime.FarmCompletedApps?.Count ?? 0;
				int skipped = runtime.FarmSkippedApps?.Count ?? 0;
				_logger.LogInformation(
					"Farm queue for {AccountName} fully drained: {Completed} app(s) completed, {Skipped} budget-skipped, {Collected} card(s) collected",
					spec.AccountName, completed, skipped, runtime.FarmCardsCollected);
				await RecordActionAsync(spec, null, "farm_completed",
					reason: $"queue drained: {completed} completed, {skipped} budget-skipped, {runtime.FarmCardsCollected} cards collected this session",
					cancellationToken: cancellationToken).ConfigureAwait(false);
				_events.Publish(null, "account.farm_progress", new Dictionary<string, object?>
				{
					["account"] = spec.AccountName,
					["kind"] = "queue_empty",
					["completed"] = completed,
					["skipped"] = skipped,
					["cardsCollected"] = runtime.FarmCardsCollected,
				});
			}

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
	/// <see cref="TradePolicy.AutoAcceptGifts"/> enabled (round 32; git history). Mirrors the
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

}
