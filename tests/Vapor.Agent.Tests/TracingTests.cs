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

	[Fact]
	public void StartExecuteSpan_HeadersWithoutTraceparent_StartsIndependentSpan()
	{
		// A non-null bag without the key takes the TryGetValue-false arm of the parse
		// chain, so no parent context is assembled. Activity.Current is cleared first:
		// StartActivity falls back to the ambient context when parentContext is default,
		// and the assertion must not be at the mercy of what the host left there.
		using var listener = new ActivityListener
		{
			ShouldListenTo = static source => source.Name == VaporAgentTracing.SourceName,
			Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
		};
		ActivitySource.AddActivityListener(listener);
		Activity? previous = Activity.Current;
		try
		{
			Activity.Current = null;
			DateTimeOffset now = DateTimeOffset.UtcNow;
			var task = new JobTask(
				"task-headers", "job-headers", "acct-1", "login", "local", null,
				JobTaskStatus.Running, 0, now, now);

			using Activity? activity = VaporAgentTracing.StartExecuteSpan(
				task, new Dictionary<string, string> { ["x-vapor-source"] = "tests" });

			Assert.NotNull(activity);
			Assert.Equal(default, activity!.ParentSpanId);
			Assert.Equal("task-headers", activity.GetTagItem("vapor.task_id"));
		}
		finally
		{
			Activity.Current = previous;
			listener.Dispose();
		}
	}

	[Fact]
	public void StartExecuteSpan_InvalidTraceparent_StartsIndependentSpan()
	{
		// The key is present but the value is garbage: TryParse fails, the third && arm
		// yields false, and the span still starts without a parent (same ambient-context
		// rationale as the test above).
		using var listener = new ActivityListener
		{
			ShouldListenTo = static source => source.Name == VaporAgentTracing.SourceName,
			Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
		};
		ActivitySource.AddActivityListener(listener);
		Activity? previous = Activity.Current;
		try
		{
			Activity.Current = null;
			DateTimeOffset now = DateTimeOffset.UtcNow;
			var task = new JobTask(
				"task-badtrace", "job-badtrace", "acct-1", "login", "local", null,
				JobTaskStatus.Running, 0, now, now);

			using Activity? activity = VaporAgentTracing.StartExecuteSpan(
				task, new Dictionary<string, string> { ["traceparent"] = "garbage" });

			Assert.NotNull(activity);
			Assert.Equal(default, activity!.ParentSpanId);
			Assert.Equal("task-badtrace", activity.GetTagItem("vapor.task_id"));
		}
		finally
		{
			Activity.Current = previous;
			listener.Dispose();
		}
	}

	[Fact]
	public void StartExecuteSpan_ValidTraceparent_ContinuesParentTrace()
	{
		// A well-formed dispatch header parents the execution span: the trace id
		// continues the dispatch trace and the parent span id points at its span.
		// The explicit parent context wins over any ambient Activity.Current, so no
		// ambient clearing is needed here.
		using var listener = new ActivityListener
		{
			ShouldListenTo = static source => source.Name == VaporAgentTracing.SourceName,
			Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
		};
		ActivitySource.AddActivityListener(listener);
		try
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			var task = new JobTask(
				"task-parented", "job-parented", "acct-1", "login", "local", null,
				JobTaskStatus.Running, 0, now, now);

			using Activity? activity = VaporAgentTracing.StartExecuteSpan(
				task,
				new Dictionary<string, string>
				{
					["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
				});

			Assert.NotNull(activity);
			Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", activity!.TraceId.ToString());
			Assert.Equal("00f067aa0ba902b7", activity.ParentSpanId.ToString());
		}
		finally
		{
			listener.Dispose();
		}
	}
}
