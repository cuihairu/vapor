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

	private static string _latestRunId(FakeCrawlJobStore jobs) =>
		jobs.Created.Select(j => j.Meta!["crawl_run_id"]).Last();
}

internal sealed class FakeCrawlAuditStore : IAuditStore
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

internal sealed class FakeCrawlJobStore : IJobStore
{
	private readonly Dictionary<string, JobWithTasks> _jobs = new();

	public List<CreateJobRequest> Created { get; } = [];
	public List<JobWithTasks> CreatedJobs { get; } = [];
	public Dictionary<string, (JobTaskStatus Status, string? Error)> Outcomes { get; } = new();
	public Dictionary<string, JsonElement> Outputs { get; } = new();
	public List<string> Cancelled { get; } = [];
	public bool ThrowOnCreate { get; set; }
	public Func<CreateJobRequest, bool>? ThrowOnCreateWhen { get; set; }

	public void Drop(string jobId) => _jobs.Remove(jobId);

	public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
	{
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
		return Task.FromResult(jobWithTasks);
	}

	public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
	{
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
				Output = Outputs.TryGetValue(jobId, out JsonElement output)
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
	public List<(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload)> Published { get; } = [];

	public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload) => Published.Add((jobId, type, payload));

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
