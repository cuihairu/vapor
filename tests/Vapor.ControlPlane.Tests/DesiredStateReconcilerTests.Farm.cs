using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Smart-farming loop tests: card drops, queue extraction, budget and stats counters.</summary>
public sealed partial class DesiredStateReconcilerTests
{
	// ── smart farming (DesiredState.Farm) ──
	/// <summary>
	/// Farming tests drive several reconcile passes per play job and need the
	/// play window settled immediately; Dispose restores the real window.
	/// </summary>
	private static void ZeroPlayInFlightWindow() =>
		DesiredStateReconciler.PlayInFlightWindow = TimeSpan.Zero;

	/// <summary>Builds a get_card_drops task output payload (in-memory dictionary form).</summary>
	private static IReadOnlyDictionary<string, object?> DropsOutput(params (uint AppId, int Drops)[] drops) =>
		DropsOutput(drops.Sum(d => (int)d.Drops), drops);

	private static IReadOnlyDictionary<string, object?> DropsOutput(int totalRemaining, params (uint AppId, int Drops)[] drops) =>
		new Dictionary<string, object?>
		{
			["drops"] = drops.Select(d => (object)new Dictionary<string, object?>
			{
				["app_id"] = (long)d.AppId,
				["drops_remaining"] = (long)d.Drops
			}).ToList(),
			// Mirrors the real action output (get_card_drops reports the total);
			// older fixtures that don't exercise the counters get the sum.
			["total_drops_remaining"] = (long)totalRemaining
		};

	/// <summary>
	/// Same report shape after a store round-trip: values deserialize back as
	/// JsonElement nodes, which the total parser must accept too.
	/// </summary>
	private static IReadOnlyDictionary<string, object?> DropsOutputJson(int totalRemaining, params (uint AppId, int Drops)[] drops)
	{
		string json = System.Text.Json.JsonSerializer.Serialize(new
		{
			drops = drops.Select(d => new { app_id = d.AppId, drops_remaining = d.Drops }),
			total_drops_remaining = totalRemaining
		});
		return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
	}

	private static IReadOnlyDictionary<string, object?> DropsOutputWithoutTotal(params (uint AppId, int Drops)[] drops) =>
		new Dictionary<string, object?>
		{
			["drops"] = drops.Select(d => (object)new Dictionary<string, object?>
			{
				["app_id"] = (long)d.AppId,
				["drops_remaining"] = (long)d.Drops
			}).ToList()
		};

	[Fact]
	public async Task FarmAccount_DispatchesCardDropsThenIdlesFirstApp()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Pass 1: no queue yet → refresh via a get_card_drops job.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);
		Assert.Equal("get_card_drops", jobs.Created[0].Action);
		Assert.Equal("desired-state", jobs.Created[0].Meta!["orchestrator"]);
		Assert.Equal(1, reconciler.CardDropsDispatched);

		// Pass 2: query finished with two apps having drops → idle the first.
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6), (620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("play_games", jobs.Created[1].Action);
		Assert.Equal("220", jobs.Created[1].Payload!["games"]);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.True(view.Idling);
		Assert.Equal(220U, view.FarmingAppId);
		Assert.Equal([220U, 620U], view.FarmQueue);
		Assert.NotNull(view.FarmQueueCheckedAt);
	}

	[Fact]
	public async Task FarmQueue_IdleAppsActAsExclusionList()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, new[] { "220" }, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6), (620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal("620", jobs.Created[1].Payload!["games"]);
		Assert.Equal([620U], reconciler.GetOrchestrationView("alice")!.FarmQueue);
	}

	[Fact]
	public async Task FarmRotation_OnRefreshMovesToNextApp()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Queue [220, 620] and start farming 220.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6), (620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null); // play job completes silently

		// While 220 still has drops and the refresh interval has not elapsed, nothing happens.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal(2, jobs.Created.Count);

		// A spec update forces a queue refresh; the new report no longer lists 220.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal(3, jobs.Created.Count);
		Assert.Equal("get_card_drops", jobs.Created[2].Action);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutput((620, 1));

		// Settling the refresh rotates to the next game with drops.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal(4, jobs.Created.Count);
		Assert.Equal("play_games", jobs.Created[3].Action);
		Assert.Equal("620", jobs.Created[3].Payload!["games"]);
		Assert.Equal(620U, reconciler.GetOrchestrationView("alice")!.FarmingAppId);
	}

	[Fact]
	public async Task FarmComplete_WithEmptyQueue_StopsIdling()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6));
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.True(reconciler.GetOrchestrationView("alice")!.Idling);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);

		// Refresh reports no drops anywhere → stop idling, keep the session online.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutput();
		await reconciler.ReconcileOnce(CancellationToken.None);

		// job 1 = card drops, job 2 = play 220, job 3 = card drops refresh, job 4 = stop.
		Assert.Equal(4, jobs.Created.Count);
		Assert.Equal("play_games", jobs.Created[3].Action);
		Assert.Equal("stop", jobs.Created[3].Payload!["action"]);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.False(view.Idling);
		Assert.Null(view.FarmingAppId);
	}

	// ── farm policy: ordering, completion marks, budget skip, efficiency stats ──
	[Theory]
	[InlineData(FarmPriorityOrder.CardsDescending, new uint[] { 730, 220, 620 })]
	[InlineData(FarmPriorityOrder.CardsAscending, new uint[] { 620, 730, 220 })]
	[InlineData(FarmPriorityOrder.AppIdAscending, new uint[] { 220, 620, 730 })]
	public void ExtractFarmQueue_PriorityOrder_SortsQueue(FarmPriorityOrder order, uint[] expected)
	{
		var spec = new AccountSpec("alice", true, AccountDesiredState.Farm,
			FarmPolicy: new FarmPolicy(PriorityOrder: order));

		// Report order (drops-remaining descending as produced by the action):
		// 730 has 3 drops, 220 has 6, 620 has 1.
		List<uint> queue = DesiredStateReconciler.ExtractFarmQueue(
			DropsOutput((730, 3), (220, 6), (620, 1)), spec);

		Assert.Equal(expected, queue);
	}

	[Fact]
	public void ExtractFarmQueue_PriorityApps_HeadTheQueueInDeclarationOrder()
	{
		var spec = new AccountSpec("alice", true, AccountDesiredState.Farm,
			FarmPolicy: new FarmPolicy(PriorityApps: [620, 730]));

		// The prioritized apps head the queue in declaration order (not report
		// or app-id order); the rest keep the policy's default report order.
		List<uint> queue = DesiredStateReconciler.ExtractFarmQueue(
			DropsOutput((730, 3), (220, 6), (620, 1), (999, 2)), spec);

		Assert.Equal([620U, 730U, 220U, 999U], queue);
	}

	[Fact]
	public void ExtractFarmQueue_PriorityAppsMissingFromReport_AreIgnored()
	{
		var spec = new AccountSpec("alice", true, AccountDesiredState.Farm,
			FarmPolicy: new FarmPolicy(PriorityApps: [888]));

		List<uint> queue = DesiredStateReconciler.ExtractFarmQueue(
			DropsOutput((220, 6), (620, 1)), spec);

		Assert.Equal([220U, 620U], queue);
	}

	[Fact]
	public async Task Farm_CompletesAppByQueueDiff_MarksAuditEventAndNeverRequeues()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions,
			auditStore: audit, broker: broker);

		// Round 1: query, then idle 220 (dispatch → settle+act → settle the play).
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6), (620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Round 2: a spec update forces a queue refresh; the new report no
		// longer lists 220 → it completed farming (audit + farm_progress event).
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None); // card_drops refresh (job 3)
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutput((620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None); // settle → completion marks + play 620 (job 4)
		jobs.Outcomes["task-4-0"] = (JobTaskStatus.Finished, null);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal([220U], view.FarmCompletedApps);
		Assert.Contains(audit.Entries, e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "farm_app_completed"));
		Assert.Contains(broker.Published, p => p.Type == "account.farm_progress"
			&& p.Payload is not null
			&& string.Equals(p.Payload["kind"], "app_completed")
			&& Convert.ToUInt64(p.Payload["appId"]!) == 220UL);

		// Round 3: another forced refresh where the report still lists 220 (a
		// lagging page) — a completed app must never re-enter the queue.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None); // card_drops refresh (job 5)
		jobs.Outcomes["task-5-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-5-0"] = DropsOutput((620, 1), (220, 4));
		await reconciler.ReconcileOnce(CancellationToken.None);

		// 220 was dispatched exactly once (round 1); the lagging report must
		// never send it back to the idler.
		Assert.Equal(1, jobs.Created.Count(j => j.Action == "play_games"
			&& string.Equals(j.Payload!["games"], "220")));
		Assert.Equal([620U], reconciler.GetOrchestrationView("alice")!.FarmQueue);
	}

	[Fact]
	public async Task Farm_BudgetExhausted_SkipsAppRotatesAndNeverRequeues()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		// double.Epsilon passes the finite-positive validation but truncates to a
		// zero TimeSpan, so the budget check trips deterministically on the next
		// act pass after the dispatch starts the clock — no clock waiting.
		FarmPolicy Policy() => new(PerGameHourBudget: double.Epsilon);
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null, farmPolicy: Policy());
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions,
			auditStore: audit, broker: broker);

		// Job 1 = card_drops; job 2 = play 220 (its budget clock starts here).
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6), (620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);

		// Settling the play job reaches the act stage where the budget trips:
		// skip 220 and rotate to 620 (job 3) in the same pass.
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal("620", jobs.Created[2].Payload!["games"]);
		Assert.Equal([220U], reconciler.GetOrchestrationView("alice")!.FarmSkippedApps);
		Assert.Contains(audit.Entries, e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "farm_app_budget_exhausted"));
		Assert.Contains(broker.Published, p => p.Type == "account.farm_progress"
			&& p.Payload is not null
			&& string.Equals(p.Payload["kind"], "app_budget_exhausted"));
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);

		// A forced refresh (job 4) whose lagging report still lists 220 must
		// not re-queue it. The bump is re-declared with the policy: PUT
		// semantics replace it.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null, farmPolicy: Policy());
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-4-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-4-0"] = DropsOutput((620, 1), (220, 4));
		await reconciler.ReconcileOnce(CancellationToken.None);

		// 220 was dispatched exactly once (before the budget tripped); the
		// lagging report must never send it back to the idler.
		Assert.Equal(1, jobs.Created.Count(j => j.Action == "play_games"
			&& string.Equals(j.Payload!["games"], "220")));
	}

	[Fact]
	public async Task Farm_QueueDrained_EmitsFarmCompletedOnceThenRestartsOnFreshDrops()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions,
			auditStore: audit, broker: broker);

		// Job 1 = card_drops; job 2 = play 220.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Job 3 = card_drops; report empty → stop idling (job 4), one
		// farm_completed audit + one queue_empty event.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutput();
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal("stop", jobs.Created[3].Payload!["action"]);
		Assert.Equal(1, audit.Entries.Count(e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "farm_completed")));
		Assert.Equal(1, broker.Published.Count(p => p.Type == "account.farm_progress"
			&& p.Payload is not null && string.Equals(p.Payload["kind"], "queue_empty")));
		jobs.Outcomes["task-4-0"] = (JobTaskStatus.Finished, null);

		// Job 5 = card_drops; while the report stays empty the notice never repeats.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-5-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-5-0"] = DropsOutput();
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(1, audit.Entries.Count(e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "farm_completed")));

		// Job 6 = card_drops; Steam grants fresh drops → the bookkeeping resets
		// and farming restarts (job 7 = play 220 again).
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-6-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-6-0"] = DropsOutput((220, 2));
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView restarted = reconciler.GetOrchestrationView("alice")!;
		Assert.True(restarted.Idling);
		Assert.Equal(220U, restarted.FarmingAppId);
		Assert.Empty(restarted.FarmCompletedApps ?? []);
		Assert.Null(restarted.FarmSkippedApps);
		// Stop jobs share the play_games action but carry no "games" payload.
		Assert.Equal(2, jobs.Created.Count(j => j.Action == "play_games"
			&& j.Payload is { } p && p.TryGetValue("games", out var games)
			&& string.Equals(games as string, "220", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task Farm_StatsCounters_AccumulateOnlyOnDecrease()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Report 1 (settled on pass 2): total 10 → baseline, collected 0.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput(totalRemaining: 10, (220, 10));
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView baseline = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(0, baseline.FarmCardsCollected);
		Assert.Equal(10, baseline.FarmCardsRemaining);
		Assert.Null(baseline.FarmCardsPerHour);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None); // settle the play job

		// Report 2 (forced refresh): total dropped to 7 → collected 3.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutput(totalRemaining: 7, (220, 7));
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView progressed = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(3, progressed.FarmCardsCollected);
		Assert.Equal(7, progressed.FarmCardsRemaining);
		Assert.NotNull(progressed.FarmCardsPerHour);

		// Report 3: a newly queued game raises the total to 12 — never negative
		// progress; the collected counter just holds.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-4-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-4-0"] = DropsOutput(totalRemaining: 12, (220, 7), (620, 5));
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView raised = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(3, raised.FarmCardsCollected);
		Assert.Equal(12, raised.FarmCardsRemaining);

		// Report 4: total back down to 9 → collected 6.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-5-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-5-0"] = DropsOutput(totalRemaining: 9, (220, 4), (620, 5));
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView final = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(6, final.FarmCardsCollected);
		Assert.Equal(9, final.FarmCardsRemaining);
	}

	[Fact]
	public async Task Farm_BudgetBelowLimit_KeepsIdlingCurrentApp()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		// A realistic budget (1h) with a clock that just started: the act stage
		// must keep idling the current app — no skip marks, no rotation.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null,
			farmPolicy: new FarmPolicy(PerGameHourBudget: 1));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created, j => j.Action == "play_games");
		Assert.Equal("220", jobs.Created[1].Payload!["games"]);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.FarmSkippedApps);
		Assert.DoesNotContain(audit.Entries, e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "farm_app_budget_exhausted"));
	}

	[Fact]
	public async Task Farm_StatsCounters_ParseTotalFromJsonRoundTrip()
	{
		// After a store round-trip the report values come back as JsonElement
		// nodes — the counters must accumulate across both report shapes.
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Report 1 (in-memory shape): total 10.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput(totalRemaining: 10, (220, 10));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal(10, reconciler.GetOrchestrationView("alice")!.FarmCardsRemaining);

		// Report 2 (round-trip shape): total 7 → collected 3.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutputJson(totalRemaining: 7, (220, 7));
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(3, view.FarmCardsCollected);
		Assert.Equal(7, view.FarmCardsRemaining);
	}

	[Fact]
	public async Task Farm_ReportWithoutUsableTotal_LeavesCountersUntouched()
	{
		// A report without a usable total (missing key or out-of-range value)
		// still drives the queue — it just contributes nothing to the counters.
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Report 1: no total key at all — queue builds, counters stay at zero.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutputWithoutTotal((220, 6));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal([220U], view.FarmQueue);
		Assert.Equal(0, view.FarmCardsCollected);
		Assert.Null(view.FarmCardsRemaining);

		// Report 2: a total beyond int range is unusable, not a crash — and
		// still not negative progress or a reset.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = new Dictionary<string, object?>
		{
			["drops"] = Array.Empty<object>(),
			["total_drops_remaining"] = (long)int.MaxValue + 5
		};
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView final = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(0, final.FarmCardsCollected);
		Assert.Null(final.FarmCardsRemaining);
	}

	[Fact]
	public async Task SpecUpdate_KeepsFarmFactsAndRestartsBudgetClock()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Job 1 = card_drops; job 2 = play 220.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput(totalRemaining: 10, (220, 10));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Job 3 = card_drops; settling it records collected = 3.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = DropsOutput(totalRemaining: 7, (220, 7));
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal(3, reconciler.GetOrchestrationView("alice")!.FarmCardsCollected);

		// A spec update is a policy edit, not a reset: the facts (marks and
		// counters) survive; only the per-game budget clock restarts.
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(3, view.FarmCardsCollected);
		Assert.Equal(7, view.FarmCardsRemaining);
		Assert.NotNull(view.FarmStatsStartedAt);
		Assert.Null(view.FarmAppStartedAt);
	}

	[Fact]
	public async Task FarmRefresh_RespectsInterval()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6));
		await reconciler.ReconcileOnce(CancellationToken.None);
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Only the initial card-drops query ran; the interval gates further queries.
		Assert.Single(jobs.Created, j => j.Action == "get_card_drops");
	}

	[Fact]
	public void ExtractFarmQueue_SurvivesJsonRoundTripAndFilters()
	{
		var spec = new AccountSpec("alice", true, AccountDesiredState.Farm, new[] { "570" });

		// Simulate the SQLite JSON round-trip: numbers become JsonElement objects.
		var raw = DropsOutput((220, 6), (570, 3), (620, 0));
		string json = System.Text.Json.JsonSerializer.Serialize(raw);
		var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);

		List<uint> queue = DesiredStateReconciler.ExtractFarmQueue(roundTripped, spec);

		// 570 excluded (IdleApps), 620 filtered (zero drops), order preserved.
		Assert.Equal([220U], queue);
		Assert.Empty(DesiredStateReconciler.ExtractFarmQueue(null, spec));
		Assert.Empty(DesiredStateReconciler.ExtractFarmQueue(new Dictionary<string, object?>(), spec));
	}

	[Fact]
	public async Task FarmToOnlineSwitch_StopsIdlingAndClearsQueue()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6));
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.True(reconciler.GetOrchestrationView("alice")!.Idling);

		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		// Settle the in-flight play job so Idling survives to the Online pass.
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal("stop", jobs.Created[2].Payload!["action"]);
		Assert.False(view.Idling);
		Assert.Null(view.FarmingAppId);
		Assert.Null(view.FarmQueue);
	}

	[Fact]
	public async Task Farm_NoCapableAgent_MarksDeviationWithoutDispatch()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", new Dictionary<string, bool> { ["login"] = true })); // no get_card_drops
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.NoAgentSkips);
		Assert.Equal("no capable agent available", reconciler.GetOrchestrationView("alice")!.LastDeviation);
	}

	[Fact]
	public async Task Farm_DryRun_GuardsDispatches()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		var audit = new FakeAuditStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, audit, dryRun: true);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.DryRunDeviations);
		Assert.Contains("get_card_drops", reconciler.GetOrchestrationView("alice")!.LastDeviation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Farm_ActiveCardDropsJob_WaitsForIt()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		DesiredStateReconciler.CardDropsInFlightWindow = TimeSpan.FromHours(1); // keep the query in flight
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
	}

	[Fact]
	public async Task Farm_CardDropsTaskFailed_MarksDeviationOnly()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Failed, "badges page unreachable");
		await reconciler.ReconcileOnce(CancellationToken.None);

		// The failed query is settled into a deviation and the stamped
		// FarmQueueCheckedAt gates the retry until the refresh interval —
		// a flapping badges page is not re-polled every pass. The login
		// budget is never consumed either way.
		Assert.Single(jobs.Created);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.NotNull(view.LastDeviation);
		Assert.Equal(0, view.LoginAttempts);
		Assert.Null(view.NextAttemptAt);
	}

	[Fact]
	public void ExtractFarmQueue_IgnoresNonDictionaryEntries()
	{
		var spec = NewSpec();
		var output = new Dictionary<string, object?>
		{
			["drops"] = new List<object?> { "garbage", 42, null }
		};

		Assert.Empty(DesiredStateReconciler.ExtractFarmQueue(output, spec));
	}

	[Fact]
	public void ExtractFarmQueue_EntryMissingFields_IsSkipped()
	{
		var spec = NewSpec();
		var output = new Dictionary<string, object?>
		{
			["drops"] = new List<object>
			{
				new Dictionary<string, object?> { ["app_id"] = 220L },               // drops_remaining missing
				new Dictionary<string, object?> { ["drops_remaining"] = 3L },       // app_id missing
				new Dictionary<string, object?> { ["app_id"] = null, ["drops_remaining"] = 3L }
			}
		};

		Assert.Empty(DesiredStateReconciler.ExtractFarmQueue(output, spec));
	}

	[Fact]
	public void ExtractFarmQueue_NumberValueShapes_AreAllAccepted()
	{
		var spec = NewSpec();
		var output = new Dictionary<string, object?>
		{
			["drops"] = new List<object>
			{
				new Dictionary<string, object?> { ["app_id"] = 220, ["drops_remaining"] = 5 },                            // int
				new Dictionary<string, object?> { ["app_id"] = 221.0, ["drops_remaining"] = 4.0 },                        // double
				new Dictionary<string, object?> { ["app_id"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("222"), ["drops_remaining"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("3") }, // JsonElement
				new Dictionary<string, object?> { ["app_id"] = "223", ["drops_remaining"] = "2" },                        // string
				new Dictionary<string, object?> { ["app_id"] = 224L, ["drops_remaining"] = 1L },                          // long
				new Dictionary<string, object?> { ["app_id"] = true, ["drops_remaining"] = 9 },                           // unsupported shape → skipped
				new Dictionary<string, object?> { ["app_id"] = 0, ["drops_remaining"] = 9 },                              // app 0 → filtered
				new Dictionary<string, object?> { ["app_id"] = 225, ["drops_remaining"] = 0 }                             // no drops → filtered
			}
		};

		Assert.Equal([220U, 221U, 222U, 223U, 224U], DesiredStateReconciler.ExtractFarmQueue(output, spec));
	}

	private static (bool Ok, uint AppId, int Remaining) ReadDropEntry(object? entry)
	{
		MethodInfo? info = typeof(DesiredStateReconciler).GetMethod("TryReadDropEntry", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(info);
		object?[] args = [entry, 0u, 0];
		bool ok = (bool)info.Invoke(null, args)!;
		return (ok, (uint)args[1]!, (int)args[2]!);
	}

	private static (bool Ok, int Total) ReadTotalDropsRemaining(IReadOnlyDictionary<string, object?> output)
	{
		MethodInfo? info = typeof(DesiredStateReconciler).GetMethod("TryReadTotalDropsRemaining", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(info);
		object?[] args = [output, 0];
		bool ok = (bool)info.Invoke(null, args)!;
		return (ok, (int)args[1]!);
	}

	[Fact]
	public void TryReadDropEntry_JsonElementArms_RejectBrokenFields()
	{
		Assert.False(ReadDropEntry(Json("""{"app_id":241}""")).Ok);                         // missing drops_remaining
		Assert.False(ReadDropEntry(Json("""{"drops_remaining":2}""")).Ok);                  // missing app_id
		Assert.False(ReadDropEntry(Json("""{"app_id":"242","drops_remaining":2}""")).Ok);   // non-number app_id
		Assert.False(ReadDropEntry(Json("""{"app_id":243,"drops_remaining":"2"}""")).Ok);   // non-number drops
		Assert.False(ReadDropEntry(Json("""{"app_id":240,"drops_remaining":-1}""")).Ok);    // drops <= 0
		Assert.False(ReadDropEntry(Json("""{"app_id":0,"drops_remaining":3}""")).Ok);       // app_id <= 0
		(bool Ok, uint AppId, int Remaining) ok = ReadDropEntry(Json("""{"app_id":244,"drops_remaining":3}"""));
		Assert.True(ok.Ok);
		Assert.Equal(244u, ok.AppId);
		Assert.Equal(3, ok.Remaining);
	}

	[Fact]
	public void TryReadTotalDropsRemaining_Arms_RejectBrokenValues()
	{
		// A fractional JSON number cannot be an int → TryGetInt32 false; a
		// negative total is rejected by the >= 0 guard.
		Assert.False(ReadTotalDropsRemaining(new Dictionary<string, object?> { ["total_drops_remaining"] = Json("1.5") }).Ok);
		Assert.False(ReadTotalDropsRemaining(new Dictionary<string, object?> { ["total_drops_remaining"] = Json("-2") }).Ok);
		// A plain JsonElement number parses.
		(bool Ok, int Total) ok = ReadTotalDropsRemaining(new Dictionary<string, object?> { ["total_drops_remaining"] = Json("7") });
		Assert.True(ok.Ok);
		Assert.Equal(7, ok.Total);
		// A string survives the dictionary fallback path.
		(bool Ok, int Total) fromString = ReadTotalDropsRemaining(new Dictionary<string, object?> { ["total_drops_remaining"] = "9" });
		Assert.True(fromString.Ok);
		Assert.Equal(9, fromString.Total);
	}

	private static AccountSpec NewSpec() =>
		new AccountStore().Upsert("alice", true, AccountDesiredState.Farm, null, null, null, note: null);

	[Fact]
	public async Task FarmQueueRefresh_DispatchReasonCyclesInitialToRefresh()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, farmRefreshSeconds: 0);

		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch 1: initial farm queue
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutputWithoutTotal();
		await reconciler.ReconcileOnce(CancellationToken.None); // settle: empty queue, nothing to do
		await reconciler.ReconcileOnce(CancellationToken.None); // zero refresh window → immediate refresh

		Assert.Equal("get_card_drops", jobs.Created[1].Action);
		string[] reasons = audit.Entries
			.Where(e => e.Details is not null
				&& e.Details.TryGetValue("orchestrationAction", out object? act)
				&& string.Equals((string?)act, "card_drops_dispatched", StringComparison.Ordinal))
			.Select(e => (string)e.Details!["reason"]!)
			.ToArray();
		Assert.Equal(new[] { "initial farm queue", "farm queue refresh" }, reasons);
	}

	[Fact]
	public void CardsPerHour_StatsClockInFuture_ReportsNoRate()
	{
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
		System.Reflection.MethodInfo cardsPerHour = typeof(DesiredStateReconciler).GetMethod(
			"CardsPerHour", BindingFlags.NonPublic | BindingFlags.Static)!;

		runtimeType.GetField("FarmStatsStartedAt")!.SetValue(runtime, DateTimeOffset.UtcNow.AddMinutes(-30));
		runtimeType.GetField("FarmCardsCollected")!.SetValue(runtime, 60);
		double? positive = (double?)cardsPerHour.Invoke(null, [runtime]);
		Assert.NotNull(positive);
		Assert.Equal(120.0, positive);

		// A stats clock stamped in the future (clock skew) yields a negative
		// window: the guard must report no rate instead of a bogus one.
		runtimeType.GetField("FarmStatsStartedAt")!.SetValue(runtime, DateTimeOffset.UtcNow.AddHours(2));
		Assert.Null((double?)cardsPerHour.Invoke(null, [runtime]));
	}

	[Fact]
	public async Task FarmBudgetSkip_DrainsQueue_WithZeroCompletedApps()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null,
			farmPolicy: new FarmPolicy(PerGameHourBudget: double.Epsilon));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, broker: broker);

		// No game ever completes (both reports carry drops) and the zero-hour
		// budget fuses each one out on its settle pass: the drain then fires
		// with zero completed and both apps budget-skipped.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = DropsOutput((220, 6), (620, 1));
		await reconciler.ReconcileOnce(CancellationToken.None); // settle drops → play 220

		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?>();
		await reconciler.ReconcileOnce(CancellationToken.None); // settle play 220 → budget-skip → play 620

		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = new Dictionary<string, object?>();
		await reconciler.ReconcileOnce(CancellationToken.None); // settle play 620 → budget-skip → drain

		Assert.Contains(audit.Entries, e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "farm_completed")
			&& ((string)e.Details["reason"]!).StartsWith("queue drained: 0 completed, 2 budget-skipped", StringComparison.Ordinal));
		IReadOnlyDictionary<string, object?> drained = broker.Published
			.Where(p => string.Equals(p.Type, "account.farm_progress", StringComparison.Ordinal))
			.Select(p => p.Payload)
			.Single(payload => payload is not null && string.Equals((string?)payload["kind"], "queue_empty", StringComparison.Ordinal))!;
		Assert.Equal(0, drained["completed"]);
		Assert.Equal(2, drained["skipped"]);
		Assert.Equal("play_games", jobs.Created[^1].Action); // the closing stop job
	}
}
