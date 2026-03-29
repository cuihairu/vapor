using System.Net.WebSockets;
using System.Text;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class AgentRegistryTests {
	[Fact]
	public void RegionsAndListAreReturnedInDeterministicOrder() {
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(new AgentHello("agent-b", "us-east", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-a", "eu-west", null, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-c", "us-east", null, null), new NoopWebSocket(), cts.Token);

		Assert.Equal(new[] { "eu-west", "us-east" }, registry.Regions());
		Assert.Equal(new[] { "agent-a", "agent-b", "agent-c" }, registry.List().Select(a => a.AgentId).ToArray());
	}

	[Fact]
	public void PickWithActionReturnsFirstCapableAgentInRegion() {
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
	public void PickWithActionTreatsMissingCapabilitiesAsSupportsAll() {
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(new AgentHello("agent-a", "local", null, null), new NoopWebSocket(), cts.Token);

		ConnectedAgent? picked = registry.Pick("local", "redeem_key");

		Assert.NotNull(picked);
		Assert.Equal("agent-a", picked!.Hello.AgentId);
	}

	[Fact]
	public void PickReturnsNullWhenNoCapableAgentExists() {
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();

		registry.Register(
			new AgentHello("agent-a", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		Assert.Null(registry.Pick("local", "login"));
		Assert.Null(registry.Pick("missing-region"));
	}

	private sealed class NoopWebSocket : WebSocket {
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort() {
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
			return Task.CompletedTask;
		}

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
			return Task.CompletedTask;
		}

		public override void Dispose() {
		}

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.AsSpan());
			return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.Span);
			return ValueTask.FromResult(new ValueWebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
			return Task.CompletedTask;
		}

		public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
			return ValueTask.CompletedTask;
		}
	}
}
