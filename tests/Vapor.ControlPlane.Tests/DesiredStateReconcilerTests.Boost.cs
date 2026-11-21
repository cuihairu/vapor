using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Playtime boosting loop tests: get_playtime, unmet targets, settle and parse paths.</summary>
public sealed partial class DesiredStateReconcilerTests
{
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
		Assert.Single(jobs.Created, j => j.Action == "get_playtime");
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

	private static (bool Ok, uint AppId, double Hours) ReadPlaytimeEntry(object? entry)
	{
		MethodInfo? info = typeof(DesiredStateReconciler).GetMethod("TryReadPlaytimeEntry", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(info);
		object?[] args = [entry, 0u, 0.0];
		bool ok = (bool)info.Invoke(null, args)!;
		return (ok, (uint)args[1]!, (double)args[2]!);
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

	// ── round 35 reconciliation guard coverage ──
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
}
