using Xunit;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;

namespace Vapor.Mcp.Tests;

public sealed class VaporReadToolsTests
{
	private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(responder(request));
		}
	}

	private static string SentPath(StubHandler handler)
		=> Assert.Single(handler.Requests).RequestUri!.ToString();

	private static ControlPlaneClient Client(StubHandler handler)
		=> new("http://cp.test", "key", new HttpClient(handler));

	[Fact]
	public async Task ListAgents_ProxiesTheAgentsEndpoint()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"agents":[{"id":"agent-1","region":"eu"}]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.ListAgents(client, CancellationToken.None);

		Assert.Equal("http://cp.test/v1/agents", SentPath(handler));
		Assert.Contains("agent-1", output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AgentStatus_ReturnsTheMatchingRow()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""
				{"agents":[
					{"id":"agent-1","region":"eu","connected":true,"connectedAt":"2026-11-14T00:00:00Z"},
					{"id":"agent-2","region":"us","connected":false}
				]}
				"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.AgentStatus(client, "AGENT-1", CancellationToken.None);

		Assert.Equal("http://cp.test/v1/agents/status", SentPath(handler));
		using JsonDocument doc = JsonDocument.Parse(output);
		Assert.Equal("agent-1", doc.RootElement.GetProperty("id").GetString());
		Assert.True(doc.RootElement.GetProperty("connected").GetBoolean());
	}

	[Fact]
	public async Task AgentStatus_SkipsMalformedIdEntries()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""
				{"agents":[
					{"region":"eu"},
					{"id":5},
					{"id":"agent-2"},
					{"id":"agent-1","connected":true}
				]}
				"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.AgentStatus(client, "agent-1", CancellationToken.None);

		using JsonDocument doc = JsonDocument.Parse(output);
		Assert.True(doc.RootElement.GetProperty("connected").GetBoolean());
	}

	[Fact]
	public async Task AgentStatus_UnknownAgent_ThrowsWithHint()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"agents":[{"id":"agent-1"}]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(
			() => VaporReadTools.AgentStatus(client, "ghost", CancellationToken.None));

		Assert.Contains("ghost", error.Message, StringComparison.Ordinal);
		Assert.Contains("not connected", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AgentStatus_UnexpectedEnvelope_Throws()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"status":[]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		await Assert.ThrowsAsync<McpException>(
			() => VaporReadTools.AgentStatus(client, "agent-1", CancellationToken.None));
	}

	[Fact]
	public async Task ListAccounts_PassesFiltersAndSanitizesOutput()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""
				{"accounts":[{"accountName":"alice","desiredState":"online","enabled":true,"password":"never"}]}
				"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.ListAccounts(client, " online ", "eu", "agent-1", CancellationToken.None);

		Assert.Equal("http://cp.test/v1/accounts?state=online&region=eu&agent=agent-1", SentPath(handler));
		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement account = Assert.Single(doc.RootElement.GetProperty("accounts").EnumerateArray());
		Assert.Equal("alice", account.GetProperty("accountName").GetString());
		Assert.False(account.TryGetProperty("password", out _));
	}

	[Fact]
	public async Task ListAccounts_WithoutFilters_UsesBarePath()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"accounts":[]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.ListAccounts(client, null, null, null, CancellationToken.None);

		Assert.Equal("http://cp.test/v1/accounts", SentPath(handler));
		Assert.Equal("""{"accounts":[]}""", output);
	}

	[Fact]
	public async Task ListPlugins_ProxiesTheInventoryEndpoint()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"agents":[{"agentId":"agent-1","plugins":[{"id":"monitoring"}]}]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.ListPlugins(client, CancellationToken.None);

		Assert.Equal("http://cp.test/v1/plugins/installed", SentPath(handler));
		Assert.Contains("monitoring", output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PluginStatus_ReturnsMatchingRowsAcrossAgents()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""
				{"agents":[
					{"agentId":"agent-1","plugins":[{"id":"csgo"},{"id":"monitoring","version":"1.0"}]},
					{"agentId":"agent-2"},
					{"agentId":"agent-3","plugins":"not-an-array"},
					{"agentId":"agent-4","plugins":[{"id":7},{"id":"MONITORING","version":"1.1"}]}
				]}
				"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.PluginStatus(client, "monitoring", CancellationToken.None);

		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement[] rows = [.. doc.RootElement.EnumerateArray()];
		Assert.Equal(2, rows.Length);
		Assert.Equal("1.0", rows[0].GetProperty("version").GetString());
		Assert.Equal("1.1", rows[1].GetProperty("version").GetString());
	}

	[Fact]
	public async Task PluginStatus_UnknownPlugin_Throws()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"agents":[{"agentId":"agent-1","plugins":[{"id":"csgo"}]}]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(
			() => VaporReadTools.PluginStatus(client, "ghost-plugin", CancellationToken.None));

		Assert.Contains("ghost-plugin", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PluginStatus_UnexpectedEnvelope_Throws()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"installed":[]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		await Assert.ThrowsAsync<McpException>(
			() => VaporReadTools.PluginStatus(client, "monitoring", CancellationToken.None));
	}

	[Fact]
	public async Task RecentEvents_DefaultLimitAndNoFilters()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"logs":[],"total":0}"""),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.RecentEvents(client, null, null, null, null, CancellationToken.None);

		Assert.Equal("http://cp.test/v1/audit/logs?limit=20", SentPath(handler));
		Assert.Equal("""{"logs":[],"total":0}""", output);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(-5, 1)]
	[InlineData(1, 1)]
	[InlineData(250, 250)]
	[InlineData(501, 500)]
	public async Task RecentEvents_ClampsLimit(int requested, int expected)
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"logs":[]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		await VaporReadTools.RecentEvents(client, requested, null, null, null, CancellationToken.None);

		Assert.Equal($"http://cp.test/v1/audit/logs?limit={expected}", SentPath(handler));
	}

	[Fact]
	public async Task RecentEvents_PassesFiltersAndSkipsBlankOnes()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"logs":[]}"""),
		});
		using ControlPlaneClient client = Client(handler);

		await VaporReadTools.RecentEvents(client, 40, "account.enabled", " alice ", "", CancellationToken.None);

		Assert.Equal(
			"http://cp.test/v1/audit/logs?limit=40&action=account.enabled&account=alice",
			SentPath(handler));
	}

	[Fact]
	public async Task MetricsSnapshot_ProxiesPrometheusText()
	{
		string metricsText = "# HELP vapor_controlplane_agents_connected Currently connected agents.\n# TYPE vapor_controlplane_agents_connected gauge\nvapor_controlplane_agents_connected 2\n";
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(metricsText),
		});
		using ControlPlaneClient client = Client(handler);

		string output = await VaporReadTools.MetricsSnapshot(client, CancellationToken.None);

		Assert.Equal("http://cp.test/metrics", SentPath(handler));
		Assert.Equal(metricsText, output);
	}
}
