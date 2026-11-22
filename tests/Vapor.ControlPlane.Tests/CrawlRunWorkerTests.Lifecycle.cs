using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Run-lifecycle tests: completion event/audit/cursor settlement, recurring and cron cursor advance, and old-run pruning.</summary>
public sealed partial class CrawlRunWorkerTests
{
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
	public async Task CronRecurringRun_AdvancesNextRunCursorFromCron()
	{
		// The cron arm of the recurring predicate: a plan carrying a cron is
		// recurring even with IntervalSeconds = 0, and its cursor advances to
		// the next cron occurrence instead of clearing like a one-shot.
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 }, cron: "0 3 * * *");

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);

		var plan = await _crawl.GetPlanAsync("plan-1");
		Assert.NotNull(plan!.NextRunAt); // cursor stays due (next 03:00), never cleared
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

}
