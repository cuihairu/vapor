using System.Diagnostics;
using Vapor.Agent;
using Vapor.Protocol;
using Xunit;

namespace Vapor.Agent.Tests;

public class TracingTests
{
	[Fact]
	public void StartExecuteSpan_WithoutListener_ReturnsNull()
	{
		// No ActivityListener is attached in the test host, so the source is inert and
		// StartActivity yields null — the span helpers must pass that through untouched.
		DateTimeOffset now = DateTimeOffset.UtcNow;
		var task = new JobTask(
			"task-1", "job-1", "acct-1", "login", "local", null,
			JobTaskStatus.Running, 0, now, now);

		Assert.Null(VaporAgentTracing.StartExecuteSpan(task, traceHeaders: null));
	}

	[Fact]
	public void StartExecuteSpan_WithListener_ReturnsActivityWithTaskTags()
	{
		// With a matching listener attached the source is live: StartActivity yields a
		// real span and the task fields land as vapor.* tags. Lives in this class so it
		// runs serially with the listener-free test above (xunit serializes a class).
		using var listener = new ActivityListener
		{
			ShouldListenTo = static source => source.Name == VaporAgentTracing.SourceName,
			Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStarted = null,
			ActivityStopped = null
		};
		ActivitySource.AddActivityListener(listener);
		try
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			var task = new JobTask(
				"task-traced", "job-traced", "acct-1", "login", "local", null,
				JobTaskStatus.Running, 0, now, now);

			Activity? activity = VaporAgentTracing.StartExecuteSpan(task, traceHeaders: null);

			Assert.NotNull(activity);
			Assert.Equal("task.execute", activity.OperationName);
			Assert.Equal("task-traced", activity.GetTagItem("vapor.task_id"));
			Assert.Equal("job-traced", activity.GetTagItem("vapor.job_id"));
			activity.Stop();
		}
		finally
		{
			listener.Dispose();
		}
	}
}
