using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Read-path tests: null-targets fallback and cross-job status aggregation.</summary>
public sealed partial class SqliteJobStoreTests
{
	[Fact]
	public async Task GetJob_NullTargetsJson_FallsBackToEmptyTargets()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, null),
			cts.Token);

		// JSON 'null' in the row drives the Deserialize to null — the store must
		// fall back to an empty target list instead of handing null to Job.
		RawExec(store, "UPDATE jobs SET targets_json = 'null' WHERE id = $id", ("$id", created.Job.Id));

		JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
		Assert.NotNull(refreshed.Job.Targets);
		Assert.Empty(refreshed.Job.Targets);
	}

	[Fact]
	public async Task GetTaskStatusCounts_AggregatesAcrossAllJobs()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
		await store.CreateJob(new CreateJobRequest("ping", "local", ["acct-2"], null, null), cts.Token);

		// Job 1 task: claim + finish. Job 2 task stays queued.
		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);
		await store.SetTaskResult(
			new TaskResult(claimed!.Id, true, null, null, DateTimeOffset.UtcNow, claimed.Attempt),
			cts.Token);

		IReadOnlyDictionary<JobTaskStatus, int> counts = await store.GetTaskStatusCounts(cts.Token);

		Assert.Equal(1, counts.GetValueOrDefault(JobTaskStatus.Queued));
		Assert.Equal(1, counts.GetValueOrDefault(JobTaskStatus.Finished));
		Assert.Equal(0, counts.GetValueOrDefault(JobTaskStatus.Running));
	}

}
