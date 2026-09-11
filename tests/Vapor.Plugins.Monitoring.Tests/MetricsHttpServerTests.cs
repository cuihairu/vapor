using System.Net.Http;
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
