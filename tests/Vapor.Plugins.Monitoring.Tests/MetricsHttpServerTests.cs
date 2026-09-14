using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Xunit;
using Vapor.Plugins.Monitoring;

namespace Vapor.Plugins.Monitoring.Tests;

public class MetricsHttpServerTests : IDisposable
{
	private readonly List<MetricsHttpServer> _servers = [];

	private MetricsHttpServer StartServer(Func<string> payloadProvider, string path = "/metrics")
	{
		// Port 0 lets the OS pick a free ephemeral port, keeping tests collision-free.
		var server = new MetricsHttpServer("127.0.0.1", 0, path, payloadProvider);
		server.Start();
		_servers.Add(server);
		return server;
	}

	[Fact]
	public void Constructor_EmptyHost_Throws()
	{
		Assert.Throws<ArgumentException>(() => new MetricsHttpServer("   ", 0, "/metrics", () => "x\n"));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(65536)]
	public void Constructor_PortOutOfRange_Throws(int port)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new MetricsHttpServer("127.0.0.1", port, "/metrics", () => "x\n"));
	}

	[Fact]
	public async Task Start_WithEphemeralPort_BindsAndReportsPort()
	{
		var server = StartServer(() => "hello\n");

		Assert.True(server.IsRunning);
		Assert.NotEqual(0, server.Port);
	}

	[Fact]
	public async Task Get_MetricsPath_ReturnsExpositionPayload()
	{
		var server = StartServer(() => "vapor_test_metric 1\n");

		using var client = new HttpClient();
		var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/metrics");

		Assert.True(response.IsSuccessStatusCode);
		Assert.Equal("text/plain; version=0.0.4; charset=utf-8", response.Content.Headers.ContentType?.ToString());
		Assert.Equal("vapor_test_metric 1\n", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Get_OtherPath_Returns404()
	{
		var server = StartServer(() => "x\n");

		using var client = new HttpClient();
		var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/other");

		Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Get_WithQueryString_ServesMetricsPath()
	{
		// Scrapers append query strings (?target=...); the server must strip them and
		// still match the metrics path instead of answering 404.
		var server = StartServer(() => "vapor_query_metric 3\n");

		using var client = new HttpClient();
		var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/metrics?scraper=test&x=1");

		Assert.True(response.IsSuccessStatusCode);
		Assert.Equal("vapor_query_metric 3\n", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Constructor_BlankPath_DefaultsToMetricsPath()
	{
		// An empty path in the constructor is normalized to the canonical /metrics route.
		var server = StartServer(() => "vapor_default_metric 4\n", path: "   ");

		using var client = new HttpClient();
		var response = await client.GetAsync($"http://127.0.0.1:{server.Port}/metrics");

		Assert.True(response.IsSuccessStatusCode);
		Assert.Equal("vapor_default_metric 4\n", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Disconnect_BeforeRequestLine_ServerKeepsServing()
	{
		// A scrape attempt that drops the connection before sending a request line must be
		// treated as an empty request: no response, no crash, endpoint stays usable.
		var server = StartServer(() => "x\n");

		using (var client = new TcpClient())
		{
			await client.ConnectAsync(IPAddress.Loopback, server.Port);
		}

		// Give the server side a moment to observe the disconnect.
		await Task.Delay(100);

		using var httpClient = new HttpClient();
		var response = await httpClient.GetAsync($"http://127.0.0.1:{server.Port}/metrics");
		Assert.True(response.IsSuccessStatusCode);
	}

	[Fact]
	public async Task OversizedRequestWithoutLine_ConnectionClosedWithoutResponse()
	{
		// 8 KiB of bytes with no CRLF hits the reader's cap: the request is abandoned
		// without a response and the server keeps serving subsequent clients.
		var server = StartServer(() => "x\n");

		using (var client = new TcpClient())
		{
			await client.ConnectAsync(IPAddress.Loopback, server.Port);
			using var stream = client.GetStream();
			var junk = new byte[8 * 1024];
			Array.Fill(junk, (byte)'a');
			await stream.WriteAsync(junk);
			await stream.FlushAsync();
		}

		await Task.Delay(200);

		using var httpClient = new HttpClient();
		var response = await httpClient.GetAsync($"http://127.0.0.1:{server.Port}/metrics");
		Assert.True(response.IsSuccessStatusCode);
	}

	[Fact]
	public async Task Stop_WhileConnectionParked_CancelsHandlerQuietly()
	{
		// A client parked in the request-line read when the server stops must be cancelled
		// through the shutdown token, never surfacing an exception from the handler task.
		var server = StartServer(() => "x\n");

		var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, server.Port);

		server.Stop();
		await Task.Delay(100);

		client.Dispose();
		Assert.False(server.IsRunning);
	}

	[Fact]
	public async Task Head_MetricsPath_OmitsBody()
	{
		var server = StartServer(() => "vapor_head_metric 2\n");

		using var request = new HttpRequestMessage(HttpMethod.Head, $"http://127.0.0.1:{server.Port}/metrics");
		using var client = new HttpClient();
		var response = await client.SendAsync(request);

		Assert.True(response.IsSuccessStatusCode);
		Assert.Empty(await response.Content.ReadAsByteArrayAsync());
	}

	[Fact]
	public async Task Stop_ReleasesPort()
	{
		var server = StartServer(() => "x\n");
		var port = server.Port;

		server.Stop();
		Assert.False(server.IsRunning);

		// The freed port should be bindable again by a new listener.
		var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
		listener.Start();
		listener.Stop();
	}

	[Fact]
	public void Start_Twice_IsIdempotent()
	{
		var server = StartServer(() => "x\n");
		var port = server.Port;

		server.Start();

		Assert.Equal(port, server.Port);
		Assert.True(server.IsRunning);
	}

	public void Dispose()
	{
		foreach (var server in _servers)
		{
			server.Dispose();
		}
	}
}
