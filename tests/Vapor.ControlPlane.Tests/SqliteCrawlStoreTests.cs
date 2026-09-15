using System.Text.Json;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SqliteCrawlStoreTests : IDisposable
{
	private readonly SqliteCrawlStore _store = new(":memory:");

	public void Dispose() => _store.Dispose();

	private static CrawlPlan SamplePlan(
		string id = "plan-1",
		string? cron = null,
		int intervalSeconds = 0,
		DateTimeOffset? nextRun = null,
		IReadOnlyList<string>? accounts = null) => new(
		Id: id,
		Name: "Top free games",
		AppIds: new List<uint> { 570, 730, 400 },
		Accounts: accounts,
		Overrides: new Dictionary<uint, string> { [730] = "bob" },
		ShardSize: 50,
		IntervalMs: 500,
		Cc: "us",
		Cron: cron,
		IntervalSeconds: intervalSeconds,
		Enabled: true,
		CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(1000),
		UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(1000),
		NextRunAt: nextRun);

	[Fact]
	public async Task Plan_RoundTripsAllFields()
	{
		await _store.UpsertPlanAsync(SamplePlan(nextRun: DateTimeOffset.FromUnixTimeMilliseconds(5000)));

		var loaded = await _store.GetPlanAsync("plan-1");

		Assert.NotNull(loaded);
		Assert.Equal("Top free games", loaded!.Name);
		Assert.Equal(new uint[] { 570, 730, 400 }, loaded.AppIds);
		Assert.Null(loaded.Accounts);
		Assert.Equal("bob", loaded.Overrides![730]);
		Assert.Equal(50, loaded.ShardSize);
		Assert.Equal(500, loaded.IntervalMs);
		Assert.Equal("us", loaded.Cc);
		Assert.True(loaded.Enabled);
		Assert.Equal(5000, loaded.NextRunAt!.Value.ToUnixTimeMilliseconds());
		Assert.Equal(0, loaded.RunCount);
	}

	[Fact]
	public async Task Plan_WithExplicitAccounts_RoundTripsThem()
	{
		await _store.UpsertPlanAsync(SamplePlan(accounts: new[] { "alice", "bob" }));

		var loaded = await _store.GetPlanAsync("plan-1");

		Assert.NotNull(loaded?.Accounts);
		Assert.Equal(new[] { "alice", "bob" }, loaded!.Accounts);
	}

	[Fact]
	public async Task UpsertPlan_ReplacesDefinitionAndCursor()
	{
		await _store.UpsertPlanAsync(SamplePlan(nextRun: DateTimeOffset.FromUnixTimeMilliseconds(5000)));
		var first = await _store.GetPlanAsync("plan-1");

		await _store.UpsertPlanAsync(first! with { Name = "Renamed", NextRunAt = DateTimeOffset.FromUnixTimeMilliseconds(9000) });

		var loaded = await _store.GetPlanAsync("plan-1");
		Assert.Equal("Renamed", loaded!.Name);
		Assert.Equal(9000, loaded.NextRunAt!.Value.ToUnixTimeMilliseconds());
	}

	[Fact]
	public async Task DeletePlan_RemovesPlanButKeepsResults()
	{
		await _store.UpsertPlanAsync(SamplePlan());
		await _store.AddResultAsync(MakeResult("run-1"));

		Assert.True(await _store.DeletePlanAsync("plan-1"));
		Assert.Null(await _store.GetPlanAsync("plan-1"));

		var results = await _store.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Single(results);
	}

	[Fact]
	public async Task ClaimDuePlan_TakesOldestDuePlanAndAdvancesBookkeeping()
	{
		await _store.UpsertPlanAsync(SamplePlan("plan-a", nextRun: DateTimeOffset.FromUnixTimeMilliseconds(2000)));
		await _store.UpsertPlanAsync(SamplePlan("plan-b", nextRun: DateTimeOffset.FromUnixTimeMilliseconds(1000)));

		var claimed = await _store.ClaimDuePlanAsync("run-42");

		Assert.NotNull(claimed);
		Assert.Equal("plan-b", claimed!.Id); // earlier due timestamp wins
		Assert.Equal(1, claimed.RunCount);
		Assert.Equal("run-42", claimed.LastRunId);

		// The due cursor stays until the worker settles the run — a re-claim
		// (crash recovery, overlapping worker without in-flight gating) still
		// sees the same plan, never silently skipping to the next one.
		Assert.Equal("plan-b", (await _store.ClaimDuePlanAsync("run-43"))!.Id);

		// Simulate the worker completing the run: one-shot clears the cursor.
		await _store.SetNextRunAsync("plan-b", null);

		var second = await _store.ClaimDuePlanAsync("run-44");
		Assert.Equal("plan-a", second!.Id);

		await _store.SetNextRunAsync("plan-a", null);
		Assert.Null(await _store.ClaimDuePlanAsync("run-45"));
	}

	[Fact]
	public async Task ClaimDuePlan_SkipsDisabledPlans()
	{
		await _store.UpsertPlanAsync(SamplePlan(nextRun: DateTimeOffset.FromUnixTimeMilliseconds(1000)));
		await _store.SetPlanEnabledAsync("plan-1", enabled: false);

		Assert.Null(await _store.ClaimDuePlanAsync("run-1"));
	}

	[Fact]
	public async Task SetNextRun_ClearsCursorForOneShots()
	{
		await _store.UpsertPlanAsync(SamplePlan(nextRun: DateTimeOffset.FromUnixTimeMilliseconds(1000)));

		await _store.SetNextRunAsync("plan-1", null);

		var loaded = await _store.GetPlanAsync("plan-1");
		Assert.Null(loaded!.NextRunAt);
	}

	[Fact]
	public async Task Results_QueryFiltersAndPaging()
	{
		await _store.UpsertPlanAsync(SamplePlan());
		await _store.AddResultAsync(MakeResult("run-1", appId: 570, account: "alice", ok: true));
		await _store.AddResultAsync(MakeResult("run-1", appId: 730, account: "bob", ok: false, error: "boom"));
		await _store.AddResultAsync(MakeResult("run-2", appId: 570, account: "alice", ok: true));

		var byRun = await _store.QueryResultsAsync(new CrawlResultQuery(RunId: "run-1"));
		Assert.Equal(2, byRun.Count);

		var failures = await _store.QueryResultsAsync(new CrawlResultQuery(Ok: false));
		var failed = Assert.Single(failures);
		Assert.Equal(730U, failed.AppId);
		Assert.False(failed.Ok);
		Assert.Equal("boom", failed.Error);

		var byAccount = await _store.QueryResultsAsync(new CrawlResultQuery(Account: "bob"));
		Assert.Single(byAccount);

		Assert.Equal(3, await _store.CountResultsAsync(new CrawlResultQuery()));

		var paged = await _store.QueryResultsAsync(new CrawlResultQuery(Limit: 2, Offset: 1));
		Assert.Equal(2, paged.Count);
	}

	[Fact]
	public async Task Results_DataJsonSurvivesRoundTrip()
	{
		await _store.UpsertPlanAsync(SamplePlan());
		var payload = JsonDocument.Parse("""{"name":"Dota 2","is_free":true}""").RootElement.Clone();
		await _store.AddResultAsync(MakeResult("run-1", data: payload));

		var loaded = Assert.Single(await _store.QueryResultsAsync(new CrawlResultQuery()));

		Assert.NotNull(loaded.Data);
		Assert.Equal("Dota 2", loaded.Data!.Value.GetProperty("name").GetString());
		Assert.True(loaded.Data.Value.GetProperty("is_free").GetBoolean());
	}

	[Fact]
	public async Task ListRuns_AggregatesNewestFirstWithOkCounts()
	{
		await _store.UpsertPlanAsync(SamplePlan());
		// run-1: two ok, one failed. run-2 (later timestamps): one ok.
		await _store.AddResultAsync(MakeResult("run-1", fetchedAtMs: 1000, ok: true));
		await _store.AddResultAsync(MakeResult("run-1", fetchedAtMs: 1010, ok: true));
		await _store.AddResultAsync(MakeResult("run-1", fetchedAtMs: 1020, ok: false, error: "x"));
		await _store.AddResultAsync(MakeResult("run-2", fetchedAtMs: 2000, ok: true));

		var runs = await _store.ListRunsAsync("plan-1");

		Assert.Equal(2, runs.Count);
		Assert.Equal("run-2", runs[0].RunId);
		Assert.Equal(1, runs[0].Total);
		Assert.Equal(1, runs[0].Ok);
		Assert.Equal("run-1", runs[1].RunId);
		Assert.Equal(3, runs[1].Total);
		Assert.Equal(2, runs[1].Ok);
		Assert.Equal(1, runs[1].Failed);
		Assert.Equal(2000, runs[0].FinishedAt!.Value.ToUnixTimeMilliseconds());
	}

	[Fact]
	public async Task PruneRuns_KeepsNewestNRunsPerPlan()
	{
		await _store.UpsertPlanAsync(SamplePlan());
		for (int run = 1; run <= 3; run++)
		{
			await _store.AddResultAsync(MakeResult($"run-{run}", fetchedAtMs: run * 1000));
		}

		int pruned = await _store.PruneRunsAsync("plan-1", keepRuns: 2);

		Assert.Equal(1, pruned);
		var remaining = await _store.QueryResultsAsync(new CrawlResultQuery());
		Assert.Equal(2, remaining.Count);
		Assert.All(remaining, r => Assert.NotEqual("run-1", r.RunId)); // oldest run dropped

		// Pruning again is idempotent (nothing older than the window).
		Assert.Equal(0, await _store.PruneRunsAsync("plan-1", keepRuns: 2));
	}

	private static CrawlResultRow MakeResult(
		string runId,
		uint appId = 570,
		string account = "alice",
		bool ok = true,
		string? error = null,
		long fetchedAtMs = 1000,
		JsonElement? data = null) => new(
		Id: 0,
		PlanId: "plan-1",
		RunId: runId,
		AppId: appId,
		Account: account,
		JobId: "job-1",
		Ok: ok,
		Error: error,
		Data: data,
		FetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs));
}
