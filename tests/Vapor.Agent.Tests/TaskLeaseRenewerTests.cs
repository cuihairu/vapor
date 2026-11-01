using Vapor.Agent;
using Vapor.Protocol;
using Xunit;

namespace Vapor.Agent.Tests;

public class TaskLeaseRenewerTests
{
	private static JobTask MakeTask(string id = "task-1", int attempt = 1) =>
		new(id, "job-1", "target-1", "ping", "local", null, JobTaskStatus.Running, attempt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

	[Fact]
	public async Task RunAsync_SendsImmediateHeartbeatAndPeriodicHeartbeats()
	{
		var sent = new List<WSMessage>();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		var renewer = new TaskLeaseRenewer(
			frame => { lock (sent) { sent.Add(frame); } return Task.CompletedTask; },
			TimeSpan.FromMilliseconds(20));

		await renewer.RunAsync(MakeTask(), cts.Token);

		Assert.True(sent.Count >= 2, $"expected immediate + periodic heartbeats, got {sent.Count}");
		Assert.All(sent, m =>
		{
			Assert.Equal("task_heartbeat", m.Type);
			Assert.NotNull(m.TaskHeartbeat);
			Assert.Equal("task-1", m.TaskHeartbeat!.TaskId);
			Assert.Equal(1, m.TaskHeartbeat.Attempt);
		});
	}

	[Fact]
	public async Task RunAsync_StopsWhenCancelled()
	{
		var sent = new List<WSMessage>();
		using var cts = new CancellationTokenSource();
		var renewer = new TaskLeaseRenewer(
			frame => { lock (sent) { sent.Add(frame); } return Task.CompletedTask; },
			TimeSpan.FromMilliseconds(10));

		var run = renewer.RunAsync(MakeTask(), cts.Token);
		await Task.Delay(80);
		cts.Cancel();
		await run;

		Assert.True(sent.Count >= 1);
	}

	[Fact]
	public async Task RunAsync_SendFailureIsSwallowedAndLoopContinues()
	{
		int calls = 0;
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
		var renewer = new TaskLeaseRenewer(
			_ => { calls++; return Task.FromException(new IOException("socket down")); },
			TimeSpan.FromMilliseconds(10));

		await renewer.RunAsync(MakeTask(), cts.Token);

		Assert.True(calls >= 1);
	}
}
