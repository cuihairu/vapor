using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class WebSocketJsonTests
{
	[Fact]
	public async Task Receive_NullJsonPayload_ThrowsInvalidOperation()
	{
		// "null" is well-formed JSON that deserializes to a null reference: the
		// receive contract treats it as an invalid message instead of handing a
		// null WSMessage to the WS handlers.
		var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
		listener.Listen(1);

		var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		clientSocket.Connect(listener.LocalEndPoint!);
		using Socket serverSocket = listener.Accept();
		listener.Dispose();

		using var serverStream = new NetworkStream(serverSocket, ownsSocket: true);
		using var clientStream = new NetworkStream(clientSocket, ownsSocket: true);
		using WebSocket server = WebSocket.CreateFromStream(
			serverStream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromMinutes(2));
		using WebSocket client = WebSocket.CreateFromStream(
			clientStream, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromMinutes(2));

		byte[] payload = "null"u8.ToArray();
		await client.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => WebSocketJson.Receive<WSMessage>(server, CancellationToken.None));
		Assert.Equal("invalid JSON message", ex.Message);
	}
}
