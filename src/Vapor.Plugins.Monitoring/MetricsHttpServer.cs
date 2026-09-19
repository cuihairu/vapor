using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vapor.Plugins.Monitoring;

/// <summary>
/// Minimal HTTP/1.1 responder that serves the Prometheus exposition payload. Uses a raw
/// <see cref="TcpListener"/> instead of Kestrel so a plugin can host (and fully release)
/// a metrics endpoint without pulling a web server into its load context.
/// </summary>
public sealed class MetricsHttpServer : IDisposable
{
	private const int MaxRequestBytes = 8 * 1024;

	private readonly string _host;
	private readonly string _path;
	private readonly Func<string> _payloadProvider;
	private readonly ILogger? _logger;
	private readonly CancellationTokenSource _cts = new();
	private TcpListener? _listener;
	private Task? _acceptLoop;

	public MetricsHttpServer(string host, int port, string path, Func<string> payloadProvider, ILogger? logger = null)
	{
		if (string.IsNullOrWhiteSpace(host))
		{
			throw new ArgumentException("Host must not be empty", nameof(host));
		}

		if (port is < 0 or > 65535)
		{
			throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535");
		}

		_host = host;
		Port = port;
		_path = NormalizePath(path);
		_payloadProvider = payloadProvider ?? throw new ArgumentNullException(nameof(payloadProvider));
		_logger = logger;
	}

	/// <summary>The actually bound port (useful when constructed with port 0 for an ephemeral port).</summary>
	public int Port { get; private set; }

	/// <summary>Whether the accept loop is running.</summary>
	public bool IsRunning => _listener is not null;

	/// <summary>Starts the listener. Idempotent.</summary>
	public void Start()
	{
		if (_listener is not null)
		{
			return;
		}

		var listener = new TcpListener(IPAddress.Parse(_host), Port);
		listener.Start();
		Port = ((IPEndPoint)listener.LocalEndpoint).Port;
		_listener = listener;
		_acceptLoop = Task.Run(AcceptLoopAsync);
	}

	/// <summary>Stops the listener and all pending work. Idempotent.</summary>
	public void Stop()
	{
		_cts.Cancel();
		_listener?.Stop();
		_listener = null;
	}

	public void Dispose()
	{
		_listener?.Dispose(); // release the socket even when Stop() never ran (CA2213)
		_listener = null;
		Stop();
		_cts.Dispose();
	}

	private async Task AcceptLoopAsync()
	{
		var listener = _listener!;
		while (!_cts.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (SocketException ex) when (!_cts.IsCancellationRequested)
			{
				_logger?.LogDebug(ex, "Metrics server accept error");
				continue;
			}
			catch (ObjectDisposedException)
			{
				break;
			}

			_ = Task.Run(() => HandleClientAsync(client, _cts.Token), _cts.Token);
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using var disposedClient = client;
		try
		{
			using var stream = client.GetStream();
			var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
			if (requestLine is null)
			{
				return;
			}

			var parts = requestLine.Split(' ');
			var method = parts.Length > 0 ? parts[0] : string.Empty;
			var rawUrl = parts.Length > 1 ? parts[1] : string.Empty;
			var path = rawUrl;
			var queryIndex = rawUrl.IndexOf('?', StringComparison.Ordinal);
			if (queryIndex >= 0)
			{
				path = rawUrl[..queryIndex];
			}

			if (!string.Equals(path, _path, StringComparison.Ordinal))
			{
				await WriteResponseAsync(stream, method, 404, "text/plain; charset=utf-8", "not found\n", cancellationToken).ConfigureAwait(false);
				return;
			}

			await WriteResponseAsync(stream, method, 200, "text/plain; version=0.0.4; charset=utf-8", _payloadProvider(), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
		{
			// Client went away or server is shutting down; nothing to do.
		}
	}

	private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var buffer = new byte[MaxRequestBytes];
		var sb = new StringBuilder(128);
		while (sb.Length < MaxRequestBytes)
		{
			var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				return null;
			}

			sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
			var text = sb.ToString();
			var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
			if (lineEnd >= 0)
			{
				return text[..lineEnd];
			}
		}

		return null;
	}

	private static async Task WriteResponseAsync(
		NetworkStream stream,
		string method,
		int statusCode,
		string contentType,
		string body,
		CancellationToken cancellationToken)
	{
		var statusText = statusCode == 200 ? "OK" : "Not Found";
		var bodyBytes = Encoding.UTF8.GetBytes(body);
		var headOnly = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

		var sb = new StringBuilder(192);
		sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(statusText).Append("\r\n");
		sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
		sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
		sb.Append("Connection: close\r\n\r\n");

		var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
		await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
		if (!headOnly)
		{
			await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
		}

		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static string NormalizePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return "/metrics";
		}

		path = path.Trim();
		return path.StartsWith('/') ? path : "/" + path;
	}
}
