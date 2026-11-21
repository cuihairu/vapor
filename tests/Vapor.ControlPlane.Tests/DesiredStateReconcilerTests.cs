using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Core reconcile-pass tests: dispatch decisions, in-flight settling, dry-run and audit guards, cancellation propagation, plus the shared test fakes.</summary>
public sealed partial class DesiredStateReconcilerTests : IDisposable
{
	public DesiredStateReconcilerTests()
	{
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.CardDropsInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.PlaytimeInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.TradeInFlightWindow = TimeSpan.Zero;
		DesiredStateReconciler.StandingInFlightWindow = TimeSpan.Zero;
	}

	public void Dispose()
	{
		DesiredStateReconciler.LoginInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.PlayInFlightWindow = TimeSpan.FromSeconds(120);
		DesiredStateReconciler.CardDropsInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.PlaytimeInFlightWindow = TimeSpan.FromSeconds(150);
		DesiredStateReconciler.TradeInFlightWindow = TimeSpan.FromSeconds(60);
		DesiredStateReconciler.StandingInFlightWindow = TimeSpan.FromSeconds(150);
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
		jobs.ArmCancelGate();
		using var reconciler = CreateReconciler(accounts, agents, jobs, intervalSeconds: 1);

		await reconciler.StartAsync(CancellationToken.None);

		// Tick 1: the login job is dispatched and the active job id pinned.
		var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (jobs.Created.Count == 0 && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		Assert.Single(jobs.Created);

		// Tick 2: disabling the account unassigns it, and the store's cancel
		// parks at the armed gate — deterministically before the ThrowOnCancel
		// check. Only the NotFoundException arm swallows, so the IOException
		// escapes to the background loop's catch (the lines under coverage);
		// arming turns that escape from a one-interval race into a
		// happens-before edge a CI stall cannot invert.
		accounts.SetEnabled("alice", enabled: false);
		await jobs.CancelGateTouched!.Task.WaitAsync(TimeSpan.FromSeconds(30));
		jobs.ThrowOnCancel = true;
		jobs.CancelGate!.SetResult();

		// Tick 3 proves the loop survived: the active job id was cleared ahead of
		// the failed cancel, so the retry unassigns cleanly.
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
	public async Task ReconcileLoop_RecordsLastPassTelemetryOnSuccess()
	{
		// Before the first pass completes the telemetry is unset (both null arms
		// of the reader properties); a successful tick fills all three fields.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(accounts, agents, jobs, intervalSeconds: 1);

		Assert.Null(reconciler.LastPassAt);
		Assert.Null(reconciler.LastPassDurationMs);
		Assert.False(reconciler.LastPassFailed);

		await reconciler.StartAsync(CancellationToken.None);

		var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (jobs.Created.Count == 0 && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		await reconciler.StopAsync(CancellationToken.None);

		Assert.False(reconciler.LastPassFailed);
		Assert.NotNull(reconciler.LastPassAt);
		Assert.NotNull(reconciler.LastPassDurationMs);
	}

	[Fact]
	public async Task ReconcileLoop_RecordsFailedPassTelemetry()
	{
		// Drives the failure arm with the same gate choreography as
		// BrokenUnassignPass (interval 1s): tick 2 parks in the armed cancel
		// gate, the escape reaches the loop catch, and RecordPass(ok: 0) lands.
		// The stop comes right after the observation, so no later tick can
		// overwrite the failed-pass telemetry before the assertions run.
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		jobs.ArmCancelGate();
		using var reconciler = CreateReconciler(accounts, agents, jobs, intervalSeconds: 1);

		await reconciler.StartAsync(CancellationToken.None);

		// Wait for pass 1 to COMPLETE (telemetry recorded), not just to have
		// dispatched: disabling afterwards means tick 2 is guaranteed to see the
		// account disabled and take the unassign path, whose cancel sits OUTSIDE
		// the per-account try — the settle path would swallow the sabotage.
		var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while ((jobs.Created.Count == 0 || reconciler.LastPassAt is null) && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		accounts.SetEnabled("alice", enabled: false);
		await jobs.CancelGateTouched!.Task.WaitAsync(TimeSpan.FromSeconds(30));

		// The failed-pass flag is transient by design: the next pass retries the
		// unassign cleanly (the active job id was cleared ahead of the failed
		// cancel) and overwrites the telemetry ~1 s later. Polling that ~1 s
		// window raced a CI stall (macos runner, 2026-09-27): a test thread
		// frozen across the window never saw the flag and burned the 30 s budget.
		// Anchor on the stop instead — the release task lets the escape land, the
		// loop records the failed pass and exits on the stop token before
		// StopAsync returns, so no later pass exists to overwrite. Release order
		// is a happens-before edge, not a wall-clock race.
		jobs.ThrowOnCancel = true;
		Task release = Task.Run(async () =>
		{
			await Task.Delay(100);
			jobs.CancelGate!.SetResult();
		});
		await reconciler.StopAsync(CancellationToken.None);
		await release;

		Assert.True(reconciler.LastPassFailed);
		Assert.NotNull(reconciler.LastPassAt);
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
		jobs.ArmCancelGate();
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

		// Disable the account and sabotage its cancel at the armed gate: the
		// next pass throws from UnassignAsync (outside the per-account try) —
		// the loop must swallow it. (Dropping the job first would route the
		// cancel through the NotFoundException arm, which is swallowed inside
		// CancelJobAsync — the sabotage would never fire.) The gate makes
		// "cancel reached" → "flag set" a happens-before edge instead of a
		// one-interval race.
		accounts.Upsert("alice", false, AccountDesiredState.Online, null, null, null, null);
		await jobs.CancelGateTouched!.Task.WaitAsync(TimeSpan.FromSeconds(30));
		jobs.ThrowOnCancel = true;
		jobs.CancelGate!.SetResult();
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

	// ── malformed-entry matrices for the private static report readers ──
	// JSON round-trips (SQLite snapshots, agent payloads) surface JsonElement
	// nodes and broken fields the dictionary-shaped fixtures never produce;
	// these pin every defensive arm of the three entry readers.
	private static System.Text.Json.JsonElement Json(string json) =>
		System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

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
		int standingRefreshSeconds = 0,
		int boostRefreshSeconds = 1800,
		IEventBroker? broker = null)
	{
		var cfg = new Config(
			"admin",
			new Dictionary<string, DateTimeOffset?>(),
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
			ReconcileStandingRefreshSeconds: standingRefreshSeconds,
			ReconcileDryRun: dryRun,
			ReconcileBoostRefreshSeconds: boostRefreshSeconds);

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

	[Fact]
	public async Task ReconcileLoop_ExitsThroughExhaustedTimer_AfterDispose()
	{
		// The loop's third exit — WaitForNextTickAsync returning false — had no
		// trigger on the service's private timer (no Dispose channel, see
		// TESTING.md round 12). Inverted out, it is deterministic with a real
		// owned timer: let one live tick fire, then dispose mid-loop. BCL
		// contract: in-flight and future waits return false after Dispose.
		var reconciler = CreateReconciler(NewAccounts(), NewRegistry(), new FakeReconcileJobStore(), intervalSeconds: 15);
		using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
		using var cts = new CancellationTokenSource();

		Task loop = reconciler.RunReconcileLoopAsync(timer, cts.Token);
		await Task.Delay(30); // at least one live tick drives ReconcileOnce through the try arm
		timer.Dispose();

		await loop; // returns (not hangs, not faults) — the exhausted-timer arm ran
	}

	/// <summary>
	/// Reflectively awaits a private instance method on the reconciler,
	/// surfacing the real exception so asserts can match it.
	/// </summary>
	private static void InvokeInstance(
		DesiredStateReconciler target,
		string method,
		params object?[] args)
	{
		MethodInfo? info = typeof(DesiredStateReconciler).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(info);
		try
		{
			((Task)info.Invoke(target, args)!).GetAwaiter().GetResult();
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
		}
	}

	// ── round 32 gift auto-accept loop ──
	private const ulong Partner64 = 76561197960265728 + 456; // whitelist entry (steamId64)

	[Fact]
	public async Task LoginRetry_DispatchReasonCyclesFromAssignToRetry()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		// No session snapshot: a sessionless account walks the login-dispatch
		// branch (a Connected snapshot would converge without a login job).
		using var reconciler = CreateReconciler(accounts, agents, jobs, auditStore: audit, cooldownSeconds: 0);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Failed, "bad password");
		await reconciler.ReconcileOnce(CancellationToken.None); // settle the failure
		await reconciler.ReconcileOnce(CancellationToken.None); // cooldown is zero → immediate retry

		string[] reasons = audit.Entries
			.Where(e => e.Details is not null
				&& e.Details.TryGetValue("orchestrationAction", out object? act)
				&& string.Equals((string?)act, "login_dispatched", StringComparison.Ordinal))
			.Select(e => (string)e.Details!["reason"]!)
			.ToArray();
		Assert.Equal(new[] { "assign", "retry 2" }, reasons);
	}

	[Fact]
	public void SpecVersionNull_StillResetsRuntimeBookkeeping()
	{
		var agents = NewRegistry(("agent-1", "us-east", null));
		using var reconciler = CreateReconciler(new AccountStore(), agents, new FakeReconcileJobStore());
		// A spec with no persisted version (never round-tripped through the
		// store) must still reset the failure budget: Version?.Version is null
		// and matches nothing.
		AccountSpec spec = new("alice", true, AccountDesiredState.Online, Region: "us-east");
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
		runtimeType.GetField("SpecVersion")!.SetValue(runtime, 1);
		runtimeType.GetField("LoginAttempts")!.SetValue(runtime, 2);
		runtimeType.GetField("NextAttemptAt")!.SetValue(runtime, DateTimeOffset.UtcNow.AddHours(1));
		runtimeType.GetField("FarmQueueCheckedAt")!.SetValue(runtime, DateTimeOffset.UtcNow);
		runtimeType.GetField("TradeOffersToAccept")!.SetValue(runtime, new List<DesiredStateReconciler.PendingGiftOffer>());

		InvokeInstance(reconciler, "ReconcileActiveAccountAsync", spec, runtime,
			new Dictionary<string, ConnectedAgent>(), new Dictionary<string, int>(), CancellationToken.None);

		Assert.Null(runtimeType.GetField("SpecVersion")!.GetValue(runtime));
		Assert.Equal(0, runtimeType.GetField("LoginAttempts")!.GetValue(runtime));
		Assert.Equal(DateTimeOffset.MinValue, runtimeType.GetField("NextAttemptAt")!.GetValue(runtime));
		Assert.Equal(DateTimeOffset.MinValue, runtimeType.GetField("FarmQueueCheckedAt")!.GetValue(runtime));
		Assert.Null(runtimeType.GetField("TradeOffersToAccept")!.GetValue(runtime));
	}

	// ── defensive-arm contracts: ActiveJobId/ActiveJobAction are always written
	// as a pair by every dispatch site, and FarmStatsStartedAt is only stamped
	// once the clock window is positive — so the settle fallback and the
	// negative-elapsed guard below cannot be reached through the reconcile
	// flow. They are driven directly (a runtime whose action half of the pair is
	// missing, and a stats clock stamped in the future) so the guards stay
	// pinned if those invariants ever change (see tests/TESTING.md). ──
	[Fact]
	public async Task SettleJob_MissingActionHalfOfPair_FallsBackToLoginAccounting()
	{
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(new AccountStore(), agents, jobs);
		var spec = new AccountSpec("alice", true, AccountDesiredState.Online, Region: "us-east");

		// "login" is none of the special settle actions, so the fallback lands in
		// the login outcome branch: a Failed task must hit the failure budget.
		JobWithTasks created = await jobs.CreateJob(
			new CreateJobRequest("login", "us-east", ["alice"], null, null), CancellationToken.None);
		foreach (JobTask task in created.Tasks)
		{
			jobs.Outcomes[task.Id] = (JobTaskStatus.Failed, "boom");
		}

		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
		runtimeType.GetField("ActiveJobId")!.SetValue(runtime, created.Job.Id);
		// ActiveJobAction stays null — the `?? LoginAction` fallback must supply it.

		InvokeInstance(reconciler, "SettleActiveJobAsync", spec, runtime,
			new Dictionary<string, ConnectedAgent>(), new Dictionary<string, int>(), CancellationToken.None);

		Assert.Equal(1, runtimeType.GetField("LoginAttempts")!.GetValue(runtime));
		Assert.Null(runtimeType.GetField("ActiveJobId")!.GetValue(runtime));
		Assert.Null(runtimeType.GetField("ActiveJobAction")!.GetValue(runtime));
		Assert.Contains("job login failed", (string?)runtimeType.GetField("LastDeviation")!.GetValue(runtime));
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

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null, int attempt = 1)
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

		/// <summary>
		/// When armed, CancelJob parks just before the ThrowOnCancel check and
		/// signals <see cref="CancelGateTouched"/> — the test sets the flag and
		/// releases, making "flag set" → "cancel observes it" a happens-before
		/// edge. A CI scheduling stall once ran the pass's cancel before the
		/// test set the flag: the pass completed cleanly, the test stayed
		/// green, and the background-loop catch arm went uncovered.
		/// </summary>
		public TaskCompletionSource? CancelGate { get; set; }
		public TaskCompletionSource? CancelGateTouched { get; private set; }

		/// <summary>Arms the deterministic cancel-sabotage handoff (one shot).</summary>
		public void ArmCancelGate()
		{
			CancelGateTouched = new(TaskCreationOptions.RunContinuationsAsynchronously);
			CancelGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

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

		public async Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken)
		{
			if (!_jobs.ContainsKey(jobId))
			{
				throw new NotFoundException("job not found");
			}

			if (CancelGate is TaskCompletionSource gate)
			{
				CancelGateTouched!.TrySetResult();
				await gate.Task.ConfigureAwait(false);
				CancelGate = null; // one-shot: consumed by the release, later cancels pass straight through
			}

			if (ThrowOnCancel)
			{
				throw new IOException("store unavailable");
			}

			Cancelled.Add(jobId);
			return [];
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
		public Task MarkTaskClaimedBy(string taskId, int attempt, string agentId, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<Dictionary<string, int>> CountRunningTasksByAgent(CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, int>());

		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => Task.FromResult(true);

		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken)
			=> throw new NotSupportedException();
		public Task<(int Jobs, int Tasks)> PurgeSecretPayloadsAsync(bool dryRun, string? backupPath, CancellationToken cancellationToken) => Task.FromResult((0, 0));
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
