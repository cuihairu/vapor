using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Covers the synthesized positional members of the internal dispatch-result
/// record (callers unpack it via deconstruction when polling account tasks).
/// </summary>
public sealed class AccountTaskRunnerTests
{
	[Fact]
	public void TaskRunResult_Deconstruct_ExposesPositionalMembers()
	{
		var output = new Dictionary<string, object?> { ["listings"] = 3 };
		var result = new TaskRunResult("job-1", "read_market_listings", JobTaskStatus.Finished, output, null);

		var (jobId, action, status, resultOutput, error) = result;

		Assert.Equal("job-1", jobId);
		Assert.Equal("read_market_listings", action);
		Assert.Equal(JobTaskStatus.Finished, status);
		Assert.Same(output, resultOutput);
		Assert.Null(error);
	}

	[Fact]
	public void TaskRunResult_Equality_RoundsTripThroughClone()
	{
		var result = new TaskRunResult("job-1", "login", JobTaskStatus.Queued, null, null);

		Assert.Equal(result, result with { });
		Assert.NotEqual(result, result with { Action = "ping" });
	}

	[Fact]
	public async Task DispatchAsync_TaskRowNotYetVisibleOnFirstPoll_KeepsWaitingUntilItFinishes()
	{
		// First poll: the job exists but the runner's task row is not visible yet
		// (store lag). Second poll: the task is Finished — the loop must keep
		// waiting across the missing row instead of failing the dispatch.
		var store = new FakeCrawlJobStore();
		TimeSpan savedWindow = AccountTaskRunner.WaitWindow;
		TimeSpan savedInterval = AccountTaskRunner.PollInterval;
		AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(5);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(1);
		try
		{
			// DispatchAsync creates "job-1"; the first GetJob reply hides the task row.
			var job = new Job("job-1", "login", null, ["acct"], new Dictionary<string, string>(), JobStatus.Queued,
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			store.GetJobReplies.Enqueue(new JobWithTasks(job, []));
			store.Outcomes["job-1"] = (JobTaskStatus.Finished, null);

			TaskRunResult result = await AccountTaskRunner.DispatchAsync(
				store, "login", "acct", new Dictionary<string, object?> { ["minutes"] = 1 }, CancellationToken.None);

			Assert.Equal("job-1", result.JobId);
			Assert.Equal(JobTaskStatus.Finished, result.Status);
		}
		finally
		{
			AccountTaskRunner.WaitWindow = savedWindow;
			AccountTaskRunner.PollInterval = savedInterval;
		}
	}
}
