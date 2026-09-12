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
	}

	public void Dispose()
	{
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.PlayInFlightWindow = TimeSpan.FromSeconds(120);
		DesiredStateReconciler.CardDropsInFlightWindow = TimeSpan.FromSeconds(150);
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
		int farmRefreshSeconds = 300)
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
			ReconcileIntervalSeconds: 15,
			ReconcileMaxAccountsPerAgent: 25,
			ReconcileMaxLoginAttempts: maxLoginAttempts,
			ReconcileLoginCooldownSeconds: cooldownSeconds,
			ReconcileSessionStalenessSeconds: 120,
			ReconcileFarmRefreshSeconds: farmRefreshSeconds,
			ReconcileDryRun: dryRun);

		return new DesiredStateReconciler(
			accounts,
			sessions ?? new SessionTracker(),
			agents,
			jobs,
			new EventBroker(),
			auditStore ?? new FakeAuditStore(),
			cfg,
			NullLogger<DesiredStateReconciler>.Instance);
	}

	internal sealed class FakeAuditStore : IAuditStore
	{
		public List<AuditEntry> Entries { get; } = [];

		public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
		{
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

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
		{
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
