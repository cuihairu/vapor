using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class PluginApiTests
{
	[Fact]
	public async Task PluginEndpoints_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage catalog = await client.GetAsync("/v1/plugins/catalog");
		using HttpResponseMessage installed = await client.GetAsync("/v1/plugins/installed");
		using HttpResponseMessage install = await client.PostAsJsonAsync("/v1/plugins/install", new { agentIds = new[] { "agent-1" } });
		using HttpResponseMessage uninstall = await client.PostAsJsonAsync("/v1/plugins/uninstall/vapor.test-plugin", new { agentIds = new[] { "agent-1" } });
		using HttpResponseMessage refresh = await client.PostAsJsonAsync("/v1/plugins/inventory/refresh", new { });

		Assert.Equal(HttpStatusCode.Unauthorized, catalog.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, installed.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, install.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, uninstall.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
	}

	[Fact]
	public async Task Catalog_NotConfigured_ReportsConfiguredFalse()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/plugins/catalog");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(doc.RootElement.GetProperty("configured").GetBoolean());
	}

	[Fact]
	public async Task Catalog_ReturnsEntriesFromIndexAndCachesThem()
	{
		var handler = new FakeIndexHandler("""{"plugins":[{"id":"vapor.beta","name":"Beta","version":"2.0.0","apiVersion":"1.0","url":"https://pkg/beta.zip","sha256":"ABC","trust":"official"},{"id":"vapor.alpha","name":"Alpha","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/a.zip","sha256":"aabb","description":"first"}]}""");
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(handler),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage first = await client.GetAsync("/v1/plugins/catalog");
		using HttpResponseMessage second = await client.GetAsync("/v1/plugins/catalog");

		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		Assert.Equal(1, handler.Calls);
		using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
		Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
		JsonElement entries = doc.RootElement.GetProperty("entries");
		Assert.Equal(2, entries.GetArrayLength());
		Assert.Equal("vapor.alpha", entries[0].GetProperty("id").GetString());
		Assert.Equal("aabb", entries[0].GetProperty("sha256").GetString());
		Assert.Equal("official", entries[1].GetProperty("trust").GetString());
	}

	[Fact]
	public async Task Catalog_FetchFailure_SurfacesErrorFieldButStill200()
	{
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler("not json at all", HttpStatusCode.InternalServerError)),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/plugins/catalog");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
	}

	[Fact]
	public async Task Catalog_InvalidCatalogBody_SurfacesErrorFieldButStill200()
	{
		// Fetch succeeds (200) but the body is not a plugin index.
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler("""{"items":[]}""")),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/plugins/catalog");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(doc.RootElement.GetProperty("configured").GetBoolean());
		Assert.Contains("not a valid catalog", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Install_RejectsMissingAgentsAndBadChecksum()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage noAgents = await client.PostAsJsonAsync("/v1/plugins/install", new { url = "https://pkg/a.zip" });
		using HttpResponseMessage noSha = await client.PostAsJsonAsync("/v1/plugins/install", new { url = "https://pkg/a.zip", agentIds = new[] { "agent-1" } });
		using HttpResponseMessage badSha = await client.PostAsJsonAsync("/v1/plugins/install", new { url = "https://pkg/a.zip", sha256 = "xyz", agentIds = new[] { "agent-1" } });
		using HttpResponseMessage neither = await client.PostAsJsonAsync("/v1/plugins/install", new { agentIds = new[] { "agent-1" } });

		Assert.Equal(HttpStatusCode.BadRequest, noAgents.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, noSha.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badSha.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);
	}

	[Fact]
	public async Task Install_CatalogMode_UnknownPluginIdReturnsNotFound()
	{
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler("""{"plugins":[]}""")),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new { pluginId = "vapor.absent", agentIds = new[] { "agent-1" } });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Install_CatalogMode_ResolvesUrlAndChecksumAndTargetsNamedAgents()
	{
		var index = """{"plugins":[{"id":"vapor.test-plugin","name":"Test","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/test.zip","sha256":"AABBCCDD"}]}""";
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler(index)),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new
		{
			pluginId = "vapor.test-plugin",
			agentIds = new[] { "agent-1", "agent-2" }
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement jobs = doc.RootElement.GetProperty("jobs");
		Assert.Equal(2, jobs.GetArrayLength());

		foreach (JsonElement job in jobs.EnumerateArray())
		{
			string jobId = job.GetProperty("jobId").GetString()!;
			JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);
			Assert.Equal("plugin_install", created.Job.Action);
			Assert.Single(created.Tasks);
			Assert.Equal($"agent:{job.GetProperty("agentId").GetString()}", created.Tasks[0].Target);

			IReadOnlyDictionary<string, object?> payload = created.Tasks[0].Payload!;
			Assert.Equal("https://pkg/test.zip", ((JsonElement)payload["url"]!).GetString());
			Assert.Equal("aabbccdd", ((JsonElement)payload["sha256"]!).GetString());
			Assert.Equal("vapor.test-plugin", ((JsonElement)payload["pluginId"]!).GetString());
		}
	}

	[Fact]
	public async Task Install_DirectMode_NormalizesShaAndPassesPayload()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new
		{
			url = "https://pkg/direct.zip",
			sha256 = new string('A', 64),
			version = "1.2.3",
			agentIds = new[] { "agent-1" }
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string jobId = doc.RootElement.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
		JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);

		IReadOnlyDictionary<string, object?> payload = created.Tasks[0].Payload!;
		Assert.Equal(new string('a', 64), ((JsonElement)payload["sha256"]!).GetString());
		Assert.Equal("1.2.3", ((JsonElement)payload["version"]!).GetString());
	}

	[Fact]
	public async Task Uninstall_ValidatesAndDispatchesByRoutePluginId()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage noAgents = await client.PostAsJsonAsync("/v1/plugins/uninstall/vapor.test-plugin", new { });
		using HttpResponseMessage dispatched = await client.PostAsJsonAsync("/v1/plugins/uninstall/vapor.test-plugin", new { agentIds = new[] { "agent-1" } });

		Assert.Equal(HttpStatusCode.BadRequest, noAgents.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, dispatched.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
		using var doc = JsonDocument.Parse(await dispatched.Content.ReadAsStringAsync());
		string jobId = doc.RootElement.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
		JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);

		Assert.Equal("plugin_uninstall", created.Job.Action);
		Assert.Equal("agent:agent-1", created.Tasks[0].Target);
	}

	[Fact]
	public async Task Uninstall_WhitespaceRouteSegmentIsRejected()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		// A route segment can carry decoded whitespace (the "%20" URL form), which
		// trims to an empty plugin id — the endpoint must reject it before dispatch.
		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/uninstall/%20", new { agentIds = new[] { "agent-1" } });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("pluginId is required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Refresh_DispatchesToRequestedAgents()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/inventory/refresh", new { agentIds = new[] { "agent-9" } });

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string jobId = doc.RootElement.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
		JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);

		Assert.Equal("plugin_list", created.Job.Action);
		Assert.Equal("agent:agent-9", created.Tasks[0].Target);
	}

	[Fact]
	public async Task Refresh_WithoutConnectedAgents_ReturnsConflict()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		// No agentIds in the request and no connected agents to default to.
		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/inventory/refresh", new { });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
	}

	[Fact]
	public async Task Refresh_PrunesStaleMirrorEntries()
	{
		// Seed the mirror with a stale agent that is no longer connected, then
		// refresh a different agent: the stale entry must drop out of the view.
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginInventory>();
			var seeded = new PluginInventory();
			seeded.Update("ghost-agent", DateTimeOffset.UnixEpoch, PluginInventoryTests.RoundTripForSeed(new Dictionary<string, object?>
			{
				["plugins"] = new List<object>
				{
					new Dictionary<string, object?>
					{
						["id"] = "vapor.old", ["name"] = "Old", ["version"] = "0.0.1", ["apiVersion"] = "1.0",
						["trust"] = null, ["permissions"] = Array.Empty<string>(), ["actions"] = Array.Empty<string>()
					}
				}
			}));
			services.AddSingleton(seeded);
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/inventory/refresh", new { agentIds = new[] { "agent-9" } });
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

		using HttpResponseMessage installed = await client.GetAsync("/v1/plugins/installed");
		using var doc = JsonDocument.Parse(await installed.Content.ReadAsStringAsync());
		foreach (JsonElement agent in doc.RootElement.GetProperty("agents").EnumerateArray())
		{
			Assert.NotEqual("ghost-agent", agent.GetProperty("agentId").GetString());
		}
	}

	[Fact]
	public async Task Installed_ReturnsEmptyMirrorUntilPluginTasksReport()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/plugins/installed");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(0, doc.RootElement.GetProperty("agents").GetArrayLength());
	}

	[Fact]
	public async Task Installed_ReturnsSeededMirrorEntries()
	{
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginInventory>();
			var seeded = new PluginInventory();
			seeded.Update("ghost-agent", DateTimeOffset.UnixEpoch, PluginInventoryTests.RoundTripForSeed(new Dictionary<string, object?>
			{
				["plugins"] = new List<object>
				{
					new Dictionary<string, object?>
					{
						["id"] = "vapor.echo", ["name"] = "Echo", ["version"] = "1.0.0", ["apiVersion"] = "1.0",
						["trust"] = "official", ["permissions"] = new[] { "network" }, ["actions"] = new[] { "plugin_echo" }
					}
				}
			}));
			services.AddSingleton(seeded);
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/plugins/installed");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement agents = doc.RootElement.GetProperty("agents");
		Assert.Equal(1, agents.GetArrayLength());
		JsonElement agent = agents[0];
		Assert.Equal("ghost-agent", agent.GetProperty("agentId").GetString());
		Assert.Equal(DateTimeOffset.UnixEpoch, agent.GetProperty("reportedAt").GetDateTimeOffset());
		JsonElement plugins = agent.GetProperty("plugins");
		Assert.Equal(1, plugins.GetArrayLength());
		Assert.Equal("vapor.echo", plugins[0].GetProperty("id").GetString());
		Assert.Equal("official", plugins[0].GetProperty("trust").GetString());
	}

	[Fact]
	public async Task PluginTaskResultOverWebSocket_RebuildsInstalledMirror()
	{
		// Full tunnel: an agent connects over the test server's WebSocket, a
		// catalog-mode install dispatches through TaskSchedulerService, the
		// agent reports back, and the installed mirror rebuilds from the
		// plugin_* task_result — the only path that feeds L2947 in Program.cs.
		var index = """{"plugins":[{"id":"vapor.tunnel","name":"Tunnel","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/tunnel.zip","sha256":"AABBCCDD"}]}""";
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler(index)),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});

		var wsClient = factory.Server.CreateWebSocketClient();
		wsClient.ConfigureRequest = request =>
			request.Headers.Authorization = "Bearer agent-token";
		using var ws = await wsClient.ConnectAsync(
			new Uri(factory.Server.BaseAddress, "/v1/agent/ws?agentId=agent-1&region=local"),
			CancellationToken.None);
		await SendTextAsync(ws, JsonSerializer.Serialize(new WSMessage("hello",
			new AgentHello("agent-1", "local",
				new Dictionary<string, bool> { ["plugin_install"] = true }, null),
			null, null), JsonDefaults.Options));

		using (var client = CreateAdminClient(factory))
		{
			using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new
			{
				pluginId = "vapor.tunnel",
				agentIds = new[] { "agent-1" }
			});
			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		}

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		WSMessage dispatched = await ReceiveTextAsync(ws, timeout.Token);
		Assert.Equal("task", dispatched.Type);
		string taskId = dispatched.Task!.Id;

		await SendTextAsync(ws, JsonSerializer.Serialize(new WSMessage("task_result", null, null,
			new TaskResult(taskId, Success: true, Error: null,
				Output: new Dictionary<string, object?>
				{
					["plugins"] = new List<object>
					{
						new Dictionary<string, object?>
						{
							["id"] = "vapor.tunnel", ["name"] = "Tunnel", ["version"] = "1.0.0", ["apiVersion"] = "1.0",
							["trust"] = "official", ["permissions"] = new[] { "network" }, ["actions"] = new[] { "plugin_echo" }
						}
					}
				},
				DateTimeOffset.UtcNow, 1)), JsonDefaults.Options));

		// The mirror rebuild is synchronous with the result handling; poll the
		// view briefly so a slower dispatch tick cannot flake the read-back.
		using var mirrorClient = CreateAdminClient(factory);
		HttpResponseMessage? mirror = null;
		string? firstPluginId = null;
		for (int attempt = 0; attempt < 50 && firstPluginId is null; attempt++)
		{
			await Task.Delay(100, CancellationToken.None);
			mirror?.Dispose();
			mirror = await mirrorClient.GetAsync("/v1/plugins/installed");
			using var doc = JsonDocument.Parse(await mirror.Content.ReadAsStringAsync());
			JsonElement agents = doc.RootElement.GetProperty("agents");
			if (agents.GetArrayLength() > 0 && agents[0].GetProperty("plugins").GetArrayLength() > 0)
			{
				firstPluginId = agents[0].GetProperty("plugins")[0].GetProperty("id").GetString();
				Assert.Equal("agent-1", agents[0].GetProperty("agentId").GetString());
			}
		}

		Assert.Equal("vapor.tunnel", firstPluginId);
		mirror?.Dispose();
	}

	[Fact]
	public async Task AgentWs_GracefulClose_UnregistersTheAgent()
	{
		// The message loop's finally must unregister the agent when its socket
		// closes gracefully — not only on abort — so /v1/agents stops listing
		// it once the close frame has been processed.
		await using var factory = CreateFactory();

		var wsClient = factory.Server.CreateWebSocketClient();
		wsClient.ConfigureRequest = request =>
			request.Headers.Authorization = "Bearer agent-token";
		using var ws = await wsClient.ConnectAsync(
			new Uri(factory.Server.BaseAddress, "/v1/agent/ws?agentId=agent-gone&region=local"),
			CancellationToken.None);
		await SendTextAsync(ws, JsonSerializer.Serialize(new WSMessage("hello",
			new AgentHello("agent-gone", "local",
				new Dictionary<string, bool>(), null),
			null, null), JsonDefaults.Options));

		// Registered before the close... The hello frame's registration is
		// asynchronous with the socket send (the test server pairs the sockets in
		// memory), so the appearance itself is polled rather than assumed.
		using (var client = CreateAdminClient(factory))
		{
			bool listed = false;
			for (int attempt = 0; attempt < 50 && !listed; attempt++)
			{
				using HttpResponseMessage roster = await client.GetAsync("/v1/agents");
				listed = (await roster.Content.ReadAsStringAsync()).Contains("agent-gone", StringComparison.Ordinal);
				if (!listed)
				{
					await Task.Delay(100, CancellationToken.None);
				}
			}

			Assert.True(listed, "agent never appeared in /v1/agents after the hello frame");
		}

		// CloseOutputAsync sends the close frame without waiting for the
		// handshake to complete: the server-side loop unwinds through its
		// IOException on the close frame, so nothing ever writes one back.
		await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

		// ...and gone from the roster once the server-side finally has run.
		using var pollClient = CreateAdminClient(factory);
		bool stillListed = true;
		for (int attempt = 0; attempt < 50 && stillListed; attempt++)
		{
			await Task.Delay(100, CancellationToken.None);
			using HttpResponseMessage roster = await pollClient.GetAsync("/v1/agents");
			stillListed = (await roster.Content.ReadAsStringAsync()).Contains("agent-gone", StringComparison.Ordinal);
		}

		Assert.False(stillListed);
	}

	private static async Task SendTextAsync(WebSocket ws, string json, CancellationToken cancellationToken = default)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(json);
		await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
	}

	private static async Task<WSMessage> ReceiveTextAsync(WebSocket ws, CancellationToken cancellationToken)
	{
		var buffer = new byte[64 * 1024];
		WebSocketReceiveResult received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
		Assert.Equal(WebSocketMessageType.Text, received.MessageType);
		return JsonSerializer.Deserialize<WSMessage>(Encoding.UTF8.GetString(buffer, 0, received.Count), JsonDefaults.Options)
			?? throw new InvalidOperationException("tunnel frame failed to deserialize");
	}

	[Fact]
	public async Task Install_DirectModeWithoutChecksum_IsRejected()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new
		{
			agentIds = new[] { "agent-9" },
			url = "https://example.com/vapor.echo.zip"
		});

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("sha256", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task StandingCheck_ConflictsBeforeOrchestratorTracksAccount()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		// Declared but never reconciled: the account passes the existence check,
		// yet the orchestrator has no runtime slot to schedule a check into.
		using HttpResponseMessage declared = await client.PutAsJsonAsync(
			"/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" } });
		Assert.Equal(HttpStatusCode.OK, declared.StatusCode);

		using HttpResponseMessage response = await client.PostAsync("/v1/accounts/alice/standing-check", null);

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("standing check not scheduled", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
	{
		var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}

	private static TestFactory CreateFactory(Action<IServiceCollection>? customize = null) => new(customize);

	private sealed class TestFactory(Action<IServiceCollection>? customize) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				customize?.Invoke(services);
			});
		}
	}

	private sealed class FakeIndexHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
	{
		public int Calls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			});
		}
	}
}

public sealed class PluginCatalogServiceTests
{
	[Fact]
	public void ParseIndex_RejectsDocumentsWithoutPluginsArray()
	{
		Assert.ThrowsAny<FormatException>(() => PluginCatalogService.ParseIndex("""{"items":[]}""", "https://x/index.json"));
		Assert.ThrowsAny<Exception>(() => PluginCatalogService.ParseIndex("not json", "https://x/index.json"));
	}

	[Fact]
	public void ParseIndex_RejectsEntriesMissingRequiredStrings()
	{
		Assert.ThrowsAny<Exception>(() => PluginCatalogService.ParseIndex(
			"""{"plugins":[{"id":"vapor.x","name":"X","version":"1.0.0","apiVersion":"1.0"}]}""",
			"https://x/index.json"));
	}

	[Fact]
	public void ParseIndex_LowercasesChecksumAndKeepsOptionalFields()
	{
		PluginCatalog catalog = PluginCatalogService.ParseIndex(
			"""{"plugins":[{"id":"vapor.x","name":"X","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/x.zip","sha256":"AABB","permissions":["actions","web"],"trust":"community"}]}""",
			"https://x/index.json");

		Assert.Single(catalog.Entries);
		PluginIndexEntry entry = catalog.Entries[0];
		Assert.Equal("aabb", entry.Sha256);
		Assert.Equal("community", entry.Trust);
		Assert.Equal(new[] { "actions", "web" }, entry.Permissions);
	}

	[Fact]
	public async Task GetCatalogAsync_NotConfigured_ReturnsUnconfiguredSnapshot()
	{
		var service = new PluginCatalogService(new HttpClient(new FakeIndexHandler("{}")), NullLogger<PluginCatalogService>.Instance, () => null);

		PluginCatalog catalog = await service.GetCatalogAsync(CancellationToken.None);

		Assert.False(catalog.Configured);
		Assert.Empty(catalog.Entries);
	}

	[Fact]
	public async Task GetCatalogAsync_CachesUntilInvalidated()
	{
		var handler = new FakeIndexHandler("""{"plugins":[]}""");
		var service = new PluginCatalogService(new HttpClient(handler), NullLogger<PluginCatalogService>.Instance, () => "https://x/index.json");

		await service.GetCatalogAsync(CancellationToken.None);
		await service.GetCatalogAsync(CancellationToken.None);
		Assert.Equal(1, handler.Calls);

		service.Invalidate();
		await service.GetCatalogAsync(CancellationToken.None);
		Assert.Equal(2, handler.Calls);
	}

	[Fact]
	public async Task GetCatalogAsync_UrlChangeBypassesStaleCache()
	{
		var handler = new FakeIndexHandler("""{"plugins":[]}""");
		string url = "https://x/index-1.json";
		var service = new PluginCatalogService(new HttpClient(handler), NullLogger<PluginCatalogService>.Instance, () => url);

		await service.GetCatalogAsync(CancellationToken.None);
		url = "https://x/index-2.json";
		await service.GetCatalogAsync(CancellationToken.None);

		Assert.Equal(2, handler.Calls);
	}

	private sealed class FakeIndexHandler(string body) : HttpMessageHandler
	{
		public int Calls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			});
		}
	}
}

public sealed class PluginInventoryTests
{
	[Fact]
	public void Entry_RecordMembers_BehaveValueLike()
	{
		var entry = new PluginInventoryEntry(
			"vapor.echo", "Echo", "1.0.0", "1.0", "official",
			new[] { "network" }, new[] { "plugin_echo" });

		PluginInventoryEntry copy = entry with { };
		Assert.True(entry.Equals(copy));
		Assert.Equal(entry.GetHashCode(), copy.GetHashCode());
		Assert.NotEqual(entry, entry with { Version = "2.0.0" });

		(string id, string name, string version, string apiVersion, string? trust, IReadOnlyList<string> permissions, IReadOnlyList<string> actions) = entry;
		Assert.Equal("vapor.echo", id);
		Assert.Equal("Echo", name);
		Assert.Equal("1.0.0", version);
		Assert.Equal("1.0", apiVersion);
		Assert.Equal("official", trust);
		Assert.Equal(new[] { "network" }, permissions);
		Assert.Equal(new[] { "plugin_echo" }, actions);

		Assert.Contains("vapor.echo", entry.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Update_EntryWithoutIdentityKeys_FallsBackToEmptyStrings()
	{
		var inventory = new PluginInventory();

		inventory.Update("agent-a", DateTimeOffset.UnixEpoch, RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new object[] { new Dictionary<string, object?>() }
		}));

		IReadOnlyDictionary<string, AgentPlugins> snapshot = inventory.Snapshot();
		AgentPlugins agent = Assert.Single(snapshot.Values);
		PluginInventoryEntry entry = Assert.Single(agent.Plugins);
		Assert.Equal(string.Empty, entry.Id);
		Assert.Equal(string.Empty, entry.Name);
		Assert.Equal(string.Empty, entry.Version);
		Assert.Equal(string.Empty, entry.ApiVersion);
		Assert.Null(entry.Trust);
	}

	[Fact]
	public void Update_RebuildsMirrorFromRoundTrippedOutput()
	{
		var inventory = new PluginInventory();
		// Agent outputs travel over the WS as JSON: mirror the exact round-trip shape.
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object>
			{
				new Dictionary<string, object?>
				{
					["id"] = "vapor.b",
					["name"] = "B",
					["version"] = "2.0.0",
					["apiVersion"] = "1.0",
					["trust"] = "official",
					["permissions"] = new List<string> { "actions" },
					["actions"] = new List<string> { "b_one" }
				},
				new Dictionary<string, object?>
				{
					["id"] = "vapor.a",
					["name"] = "A",
					["version"] = "1.0.0",
					["apiVersion"] = "1.0",
					["trust"] = "unknown",
					["permissions"] = Array.Empty<string>(),
					["actions"] = Array.Empty<string>()
				}
			}
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		var snapshot = inventory.Snapshot();
		Assert.Single(snapshot);
		AgentPlugins agent = snapshot["agent-1"];
		Assert.Equal(2, agent.Plugins.Count);
		Assert.Equal("vapor.a", agent.Plugins[0].Id);
		Assert.Equal("vapor.b", agent.Plugins[1].Id);
		Assert.Equal("official", agent.Plugins[1].Trust);
		Assert.Equal(new[] { "b_one" }, agent.Plugins[1].Actions);
	}

	[Fact]
	public void Update_IgnoresOutputWithoutUsablePluginsArray()
	{
		var inventory = new PluginInventory();

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(new Dictionary<string, object?> { ["pluginId"] = "vapor.x" }));
		Assert.Empty(inventory.Snapshot());

		// A plugins key holding a non-array value is ignored just the same.
		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(new Dictionary<string, object?> { ["plugins"] = "bogus" }));
		Assert.Empty(inventory.Snapshot());

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, null);
		Assert.Empty(inventory.Snapshot());
	}

	[Fact]
	public void Update_SkipsNonObjectArrayEntries()
	{
		// Agents are third-party code: a malformed "plugins" entry (a scalar or an
		// array element instead of an object) must be skipped, not crash the mirror.
		var inventory = new PluginInventory();
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object?>
			{
				"vapor.bogus",
				new Dictionary<string, object?>
				{
					["id"] = "vapor.a",
					["name"] = "A",
					["version"] = "1.0.0",
					["apiVersion"] = "1.0",
					["trust"] = "official",
					["permissions"] = Array.Empty<string>(),
					["actions"] = Array.Empty<string>()
				},
				new List<object?> { "vapor.nested" },
				42
			}
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		AgentPlugins agent = inventory.Snapshot()["agent-1"];
		Assert.Equal("vapor.a", Assert.Single(agent.Plugins).Id);
	}

	[Fact]
	public void Update_ReplacesWholeListPerAgent()
	{
		var inventory = new PluginInventory();
		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(PluginsOutput("vapor.a", "vapor.b")));
		inventory.Update("agent-1", DateTimeOffset.UtcNow, RoundTrip(PluginsOutput("vapor.a")));

		AgentPlugins agent = inventory.Snapshot()["agent-1"];
		Assert.Single(agent.Plugins);
		Assert.Equal("vapor.a", agent.Plugins[0].Id);
	}

	[Fact]
	public void Remove_DropsAgentEntry()
	{
		var inventory = new PluginInventory();
		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(PluginsOutput("vapor.a")));

		inventory.Remove("agent-1");

		Assert.Empty(inventory.Snapshot());
	}

	private static Dictionary<string, object?> PluginsOutput(params string[] ids) => new()
	{
		["plugins"] = ids.Select(id => (object)new Dictionary<string, object?>
		{
			["id"] = id,
			["name"] = id,
			["version"] = "1.0.0",
			["apiVersion"] = "1.0",
			["trust"] = null,
			["permissions"] = Array.Empty<string>(),
			["actions"] = Array.Empty<string>()
		}).ToList()
	};

	private static IReadOnlyDictionary<string, object?> RoundTrip(IReadOnlyDictionary<string, object?> output) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(output))!;

	/// <summary>Visible to other test classes seeding a PluginInventory mirror.</summary>
	internal static IReadOnlyDictionary<string, object?> RoundTripForSeed(IReadOnlyDictionary<string, object?> output) =>
		RoundTrip(output);
}

public sealed class HostTargetedDispatchTests
{
	[Fact]
	public async Task DispatchOnce_RoutesHostTaskToNamedAgentNotRegionPick()
	{
		// Two agents in the region; the deterministic pick would always choose
		// agent-1. The host-targeted task must land on agent-2.
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["plugin_install"] = true }, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-2", "local", new Dictionary<string, bool> { ["plugin_install"] = true }, null), new NoopWebSocket(), cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "plugin_install", target: "agent:agent-2"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Empty(store.RequeuedTaskIds);
		var published = Assert.Single(events.Events);
		Assert.Equal("task.dispatched", published.Type);
		Assert.Equal("agent-2", published.Payload!["agentId"]?.ToString());
	}

	[Fact]
	public async Task DispatchOnce_RequeuesHostTaskWhenNamedAgentIsOffline()
	{
		// Only agent-1 is connected; a task targeted at agent-2 must not run there.
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["plugin_install"] = true }, null), new NoopWebSocket(), cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "plugin_install", target: "agent:agent-2"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "task-1" }, store.RequeuedTaskIds);
		var published = Assert.Single(events.Events);
		Assert.Equal("task.dispatch_failed", published.Type);
	}

	[Fact]
	public async Task DispatchOnce_FailsHostTaskWhenNamedAgentLacksCapability()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["login"] = true }, null), new NoopWebSocket(), cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "plugin_install", target: "agent:agent-1"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "task-1" }, store.RequeuedTaskIds);
	}

	private static Config CreateConfig() => new("", new HashSet<string>(StringComparer.Ordinal), "test.db", 300, false);

	private static JobTask CreateTask(string taskId, string jobId, string region, string action, string target)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new JobTask(taskId, jobId, target, action, region, null, JobTaskStatus.Queued, 0, now, now);
	}

	private sealed class NoopWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose()
		{
		}
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			Task.FromCanceled<WebSocketReceiveResult>(cancellationToken);
		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class FakeJobStore : IJobStore
	{
		public Queue<JobTask> QueuedTasks { get; init; } = new();
		public List<string> RequeuedTaskIds { get; } = [];

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<Vapor.Protocol.JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken)
		{
			if (QueuedTasks.Count == 0)
			{
				return Task.FromResult<JobTask?>(null);
			}

			return Task.FromResult<JobTask?>(QueuedTasks.Dequeue());
		}

		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => Task.FromResult<Job?>(null);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);

		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken)
		{
			RequeuedTaskIds.Add(taskId);
			return Task.CompletedTask;
		}

		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
	}

	private sealed class RecordingEventBroker : IEventBroker
	{
		public List<Event> Events { get; } = [];

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
			Events.Add(new Event(Guid.NewGuid().ToString("N"), jobId, type, DateTimeOffset.UtcNow, payload));
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
		{
		}

		public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
		{
			await Task.CompletedTask;
			yield break;
		}

		public async IAsyncEnumerable<SessionEvent> SubscribeSessions([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			await Task.CompletedTask;
			yield break;
		}

		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			await Task.CompletedTask;
			yield break;
		}
	}
}
