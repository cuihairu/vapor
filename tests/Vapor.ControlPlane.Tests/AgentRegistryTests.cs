using System.Net.WebSockets;
using System.Text;
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
