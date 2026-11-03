using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class AgentRegistryTests
{
	[Fact]
	public void RegionsAndListAreReturnedInDeterministicOrder()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(new AgentHello("agent-b", "us-east", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-a", "eu-west", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-c", "us-east", null, null), new NoopWebSocket(), cts.Token);

		Assert.Equal(new[] { "eu-west", "us-east" }, registry.Regions());
		Assert.Equal(new[] { "agent-a", "agent-b", "agent-c" }, registry.List().Select(a => a.AgentId).ToArray());
	}

	[Fact]
	public void Pick_NoAgentInRegion_ReturnsNull()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(
			new AgentHello("agent-a", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		// Same agent, same action — only the region differs, so the candidate filter drops it.
		Assert.Null(registry.Pick("eu-west", "ping"));
		Assert.NotNull(registry.Pick("local", "ping"));
	}

	[Fact]
	public void PickWithActionReturnsFirstCapableAgentInRegion()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(
			new AgentHello("agent-b", "local", new Dictionary<string, bool> { ["login"] = false, ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);
		registry.Register(
			new AgentHello("agent-a", "local", new Dictionary<string, bool> { ["LOGIN"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		ConnectedAgent? picked = registry.Pick("local", "login");

		Assert.NotNull(picked);
		Assert.Equal("agent-a", picked!.Hello.AgentId);
	}

	[Fact]
	public void PickWithActionTreatsMissingCapabilitiesAsSupportsAll()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), cts.Token);

		ConnectedAgent? picked = registry.Pick("local", "redeem_key");

		Assert.NotNull(picked);
		Assert.Equal("agent-a", picked!.Hello.AgentId);
	}

	[Fact]
	public void PickReturnsNullWhenNoCapableAgentExists()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(
			new AgentHello("agent-a", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		Assert.Null(registry.Pick("local", "login"));
		Assert.Null(registry.Pick("missing-region"));
	}

	[Fact]
	public void PickByRegion_ReturnsLowestAgentId()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(new AgentHello("agent-b", "us-east", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-a", "us-east", null, null), new NoopWebSocket(), cts.Token);

		ConnectedAgent? picked = registry.Pick("us-east");

		Assert.NotNull(picked);
		Assert.Equal("agent-a", picked!.Hello.AgentId);
	}

	[Fact]
	public void Pick_WithLoad_OrdersByLeastLoadedThenAgentId()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(new AgentHello("agent-c", "local", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-b", "local", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), cts.Token);

		var load = new Dictionary<string, int> { ["agent-a"] = 2, ["agent-b"] = 2, ["agent-c"] = 1 };

		// The least-loaded candidate wins over the lowest id.
		ConnectedAgent? picked = registry.Pick("local", action: null, load: load);

		Assert.NotNull(picked);
		Assert.Equal("agent-c", picked!.Hello.AgentId);

		load["agent-c"] = 2; // All tied — the id tie-break decides.
		Assert.Equal("agent-a", registry.Pick("local", action: null, load: load)!.Hello.AgentId);
	}

	[Fact]
	public void Pick_WithLoad_TreatsAgentsMissingFromTheSnapshotAsUnloaded()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		// agent-a is absent from the load snapshot (never attributed anything).
		registry.Register(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-b", "local", null, null), new NoopWebSocket(), cts.Token);

		var load = new Dictionary<string, int> { ["agent-b"] = 3 };

		ConnectedAgent? picked = registry.Pick("local", action: null, load: load);

		Assert.NotNull(picked);
		Assert.Equal("agent-a", picked!.Hello.AgentId);
	}

	[Fact]
	public void PickWithAction_LeastLoadedWinsOverTheLowerIdAmongCapableAgents()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		// agent-a supports everything (no capability map) but is already busy.
		registry.Register(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(
			new AgentHello("agent-b", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		var load = new Dictionary<string, int> { ["agent-a"] = 5, ["agent-b"] = 0 };

		ConnectedAgent? picked = registry.Pick("local", "ping", load);

		Assert.NotNull(picked);
		Assert.Equal("agent-b", picked!.Hello.AgentId);
	}

	[Fact]
	public void PickWithAction_OrdersMultipleCapableCandidatesByAgentId()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		// Two capable candidates in the region: the ordering selector must run for each.
		registry.Register(new AgentHello("agent-c", "local", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-b", "local", new Dictionary<string, bool> { ["ping"] = true }, null), new NoopWebSocket(), cts.Token);

		ConnectedAgent? picked = registry.Pick("local", "ping");

		Assert.NotNull(picked);
		Assert.Equal("agent-b", picked!.Hello.AgentId);
	}

	[Fact]
	public async Task SendLoop_CompletesQuietlyWhenChannelClosesOrSocketFails()
	{
		// A socket whose sends fail: the send pump must swallow the failure instead
		// of crashing the process, then exit when the channel closes.
		var failing = new ConnectedAgent(new AgentHello("agent-a", "local", null, null), new ThrowingWebSocket());
		failing.EnqueueTaskCancel(new TaskCancel("task-1", 1, DateTimeOffset.UtcNow));
		failing.StartSendLoop(CancellationToken.None);
		await Task.Delay(100); // give the pump time to attempt (and fail) the send

		var closing = new ConnectedAgent(new AgentHello("agent-b", "local", null, null), new NoopWebSocket());
		CompleteSendChannel(closing);
		closing.StartSendLoop(CancellationToken.None);
		await Task.Delay(100); // WaitToReadAsync returns false; the loop exits cleanly

		// No exception escaping this test is the assertion.
	}

	[Fact]
	public void EnqueueTask_WhenQueueIsFull_RejectsWithoutSilentlyDroppingOldest()
	{
		// ARCH-04: the send queue must surface back-pressure instead of silently
		// dropping the oldest message. With capacity 1 and the send loop stopped,
		// the first task is accepted, the second is rejected, and the counter rises.
		var agent = new ConnectedAgent(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), sendQueueCapacity: 1);

		Assert.True(agent.EnqueueTask(new JobTask("task-1", "job-1", "target-1", "ping", "local", null, JobTaskStatus.Queued, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
		Assert.False(agent.EnqueueTask(new JobTask("task-2", "job-1", "target-1", "ping", "local", null, JobTaskStatus.Queued, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

		Assert.Equal(1, agent.SendQueueRejected);
	}

	[Fact]
	public void EnqueueTaskCancel_WhenQueueIsFull_RejectsAndIncrementsCounter()
	{
		// ARCH-04 cancel-message scenario: a queued cancel that cannot be written
		// must not be silently dropped — the caller sees false and the counter rises.
		var agent = new ConnectedAgent(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), sendQueueCapacity: 1);

		Assert.True(agent.EnqueueTaskCancel(new TaskCancel("task-1", 1, DateTimeOffset.UtcNow)));
		Assert.False(agent.EnqueueTaskCancel(new TaskCancel("task-2", 1, DateTimeOffset.UtcNow)));

		Assert.Equal(1, agent.SendQueueRejected);
	}

	[Fact]
	public void EnqueueTask_DoesNotDropOldestMessageWhenQueueIsFull()
	{
		// The accepted task stays in the channel; the rejected one is not written.
		// Drain the channel after the rejected write and confirm only the accepted
		// task remains — DropOldest would have evicted it.
		var agent = new ConnectedAgent(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), sendQueueCapacity: 1);
		var first = new JobTask("task-1", "job-1", "target-1", "ping", "local", null, JobTaskStatus.Queued, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		var second = new JobTask("task-2", "job-1", "target-1", "ping", "local", null, JobTaskStatus.Queued, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

		Assert.True(agent.EnqueueTask(first));
		Assert.False(agent.EnqueueTask(second));

		Channel<WSMessage> channel = SendChannelOf(agent);
		Assert.True(channel.Reader.TryRead(out WSMessage? msg));
		Assert.Equal("task-1", msg!.Task!.Id);
		Assert.False(channel.Reader.TryRead(out _));
	}

	[Fact]
	public void EnqueueTask_RejectsWhenQueueCapacityIsExhausted_WithoutDroppingPriorMessages()
	{
		// Capacity 3, no consumer: fills exactly to capacity, the 4th write is
		// rejected, and all 3 accepted messages remain in order.
		var agent = new ConnectedAgent(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), sendQueueCapacity: 3);
		for (int i = 1; i <= 3; i++)
		{
			Assert.True(agent.EnqueueTask(MakeTask(i)));
		}
		Assert.False(agent.EnqueueTask(MakeTask(4)));

		Channel<WSMessage> channel = SendChannelOf(agent);
		for (int i = 1; i <= 3; i++)
		{
			Assert.True(channel.Reader.TryRead(out WSMessage? msg));
			Assert.Equal($"task-{i}", msg!.Task!.Id);
		}
		Assert.False(channel.Reader.TryRead(out _));
		Assert.Equal(1, agent.SendQueueRejected);
	}

	[Fact]
	public void Constructor_RejectsNonPositiveQueueCapacity()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new ConnectedAgent(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), sendQueueCapacity: 0));
	}

	[Fact]
	public void TaskBufferCapacity_IsParsedFromHelloMeta()
	{
		var bounded = new ConnectedAgent(
			new AgentHello("agent-a", "local", null, new Dictionary<string, string> { ["task_buffer_capacity"] = "16" }),
			new NoopWebSocket());
		var unbounded = new ConnectedAgent(new AgentHello("agent-b", "local", null, null), new NoopWebSocket());
		var invalid = new ConnectedAgent(
			new AgentHello("agent-c", "local", null, new Dictionary<string, string> { ["task_buffer_capacity"] = "nope" }),
			new NoopWebSocket());

		Assert.Equal(16, bounded.TaskBufferCapacity);
		Assert.Equal(17, bounded.MaxInFlight);
		Assert.Null(unbounded.TaskBufferCapacity);
		Assert.Null(unbounded.MaxInFlight);
		Assert.Null(invalid.TaskBufferCapacity);
		Assert.Null(invalid.MaxInFlight);
	}

	[Fact]
	public void AcceptsInFlight_StopsAtBufferCapacityPlusOneRunningTask()
	{
		var agent = new ConnectedAgent(
			new AgentHello("agent-a", "local", null, new Dictionary<string, string> { ["task_buffer_capacity"] = "1" }),
			new NoopWebSocket());
		var zero = new Dictionary<string, int>();
		var one = new Dictionary<string, int> { ["agent-a"] = 1 };
		var two = new Dictionary<string, int> { ["agent-a"] = 2 };

		Assert.True(agent.AcceptsInFlight(null));
		Assert.True(agent.AcceptsInFlight(zero));
		Assert.True(agent.AcceptsInFlight(one));
		Assert.False(agent.AcceptsInFlight(two));
	}

	private static JobTask MakeTask(int i) =>
		new($"task-{i}", "job-1", "target-1", "ping", "local", null, JobTaskStatus.Queued, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

	private static Channel<WSMessage> SendChannelOf(ConnectedAgent agent)
	{
		return (Channel<WSMessage>)typeof(ConnectedAgent)
			.GetField("_send", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(agent)!;
	}

	private static void CompleteSendChannel(ConnectedAgent agent)
	{
		var channel = (System.Threading.Channels.Channel<WSMessage>)typeof(ConnectedAgent)
			.GetField("_send", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(agent)!;
		channel.Writer.Complete();
	}

	private sealed class ThrowingWebSocket : NoopWebSocket
	{
		public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			throw new IOException("socket gone");
		}
	}

	private class NoopWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public override void Dispose()
		{
		}

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
		{
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.AsSpan());
			return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		{
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.Span);
			return ValueTask.FromResult(new ValueWebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
		{
			return ValueTask.CompletedTask;
		}
	}
}
