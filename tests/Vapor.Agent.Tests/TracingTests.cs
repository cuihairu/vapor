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
}
