using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Read-model tests: the orchestration view and per-account farm summaries.</summary>
public sealed partial class DesiredStateReconcilerTests
{
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
	public async Task GetFarmSummaries_CopyOnWriteLists_SurfaceBothNullAndPopulated()
	{
		// The snapshot's `?.ToList()` copy must hold on both sides of the
		// null-guard: a runtime that never populated its farm lists reads as null,
		// and one that did surfaces a stable copy (not the shared mutable list).
		AccountStore accounts = new();
		accounts.Upsert("alice", true, AccountDesiredState.Farm, null, null, null, null);
		var agents = NewRegistry(("agent-1", "us-east", null));
		var jobs = new FakeReconcileJobStore();
		var sessions = new SessionTracker();
		sessions.Update("alice", "state_changed", "Connected", null);
		using var reconciler = CreateReconciler(accounts, agents, jobs, sessions);

		await reconciler.ReconcileOnce(CancellationToken.None);

		System.Collections.IDictionary runtimes = (System.Collections.IDictionary)typeof(DesiredStateReconciler)
			.GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reconciler)!;
		Type runtimeType = typeof(DesiredStateReconciler).GetNestedType("AccountRuntime", BindingFlags.NonPublic)!;
		object alice = runtimes["alice"]!;
		runtimeType.GetField("FarmQueue")!.SetValue(alice, null);
		runtimeType.GetField("FarmCompletedApps")!.SetValue(alice, null);
		runtimeType.GetField("FarmSkippedApps")!.SetValue(alice, null);

		FarmAccountView nullView = reconciler.GetFarmSummaries().Single(s => s.AccountName == "alice");
		Assert.Null(nullView.Queue);
		Assert.Null(nullView.CompletedApps);
		Assert.Null(nullView.SkippedApps);

		// Populated side: the view hands out copies, so mutating the snapshot
		// cannot corrupt the runtime bookkeeping the reconciler writes next pass.
		var populated = new List<uint> { 100U, 200U };
		runtimeType.GetField("FarmQueue")!.SetValue(alice, populated);
		runtimeType.GetField("FarmCompletedApps")!.SetValue(alice, new List<uint> { 300U });
		runtimeType.GetField("FarmSkippedApps")!.SetValue(alice, new List<uint> { 400U });

		FarmAccountView fullView = reconciler.GetFarmSummaries().Single(s => s.AccountName == "alice");
		Assert.Equal([100U, 200U], fullView.Queue);
		Assert.Equal([300U], fullView.CompletedApps);
		Assert.Equal([400U], fullView.SkippedApps);
	}
}
