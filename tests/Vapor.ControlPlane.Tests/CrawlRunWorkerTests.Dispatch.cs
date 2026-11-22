using System.Text.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Dispatch-path tests: one-shot plan sharding, shard outcome persistence, per-app error rows, empty/explicit-account/overlap handling and whole-shard failures.</summary>
public sealed partial class CrawlRunWorkerTests
{
	[Fact]
	public async Task OneShotPlan_DispatchesShardedJobsWithCrawlMeta()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, "eu", null, null);
		_accounts.Upsert("bob", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 1, 2, 3, 4, 5 }, shardSize: 2);

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);

		Assert.Equal(3, _jobs.Created.Count); // ceil(5/2) per pool round-robin → 3 shards
		Assert.All(_jobs.Created, job =>
		{
			Assert.Equal("get_game_info_batch", job.Action);
			Assert.Equal("crawl", job.Meta!["origin"]);
			Assert.Equal("plan-1", job.Meta["crawl_plan_id"]);
			Assert.False(string.IsNullOrEmpty(job.Meta["crawl_run_id"]));
		});
		Assert.Equal(1, worker.RunsTriggered);
		Assert.Equal(3, worker.TasksDispatched);
		Assert.Contains(_events.Published, e => e.Type == "crawl.run_triggered");
	}

	[Fact]
	public async Task ShardOutcomes_PersistPerAppRowsAndCounters()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch

		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 }, (730, "Game 730 not found or store request failed"));

		await worker.RunTickAsync(CancellationToken.None); // poll & settle

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		var okRow = rows.Single(r => r.Ok);
		Assert.Equal(570U, okRow.AppId);
		Assert.Equal("alice", okRow.Account);
		Assert.Equal("game-570", okRow.Data!.Value.GetProperty("name").GetString());
		var failRow = rows.Single(r => !r.Ok);
		Assert.Equal(730U, failRow.AppId);
		Assert.Contains("not found", failRow.Error);

		Assert.Equal(1, worker.AppsSucceeded);
		Assert.Equal(1, worker.AppsFailed);
		Assert.Equal(1, worker.RunsCompleted);
	}

	[Fact]
	public async Task TerminalJobWithNoTasks_SynthesizesFailedShard()
	{
		// A terminal job whose task list is empty: the poll synthesizes a failed
		// task ("job has no tasks") whose non-null error then flows into the
		// per-app failure rows.
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		JobWithTasks dispatched = _jobs.CreatedJobs.Single();
		_jobs.GetJobReplies.Enqueue(dispatched with { Job = dispatched.Job with { Status = JobStatus.Failed }, Tasks = [] });

		await worker.RunTickAsync(CancellationToken.None); // poll

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		CrawlResultRow row = Assert.Single(rows);
		Assert.False(row.Ok);
		Assert.Equal("job has no tasks", row.Error);
	}

	[Fact]
	public async Task TerminalFailedTaskWithoutError_FallsBackToStatusText()
	{
		// A failed task carrying no error string: the shard-wide failure rows
		// use the "crawl task ended with status {Status}" fallback text.
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Failed, null);

		await worker.RunTickAsync(CancellationToken.None); // poll

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		CrawlResultRow row = Assert.Single(rows);
		Assert.False(row.Ok);
		Assert.Equal($"crawl task ended with status {JobTaskStatus.Failed}", row.Error);
	}

	[Fact]
	public async Task ErrorRows_MissingOrNonStringError_PersistNullErrorText()
	{
		// Error entries whose "error" property is absent or not a string must
		// persist with a null error text instead of throwing on the read.
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
		{
			["games"] = Array.Empty<JsonElement>(),
			["errors"] = new object[]
			{
				new { app_id = 570 },                 // no "error" property at all
				new { app_id = 730, error = 123 }     // "error" present but not a string
			}
		});
		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => { Assert.False(r.Ok); Assert.Null(r.Error); });
	}

	[Fact]
	public async Task EmptyPool_SkipsRunWithEventAndClearsCursor()
	{
		await SeedOneShotPlanAsync(accounts: "ghost-account");

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch → empty pool, run registered

		Assert.Empty(_jobs.Created);
		Assert.Empty(_events.Published); // skipped only settles on the next poll

		await worker.RunTickAsync(CancellationToken.None); // poll → completes as skipped

		var skipped = Assert.Single(_events.Published, e => e.Type == "crawl.run_skipped");
		Assert.Equal("skipped", skipped.Payload!["status"]);
		Assert.Null((await _crawl.GetPlanAsync("plan-1"))!.NextRunAt);
	}

	[Fact]
	public async Task ExplicitAccounts_FilterOutDisabledOnes()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		_accounts.Upsert("carol", false, AccountDesiredState.Offline, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730, 400 }, accounts: "alice,carol");

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);

		// carol is declared but disabled — every shard must land on alice.
		Assert.All(_jobs.Created, job => Assert.Equal("alice", job.Targets.Single()));
		Assert.Single(_jobs.Created.Select(j => j.Targets.Single()).Distinct());
	}

	[Fact]
	public async Task OverlappingRun_OnlyCountsTheSkipWithoutDuplicateDispatch()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		// Recurring so the cursor stays due; the first run never settles in this test.
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 }, intervalSeconds: 60);

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch run 1
		int jobsAfterFirst = _jobs.Created.Count;

		await worker.RunTickAsync(CancellationToken.None); // plan still in flight

		Assert.Equal(jobsAfterFirst, _jobs.Created.Count); // no duplicate dispatch
		Assert.Equal(1, worker.OverlapSkips);
		Assert.Equal(0, _events.Published.Count(e => e.Type == "crawl.run_skipped")); // no event spam
	}

	[Fact]
	public async Task VanishedJob_IsRecordedAsWholeShardFailure()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730, 400 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Drop(jobId);

		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(3, rows.Count);
		Assert.All(rows, r => Assert.Equal("job not found", r.Error));
	}

	[Fact]
	public async Task WholeShardTaskFailure_RecordsEveryDispatchedApp()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730, 400 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Failed, "agent lost");

		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(3, rows.Count);
		Assert.All(rows, r => { Assert.False(r.Ok); Assert.Equal("agent lost", r.Error); });
		// Every app failed → the run settles as "failed", not "completed".
		Assert.Equal("failed", _events.Published.Single(e => e.Type == "crawl.run_completed").Payload!["status"]);
	}

	[Fact]
	public async Task DispatchFailure_MarksRunFailedAndSettlesOneShotCursor()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_jobs.ThrowOnCreate = true;

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);

		Assert.Empty(_jobs.Created);
		Assert.Equal(1, worker.RunsFailed);
		Assert.Contains(_audit.Entries, e => e.Action == "crawl.run_failed");
		Assert.Null((await _crawl.GetPlanAsync("plan-1"))!.NextRunAt);
	}

	[Fact]
	public async Task PartialDispatchFailure_KeepsRunInFlightForSettling()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 }, shardSize: 1); // 2 shards

		var worker = CreateWorker();
		_jobs.ThrowOnCreateWhen = request => _jobs.Created.Count == 1; // second dispatch throws

		await worker.RunTickAsync(CancellationToken.None);

		Assert.Single(_jobs.Created); // first shard dispatched
		Assert.Equal(0, worker.RunsFailed);   // dispatch failure with in-flight shards ≠ failed run
		Assert.Equal(0, worker.RunsCompleted); // the run stays in flight, waiting to settle
	}

	[Fact]
	public async Task PlannerWarnings_DoNotBlockDispatch()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		// The override points outside the pool: the planner warns and falls back
		// to round-robin instead of dropping the app.
		var plan = new CrawlPlan(
			Id: "plan-1", Name: "warn", AppIds: new uint[] { 570, 730 },
			Accounts: null, Overrides: new Dictionary<uint, string> { [570] = "ghost" },
			ShardSize: 50, IntervalMs: 100, Cc: "us", Cron: null, IntervalSeconds: 0,
			Enabled: true, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
			NextRunAt: DateTimeOffset.UtcNow.AddMilliseconds(-10));
		await _crawl.UpsertPlanAsync(plan);

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);

		Assert.Equal(1, worker.RunsTriggered);
		Assert.Single(_jobs.Created); // both apps dispatched on alice despite the warning
		Assert.Contains("570", _jobs.Created.Single().Payload!["app_ids"]!.ToString(), StringComparison.Ordinal);
		Assert.Contains("730", _jobs.Created.Single().Payload!["app_ids"]!.ToString(), StringComparison.Ordinal);
	}

}
