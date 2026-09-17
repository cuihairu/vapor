using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class CrawlRunWorkerTests : IDisposable
{
	private readonly SqliteCrawlStore _crawl = new(":memory:");
	private readonly AccountStore _accounts = new();
	private readonly FakeCrawlJobStore _jobs = new();
	private readonly FakeCrawlEventBroker _events = new();
	private readonly FakeCrawlAuditStore _audit = new();

	public void Dispose() => _crawl.Dispose();

	private CrawlRunWorker CreateWorker(
		int runTimeoutSeconds = 1800,
		int keepRuns = 10,
		int maxAppsPerTask = 200,
		int tickSeconds = 5) =>
		new(
			_crawl,
			_accounts,
			_jobs,
			_events,
			_audit,
			new Config(
				"admin",
				new HashSet<string>(StringComparer.Ordinal),
				":memory:",
				TaskLeaseSeconds: 300,
				EnableSwagger: false,
				AuditDbPath: ":memory:",
				CrawlWorkerTickSeconds: tickSeconds,
				CrawlKeepRuns: keepRuns,
				CrawlRunTimeoutSeconds: runTimeoutSeconds,
				CrawlMaxAppsPerTask: maxAppsPerTask),
			NullLogger<CrawlRunWorker>.Instance);

	[Fact]
	public async Task Worker_WithTickKillSwitch_CompletesWithoutScheduling()
	{
		// 0 or below is the documented kill switch: ExecuteAsync returns instead
		// of entering the tick loop, so the hosted task settles almost instantly.
		CrawlRunWorker worker = CreateWorker(tickSeconds: 0);

		await worker.StartAsync(CancellationToken.None);
		if (worker.ExecuteTask is not null)
		{
			await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
		}

		Assert.Equal(0, worker.RunsTriggered);
		Assert.Equal(0, worker.TasksDispatched);
	}

	private async Task<CrawlPlan> SeedOneShotPlanAsync(
		string id = "plan-1",
		uint[]? appIds = null,
		string? accounts = null,
		int intervalSeconds = 0,
		string? cron = null,
		int shardSize = 50)
	{
		var plan = new CrawlPlan(
			Id: id,
			Name: "test plan",
			AppIds: appIds ?? new uint[] { 570, 730, 400, 620, 440 },
			Accounts: accounts == null ? null : accounts.Split(','),
			Overrides: null,
			ShardSize: shardSize,
			IntervalMs: 100,
			Cc: "us",
			Cron: cron,
			IntervalSeconds: intervalSeconds,
			Enabled: true,
			CreatedAt: DateTimeOffset.UtcNow,
			UpdatedAt: DateTimeOffset.UtcNow,
			NextRunAt: DateTimeOffset.UtcNow.AddMilliseconds(-10)); // due

		await _crawl.UpsertPlanAsync(plan);
		return plan;
	}

	private static JsonElement BatchOutput(uint[] okApps, params (uint AppId, string Error)[] errors)
	{
		var output = new Dictionary<string, object?>
		{
			["games"] = JsonSerializer.SerializeToElement(okApps.Select(id => new { app_id = id, name = $"game-{id}" }).ToArray()),
			["errors"] = JsonSerializer.SerializeToElement(errors.Select(e => new { app_id = e.AppId, error = e.Error }).ToArray())
		};
		return JsonSerializer.SerializeToElement(output);
	}

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
	public async Task CompletedRun_PublishesEventWritesAuditAndClearsOneShotCursor()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);

		var completed = Assert.Single(_events.Published, e => e.Type == "crawl.run_completed");
		Assert.Equal("completed", completed.Payload!["status"]);
		Assert.Equal(1, completed.Payload!["ok"]);

		Assert.Contains(_audit.Entries, e => e.Action == "crawl.run_triggered");
		Assert.Contains(_audit.Entries, e => e.Action == "crawl.run_completed");

		var plan = await _crawl.GetPlanAsync("plan-1");
		Assert.Null(plan!.NextRunAt); // one-shot cursor cleared
		Assert.Equal(1, plan.RunCount);
	}

	[Fact]
	public async Task RecurringRun_AdvancesNextRunCursor()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 }, intervalSeconds: 60);

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);

		var plan = await _crawl.GetPlanAsync("plan-1");
		Assert.NotNull(plan!.NextRunAt);
		DateTimeOffset next = plan.NextRunAt!.Value;
		Assert.True(next > DateTimeOffset.UtcNow.AddSeconds(55), $"next run should be ~60s out, was {next}");
		Assert.True(next <= DateTimeOffset.UtcNow.AddSeconds(61));
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
	public async Task TimedOutRun_CancelsOutstandingJobsAndRecordsFailures()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 });

		var worker = CreateWorker(runTimeoutSeconds: 0); // deadline already lapsed on the next tick
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		string jobId = _jobs.CreatedJobs.Single().Job.Id;

		await worker.RunTickAsync(CancellationToken.None); // poll → timeout path

		Assert.Contains(jobId, _jobs.Cancelled);
		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => { Assert.False(r.Ok); Assert.Equal("crawl run timed out", r.Error); });
		Assert.Equal(1, worker.RunsTimedOut);

		var completed = Assert.Single(_events.Published, e => e.Type == "crawl.run_completed");
		Assert.Equal("timeout", completed.Payload!["status"]);
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
	public async Task ZeroTickConfig_DisablesTheWorker()
	{
		var worker = CreateWorker(tickSeconds: 0);
		await SeedOneShotPlanAsync();

		await worker.StartAsync(CancellationToken.None);
		await worker.StopAsync(CancellationToken.None);

		Assert.Empty(_jobs.Created); // loop never ran
	}

	[Fact]
	public async Task ReadJobCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.GetJobExceptions.Enqueue(new OperationCanceledException());
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// The poll path rethrows cancellation so the hosting loop can shut down.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(cts.Token));
	}

	[Fact]
	public async Task ReadJobFailure_IsSwallowedAndRunStaysInFlight()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.GetJobExceptions.Enqueue(new IOException("store hiccup"));

		await worker.RunTickAsync(CancellationToken.None); // poll → unreadable job stays in flight

		Assert.Equal(0, worker.RunsCompleted);
		Assert.Empty(await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1")));

		// The run settles normally once the job store is readable again.
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);
		Assert.Equal(1, worker.RunsCompleted);
	}

	[Fact]
	public async Task TimedOutRun_WhenCancelItselfFails_StillRecordsShardFailures()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 });

		var worker = CreateWorker(runTimeoutSeconds: 0);
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.CancelThrows = true;

		await worker.RunTickAsync(CancellationToken.None); // poll → timeout path, CancelJob throws

		// The cancel failure is logged and swallowed; the shard still records failures.
		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => Assert.Equal("crawl run timed out", r.Error));
		Assert.Equal(1, worker.RunsTimedOut);
	}

	[Fact]
	public async Task TimedOutRun_WhenCancelIsCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker(runTimeoutSeconds: 0);
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.CancelCanceledThrows = true;
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Cancellation escaping CancelJob must not be mistaken for a store
		// failure (which would swallow it) — it rethrows so the loop can stop.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(cts.Token));
	}

	[Fact]
	public async Task DispatchCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_jobs.ThrowCanceledOnCreate = true;
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var worker = CreateWorker();

		// Dispatch cancellation must escape the generic dispatch-failure handler
		// so the hosting loop breaks instead of bookkeeping a failed run.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(cts.Token));
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

	[Fact]
	public async Task AuditFailure_NeverBlocksTheRun()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_audit.ThrowOnRecord = true;

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);

		// Dispatch succeeded even though the audit write failed.
		Assert.Equal(1, worker.RunsTriggered);
		Assert.Single(_jobs.Created);
	}

	[Fact]
	public async Task BatchOutputWithoutGamesOrErrorsKeys_SettlesWithZeroRows()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = JsonSerializer.SerializeToElement(new { fetched = 0 });

		await worker.RunTickAsync(CancellationToken.None);

		Assert.Equal(1, worker.RunsCompleted);
		Assert.Empty(await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1")));
	}

	[Fact]
	public async Task BatchOutputAsDotNetObjects_ParsesGamesList()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		// In-memory output (no JSON round-trip): the games list stays a .NET list
		// of plain objects and must parse the same as the JsonElement variant.
		_jobs.RawOutputs[jobId] = new Dictionary<string, object?>
		{
			["games"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 570, ["name"] = "game-570" } }
		};

		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		var okRow = Assert.Single(rows);
		Assert.True(okRow.Ok);
		Assert.Equal(570U, okRow.AppId);
		Assert.Equal("game-570", okRow.Data!.Value.GetProperty("name").GetString());
		Assert.Equal(1, worker.AppsSucceeded);
	}

	[Fact]
	public async Task BatchOutputMixedList_PassesJsonElementsThroughUntouched()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		// A mixed list — pre-serialized JsonElements next to plain .NET objects —
		// must pass the elements through without a second serialization.
		_jobs.RawOutputs[jobId] = new Dictionary<string, object?>
		{
			["games"] = new List<object?>
			{
				JsonSerializer.SerializeToElement(new { app_id = 570, name = "game-570" }),
				new Dictionary<string, object?> { ["app_id"] = 730, ["name"] = "game-730" }
			}
		};

		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.Equal("game-570", rows.Single(r => r.AppId == 570).Data!.Value.GetProperty("name").GetString());
		Assert.Equal("game-730", rows.Single(r => r.AppId == 730).Data!.Value.GetProperty("name").GetString());
		Assert.Equal(2, worker.AppsSucceeded);
	}

	[Fact]
	public async Task KeepRuns_PrunesOldRunsOnCompletion()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);

		var worker = CreateWorker(keepRuns: 1);
		// Run 1 completes.
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);
		string first = _jobs.CreatedJobs.Single(j => j.Job.Meta!["crawl_run_id"] == _latestRunId(_jobs)).Job.Id;
		_jobs.Outcomes[first] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[first] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);

		Assert.Single(await _crawl.ListRunsAsync("plan-1"));

		// Second run for the same plan: re-arm the cursor and settle again.
		var plan = await _crawl.GetPlanAsync("plan-1");
		await _crawl.UpsertPlanAsync(plan! with { NextRunAt = DateTimeOffset.UtcNow.AddMilliseconds(-5) });
		await worker.RunTickAsync(CancellationToken.None);
		string secondJob = _jobs.CreatedJobs.Where(j => j.Job.Meta!["crawl_plan_id"] == "plan-1").OrderBy(j => j.Job.Id).Last().Job.Id;
		_jobs.Outcomes[secondJob] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[secondJob] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);

		Assert.Single(await _crawl.ListRunsAsync("plan-1")); // run 1 pruned, run 2 kept
	}

	[Fact]
	public async Task AuditCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_audit.ThrowCanceledOnRecord = true;

		var worker = CreateWorker();

		// Cancellation escaping the audit write must not be mistaken for an audit
		// failure (which would swallow it) — it rethrows so the loop can stop.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(CancellationToken.None));
	}

	[Fact]
	public async Task BrokenTick_LogsErrorAndKeepsServing()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		// Real hosting loop: a tick that throws must be logged, not kill the service.
		var worker = CreateWorker(tickSeconds: 1);
		await worker.StartAsync(CancellationToken.None);
		try
		{
			// Wait for the *published event*, not the created job: the job exists before
			// its run event is published, and arming ThrowOnPublishRuns while tick 1's
			// publish is still in flight would break the wrong tick's event instead.
			await WaitUntilAsync(() => RunEventsPublished() == 1); // tick 1: dispatch

			string jobId = _jobs.CreatedJobs[0].Job.Id;
			_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
			_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
			_events.ThrowOnPublishRuns = 1; // tick 2: completion event blows up mid-tick

			// Tick 2 fails inside CompleteRunAsync (publish throws); the cursor was never
			// settled, so tick 3 claims the still-due plan again — proof the loop survived.
			// Waiting on the second run event (not Created.Count) is what makes the
			// Contains assertion below race-free: the job is created before its event
			// is published, so Created.Count == 2 can fire mid-publish (CI flake,
			// macos Debug 2026-09-17: the assertion ran against a one-event list).
			await WaitUntilAsync(() => RunEventsPublished() == 2);

			Assert.Equal(2, worker.RunsTriggered);
			Assert.Contains(_events.Published, e => e.Type == "crawl.run_triggered"
				&& Equals(e.Payload!["run_id"], _jobs.Created[1].Meta!["crawl_run_id"]));
		}
		finally
		{
			await worker.StopAsync(CancellationToken.None);
		}

		// Reaching tick 3 already proves the loop survived the broken tick (an escaping
		// tick failure would have faulted ExecuteTask and stopped all further ticks).
		Assert.NotNull(worker.ExecuteTask);
		Assert.True(worker.ExecuteTask!.IsCompleted);
	}

	[Fact]
	public async Task StopRequestedWhileDispatchInFlight_BreaksLoopQuietly()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		// The dispatch call parks in the gate; stop is requested, then the gate opens
		// so CreateJob throws cancellation on an already-cancelled stopping token —
		// the loop must break through the OperationCanceledException arm, not the
		// generic one (which would keep the service running past shutdown).
		var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var openGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_jobs.CreateGate = _ =>
		{
			createEntered.TrySetResult();
			return openGate.Task;
		};
		_jobs.ThrowCanceledOnCreate = true;

		var worker = CreateWorker(tickSeconds: 1);
		await worker.StartAsync(CancellationToken.None);
		await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // tick 1 parked in dispatch

		Task stopTask = worker.StopAsync(CancellationToken.None); // cancels the stopping token
		openGate.TrySetResult();

		await stopTask.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.NotNull(worker.ExecuteTask);
		Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully); // broke out, did not fault
		Assert.Equal(0, worker.RunsCompleted); // the aborted run never settled
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? budget = null)
	{
		// Ticks come from a real PeriodicTimer (1s), so wait for observable state
		// instead of guessing timing; the budget only covers timer drift on slow CI.
		var deadline = DateTimeOffset.UtcNow + (budget ?? TimeSpan.FromSeconds(30));
		while (!condition())
		{
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException("Condition not reached within budget");
			}

			await Task.Delay(10);
		}
	}

	private static string _latestRunId(FakeCrawlJobStore jobs) =>
		jobs.Created.Select(j => j.Meta!["crawl_run_id"]).Last();

	private int RunEventsPublished() =>
		_events.Published.Count(e => e.Type == "crawl.run_triggered");
}

internal sealed class FakeCrawlAuditStore : IAuditStore
{
	public List<AuditEntry> Entries { get; } = [];
	public bool ThrowOnRecord { get; set; }
	/// <summary>RecordAsync throws cancellation (the stopping-token rethrow path).</summary>
	public bool ThrowCanceledOnRecord { get; set; }

	public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
	{
		if (ThrowCanceledOnRecord)
		{
			throw new OperationCanceledException();
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

internal sealed class FakeCrawlJobStore : IJobStore
{
	private readonly Dictionary<string, JobWithTasks> _jobs = new();

	public List<CreateJobRequest> Created { get; } = [];
	public List<JobWithTasks> CreatedJobs { get; } = [];
	public Dictionary<string, (JobTaskStatus Status, string? Error)> Outcomes { get; } = new();
	public Dictionary<string, JsonElement> Outputs { get; } = new();
	/// <summary>Task output handed through without a JSON round-trip (in-memory object shapes).</summary>
	public Dictionary<string, IReadOnlyDictionary<string, object?>> RawOutputs { get; } = new();
	public List<string> Cancelled { get; } = [];
	public bool ThrowOnCreate { get; set; }
	public bool ThrowCanceledOnCreate { get; set; }
	public bool CancelThrows { get; set; }
	/// <summary>CancelJob throws cancellation (the stopping-token rethrow path).</summary>
	public bool CancelCanceledThrows { get; set; }
	/// <summary>Exceptions thrown from GetJob once each (OCE → rethrow path, IOException → swallow path).</summary>
	public Queue<Exception> GetJobExceptions { get; } = new();
	public Func<CreateJobRequest, bool>? ThrowOnCreateWhen { get; set; }
	/// <summary>Awaited inside CreateJob before the throw decision (parks a dispatch mid-call).</summary>
	public Func<CreateJobRequest, Task>? CreateGate { get; set; }

	public void Drop(string jobId) => _jobs.Remove(jobId);

	public async Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
	{
		if (CreateGate is { } gate)
		{
			await gate(request);
		}

		if (ThrowCanceledOnCreate)
		{
			throw new OperationCanceledException(cancellationToken);
		}

		if (ThrowOnCreate || ThrowOnCreateWhen?.Invoke(request) == true)
		{
			throw new IOException("store unavailable");
		}

		string jobId = $"job-{Created.Count + 1}";
		DateTimeOffset now = DateTimeOffset.UtcNow;
		var job = new Job(jobId, request.Action, request.Region, request.Targets, request.Meta, JobStatus.Queued, now, now);
		var task = new JobTask(jobId + "-t1", jobId, request.Targets.FirstOrDefault() ?? "", request.Action, request.Region, request.Payload, JobTaskStatus.Queued, 1, now, now);
		var jobWithTasks = new JobWithTasks(job, [task]);
		_jobs[jobId] = jobWithTasks;
		Created.Add(request);
		CreatedJobs.Add(jobWithTasks);
		return jobWithTasks;
	}

	public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
	{
		if (GetJobExceptions.Count > 0)
		{
			throw GetJobExceptions.Dequeue();
		}

		if (!_jobs.TryGetValue(jobId, out JobWithTasks? jobWithTasks))
		{
			throw new NotFoundException("job not found");
		}

		if (Outcomes.TryGetValue(jobId, out (JobTaskStatus Status, string? Error) outcome))
		{
			JobTask task = jobWithTasks.Tasks[0] with
			{
				Status = outcome.Status,
				Error = outcome.Error,
				Output = RawOutputs.TryGetValue(jobId, out IReadOnlyDictionary<string, object?>? raw)
					? raw
					: Outputs.TryGetValue(jobId, out JsonElement output)
						? JsonSerializer.Deserialize<IReadOnlyDictionary<string, object?>>(output.GetRawText(), JsonDefaults.Options)
						: null
			};
			Job finishedJob = jobWithTasks.Job with
			{
				Status = outcome.Status switch
				{
					JobTaskStatus.Finished => JobStatus.Finished,
					JobTaskStatus.Failed => JobStatus.Failed,
					_ => JobStatus.Canceled
				}
			};
			return Task.FromResult(new JobWithTasks(finishedJob, [task]));
		}

		return Task.FromResult(jobWithTasks);
	}

	public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken)
	{
		if (CancelCanceledThrows)
		{
			throw new OperationCanceledException(cancellationToken);
		}

		if (CancelThrows)
		{
			throw new IOException("cancel unavailable");
		}

		Cancelled.Add(jobId);
		return Task.FromResult<IReadOnlyList<TaskCancel>>([]);
	}

	public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<Job>>([]);

	public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<Job>>([]);

	public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken)
		=> Task.FromResult<Job?>(null);

	public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<JobTask>>([]);

	public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => Task.FromResult<JobTask?>(null);

	public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);

	public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyDictionary<JobTaskStatus, int>>(new Dictionary<JobTaskStatus, int>());
}

internal sealed class FakeCrawlEventBroker : IEventBroker
{
	private readonly object _lock = new();
	private readonly List<(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload)> _published = [];

	/// <summary>
	/// Snapshot on read: Publish runs on the worker's background loop while
	/// assertions and WaitUntilAsync predicates enumerate concurrently.
	/// </summary>
	public IReadOnlyList<(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload)> Published
	{
		get { lock (_lock) return _published.ToArray(); }
	}

	/// <summary>Publish throws InvalidOperationException this many times before behaving again.</summary>
	public int ThrowOnPublishRuns { get; set; }

	public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
	{
		if (ThrowOnPublishRuns > 0)
		{
			ThrowOnPublishRuns--;
			throw new InvalidOperationException("event broker unavailable");
		}

		lock (_lock)
		{
			_published.Add((jobId, type, payload));
		}
	}

	public void PublishSession(string accountName, string eventType, string state, string? message = null)
	{
	}

	public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
	{
	}

	public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
	{
		await Task.CompletedTask;
		yield break;
	}

	public async IAsyncEnumerable<SessionEvent> SubscribeSessions([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
	{
		await Task.CompletedTask;
		yield break;
	}

	public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
	{
		await Task.CompletedTask;
		yield break;
	}
}
