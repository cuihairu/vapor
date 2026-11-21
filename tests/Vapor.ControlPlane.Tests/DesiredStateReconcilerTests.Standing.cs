using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Standing check loop tests: summary settle, ban quarantine, request gating.</summary>
public sealed partial class DesiredStateReconcilerTests
{
	// ── round 38 P2 standing check loop ──
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
}
