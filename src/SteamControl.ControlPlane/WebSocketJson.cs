using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public static class WebSocketJson {
	public static async Task<T> Receive<T>(WebSocket socket, CancellationToken cancellationToken) {
		ArrayBufferWriter<byte> buffer = new();
		byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);

		try {
			while (true) {
				var result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close) {
					throw new IOException("websocket closed");
				}

				buffer.Write(new ReadOnlySpan<byte>(chunk, 0, result.Count));
				if (result.EndOfMessage) {
					break;
				}
			}

			return JsonSerializer.Deserialize<T>(buffer.WrittenSpan, JsonDefaults.Options) ?? throw new InvalidOperationException("invalid JSON message");
		} finally {
			ArrayPool<byte>.Shared.Return(chunk);
		}
	}

	public static async Task Send<T>(WebSocket socket, T value, CancellationToken cancellationToken) {
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
		await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
	}
}
