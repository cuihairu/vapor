using System.Net.WebSockets;
using System.Reflection;
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

		var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (jobs.Created.Count == 0 && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		accounts.SetEnabled("alice", enabled: false);
		await jobs.CancelGateTouched!.Task.WaitAsync(TimeSpan.FromSeconds(30));
		jobs.ThrowOnCancel = true;
		jobs.CancelGate!.SetResult();

		deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (!reconciler.LastPassFailed && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		await reconciler.StopAsync(CancellationToken.None);

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

	// ── malformed-entry matrices for the private static report readers ──
	// JSON round-trips (SQLite snapshots, agent payloads) surface JsonElement
	// nodes and broken fields the dictionary-shaped fixtures never produce;
	// these pin every defensive arm of the three entry readers.

	private static System.Text.Json.JsonElement Json(string json) =>
		System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

	private static (bool Ok, uint AppId, double Hours) ReadPlaytimeEntry(object? entry)
	{
		MethodInfo? info = typeof(DesiredStateReconciler).GetMethod("TryReadPlaytimeEntry", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(info);
		object?[] args = [entry, 0u, 0.0];
		bool ok = (bool)info.Invoke(null, args)!;
		return (ok, (uint)args[1]!, (double)args[2]!);
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
	public void TryReadPlaytimeEntry_JsonElementArms_RejectBrokenFields()
	{
		// Missing hours / missing app_id / non-number app_id → TryGetProperty
		// or the ValueKind guard fails.
		Assert.False(ReadPlaytimeEntry(Json("""{"app_id":231}""")).Ok);
		Assert.False(ReadPlaytimeEntry(Json("""{"hours":1.5}""")).Ok);
		Assert.False(ReadPlaytimeEntry(Json("""{"app_id":"232","hours":1}""")).Ok);
		// Negative hours → the >= 0 guard fails.
		Assert.False(ReadPlaytimeEntry(Json("""{"app_id":230,"hours":-1}""")).Ok);
		// app_id beyond uint.MaxValue → NormalizeAppId rejects it.
		Assert.False(ReadPlaytimeEntry(Json("""{"app_id":5000000000,"hours":1}""")).Ok);
		// app_id at or below zero → same rejection.
		Assert.False(ReadPlaytimeEntry(Json("""{"app_id":-5,"hours":1}""")).Ok);
		// Well-formed entry still parses.
		(bool Ok, uint AppId, double Hours) ok = ReadPlaytimeEntry(Json("""{"app_id":234,"hours":2.5}"""));
		Assert.True(ok.Ok);
		Assert.Equal(234u, ok.AppId);
		Assert.Equal(2.5, ok.Hours);
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
		int standingRefreshSeconds = 0,
		int boostRefreshSeconds = 1800,
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

	// ── §38 P2 standing check loop ──

	private static Dictionary<string, object?> StandingOutput(string summary, string economy = "none") => new()
	{
		["standing"] = summary,
		["economyBan"] = economy,
		["vacBanned"] = summary != "clean",
	};

	[Fact]
	public async Task StandingCheck_DispatchesOnInterval_AndSettlesSummary()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, standingRefreshSeconds: 60);

		// Pass 1: the standing check claims the single per-account slot.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Contains(jobs.Created, r => r.Action == "check_account_standing");
		Assert.Equal("check_account_standing", reconciler.GetOrchestrationView("alice")!.ActiveJobAction);

		// Pass 2: the clean output settles onto the runtime view.
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("clean");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal("clean", view.Standing);
		Assert.False(view.StandingQuarantined);
		Assert.NotNull(view.StandingCheckedAt);
	}

	[Fact]
	public async Task StandingCheck_BannedResult_QuarantinesAlertsAndAudits()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, standingRefreshSeconds: 60, broker: broker);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("banned", economy: "banned");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal("banned", view.Standing);
		Assert.True(view.StandingQuarantined);

		AuditEntry entry = Assert.Single(audit.Entries, e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "standing_quarantined"));
		Assert.Equal("banned", entry.Details!["reason"]);

		Assert.Contains(broker.Published, p => p.Type == "account.standing_alert"
			&& string.Equals(p.Payload!["account"], "alice"));
	}

	[Fact]
	public async Task StandingAlert_MinimalAgentOutput_PublishesNullBanFlags()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, broker: broker, standingRefreshSeconds: 60);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		// An agent output carrying only the summary (no ban-flag keys at all)
		// must still alert and quarantine — the missing flags surface as nulls
		// in the event payload instead of breaking the publish.
		jobs.Outputs["task-1-0"] = new Dictionary<string, object?> { ["standing"] = "banned" };
		await reconciler.ReconcileOnce(CancellationToken.None);

		IReadOnlyDictionary<string, object?> alert = broker.Published
			.Where(p => string.Equals(p.Type, "account.standing_alert", StringComparison.Ordinal))
			.Select(p => p.Payload)
			.Single(payload => payload is not null && string.Equals((string?)payload["account"], "alice", StringComparison.Ordinal))!;
		Assert.Equal("banned", alert["standing"]);
		Assert.Null(alert["economyBan"]);
		Assert.Null(alert["vacBanned"]);
		Assert.True(reconciler.GetOrchestrationView("alice")!.StandingQuarantined);
	}

	[Fact]
	public async Task StandingCheck_CleanAfterBanned_ReleasesQuarantine()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, standingRefreshSeconds: 60);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("banned");
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.True(reconciler.GetOrchestrationView("alice")!.StandingQuarantined);

		// Force the next check instead of waiting out the 60s refresh window
		// (same entry point the dashboard "run check now" button uses).
		Assert.True(reconciler.RequestStandingCheck("alice"));
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = StandingOutput("clean");
		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch forced check
		await reconciler.ReconcileOnce(CancellationToken.None); // settle it

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal("clean", view.Standing);
		Assert.False(view.StandingQuarantined);
		Assert.Contains(audit.Entries, e => e.Details is not null
			&& string.Equals(e.Details["orchestrationAction"], "standing_released"));
	}

	[Fact]
	public async Task QuarantinedAccount_TradeLoopSkipsDispatch()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, standingRefreshSeconds: 60);

		// Ban detection first.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("banned");
		await reconciler.ReconcileOnce(CancellationToken.None);

		// Now the trade loop is due (TradeOfferCheckedAt == MinValue) but the
		// quarantine gate must keep every trade job from being dispatched.
		int createdBefore = jobs.Created.Count;
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.DoesNotContain(jobs.Created, r => r.Action == "get_trade_offers");
		Assert.Equal(createdBefore, jobs.Created.Count);
	}

	// ── defensive-arm contracts: the two trade guards below cannot be reached
	// through the reconcile flow — an agent loss is swept at the top of the
	// next pass (assignment cancelled and cleared) before any settle could
	// chain a confirmation, and a dry-run pass never holds a non-empty accept
	// queue because the scan that fills the queue is itself dry-run-gated.
	// They are driven directly so the guard behavior stays pinned if those
	// invariants ever change (see tests/TESTING.md). ──

	[Fact]
	public void ConfirmationWithoutAgent_SurfacesGoneDeviation()
	{
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(new AccountStore(), agents, jobs);
		var spec = new AccountSpec("alice", true, AccountDesiredState.Online, Region: "us-east");
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
		runtimeType.GetField("AssignedAgent")!.SetValue(runtime, "agent-1");

		// The connected set no longer holds agent-1: the confirmation must
		// surface the deviation instead of dispatching.
		InvokeInstance(reconciler, "DispatchTradeConfirmAsync", spec, runtime,
			new Dictionary<string, ConnectedAgent>(), new Dictionary<string, int>(), 111UL,
			CancellationToken.None);

		Assert.Equal(
			"gift offer 111 accepted but its agent is gone for the mobile confirmation",
			(string?)runtimeType.GetField("LastDeviation")!.GetValue(runtime));
		Assert.Empty(jobs.Created);
	}

	[Fact]
	public void DryRunQueuedAccept_SurfacesGuardDeviationWithoutDispatch()
	{
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		using var reconciler = CreateReconciler(new AccountStore(), agents, jobs, dryRun: true);
		var spec = new AccountSpec("alice", true, AccountDesiredState.Online, Region: "us-east");
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		object runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
		runtimeType.GetField("TradeOffersToAccept")!.SetValue(runtime,
			new List<DesiredStateReconciler.PendingGiftOffer> { new(111, Partner64) });
		var connected = new Dictionary<string, ConnectedAgent> { ["agent-1"] = agents.Get("agent-1")! };

		InvokeInstance(reconciler, "ReconcileTradeAsync", spec, runtime, connected,
			new Dictionary<string, int>(), DateTimeOffset.UtcNow, CancellationToken.None);

		Assert.Equal($"would accept gift offer 111 from {Partner64}",
			(string?)runtimeType.GetField("LastDeviation")!.GetValue(runtime));
		Assert.Empty(jobs.Created);
		Assert.Equal(1L, reconciler.DryRunDeviations);
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

	[Fact]
	public async Task RequestStandingCheck_RefusedWhileSlotBusyUnknownOrDisabled()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, standingRefreshSeconds: 60);

		// Slot busy: pass 1 dispatches, the forced request is refused until settle.
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.False(reconciler.RequestStandingCheck("alice"));

		// Disabled: the reconciler created without standing checks never schedules one.
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("clean");
		await reconciler.ReconcileOnce(CancellationToken.None);
		using var disabled = CreateReconciler(accounts, agents, new FakeReconcileJobStore(), sessions, standingRefreshSeconds: 0);
		Assert.False(disabled.RequestStandingCheck("alice"));

		// Unknown account: nothing to schedule for.
		Assert.False(reconciler.RequestStandingCheck("ghost"));

		// Free slot + enabled + known: the request lands and the next pass re-checks.
		Assert.True(reconciler.RequestStandingCheck("alice"));
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("check_account_standing", reconciler.GetOrchestrationView("alice")!.ActiveJobAction);
	}

	[Fact]
	public async Task StandingCheck_NoAgentOrDryRun_SkipsDispatch()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);

		// No connected agent: the standing check is skipped with a deviation.
		var emptyAgents = NewRegistry();
		using (var reconciler = CreateReconciler(accounts, emptyAgents, jobs, sessions, standingRefreshSeconds: 60))
		{
			await reconciler.ReconcileOnce(CancellationToken.None);
			Assert.Empty(jobs.Created);
			Assert.Equal("no capable agent available", reconciler.GetOrchestrationView("alice")!.LastDeviation);
		}

		// Dry-run: the dispatch is guarded right after agent selection.
		var agents = NewRegistry(("agent-1", "us-east", null));
		using var dryRun = CreateReconciler(accounts, agents, jobs, sessions, dryRun: true, standingRefreshSeconds: 60);
		await dryRun.ReconcileOnce(CancellationToken.None);
		Assert.Empty(jobs.Created);
	}

	[Fact]
	public async Task StandingCheck_SettleDefensivePaths_RecordDeviationWithoutState()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, standingRefreshSeconds: 60);

		// ① Outcome carries no task at all: nothing to read, state untouched.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = new Dictionary<string, object?> { ["standing"] = "clean" };
		jobs.StripTasks("job-1");
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.Standing);

		// ② Task failed: deviation, no quarantine, retry on a later pass.
		Assert.True(reconciler.RequestStandingCheck("alice"));
		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch job-2
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Failed, "agent reported steam web error");
		await reconciler.ReconcileOnce(CancellationToken.None);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Null(view.Standing);
		Assert.False(view.StandingQuarantined);
		Assert.Contains("steam web error", view.LastDeviation, StringComparison.Ordinal);

		// ③ Finished but the output lacks the standing summary: unreadable.
		Assert.True(reconciler.RequestStandingCheck("alice"));
		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch job-3
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-3-0"] = new Dictionary<string, object?> { ["economyBan"] = "none" };
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal("standing check output unreadable", reconciler.GetOrchestrationView("alice")!.LastDeviation);
	}

	[Fact]
	public async Task StandingSummaries_MirrorRuntimeStatePerAccount()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, standingRefreshSeconds: 60);

		// Dispatched but not yet settled: tracked, but nothing measured yet.
		await reconciler.ReconcileOnce(CancellationToken.None);
		AccountStandingView pending = Assert.Single(reconciler.GetStandingSummaries());
		Assert.Equal(("alice", null, false, null), (pending.AccountName, pending.Standing, pending.Quarantined, pending.CheckedAt));

		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("banned", economy: "banned");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountStandingView banned = Assert.Single(reconciler.GetStandingSummaries());
		Assert.Equal("alice", banned.AccountName);
		Assert.Equal("banned", banned.Standing);
		Assert.True(banned.Quarantined);
		Assert.NotNull(banned.CheckedAt);
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

	// ── §35 reconciliation guard coverage ──

	[Fact]
	public async Task Boost_NoCapableAgent_MarksDeviationWithoutDispatch()
	{
		AccountStore accounts = NewBoostStore("alice", [(220, 10.0)]);
		var agents = NewRegistry(("agent-1", "us-east", new Dictionary<string, bool> { ["login"] = true })); // no get_playtime
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
	public async Task Boost_DryRun_GuardsPlaytimeDispatch()
	{
		AccountStore accounts = NewBoostStore("alice", [(220, 10.0)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, dryRun: true);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.DryRunDeviations);
		Assert.Contains("get_playtime", reconciler.GetOrchestrationView("alice")!.LastDeviation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Boost_ActivePlaytimeJob_WaitsForIt()
	{
		AccountStore accounts = NewBoostStore("alice", [(220, 10.0)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		DesiredStateReconciler.PlaytimeInFlightWindow = TimeSpan.FromHours(1); // keep the query in flight
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created); // the second pass sees the query in flight and waits
	}

	[Fact]
	public async Task TradePolicy_NoCapableAgent_SkipsScan()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", new Dictionary<string, bool> { ["login"] = true })); // no get_trade_offers
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
	public async Task TradePolicy_DryRun_GuardsScanDispatch()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, dryRun: true);

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(jobs.Created);
		Assert.Equal(1, reconciler.DryRunDeviations);
		Assert.Contains("get_trade_offers", reconciler.GetOrchestrationView("alice")!.LastDeviation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GiftAccept_NoCapableAgent_MarksDeviationWithoutDispatch()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", new Dictionary<string, bool> { ["get_trade_offers"] = true })); // scan ok, no accept_trade_offer
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Pass 1 dispatches the scan; pass 2 settles it into the accept queue and
		// immediately tries to dispatch the accept (no capable agent — skip), and
		// pass 3 retries the same dispatch (skip again) — the queue survives for a
		// later pass instead of being dropped.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created); // scan only — the accept was never dispatched
		Assert.Equal(2, reconciler.NoAgentSkips);
		Assert.Equal([111UL], reconciler.GetOrchestrationView("alice")!.TradeOffersToAccept);
	}

	[Fact]
	public async Task TradeEvaluation_MoreThan200Decisions_TruncatesAudit()
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

		// 201 unknown-partner offers: every one is audited as a skip decision,
		// but the details payload caps at 200 and raises decisionsTruncated.
		var offers = new (string, string, int, bool)[201];
		for (int i = 0; i < offers.Length; i++)
		{
			offers[i] = ((1000 + i).ToString(), "999", 0, false);
		}

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(offers);
		await reconciler.ReconcileOnce(CancellationToken.None);

		AuditEntry entry = Assert.Single(audit.Entries, e => e.Action == "trade.policy_evaluated");
		Assert.Equal(201, entry.Details!["evaluatedCount"]);
		Assert.Equal(0, entry.Details!["eligibleCount"]);
		Assert.Equal(true, entry.Details!["decisionsTruncated"]);
		var decisions = Assert.IsType<List<Dictionary<string, object?>>>(entry.Details!["decisions"]);
		Assert.Equal(200, decisions.Count);
	}

	[Fact]
	public async Task TradeEvaluation_AuditFailure_IsIsolatedFromOrchestration()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore { ThrowOnRecord = true };
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit);

		// The evaluation audit fails to persist, but the queue and the accept
		// dispatch march on — audit isolation must not stall the policy loop.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("accept_trade_offer", jobs.Created[1].Action);
		Assert.Empty(audit.Entries);
	}

	[Fact]
	public async Task TradeEvaluation_AuditCancellation_PropagatesOce()
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

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		audit.ThrowOceOnRecord = true; // cancellation is not an isolation case — it propagates

		await Assert.ThrowsAsync<OperationCanceledException>(() => reconciler.ReconcileOnce(CancellationToken.None));
	}

	[Fact]
	public async Task AutoAccept_AuditFailure_IsIsolatedFromOrchestration()
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

		// The scan settles normally; from the accept dispatch on, the audit
		// store is down — the accept still completes and the loop stays alive.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		int settled = audit.Entries.Count; // the earlier passes audited normally
		audit.ThrowOnRecord = true;
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?> { ["requires_mobile_confirmation"] = false };
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count); // scan + accept, no crash in between
		Assert.Equal(settled, audit.Entries.Count); // the auto-accept audit was dropped
	}

	[Fact]
	public async Task AutoAccept_AuditCancellation_PropagatesOce()
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

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?> { ["requires_mobile_confirmation"] = false };
		audit.ThrowOceOnRecord = true; // cancellation during the accept-settle audit propagates

		await Assert.ThrowsAsync<OperationCanceledException>(() => reconciler.ReconcileOnce(CancellationToken.None));
	}

	[Fact]
	public void ExtractPendingGiftOffers_UnusableEntries_AreDroppedOrSafe()
	{
		AccountStore accounts = new();
		AccountSpec spec = accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));

		// A zero offer id cannot be referenced by a later accept — dropped.
		Assert.Empty(DesiredStateReconciler.ExtractPendingGiftOffers(
			TradeOutput(("0", "456", 0, IsOurs: false)), spec));

		// SQLite round-trip array carrying a non-object element: unusable,
		// dropped, the rest still evaluated.
		using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
			"""
			["not-an-offer", {"trade_offer_id":"111","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":false}]
			"""))
		{
			List<DesiredStateReconciler.PendingGiftOffer> offers =
				DesiredStateReconciler.ExtractPendingGiftOffers(
					new Dictionary<string, object?> { ["received_offers"] = doc.RootElement.Clone() }, spec);
			Assert.Single(offers);
			Assert.Equal(111UL, offers[0].OfferId);
		}

		// A fresh-dispatch list carrying a bare string entry: unusable, dropped.
		var mixed = new Dictionary<string, object?>
		{
			["received_offers"] = new List<object?>
			{
				"junk",
				new Dictionary<string, object?> { ["trade_offer_id"] = "222", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = false }
			}
		};
		List<DesiredStateReconciler.PendingGiftOffer> dictOffers =
			DesiredStateReconciler.ExtractPendingGiftOffers(mixed, spec);
		Assert.Single(dictOffers);
		Assert.Equal(222UL, dictOffers[0].OfferId);
	}

	[Fact]
	public void EvaluateTradeOffers_MalformedFieldArms_StaySafeOrAcceptGifts()
	{
		// A whitelist holding both canonical SteamId64 entries and a bare
		// account number (below the SteamId64 base) — the bare one maps to 0
		// and is filtered out of the effective set.
		AccountStore accounts = new();
		AccountSpec spec = accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64, 456]));

		// Every short-circuit arm of the JsonElement reader, one broken field
		// per entry: each malformed offer must stay out of the accept queue.
		using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse("""
			[
				{"trade_offer_id":"abc","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":false},
				{"partner_steam_id":"456","items_to_give_count":0,"is_our_offer":false},
				{"trade_offer_id":"112","items_to_give_count":0,"is_our_offer":false},
				{"trade_offer_id":"113","partner_steam_id":"xyz","items_to_give_count":0,"is_our_offer":false},
				{"trade_offer_id":"114","partner_steam_id":"456","items_to_give_count":1.5,"is_our_offer":false},
				{"trade_offer_id":"115","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":"true"}
			]
			"""))
		{
			List<DesiredStateReconciler.PendingGiftOffer> jsonOffers =
				DesiredStateReconciler.EvaluateTradeOffers(
					new Dictionary<string, object?> { ["received_offers"] = doc.RootElement.Clone() }, spec).Accepted;
			// 115 survives: an unreadable is_our_offer reads as "not ours", and a
			// whitelisted pure gift is acceptable by policy.
			PendingGiftOfferAssert(jsonOffers, [115UL]);
		}

		// The dictionary reader breaks on the same axes (missing key, null
		// value, unparseable text) — plus a missing is_our_offer key.
		var dict = new Dictionary<string, object?>
		{
			["received_offers"] = new List<object?>
			{
				new Dictionary<string, object?> { ["trade_offer_id"] = null!, ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "abc", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "116", ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "117", ["partner_steam_id"] = null!, ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "118", ["partner_steam_id"] = "xyz", ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "119", ["partner_steam_id"] = "456", ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "120", ["partner_steam_id"] = "456", ["items_to_give_count"] = null!, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "121", ["partner_steam_id"] = "456", ["items_to_give_count"] = "lots", ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "122", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0 }
			}
		};
		List<DesiredStateReconciler.PendingGiftOffer> dictOffers =
			DesiredStateReconciler.EvaluateTradeOffers(dict, spec).Accepted;
		// 120's unreadable give-count reads as int.MaxValue (asks items → skip);
		// 122's missing flag reads as "not ours" → acceptable.
		PendingGiftOfferAssert(dictOffers, [122UL]);

		static void PendingGiftOfferAssert(List<DesiredStateReconciler.PendingGiftOffer> offers, ulong[] expected) =>
			Assert.Equal(expected, offers.Select(o => o.OfferId));
	}

	[Fact]
	public void EvaluateTradeOffers_IsOurOfferFlagArms_MissingTrueAndFalse()
	{
		AccountStore accounts = new();
		AccountSpec spec = accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));

		// The JsonElement reader's is_our_offer axis: a missing flag reads as
		// "not ours" (the pure gift is acceptable), an explicit true is our own
		// offer (skipped as our_offer), an explicit false stays acceptable.
		using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse("""
			[
				{"trade_offer_id":"510","partner_steam_id":"456","items_to_give_count":0},
				{"trade_offer_id":"511","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":true},
				{"trade_offer_id":"512","partner_steam_id":"456","items_to_give_count":0,"is_our_offer":false}
			]
			"""))
		{
			(List<DesiredStateReconciler.PendingGiftOffer> Accepted, List<DesiredStateReconciler.TradeOfferDecision> Decisions) json =
				DesiredStateReconciler.EvaluateTradeOffers(
					new Dictionary<string, object?> { ["received_offers"] = doc.RootElement.Clone() }, spec);
			Assert.Equal([510UL, 512UL], json.Accepted.Select(o => o.OfferId));
			Assert.Equal(new[] { "accept", "skip", "accept" }, json.Decisions.Select(d => d.Decision));
			DesiredStateReconciler.TradeOfferDecision ours = Assert.Single(json.Decisions, d => d.Decision == "skip");
			Assert.Equal(511UL, ours.OfferId);
			Assert.Equal("our_offer", ours.Reason);
		}

		// The dictionary reader's same axis: the missing key and an explicit
		// false stay gifts, an explicit true is skipped as our own offer, and
		// a non-bool value ("true" survived some round-trip as text) reads as
		// "not ours" — the gift stays acceptable.
		var dict = new Dictionary<string, object?>
		{
			["received_offers"] = new List<object?>
			{
				new Dictionary<string, object?> { ["trade_offer_id"] = "610", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0 },
				new Dictionary<string, object?> { ["trade_offer_id"] = "611", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = true },
				new Dictionary<string, object?> { ["trade_offer_id"] = "612", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = false },
				new Dictionary<string, object?> { ["trade_offer_id"] = "613", ["partner_steam_id"] = "456", ["items_to_give_count"] = 0, ["is_our_offer"] = "true" }
			}
		};
		(List<DesiredStateReconciler.PendingGiftOffer> Accepted, List<DesiredStateReconciler.TradeOfferDecision> Decisions) dictResult =
			DesiredStateReconciler.EvaluateTradeOffers(dict, spec);
		Assert.Equal([610UL, 612UL, 613UL], dictResult.Accepted.Select(o => o.OfferId));
		DesiredStateReconciler.TradeOfferDecision dictOurs = Assert.Single(dictResult.Decisions, d => d.Decision == "skip");
		Assert.Equal(611UL, dictOurs.OfferId);
		Assert.Equal("our_offer", dictOurs.Reason);
	}

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
	public async Task PlaytimeRefresh_DispatchReasonCyclesInitialToRefresh()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, boostRefreshSeconds: 0);

		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch 1: initial playtime query
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 100));
		await reconciler.ReconcileOnce(CancellationToken.None); // settle: all targets met, nothing idled
		await reconciler.ReconcileOnce(CancellationToken.None); // zero refresh window → immediate refresh

		Assert.Equal("get_playtime", jobs.Created[1].Action);
		string[] reasons = audit.Entries
			.Where(e => e.Details is not null
				&& e.Details.TryGetValue("orchestrationAction", out object? act)
				&& string.Equals((string?)act, "playtime_dispatched", StringComparison.Ordinal))
			.Select(e => (string)e.Details!["reason"]!)
			.ToArray();
		Assert.Equal(new[] { "initial playtime query", "playtime refresh" }, reasons);
	}

	[Fact]
	public async Task TradeScanRefresh_DispatchReasonCyclesInitialToRefresh()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var audit = new FakeAuditStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, auditStore: audit, tradeRefreshSeconds: 0);

		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch 1: initial scan
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("333", "999", 0, IsOurs: false)); // outside whitelist → nothing to accept
		await reconciler.ReconcileOnce(CancellationToken.None); // settle
		await reconciler.ReconcileOnce(CancellationToken.None); // zero refresh window → immediate refresh

		Assert.Equal("get_trade_offers", jobs.Created[1].Action);
		string[] reasons = audit.Entries
			.Where(e => e.Details is not null
				&& e.Details.TryGetValue("orchestrationAction", out object? act)
				&& string.Equals((string?)act, "trade_offers_dispatched", StringComparison.Ordinal))
			.Select(e => (string)e.Details!["reason"]!)
			.ToArray();
		Assert.Equal(new[] { "initial trade-offer scan", "trade-offer refresh" }, reasons);
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
	public async Task BoostTargetsMet_WhileNotIdling_DispatchesNothing()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Pass 1 dispatches the refresh; pass 2 settles a report where every
		// target is already met. The account never idled and no boost games are
		// playing, so the stop branch (Idling || BoostPlayingApps) must not fire.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 100));
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.False(view.Idling);
		Assert.NotNull(view.BoostCheckedAt);
	}

	[Fact]
	public async Task BoostTargetsMet_WhileBoostAppsStillPlaying_DispatchesStop()
	{
		ZeroPlayInFlightWindow();
		AccountStore accounts = NewBoostStore("alice", [(220u, 100)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Pass 1 dispatches the refresh; pass 2 settles an all-targets-met
		// report while nothing is idled and nothing plays — no stop yet.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = PlaytimeOutput((220, 100));
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Single(jobs.Created);

		// The session still plays boost apps although the Idling flag is long
		// gone (leftover bookkeeping from an earlier cycle): the stop must fire
		// on the playing set alone.
		System.Collections.IDictionary runtimes = (System.Collections.IDictionary)typeof(DesiredStateReconciler)
			.GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reconciler)!;
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		runtimeType.GetField("BoostPlayingApps")!.SetValue(runtimes["alice"]!, new List<uint> { 220 });

		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
		Assert.Equal("play_games", jobs.Created[1].Action);
		Assert.Equal("stop", jobs.Created[1].Payload!["action"]);
		Assert.False(reconciler.GetOrchestrationView("alice")!.Idling);
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

	[Fact]
	public async Task Standing_SecondBan_DoesNotRepeatQuarantineAlert()
	{
		AccountStore accounts = NewAccounts(("alice", true, AccountDesiredState.Online, null, null, null));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var broker = new RecordingBroker();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions, broker: broker, standingRefreshSeconds: 60);

		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = StandingOutput("banned");
		await reconciler.ReconcileOnce(CancellationToken.None); // first ban → quarantine alert

		Assert.True(reconciler.RequestStandingCheck("alice"));
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = StandingOutput("banned");
		await reconciler.ReconcileOnce(CancellationToken.None); // dispatch the forced check
		await reconciler.ReconcileOnce(CancellationToken.None); // settle: still banned, already quarantined

		Assert.Single(broker.Published, p => string.Equals(p.Type, "account.standing_alert", StringComparison.Ordinal));
		Assert.True(reconciler.GetOrchestrationView("alice")!.StandingQuarantined);
	}

	[Fact]
	public async Task GiftAccept_TaskFailed_MarksDeviation()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None); // scan
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None); // settle → accept dispatch

		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Failed, "steam declined");
		await reconciler.ReconcileOnce(CancellationToken.None); // settle the failed accept

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Equal("gift offer 111 accept outcome: Failed steam declined", view.LastDeviation);
	}

	[Fact]
	public async Task GiftAccept_TaskFailed_WithLostDecision_MarksUnavailableOfferDeviation()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None); // scan
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None); // settle → accept dispatch (AcceptingOffer set)

		// The decision is lost before the settle (an assignment sweep or a
		// spec bump cleared the bookkeeping): the failed accept must mark the
		// deviation without an offer id — the empty interpolation slot reads
		// as the double space between "offer" and "accept". The pending queue
		// is cleared with it, so the settle pass drains nothing and the
		// deviation survives to the view.
		System.Collections.IDictionary runtimes = (System.Collections.IDictionary)typeof(DesiredStateReconciler)
			.GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reconciler)!;
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		runtimeType.GetField("AcceptingOffer")!.SetValue(runtimes["alice"]!, null);
		runtimeType.GetField("TradeOffersToAccept")!.SetValue(runtimes["alice"]!, null);

		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Failed, "steam declined");
		await reconciler.ReconcileOnce(CancellationToken.None); // settle the failed accept

		Assert.Equal("gift offer  accept outcome: Failed steam declined",
			reconciler.GetOrchestrationView("alice")!.LastDeviation);
	}

	[Fact]
	public async Task GiftAccept_AcceptedOfferLost_ChainsConfirmWithZeroOfferId()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None); // scan
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None); // settle → accept dispatch (AcceptingOffer set)

		// Simulate the decision being lost between dispatch and settle (an
		// assignment sweep or a spec bump clears the bookkeeping): the settle
		// must still chain the confirmation, with a zero offer id.
		System.Collections.IDictionary runtimes = (System.Collections.IDictionary)typeof(DesiredStateReconciler)
			.GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reconciler)!;
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		runtimeType.GetField("AcceptingOffer")!.SetValue(runtimes["alice"]!, null);

		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?> { ["requires_mobile_confirmation"] = true };
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(3, jobs.Created.Count);
		Assert.Equal("confirm_trade_offer", jobs.Created[2].Action);
		Assert.Equal("0", jobs.Created[2].Payload!["trade_offer_id"]);
	}

	[Fact]
	public async Task GetFarmSummaries_ListsAccountsSorted()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		accounts.Upsert("bob", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		sessions.Update("bob", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None); // both dispatch a card-drops refresh

		IReadOnlyList<FarmAccountView> summaries = reconciler.GetFarmSummaries();
		Assert.Equal(["alice", "bob"], summaries.Select(s => s.AccountName));
		Assert.All(summaries, s => Assert.Null(s.FarmingAppId));
	}

	[Fact]
	public async Task GetFarmSummaries_QueueRefreshedAt_FollowsFarmQueueCheckedAt()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		accounts.Upsert("bob", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		sessions.Update("bob", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None); // both accounts tracked (dispatch only, no stamp yet)

		// Default bookkeeping (no refresh ever ran): the sentinel reads as null.
		IReadOnlyList<FarmAccountView> before = reconciler.GetFarmSummaries();
		Assert.All(before, s => Assert.Null(s.QueueRefreshedAt));

		// A stamped queue refresh surfaces verbatim for its account only.
		DateTimeOffset stamped = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
		System.Collections.IDictionary runtimes = (System.Collections.IDictionary)typeof(DesiredStateReconciler)
			.GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reconciler)!;
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		runtimeType.GetField("FarmQueueCheckedAt")!.SetValue(runtimes["alice"]!, stamped);

		IReadOnlyList<FarmAccountView> after = reconciler.GetFarmSummaries();
		Assert.Equal(stamped, after.Single(s => s.AccountName == "alice").QueueRefreshedAt);
		Assert.Null(after.Single(s => s.AccountName == "bob").QueueRefreshedAt);
	}

	[Fact]
	public void ExtractBoostUnmet_EmptyOrMalformedTargets_ReportSafe()
	{
		AccountStore accounts = new();

		// No targets at all: nothing to boost — an empty set, not a null (null
		// would read as "unusable report" and keep retrying). The store rejects a
		// Boost account without targets, so this shape only surfaces directly.
		AccountSpec bare = accounts.Upsert("bare", true, AccountDesiredState.Online, null, null, null, null);
		Assert.Empty(DesiredStateReconciler.ExtractBoostUnmet(PlaytimeOutput((220, 1.0)), bare)!);

		// A playtimes value that is neither an array nor a list (a bare string
		// survived some round-trip): the report is unusable, not "all met".
		AccountSpec boosted = accounts.Upsert("alice", true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220, 10.0)]);
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(
			new Dictionary<string, object?> { ["playtimes"] = "garbage" }, boosted));

		// A list carrying a non-dictionary entry poisons the whole report.
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(
			new Dictionary<string, object?>
			{
				["playtimes"] = new List<object?> { "junk", new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = 1.0 } }
			}, boosted));
	}

	[Fact]
	public void ExtractBoostUnmet_HoursRawShapes_BoxedDoubleJsonNumberAndMissing()
	{
		AccountStore accounts = new();
		AccountSpec spec = accounts.Upsert("alice", true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220, 10.0)]);

		// Boxed double and long hours read straight from the dictionary.
		Assert.Empty(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = 10.0 } }
		}, spec)!);
		Assert.Empty(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = 10L } }
		}, spec)!);

		// A JSON number hours (nested round-trip) parses through the element arm.
		System.Text.Json.JsonElement jsonHours =
			System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("10.0");
		Assert.Empty(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = jsonHours } }
		}, spec)!);

		// A missing hours key is unreadable — the report is poisoned.
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220L } }
		}, spec));
	}

	[Fact]
	public void ExtractBoostUnmet_NumberCoercion_CoversAllValueShapes()
	{
		AccountStore accounts = new();
		AccountSpec spec = accounts.Upsert("alice", true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220, 10.0)]);

		// int app id with an int hour value.
		Assert.Equal([220u], DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220, ["hours"] = 5 } }
		}, spec));

		// String hour values parse in the invariant culture.
		Assert.Empty(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = "10.5" } }
		}, spec)!);

		// SQLite round-trip: both values arrive as JsonElement numbers.
		Dictionary<string, object?> roundTripped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
			System.Text.Json.JsonSerializer.Serialize(PlaytimeOutput((220, 10.0))))!;
		Assert.Empty(DesiredStateReconciler.ExtractBoostUnmet(roundTripped, spec)!);

		// An unreadable hour value poisons the report (conservative).
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 220L, ["hours"] = true } }
		}, spec));

		// A missing app id is unusable even next to a valid reading.
		Assert.Null(DesiredStateReconciler.ExtractBoostUnmet(new Dictionary<string, object?>
		{
			["playtimes"] = new List<object?> { new Dictionary<string, object?> { ["hours"] = 1.0 } }
		}, spec));
	}

	[Fact]
	public async Task PlaytimeSettle_StrippedTasks_ReturnsQuietly()
	{
		AccountStore accounts = NewBoostStore("alice", [(220, 10.0)]);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// The outcome row vanished (store recovered without tasks): the settle
		// stamps the refresh interval and returns quietly — no deviation storm.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.StripTasks("job-1");
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created); // no retry storm inside the refresh interval
		Assert.Null(reconciler.GetOrchestrationView("alice")!.LastDeviation);
	}

	[Fact]
	public async Task TradeScanSettle_StrippedTasks_ReturnsQuietly()
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
		jobs.StripTasks("job-1");
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Single(jobs.Created);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.TradeOffersToAccept);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.LastDeviation);
	}

	[Fact]
	public async Task AcceptSettle_StrippedTasks_DrainsQueueAndReturnsQuietly()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// The accept's outcome row vanished: the decision is drained whatever
		// the outcome (never spun on within this cycle) and the settle is quiet.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.StripTasks("job-2");
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Empty(reconciler.GetOrchestrationView("alice")!.TradeOffersToAccept!);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.LastDeviation);
	}

	[Fact]
	public async Task AcceptSettle_Failed_MarksDeviationAndDrainsQueue()
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
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Failed, "steam rejected");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Contains("accept outcome", view.LastDeviation, StringComparison.Ordinal);
		Assert.Contains("steam rejected", view.LastDeviation, StringComparison.Ordinal);
		Assert.Empty(view.TradeOffersToAccept!); // drained — retried only after the next re-detect
	}

	[Fact]
	public async Task AcceptFinished_MissingConfirmationFlag_DoesNotChainConfirm()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// The accept output omits requires_mobile_confirmation entirely — read
		// as false, no confirm job chains (a missing flag never chains).
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = new Dictionary<string, object?>(); // no flag key at all
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(2, jobs.Created.Count);
	}

	[Fact]
	public async Task ConfirmSettle_Failed_MarksDeviation()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Scan → accept → the chained confirm fails: its outcome shows up as a
		// deviation (the accept itself already went through).
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{"requires_mobile_confirmation": true}""")!; // SQLite round-trip shape reads as true
		await reconciler.ReconcileOnce(CancellationToken.None);
		Assert.Equal(3, jobs.Created.Count); // confirm chained via the JsonElement flag

		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Failed, "confirmation expired");
		await reconciler.ReconcileOnce(CancellationToken.None);

		AccountOrchestrationView view = reconciler.GetOrchestrationView("alice")!;
		Assert.Contains("trade mobile confirmation outcome", view.LastDeviation, StringComparison.Ordinal);
		Assert.Contains("confirmation expired", view.LastDeviation, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ConfirmSettle_Finished_LeavesQuietly()
	{
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [Partner64]));
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		// Scan → accept → chained confirm finishes cleanly: no deviation, the
		// policy loop is fully drained.
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-1-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-1-0"] = TradeOutput(("111", "456", 0, IsOurs: false));
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-2-0"] = (JobTaskStatus.Finished, null);
		jobs.Outputs["task-2-0"] = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{"requires_mobile_confirmation": true}""")!;
		await reconciler.ReconcileOnce(CancellationToken.None);
		jobs.Outcomes["task-3-0"] = (JobTaskStatus.Finished, null);
		await reconciler.ReconcileOnce(CancellationToken.None);

		Assert.Equal(3, jobs.Created.Count);
		Assert.Null(reconciler.GetOrchestrationView("alice")!.LastDeviation);
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
