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
}
