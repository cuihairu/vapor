using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Trade offer loop tests: scan, evaluation, auto-accept, confirm chaining, settle guards.</summary>
public sealed partial class DesiredStateReconcilerTests
{
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
}
