using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

public sealed class HostActionExecutorTests
{
	[Fact]
	public async Task ExecuteAsync_RefusesTaskTargetedAtAnotherAgent()
	{
		var action = new PluginListAction("/tmp/plugins", null);
		var task = CreateTask("agent:other-agent");

		(bool success, string? error, _) = await HostActionExecutor.ExecuteAsync(
			action, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Contains("agent:other-agent", error);
		Assert.Contains("this-agent", error);
	}

	[Fact]
	public async Task ExecuteAsync_RunsHostActionWithoutSession()
	{
		var action = new PluginListAction("/tmp/plugins", null);
		var task = CreateTask("agent:this-agent");

		(bool success, string? error, IReadOnlyDictionary<string, object?>? output) = await HostActionExecutor.ExecuteAsync(
			action, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		Assert.Null(error);
		Assert.NotNull(output);
		Assert.Equal(0, output!["count"]);
	}

	[Fact]
	public async Task ExecuteAsync_WrapsUnexpectedFailureAsFailedResult()
	{
		var throwing = new ThrowingHostAction();
		var task = CreateTask("agent:this-agent");

		(bool success, string? error, _) = await HostActionExecutor.ExecuteAsync(
			throwing, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Equal("exploded", error);
	}

	[Fact]
	public async Task ExecuteAsync_PropagatesCancellationInsteadOfWrappingIt()
	{
		var canceling = new CancelingHostAction();
		var task = CreateTask("agent:this-agent");

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostActionExecutor.ExecuteAsync(
			canceling, task, "this-agent", NullLogger.Instance, new CancellationToken(canceled: true)));
	}

	[Fact]
	public async Task ExecuteAsync_EnforcesDeclaredTimeout_WithStructuredResult()
	{
		var hanging = new HangingHostAction();
		var task = CreateTask("agent:this-agent");

		(bool success, string? error, IReadOnlyDictionary<string, object?>? output) = await HostActionExecutor.ExecuteAsync(
			hanging, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Equal("action timeout", error);
		Assert.Null(output);
	}

	[Fact]
	public async Task ExecuteAsync_NoDeclaredTimeout_NeverCancelsOnItsOwn()
	{
		var unbounded = new WaitingHostAction();
		var task = CreateTask("agent:this-agent");

		using var callerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		(bool success, string? error, _) = await HostActionExecutor.ExecuteAsync(
			unbounded, task, "this-agent", NullLogger.Instance, callerCts.Token);

		Assert.True(success);
		Assert.Null(error);
		// The executor must not have raced its own timeout: only the caller's
		// 10 s budget could have cancelled a task that declares no timeout.
		Assert.False(unbounded.WasCancelled);
	}

	private static JobTask CreateTask(string target)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new JobTask("task-1", "job-1", target, "plugin_list", "local", null, JobTaskStatus.Running, 0, now, now);
	}

	private sealed class ThrowingHostAction : IHostAction
	{
		public string Name => "boom";
		public ActionMetadata Metadata => new(Name, "throws");
		public Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("exploded");
	}

	private sealed class CancelingHostAction : IHostAction
	{
		public string Name => "canceling";
		public ActionMetadata Metadata => new(Name, "cancels");
		public Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken) =>
			throw new OperationCanceledException(cancellationToken);
	}

	private sealed class HangingHostAction : IHostAction
	{
		public string Name => "hanging";
		public ActionMetadata Metadata => new(Name, "never completes on its own", RequiresLogin: false, TimeoutSeconds: 1);

		public async Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return new ActionResult(true, null, null);
		}
	}

	private sealed class WaitingHostAction : IHostAction
	{
		public bool WasCancelled { get; private set; }

		public string Name => "waiting";
		public ActionMetadata Metadata => new(Name, "declares no timeout");

		public async Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			WasCancelled = cancellationToken.IsCancellationRequested;
			return new ActionResult(true, null, null);
		}
	}
}
