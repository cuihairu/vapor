using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class DesiredStateReconcilerTests : IDisposable
{
	public DesiredStateReconcilerTests()
	{
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.CardDropsInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.PlaytimeInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.TradeInFlightWindow = TimeSpan.Zero;
	}

	public void Dispose()
	{
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.PlayInFlightWindow = TimeSpan.FromSeconds(120);
		DesiredStateReconciler.CardDropsInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.PlaytimeInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.TradeInFlightWindow = TimeSpan.FromSeconds(60);
	}

	[Fact]
	public async Task OnlineAccount_WithoutSession_DispatchesLoginJob()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, "us-east", null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Equal("login", jobs.Created[0].Action);
		Assert.Equal(new[] { "alice" }, jobs.Created[0].Targets);
		Assert.Equal("us-east", jobs.Created[0].Region);
		Assert.Equal("desired-state", jobs.Created[0].Meta!["orchestrator"]);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("alice")!.AssignedAgent);
		Assert.Equal(1, reconciler.LoginsDispatched);
	}

	[Fact]
	public async Task RegionConstraint_SkipsAgentsInOtherRegions()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, "eu-west", null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.NoAgentSkips);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.AssignedAgent);
	}

	[Fact]
	public async Task AgentWithoutLoginCapability_IsSkipped()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", new Dictionary<string, bool> { ["login"] = false, ["ping"] = true }));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.NoAgentSkips);
	}

	[Fact]
	public async Task PinnedAgentOffline_FallsBackToCapablePool()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, "agent-pinned"));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("alice")!.AssignedAgent);
	}

	[Fact]
	public async Task ConnectedSession_WithIdleApps_DispatchesPlayJob()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Idle, new[] { "730", "570" }, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Equal("play_games", jobs.Created[0].Action);
		Assert.Equal("730,570", jobs.Created[0].Payload!["games"]);
		Assert.True(reconciler.GetOrchestrationView("alice")!.Idling);
	}

	[Fact]
	public async Task ConnectedSession_WithoutIdleApps_DoesNotDispatchAnything()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Idle, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
	}

	[Fact]
	public async Task SwitchingFromIdleToOnline_DispatchesStopJob()
	{
		AccountStore accounts = new();
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		accounts.Upsert("alice", true, AccountDesiredState.Idle, new[] { "730" }, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.True(reconciler.GetOrchestrationView("alice")!.Idling);

		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("stop", jobs.Created[1].Payload!["action"]);
		Assert.False(reconciler.GetOrchestrationView("alice")!.Idling);
	}

	[Fact]
	public async Task LoginFailedSnapshot_CountsFailureAndAppliesCooldown()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "LoginFailed", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, cooldownSeconds: 3600);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(1, view.LoginAttempts);
		Assert.NotNull(view.NextAttemptAt);
		Assert.Contains("login failed", view.LastDeviation);
	}

	[Fact]
	public async Task ThrottledAccount_SkipsDispatchUntilSpecUpdated()
	{
		AccountStore accounts = new();
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "LoginFailed", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, cooldownSeconds: 0, maxLoginAttempts: 1);

		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);

		// Failure observed and the attempt budget (1) is exhausted immediately → throttled, no dispatch.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Empty(jobs.Created);
		Assert.True(reconciler.ThrottledSkips >= 1);

		// Updating the spec bumps the version and resets the failure budget.
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
	}

	[Fact]
	public async Task AgentLoss_TriggersRebalanceToSurvivingAgent()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-a", "us-east", null), ("agent-b", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, cooldownSeconds: 0);

		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("agent-a", reconciler.GetOrchestrationView("alice")!.AssignedAgent);

		agents.Unregister("agent-a");
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(1, reconciler.Rebalances);
		Assert.Contains("job-1", jobs.Cancelled);
		Assert.Equal("agent-b", reconciler.GetOrchestrationView("alice")!.AssignedAgent);
		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("login", jobs.Created[1].Action);
	}

	[Fact]
	public async Task DisabledAccount_IsUnassignedAndInFlightJobCancelled()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);

		accounts.SetEnabled("alice", enabled: false);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Contains("job-1", jobs.Cancelled);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.AssignedAgent);
		Assert.Equal(1, reconciler.Unassignments);
	}

	[Fact]
	public async Task DesiredOffline_UnassignsAccount()
	{
		AccountStore accounts = new();
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);

		accounts.Upsert("alice", true, AccountDesiredState.Offline, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Contains("job-1", jobs.Cancelled);
		Assert.Equal(1, reconciler.Unassignments);
	}

	[Fact]
	public async Task BrokenUnassignPass_IsLoggedAndLoopKeepsServing()
	{
		// Drives the background loop (not ReconcileOnce directly): the cancel store
		// blowing up during the unassign path escapes the per-account catch into the
		// ExecuteAsync catch — the pass is logged as broken and the loop keeps ticking.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, intervalSeconds: 1);

		await reconciler.StartAsync(CancellationToken.None);

		// Tick 1: the login job is dispatched and the active job id pinned.
		var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (jobs.Created.Count == 0 && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		Assert.Single(jobs.Created);

		// Tick 2: disabling the account unassigns it, but the store's cancel throws —
		// only the NotFoundException arm swallows, so the IOException escapes to the
		// background loop's catch (the line under coverage).
		accounts.SetEnabled("alice", enabled: false);
		jobs.ThrowOnCancel = true;

		// Tick 3 proves the loop survived: the active job id was cleared ahead of the
		// failed cancel, so the retry unassigns cleanly.
		deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (reconciler.Unassignments == 0 && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		await reconciler.StopAsync(CancellationToken.None);

		Assert.Equal(1, reconciler.Unassignments);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.AssignedAgent);
	}

	[Fact]
	public async Task DryRun_ReportsDeviationsWithoutDispatching()
	{
		var audit = new FakeAuditStore();
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit, dryRun: true);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.DryRunDeviations);
		Assert.Contains(audit.Entries, e => e.Action == "account.reconciled.dry_run");
	}

	[Fact]
	public async Task SettleActiveJob_CountsFailedLoginJobOutcome()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, cooldownSeconds: 3600);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Failed, "bad password");

		// Second pass settles the in-flight job (window shrunk to zero in the fixture).
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal(1, view.LoginAttempts);
		Assert.NotNull(view.NextAttemptAt);
		Assert.Null(view.ActiveJobId);

		// Cooldown holds: no new dispatch on the following pass.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);
	}

	[Fact]
	public async Task SettleActiveJob_CancelledPlayJobClearsIdling()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Idle, new[] { "730" }, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		DesiredStateReconciler.PlayInFlightWindow = TimeSpan.Zero; // settle from the second pass on (restored in Dispose)
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Canceled, null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, cooldownSeconds: 3600);

		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.True(reconciler.GetOrchestrationView("alice")!.Idling);

		// Second pass settles the cancelled play job; the cooldown blocks an immediate re-dispatch.
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Equal("play_games", jobs.Created[0].Action);
		Assert.False(reconciler.GetOrchestrationView("alice")!.Idling);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.ActiveJobId);
	}

	[Fact]
	public async Task TransitionalState_DoesNotDispatch()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "2fa_required", "ConnectingWait2FA", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
	}

	// ── smart farming (DesiredState.Farm) ──

	/// <summary>
	/// Farming tests drive several reconcile passes per play job and need the
	/// play window settled immediately; Dispose restores the real window.
	/// </summary>
	private static void ZeroPlayInFlightWindow() =>
		DesiredStateReconciler.PlayInFlightWindow = TimeSpan.Zero;

	/// <summary>Builds a get_card_drops task output payload (in-memory dictionary form).</summary>
	private static IReadOnlyDictionary<string, object?> DropsOutput(params (uint AppId, int Drops)[] drops) =>
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
		Assert.Single(jobs.Created.Where(j => j.Action == "get_card_drops"));
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

	// ── playtime boosting (DesiredState.Boost) ──

	/// <summary>Builds a get_playtime task output payload (in-memory dictionary form).</summary>
	private static IReadOnlyDictionary<string, object?> PlaytimeOutput(params (uint AppId, double Hours)[] games) =>
		new Dictionary<string, object?>
		{
			["playtimes"] = games.Select(g => (object)new Dictionary<string, object?>
			{
				["app_id"] = (long)g.AppId,
				["hours"] = g.Hours
			}).ToList()
		};

	private static AccountStore NewBoostStore(string name, (uint AppId, double TargetHours)[] targets, string[]? excluded = null)
	{
		var store = new AccountStore();
		store.Upsert(name, true, AccountDesiredState.Boost, excluded, null, null, null,
			boostTargets: targets.Select(t => new BoostTarget(t.AppId, t.TargetHours)).ToList());
		return store;
	}

	[Fact]
	public async Task BoostAccount_DispatchesPlaytimeThenIdlesUnmetApps()
	{
		AccountStore accounts = NewBoostStore("alice", [(220u, 100), (620u, 10)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Pass 1: no report yet → refresh via a get_playtime job.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);
		Assert.Equal("get_playtime", jobs.Created[0].Action);
		Assert.Equal(1, reconciler.PlaytimesDispatched);

		// Pass 2: report shows 220 below target, 620 at target → idle 220 only.
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 36.5), (620, 12.0), (440, 999));
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("play_games", jobs.Created[1].Action);
		Assert.Equal("220", jobs.Created[1].Payload!["games"]);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.True(view.Idling);
		Assert.Equal([220U], view.BoostUnmetApps);
		Assert.NotNull(view.BoostCheckedAt);
	}

	[Fact]
	public async Task BoostSchedule_MultipleUnmetApps_IdleTogether()
	{
		AccountStore accounts = NewBoostStore("alice", [(220u, 100), (620u, 100), (730u, 5)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 10), (620, 20), (730, 9));
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Both unmet apps idle in one play job; 730 is above target.
		Assert.Equal("220,620", jobs.Created[1].Payload!["games"]);
		Assert.Equal([220U, 620U], reconciler.GetOrchestrationView("alice")!.BoostUnmetApps);
	}

	[Fact]
	public async Task BoostSchedule_IdleAppsActAsExclusionList()
	{
		AccountStore accounts = NewBoostStore("alice", [(220u, 100), (620u, 100)], excluded: ["220"]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 1), (620, 2));
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal("620", jobs.Created[1].Payload!["games"]);
		Assert.Equal([620U], reconciler.GetOrchestrationView("alice")!.BoostUnmetApps);
	}

	[Fact]
	public async Task BoostComplete_WhenAllTargetsMet_StopsIdling()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 36.5));
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.True(reconciler.GetOrchestrationView("alice")!.Idling);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);

		// Refresh reports 220 has reached its target → stop idling, keep online.
		accounts.Upsert("alice", true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220u, 100)]);
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = PlaytimeOutput((220, 150.25));
		await reconciler.ReconcileOnce(CancellationToken.None);

		// job 1 = playtime query, job 2 = play 220, job 3 = playtime refresh, job 4 = stop.
		Assert.Equal(4, jobs.Created.Count);
		Assert.Equal("play_games", jobs.Created[3].Action);
		Assert.Equal("stop", jobs.Created[3].Payload!["action"]);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.False(view.Idling);
		Assert.Empty(view.BoostUnmetApps!);
	}

	[Fact]
	public async Task BoostRefresh_RespectsInterval()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 36.5));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Only the initial playtime query ran; the interval gates further queries.
		Assert.Single(jobs.Created.Where(j => j.Action == "get_playtime"));
	}

	[Fact]
	public async Task BoostQueryFailure_MarksDeviationWithoutTouchingLoginBudget()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Failed, "games tab unavailable");
		await reconciler.ReconcileOnce(CancellationToken.None);

		// The failure is a deviation, never a login failure; the set stays unset
		// so the next pass retries only after the refresh interval.
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.NotNull(view.LastDeviation);
		Assert.Equal(0, view.LoginAttempts);
		Assert.Null(view.BoostUnmetApps);
		Assert.NotNull(view.BoostCheckedAt);
	}

	[Fact]
	public async Task BoostInvalidOutput_MarksDeviationWithoutStopOrRetryStorm()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// First report establishes a known unmet set.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 10));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		Assert.Equal([220U], reconciler.GetOrchestrationView("alice")!.BoostUnmetApps);

		// A spec update forces a refresh (the reset also drops the old set); the
		// malformed report must not be read as "all targets met" — no stop goes
		// out, the deviation is kept, and the retry waits for the refresh interval.
		accounts.Upsert("alice", true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220u, 100)]);
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("get_playtime", jobs.Created[2].Action);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = new Dictionary<string, object?> { ["games_count"] = 1 };
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Still only 3 jobs: the unusable report dispatched neither a stop nor an
		// immediate re-query (the stamped BoostCheckedAt gates the retry).
		Assert.Equal(3, jobs.Created.Count);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(view.BoostUnmetApps);
		Assert.NotNull(view.LastDeviation);
		Assert.True(view.Idling);
	}

	[Fact]
	public void ExtractBoostUnmet_SurvivesJsonRoundTripAndFilters()
	{
		var spec = new AccountSpec("alice", true, AccountDesiredState.Boost,
			new[] { "620" }, null, null, null, null, false,
			[new BoostTarget(220u, 100), new BoostTarget(620u, 50), new BoostTarget(730u, 10)]);

		// Simulate the SQLite JSON round-trip: numbers become JsonElement objects.
		var raw = PlaytimeOutput((220, 36.5), (620, 99), (730, 12));
		string json = System.Text.Json.JsonSerializer.Serialize(raw);
		var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json);

		List<uint>? unmet = DesiredStateReconciler.ExtractBoostUnmet(roundTripped, spec);

		// 220 below target (unmet), 620 excluded via IdleApps, 730 above target.
		Assert.Equal([220U], unmet);
		// Missing/invalid outputs are unusable, never "all targets met".
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(null, spec));
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>(), spec));
	}

	[Fact]
	public void ExtractBoostUnmet_MissingAppOrBadEntry_IsConservative()
	{
		var spec = new AccountSpec("alice", true, AccountDesiredState.Boost,
			null, null, null, null, null, false,
			[new BoostTarget(220u, 100), new BoostTarget(620u, 10)]);

		// An app absent from the report stays unmet — never read as met.
		Assert.Equal(
			[220U, 620U],
			DesiredStateReconciler.ExtractBoostUnmet(PlaytimeOutput((220, 36.5)), spec));

		// A malformed entry invalidates the whole report.
		var bad = new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?>
			{
				new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = 36.5 },
				new Dictionary<string, object?> { ["app_id"] = "not-a-number", ["hours"] = 1.0 }
			}
		};
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(bad, spec));
	}

	// ── execution-path hardening (loops, isolation, no-agent, shape quirks) ──

	[Fact]
	public async Task ExecuteAsync_NonPositiveInterval_DisablesReconciler()
	{
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null)), NewRegistry(("agent-1", "us-east", null)), jobs, intervalSeconds: 0);
		using var cts = new CancellationTokenSource();

		await reconciler.StartAsync(cts.Token);
		await Task.Delay(200);
		await reconciler.StopAsync(cts.Token);

		Assert.Empty(jobs.Created);
	}

	[Fact]
	public async Task ExecuteAsync_UnhandledExceptionInPass_KillsNoPass()
	{
		// An exception outside the per-account try (Unassign → cancel) must be
		// absorbed by the background loop so the service survives for later passes.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, intervalSeconds: 1);
		using var cts = new CancellationTokenSource();
		await reconciler.StartAsync(cts.Token);

		// Wait for pass 1 to dispatch a login job.
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (jobs.Created.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(50);
		}
		Assert.Single(jobs.Created);

		// Disable the account and sabotage its cancel: the next pass throws from
		// UnassignAsync (outside the per-account try) — the loop must swallow it.
		accounts.Upsert("alice", false, AccountDesiredState.Online, null, null, null, null);
		jobs.Drop("job-1");
		jobs.ThrowOnCancel = true;
		await Task.Delay(2500);

		// StopAsync completing without observation proves the loop is still alive.
		await reconciler.StopAsync(cts.Token);
		Assert.Single(jobs.Created);
	}

	[Fact]
	public async Task InFlightLoginJobWithinWindow_WaitsWithoutSettling()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.FromHours(1); // keep the job in flight
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.NotNull(view.ActiveJobId);
	}

	[Fact]
	public async Task ConnectedSession_ClearsInFlightLoginWithoutSettling()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.FromHours(1); // keep it in flight
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		sessions.Update("alice", "state_changed", "Connected", null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(view.ActiveJobId);
		Assert.Null(view.ActiveJobAction);
	}

	[Fact]
	public async Task ConnectedSession_ClearsLoginBackoffAndRedispatches()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, cooldownSeconds: 3600);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Failed, "bad password");
		await reconciler.ReconcileOnce(CancellationToken.None); // settle → attempts=1, long cooldown
		Assert.Equal(1, reconciler.GetOrchestrationView("alice")!.LoginAttempts);
		Assert.NotNull(reconciler.GetOrchestrationView("alice")!.NextAttemptAt);

		// The account recovers: a fresh Connected snapshot clears the backoff
		// entirely (NextAttemptAt reset to the epoch).
		sessions.Update("alice", "state_changed", "Connected", null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(0, reconciler.GetOrchestrationView("alice")!.LoginAttempts);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.NextAttemptAt);
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
	public async Task Idle_NoCapableAgent_MarksDeviationWithoutDispatch()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Idle, new[] { "730" }, null, null));
		var agents = NewRegistry(("agent-1", "us-east", new Dictionary<string, bool> { ["login"] = true })); // no play_games
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
	public async Task SettleActiveJob_MissingJob_ClearsInFlightState()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Drop("job-1"); // store lost the job (e.g. retention purge)
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(view.ActiveJobId);
		Assert.Null(view.ActiveJobAction);
	}

	[Fact]
	public async Task Unassign_CancelJobMissing_IsTolerated()
	{
		AccountStore accounts = new();
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);

		// The job vanished before the cancel reached the store — unassign still completes.
		jobs.Drop("job-1");
		accounts.Upsert("alice", true, AccountDesiredState.Offline, null, null, null, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(1, reconciler.Unassignments);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.ActiveJobId);
	}

	[Fact]
	public async Task Unassign_Noop_WhenNothingAssigned()
	{
		AccountStore accounts = NewAccounts(("alice", false, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(0, reconciler.Unassignments);
	}

	[Fact]
	public async Task PinnedOnlineAgent_ReceivesDispatch()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, "agent-2"));
		var agents = NewRegistry(
			("agent-1", "us-east", null),
			("agent-2", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		// The pinned agent wins even though agent-1 is equally idle.
		Assert.Single(jobs.Created);
		Assert.Equal("agent-2", reconciler.GetOrchestrationView("alice")!.AssignedAgent);
	}

	[Fact]
	public async Task DryRun_AuditFailure_IsSwallowed()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore { ThrowOnRecord = true };
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit, dryRun: true);

		// Must not throw even though the dry-run audit entry cannot be persisted.
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.DryRunDeviations);
	}

	[Fact]
	public async Task AuditFailure_DoesNotBlockDispatch()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore { ThrowOnRecord = true };
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit);

		await reconciler.ReconcileOnce(CancellationToken.None);

		// The login job is dispatched and accounted even though its audit entry was lost.
		Assert.Single(jobs.Created);
		Assert.Equal("job-1", reconciler.GetOrchestrationView("alice")!.ActiveJobId);
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

	private static AccountSpec NewSpec() =>
		new AccountStore().Upsert("alice", true, AccountDesiredState.Farm, null, null, null, note: null);

	// ── guard & cancellation propagation paths ──

	[Fact]
	public async Task ReconcileOnce_CancelledBeforeFirstAccount_ReturnsWithoutDispatching()
	{
		// The per-account cancellation check runs before any work: a cancelled pass
		// must be a silent no-op even when capable agents are available.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		await reconciler.ReconcileOnce(cts.Token);

		Assert.Empty(jobs.Created);
		Assert.Equal(0, reconciler.NoAgentSkips);
		Assert.Null(reconciler.GetOrchestrationView("alice"));
	}

	[Fact]
	public async Task ReconcileOnce_LoginAuditCancelled_PropagatesOutOfPass()
	{
		// RecordActionAsync rethrows OperationCanceledException so a stopping pass
		// unwinds instead of reporting the audit loss as an ordinary failure.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore { ThrowOceOnRecord = true };
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit);

		await Assert.ThrowsAsync<OperationCanceledException>(() => reconciler.ReconcileOnce(CancellationToken.None));

		Assert.Single(jobs.Created); // Dispatch happened; only the audit write was cancelled.
	}

	[Fact]
	public async Task ReconcileOnce_DryRunAuditCancelled_PropagatesOutOfPass()
	{
		// Same propagation rule for the dry-run guard: cancellation must not be
		// mistaken for an audit persistence failure (which would be swallowed).
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore { ThrowOceOnRecord = true };
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit, dryRun: true);

		await Assert.ThrowsAsync<OperationCanceledException>(() => reconciler.ReconcileOnce(CancellationToken.None));

		Assert.Empty(jobs.Created);
	}

	[Fact]
	public async Task ExecuteAsync_LoginAuditCancelledMidPass_StopsLoopViaCancellationCatch()
	{
		// The audit write cancels the stop token and then throws: ExecuteAsync's
		// OperationCanceledException catch must end the service cleanly (no retry,
		// no crash) — this is the documented shutdown path for a stopping pass.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var cts = new CancellationTokenSource();
		var audit = new FakeAuditStore
		{
			ThrowOceOnRecord = true,
			OnRecord = cts.Cancel
		};
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit, intervalSeconds: 1);

		await reconciler.StartAsync(cts.Token);
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (jobs.Created.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(50);
		}
		Assert.Single(jobs.Created);

		await reconciler.StopAsync(CancellationToken.None);

		Assert.Single(jobs.Created); // No further pass ran after the cancelled one.
	}

	[Fact]
	public async Task ReconcileOnce_CreateJobFailsForOneAccount_OtherAccountsStillReconciled()
	{
		// One broken account must not block the rest of the pass: alice's dispatch
		// throws, bob's is still dispatched on the same pass.
		AccountStore accounts = NewAccounts(
			("alice", true, AccountDesiredState.Online, null, null, null),
			("bob", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore { ThrowOnCreateWhen = r => r.Targets.Contains("alice") };
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Equal(new[] { "bob" }, jobs.Created[0].Targets);
		// alice's runtime exists (the pass visited it) but no agent was ever assigned.
		Assert.Null(reconciler.GetOrchestrationView("alice")!.AssignedAgent);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("bob")!.AssignedAgent);
	}

	[Fact]
	public async Task SettleActiveJob_CardDropsOutcomeWithoutTasks_SettlesQuietly()
	{
		// A card-drops outcome whose task rows vanished is not a failure: the settle
		// stamps the refresh check without a deviation, and the stamped check gates
		// the retry until the refresh interval — the vanished outcome neither
		// re-queries immediately nor touches the login budget.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Farm, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.StripTasks("job-1");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(view.ActiveJobId); // settled job-1 quietly
		Assert.Null(view.FarmQueue);
		Assert.Equal(0, view.LoginAttempts); // Never consumes the login budget.
		Assert.NotNull(view.FarmQueueCheckedAt); // The settle stamped the refresh check.
		Assert.Null(view.LastDeviation); // The vanished outcome stays quiet.

		Assert.Single(jobs.Created); // no immediate re-query
	}

	[Fact]
	public async Task SettleActiveJob_LoginOutcomeWithoutTasks_ClearsInFlightQuietly()
	{
		// A login outcome with no task rows clears the in-flight state without
		// counting a failure (there is nothing to attribute the failure to).
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.StripTasks("job-1");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(view.ActiveJobId);
		Assert.Equal(0, view.LoginAttempts);
	}

	[Fact]
	public async Task RebalanceWithoutActiveJob_SkipsCancelAndReassigns()
	{
		// When the login job already converged (the session reports Connected), the
		// runtime keeps the assignment but no active job. Losing that agent must
		// tolerate the absent job on rebalance (cancel no-op) without extra dispatches.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-a", "us-east", null), ("agent-b", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("agent-a", reconciler.GetOrchestrationView("alice")!.AssignedAgent);

		// The login lands: the session goes Connected, clearing the active job while
		// the assignment survives.
		sessions.Update("alice", "state_changed", "Connected", null);
		await reconciler.ReconcileOnce(CancellationToken.None);
		AccountOrchestrationView settled = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(settled.ActiveJobId);
		Assert.Equal("agent-a", settled.AssignedAgent);

		agents.Unregister("agent-a");
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(1, reconciler.Rebalances);
		Assert.Empty(jobs.Cancelled); // Nothing in flight to cancel.
		Assert.Single(jobs.Created);  // A Connected account dispatches nothing further.
		Assert.Null(reconciler.GetOrchestrationView("alice")!.AssignedAgent);
	}

	[Fact]
	public async Task DryRun_ConnectedIdleAccount_GuardsPlayDispatch()
	{
		// Dry-run reports the play deviation instead of dispatching; the account
		// must not start idling.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Idle, new[] { "730" }, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		var audit = new FakeAuditStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, audit, dryRun: true);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.False(reconciler.GetOrchestrationView("alice")!.Idling);
		Assert.Contains("play_games", reconciler.GetOrchestrationView("alice")!.LastDeviation, StringComparison.Ordinal);
		Assert.Contains(audit.Entries, e => e.Action == "account.reconciled.dry_run");
	}

	[Fact]
	public async Task DryRun_AgentDisappearedAfterAssignment_GuardsRebalance()
	{
		// Config is immutable per deployment, so a dry-run switch can only be exercised
		// by swapping the private cfg: pass 1 assigns under normal mode, pass 2 runs
		// dry-run with the assigned agent gone — only the deviation is recorded.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("alice")!.AssignedAgent);

		SwapDryRun(reconciler, dryRun: true);
		agents.Unregister("agent-1");
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(0, reconciler.Rebalances);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("alice")!.AssignedAgent); // Kept, not dropped.
		Assert.Contains("disconnected", reconciler.GetOrchestrationView("alice")!.LastDeviation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DryRun_DisabledAccountAfterAssignment_GuardsUnassign()
	{
		// Same immutable-config caveat as the rebalance guard: disabling an account
		// under dry-run must report the unassign without dropping the assignment.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs);

		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("alice")!.AssignedAgent);

		SwapDryRun(reconciler, dryRun: true);
		accounts.SetEnabled("alice", enabled: false);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(0, reconciler.Unassignments);
		Assert.Equal("agent-1", reconciler.GetOrchestrationView("alice")!.AssignedAgent);
		Assert.Empty(jobs.Cancelled);
		Assert.Contains("unassign", reconciler.GetOrchestrationView("alice")!.LastDeviation, StringComparison.Ordinal);
	}

	[Fact]
	public void AccountOrchestrationView_OptionalFields_DefaultToNull()
	{
		// Covers the record's default-parameter arms: constructing without the
		// optional tail must leave every optional field null.
		AccountOrchestrationView view = new("agent-1", "job-1", "login", 0, null, Idling: false);

		Assert.Null(view.FarmingAppId);
		Assert.Null(view.FarmQueue);
		Assert.Null(view.FarmQueueCheckedAt);
		Assert.Null(view.LastAction);
		Assert.Null(view.LastActionAt);
		Assert.Null(view.LastDeviation);
	}

	/// <summary>
	/// Swaps the reconciler's immutable Config record for a dry-run variant. The
	/// product never does this at runtime (config is fixed at startup); reflection is
	/// the only way to reach the mixed-mode guard branches from a single instance.
	/// </summary>
	private static void SwapDryRun(DesiredStateReconciler reconciler, bool dryRun)
	{
		System.Reflection.FieldInfo field = typeof(DesiredStateReconciler).GetField("_cfg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Missing reconciler config field.");
		var cfg = (Config?)field.GetValue(reconciler) ?? throw new InvalidOperationException("Reconciler config is unset.");
		field.SetValue(reconciler, cfg with { ReconcileDryRun = dryRun });
	}

	// ── fixtures ──

	private static AccountStore NewAccounts(params (string Name, bool Enabled, AccountDesiredState State, string[]? Apps, string? Region, string? Agent)[] specs)
	{
		var store = new AccountStore();
		foreach (var (name, enabled, state, apps, region, agent) in specs)
		{
			store.Upsert(name, enabled, state, apps, region, agent, note: null);
		}

		return store;
	}

	private static AgentRegistry NewRegistry(params (string Id, string Region, Dictionary<string, bool>? Capabilities)[] agents)
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		foreach (var (id, region, capabilities) in agents)
		{
			registry.Register(new AgentHello(id, region, capabilities, null), new NoopWebSocket(), cts.Token);
		}

		return registry;
	}

	private static DesiredStateReconciler CreateReconciler(
		AccountStore accounts,
		AgentRegistry agents,
		FakeReconcileJobStore jobs,
		SessionTracker? sessions = null,
		IAuditStore? auditStore = null,
		int cooldownSeconds = 60,
		int maxLoginAttempts = 3,
		bool dryRun = false,
		int farmRefreshSeconds = 300,
		int intervalSeconds = 15,
		int tradeRefreshSeconds = 600,
		IEventBroker? broker = null)
	{
		var cfg = new Config(
			"admin",
			new HashSet<string>(StringComparer.Ordinal),
			":memory:",
			TaskLeaseSeconds: 300,
			EnableSwagger: false,
			AuditDbPath: ":memory:",
			TaskMaxDispatchAttempts: 10,
			TaskDispatchRetryDelayMs: 2000,
			ReconcileIntervalSeconds: intervalSeconds,
			ReconcileMaxAccountsPerAgent: 25,
			ReconcileMaxLoginAttempts: maxLoginAttempts,
			ReconcileLoginCooldownSeconds: cooldownSeconds,
			ReconcileSessionStalenessSeconds: 120,
			ReconcileFarmRefreshSeconds: farmRefreshSeconds,
			ReconcileTradeRefreshSeconds: tradeRefreshSeconds,
			ReconcileDryRun: dryRun);

		return new DesiredStateReconciler(
			accounts,
			sessions ?? new SessionTracker(),
			agents,
			jobs,
			broker ?? new EventBroker(),
			auditStore ?? new FakeAuditStore(),
			cfg,
			NullLogger<DesiredStateReconciler>.Instance);
	}

	// ── §32 gift auto-accept loop ──

	private const ulong Partner64 = 76561197960265728 + 456; // whitelist entry (steamId64)

	private static IReadOnlyDictionary<string, object?> TradeOutput(params (string OfferId, string PartnerAccount, int GiveCount, bool IsOurs)[] offers)
	{
		return new Dictionary<string, object?>
		{
			["received_offers"] = offers.Select(o => new Dictionary<string, object?>
			{
				["trade_offer_id"] = o.OfferId,
				["partner_steam_id"] = o.PartnerAccount,
				["items_to_give_count"] = o.GiveCount,
				["is_our_offer"] = o.IsOurs
			}).ToList()
		};
	}

	[Fact]
	public async Task TradePolicyDisabled_ConnectedAccount_NeverScansTrades()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
	}

	[Fact]
	public async Task TradePolicyEnabled_ConnectedAccount_DispatchesActiveOnlyScan()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Equal("get_trade_offers", jobs.Created[0].Action);
		Assert.Equal(true, jobs.Created[0].Payload!["active_only"]);
		Assert.Equal("desired-state", jobs.Created[0].Meta!["orchestrator"]);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.TradeOfferCheckedAt);
	}

	[Fact]
	public async Task ScanResult_AcceptsOnlyWhitelistedGiftsOnlyOffers()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Pass 1: dispatch the scan. Pass 2: settle it — three offers where only
		// the whitelisted, gifts-only one qualifies (give>0 skipped, partner
		// outside the whitelist skipped) — and dispatch its accept.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(
			("111", "456", 0, IsOurs: false),   // whitelisted partner, pure gift → accept
			("222", "456", 2, IsOurs: false),   // whitelisted partner but asks items → skip (red line ③)
			("333", "999", 0, IsOurs: false));  // pure gift but outside the whitelist → skip (red line ②)
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("accept_trade_offer", jobs.Created[1].Action);
		Assert.Equal("111", jobs.Created[1].Payload!["trade_offer_id"]);
		Assert.Equal(Partner64.ToString(), jobs.Created[1].Payload!["partner_steam_id"]);
		Assert.Equal(true, jobs.Created[1].Payload!["verify_state"]);
		Assert.Equal([111UL], reconciler.GetOrchestrationView("alice")!.TradeOffersToAccept);
	}

	[Fact]
	public async Task AcceptFinished_WithMobileConfirmationRequired_ChainsConfirmJob()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Scan → accept dispatch.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Accept completes and Steam wants a mobile confirmation: the settle
		// pass chains confirm_trade_offer immediately.
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?> { ["requires_mobile_confirmation"] = true };
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(3, jobs.Created.Count);
		Assert.Equal("confirm_trade_offer", jobs.Created[2].Action);
		Assert.Equal("111", jobs.Created[2].Payload!["trade_offer_id"]);
		Assert.Empty(reconciler.GetOrchestrationView("alice")!.TradeOffersToAccept!);
	}

	[Fact]
	public async Task ScanResult_EvaluationAudit_RecordsEveryOfferDecision()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit);

		// Pass 1: dispatch the scan. Pass 2: settle it against five offers —
		// every decision (the accept and every skip reason) must reach the
		// audit trail, not just the offers that qualify.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(
			("111", "456", 0, IsOurs: false),              // whitelisted, pure gift → accept
			("222", "456", 2, IsOurs: false),              // asks items → has_give_items
			("333", "999", 0, IsOurs: false),              // unknown partner → partner_not_whitelisted
			("444", "456", 0, IsOurs: true),               // our own (counter) offer → our_offer
			("555", "456", int.MaxValue, IsOurs: false));  // unreadable give count → safe-side skip
		await reconciler.ReconcileOnce(CancellationToken.None);

		AuditEntry entry = Assert.Single(audit.Entries, e => e.Action == "trade.policy_evaluated");
		Assert.Equal("alice", entry.AccountName);
		Assert.Equal("orchestrator", entry.Actor);
		Assert.Equal(true, entry.Details!["policyEnabled"]);
		Assert.Equal(1, entry.Details!["eligibleCount"]);
		var decisions = Assert.IsType<List<Dictionary<string, object?>>>(entry.Details!["decisions"]);
		Assert.Equal(5, decisions.Count);
		Assert.Equal("accept", decisions.First(d => (string)d["offerId"]! == "111")["decision"]);
		Assert.Null(decisions.First(d => (string)d["offerId"]! == "111")["reason"]);
		Assert.Equal("has_give_items", decisions.First(d => (string)d["offerId"]! == "222")["reason"]);
		Assert.Equal("partner_not_whitelisted", decisions.First(d => (string)d["offerId"]! == "333")["reason"]);
		Assert.Equal("our_offer", decisions.First(d => (string)d["offerId"]! == "444")["reason"]);
		Assert.Equal("give_count_unreadable", decisions.First(d => (string)d["offerId"]! == "555")["reason"]);
		// Partner ids are strings (JS-precision-safe) in the 64-bit domain.
		Assert.Equal(Partner64.ToString(), decisions.First(d => (string)d["offerId"]! == "111")["partnerSteamId64"]);
	}

	[Fact]
	public async Task AcceptFinished_RecordsAutoAcceptedAuditAndWebhookEvent()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, broker: broker);

		// Scan → accept dispatch → the accept settles WITHOUT a mobile
		// confirmation requirement: the auto-accept is final, so the audit
		// entry and the trade.auto_accepted webhook event fire and no confirm
		// job follows.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?> { ["requires_mobile_confirmation"] = false };
		await reconciler.ReconcileOnce(CancellationToken.None);

		AuditEntry entry = Assert.Single(audit.Entries, e => e.Action == "trade.auto_accepted");
		Assert.Equal("alice", entry.AccountName);
		Assert.Equal("orchestrator", entry.Actor);
		Assert.Equal("111", entry.Details!["offerId"]);
		Assert.Equal(Partner64.ToString(), entry.Details!["partnerSteamId64"]);
		Assert.Equal(false, entry.Details!["requiresMobileConfirmation"]);

		// The webhook pipeline forwards broker events verbatim (sink rules
		// filter by type), so publishing the typed event IS the integration.
		(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload) published =
			Assert.Single(broker.Published, p => p.Type == "trade.auto_accepted");
		Assert.NotNull(published.JobId);
		Assert.Equal("alice", published.Payload!["accountName"]);
		Assert.Equal("111", published.Payload!["offerId"]);
		Assert.Equal(2, jobs.Created.Count); // scan + accept only — no confirm job
	}

	[Fact]
	public async Task ScanFailed_MarksDeviationWithoutLoginBudgetAndWaitsInterval()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, tradeRefreshSeconds: 3600);

		// Pass 1 dispatches the scan; pass 2 settles it as failed — a deviation,
		// never a login failure; CheckedAt is stamped so the throttle (1h) holds.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Failed, "steam down");
		await reconciler.ReconcileOnce(CancellationToken.None);

		// The deviation is visible right after the failing settle (a later
		// healthy pass clears LastDeviation — same semantics as farm/boost).
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Contains("get_trade_offers", view.LastDeviation);
		Assert.Equal(0, view.LoginAttempts);
		Assert.Null(view.NextAttemptAt);

		// Still inside the throttle window: no re-scan.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);
	}

	[Fact]
	public void ExtractPendingGiftOffers_HandlesJsonElementAndDictionaryForms()
	{
		AccountStore accounts = new();
		AccountSpec spec = accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));

		// SQLite round-trip shape: everything is a JsonElement. Missing give
		// count reads as "unusable" (never auto-accepted); our own offers and
		// unusable zero partner ids are skipped.
		using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
			"""
			[
				{"trade_offer_id":"111","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":false},
				{"trade_offer_id":"444","partner_steam_id":"456","is_our_offer":false},
				{"trade_offer_id":"555","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":true},
				{"trade_offer_id":"666","partner_steam_id":"0","items_to_give_count":0,"is_our_offer":false}
			]
			"""))
		{
			var jsonOutput = new Dictionary<string, object?> { ["received_offers"] = doc.RootElement.Clone() };
			System.Collections.Generic.List<DesiredStateReconciler.PendingGiftOffer> offers =
				DesiredStateReconciler.ExtractPendingGiftOffers(jsonOutput, spec);
			Assert.Single(offers);
			Assert.Equal(111UL, offers[0].OfferId);
			Assert.Equal(Partner64, offers[0].PartnerSteamId64);
		}

		// Fresh dispatch shape: in-memory dictionaries.
		var dictOutput = TradeOutput(("111", "456", 0, IsOurs: false));
		Assert.Single(DesiredStateReconciler.ExtractPendingGiftOffers(dictOutput, spec));

		// No policy / empty whitelist → nothing qualifies, ever.
		AccountSpec bare = accounts.Upsert("bob", true, AccountDesiredState.Online, null, null, null, null);
		Assert.Empty(DesiredStateReconciler.ExtractPendingGiftOffers(dictOutput, bare));
	}

	[Fact]
	public async Task ExecuteAsync_ABrokenReconcilePassDoesNotKillTheService()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, "us-east", null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore { ThrowOnCreate = true };
		using var reconciler = CreateReconciler(accounts, agents, jobs, intervalSeconds: 1);

		await reconciler.StartAsync(CancellationToken.None);
		// The one-second tick runs a pass whose store throws; the loop-level catch
		// must swallow the failure and leave the service alive for the next tick.
		await Task.Delay(1300);
		await reconciler.StopAsync(CancellationToken.None);

		// Reaching a clean stop after a broken pass is the assertion.
	}

	/// <summary>
	/// Records every Publish call so tests can assert the reconciler's broker
	/// events (the webhook pipeline forwards them verbatim as sink-filtered
	/// notification types). Subscription streams stay empty — the reconciler
	/// only publishes.
	/// </summary>
	internal sealed class RecordingBroker : IEventBroker
	{
		public List<(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload)> Published { get; } = [];

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
			=> Published.Add((jobId, type, payload));

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
		{
		}

		public async IAsyncEnumerable<Event> Subscribe(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
			string jobId)
		{
			await Task.Yield();
			yield break;
		}

		public async IAsyncEnumerable<SessionEvent> SubscribeSessions(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
			string? accountName = null)
		{
			await Task.Yield();
			yield break;
		}

		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
			string? accountName = null)
		{
			await Task.Yield();
			yield break;
		}
	}

	internal sealed class FakeAuditStore : IAuditStore
	{
		public List<AuditEntry> Entries { get; } = [];

		/// <summary>Makes RecordAsync throw (audit-isolation paths).</summary>
		public bool ThrowOnRecord { get; set; }

		/// <summary>Makes RecordAsync throw OperationCanceledException (cancellation propagation paths).</summary>
		public bool ThrowOceOnRecord { get; set; }

		/// <summary>Invoked at the start of every RecordAsync (lets tests cancel the stop token mid-pass).</summary>
		public Action? OnRecord { get; set; }

		public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
		{
			OnRecord?.Invoke();
			if (ThrowOceOnRecord)
			{
				throw new OperationCanceledException("audit store canceled");
			}

			if (ThrowOnRecord)
			{
				throw new IOException("audit store unavailable");
			}

			Entries.Add(entry);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<AuditEntry>>(Entries);

		public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken)
			=> Task.FromResult(Entries.Count);
	}

	internal sealed class FakeReconcileJobStore : IJobStore
	{
		private readonly Dictionary<string, JobWithTasks> _jobs = new();
		private int _counter;

		public List<CreateJobRequest> Created { get; } = [];
		public Dictionary<string, (JobTaskStatus Status, string? Error)> Outcomes { get; } = new();
		public Dictionary<string, IReadOnlyDictionary<string, object?>?> Outputs { get; } = new();
		public List<string> Cancelled { get; } = [];

		/// <summary>Forgets a job so lookups and cancels hit the NotFoundException path.</summary>
		public void Drop(string jobId) => _jobs.Remove(jobId);

		/// <summary>
		/// Empties a job's task list so GetJob returns the job with no tasks at all
		/// (the settle path for an outcome whose target row vanished).
		/// </summary>
		public void StripTasks(string jobId)
		{
			if (_jobs.TryGetValue(jobId, out JobWithTasks? job))
			{
				_jobs[jobId] = new JobWithTasks(job.Job, []);
			}
		}

		/// <summary>Makes CreateJob throw (per-account isolation path).</summary>
		public bool ThrowOnCreate { get; set; }

		/// <summary>Only throws for requests matching this predicate (selective-failure isolation tests).</summary>
		public Func<CreateJobRequest, bool>? ThrowOnCreateWhen { get; set; }

		/// <summary>Makes CancelJob throw a non-NotFoundException (background-loop isolation path).</summary>
		public bool ThrowOnCancel { get; set; }

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
		{
			if (ThrowOnCreate || ThrowOnCreateWhen?.Invoke(request) == true)
			{
				throw new IOException("store unavailable");
			}

			Created.Add(request);
			int n = Interlocked.Increment(ref _counter);
			var job = new Job($"job-{n}", request.Action, request.Region, request.Targets, request.Meta, JobStatus.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			var tasks = request.Targets
				.Select((target, i) => new JobTask($"task-{n}-{i}", job.Id, target, request.Action, request.Region, request.Payload, JobTaskStatus.Queued, 0, job.CreatedAt, job.CreatedAt))
				.ToList();
			var result = new JobWithTasks(job, tasks);
			_jobs[job.Id] = result;
			return Task.FromResult(result);
		}

		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
		{
			if (!_jobs.TryGetValue(jobId, out JobWithTasks? job))
			{
				throw new NotFoundException("job not found");
			}

			return Task.FromResult(new JobWithTasks(
				job.Job,
				job.Tasks.Select(t => Outcomes.TryGetValue(t.Id, out (JobTaskStatus Status, string? Error) outcome)
					? new JobTask(t.Id, t.JobId, t.Target, t.Action, t.Region, t.Payload, outcome.Status, t.Attempt, t.CreatedAt, t.UpdatedAt, outcome.Error,
						Outputs.TryGetValue(t.Id, out IReadOnlyDictionary<string, object?>? output) ? output : t.Output)
					: t)
					.ToList()));
		}

		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken)
		{
			if (!_jobs.ContainsKey(jobId))
			{
				throw new NotFoundException("job not found");
			}

			if (ThrowOnCancel)
			{
				throw new IOException("store unavailable");
			}

			Cancelled.Add(jobId);
			return Task.FromResult<IReadOnlyList<TaskCancel>>([]);
		}

		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<Job>>(_jobs.Values.Select(j => j.Job).ToList());

		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<JobTask>>(_jobs.Values
				.SelectMany(j => j.Tasks)
				.Where(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(t => t.CreatedAt)
				.Take(limit)
				.ToList());

		public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyDictionary<JobTaskStatus, int>>(new Dictionary<JobTaskStatus, int>());

		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken)
			=> Task.FromResult<JobTask?>(null);

		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<Job>>([]);

		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken)
			=> Task.FromResult(false);

		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken)
			=> Task.FromResult<Job?>(null);

		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken)
			=> Task.FromResult(false);

		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);

		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => Task.FromResult(true);

		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
	}

	private sealed class NoopWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public override void Dispose()
		{
		}

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.AsSpan());
			return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		{
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.Span);
			return ValueTask.FromResult(new ValueWebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
			=> ValueTask.CompletedTask;
	}
}
