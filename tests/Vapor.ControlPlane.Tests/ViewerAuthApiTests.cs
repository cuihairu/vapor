using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// End-to-end behavior of the read-only viewer key (demo console access):
/// a safe-method request carrying the viewer key reads like an admin request
/// (the edge middleware upgrades it before the endpoint's own auth check),
/// while any write still sees the raw viewer token and is rejected with 401 —
/// including the agent tunnel, whose agent-key check the upgraded header can
/// never satisfy. Controls pin the unchanged no-key and admin-key behavior.
/// </summary>
public sealed class ViewerAuthApiTests
{
	[Fact]
	public async Task GetAgents_WithViewerKey_Returns200()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory, "viewer-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/agents");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		JsonElement body = await ReadJson(response);
		Assert.Equal(JsonValueKind.Array, body.GetProperty("agents").ValueKind);
	}

	[Fact]
	public async Task GetAgents_WithViewerKeyViaQuery_Returns200()
	{
		// SSE consumers authenticate through the ?authorization= query parameter
		// (EventSource cannot set headers); the edge upgrade must see it too.
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/agents?authorization=viewer-token");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task CreateJob_WithViewerKey_IsRejected()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory, "viewer-token");

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/jobs",
			new { action = "ping", region = "demo-west", targets = new[] { "demo-alpha" } });

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task PutGlobalConfig_WithViewerKey_IsRejected()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory, "viewer-token");

		using HttpResponseMessage response = await client.PutAsJsonAsync("/v1/config/global",
			new { settings = new Dictionary<string, object?>() });

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task AgentTunnel_WithViewerKey_IsRejected()
	{
		// The upgrade swaps the viewer token for the admin key, which is never
		// an agent key — the tunnel stays closed to the read-only credential.
		await using var factory = CreateFactory();
		using var client = CreateClient(factory, "viewer-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/agent/ws?agentId=agent-1&region=demo-west");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetAgents_WithoutKey_IsRejected()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/agents");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task GetAgents_WithAdminKey_Returns200()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory, "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/agents");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

	private static HttpClient CreateClient(TestFactory factory, string? bearer = null)
	{
		HttpClient client = factory.CreateClient();
		if (bearer is not null)
		{
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		}

		return client;
	}

	private static TestFactory CreateFactory() => new();

	private sealed class TestFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.AddSingleton(new Config("admin-token",
					new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null },
					":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:",
					ViewerApiKey: "viewer-token"));
			});
		}
	}
}
