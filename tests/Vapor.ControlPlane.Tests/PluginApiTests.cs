using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
		using HttpResponseMessage install = await client.PostAsJsonAsync("/v1/plugins/install", new { agentIds = new[] { "agent-9" } });
		using HttpResponseMessage uninstall = await client.PostAsJsonAsync("/v1/plugins/uninstall/vapor.test-plugin", new { agentIds = new[] { "agent-9" } });
		using HttpResponseMessage refresh = await client.PostAsJsonAsync("/v1/plugins/inventory/refresh", new { });
		using HttpResponseMessage updateCheck = await client.PostAsJsonAsync("/v1/plugins/update-check", new { });

		Assert.Equal(HttpStatusCode.Unauthorized, catalog.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, installed.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, install.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, uninstall.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, updateCheck.StatusCode);
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
		using HttpResponseMessage noSha = await client.PostAsJsonAsync("/v1/plugins/install", new { url = "https://pkg/a.zip", agentIds = new[] { "agent-9" } });
		using HttpResponseMessage badSha = await client.PostAsJsonAsync("/v1/plugins/install", new { url = "https://pkg/a.zip", sha256 = "xyz", agentIds = new[] { "agent-9" } });
		using HttpResponseMessage neither = await client.PostAsJsonAsync("/v1/plugins/install", new { agentIds = new[] { "agent-9" } });

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

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new { pluginId = "vapor.absent", agentIds = new[] { "agent-9" } });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Install_CatalogMode_ResolvesUrlAndChecksumAndTargetsNamedAgents()
	{
		var index = """{"plugins":[{"id":"vapor.test-plugin","name":"Test","publisher":"vapor","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/test.zip","sha256":"AABBCCDD","trust":"official","permissions":["actions","web"],"signing":{"publicKey":"dGhlLWtleQ==","signature":"dGhlLXNpZw=="}}]}""";
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

			// The index's own declaration travels with the instruction so the agent can
			// reconcile the package manifest against what the catalog advertised.
			Assert.Equal("official", ((JsonElement)payload["expectedTrust"]!).GetString());
			Assert.Equal(
				new[] { "actions", "web" },
				((JsonElement)payload["expectedPermissions"]!).EnumerateArray().Select(e => e.GetString()).ToArray());
			Assert.Equal("1.0", ((JsonElement)payload["expectedApiVersion"]!).GetString());
			Assert.Equal("vapor", ((JsonElement)payload["expectedPublisher"]!).GetString());

			// The declared signature travels as both halves so a half-declared
			// signature can never reach the agent as a partial trust instruction.
			Assert.Equal("dGhlLWtleQ==", ((JsonElement)payload["expectedSigningPublicKey"]!).GetString());
			Assert.Equal("dGhlLXNpZw==", ((JsonElement)payload["expectedSigningSignature"]!).GetString());
		}
	}

	[Fact]
	public async Task Install_CatalogMode_OmitsExpectationKeysTheIndexDoesNotDeclare()
	{
		// trust omitted entirely; permissions present but empty — neither may reach the
		// instruction as an expectation, only the mandatory apiVersion does.
		var index = """{"plugins":[{"id":"vapor.test-plugin","name":"Test","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/test.zip","sha256":"AABBCCDD","permissions":[]}]}""";
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
			agentIds = new[] { "agent-9" }
		});

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string jobId = doc.RootElement.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
		JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);

		IReadOnlyDictionary<string, object?> payload = created.Tasks[0].Payload!;
		Assert.False(payload.ContainsKey("expectedTrust"));
		Assert.False(payload.ContainsKey("expectedPermissions"));
		Assert.False(payload.ContainsKey("expectedPublisher"));
		Assert.False(payload.ContainsKey("expectedSigningPublicKey"));
		Assert.False(payload.ContainsKey("expectedSigningSignature"));
		Assert.Equal("1.0", ((JsonElement)payload["expectedApiVersion"]!).GetString());
	}

	[Fact]
	public async Task Install_AuditRecordsWhetherTheCatalogEntryIsSigned()
	{
		// A declared signature is a claim the operator has to back with pinned keys;
		// the audit trail records whether the catalog made that claim.
		static async Task<IReadOnlyDictionary<string, object?>?> InstallAuditAsync(
			string? index,
			bool catalogMode)
		{
			await using var factory = CreateFactory(services =>
			{
				if (index is not null)
				{
					services.RemoveAll<PluginCatalogService>();
					services.AddSingleton(_ => new PluginCatalogService(
						new HttpClient(new FakeIndexHandler(index)),
						NullLogger<PluginCatalogService>.Instance,
						() => "https://plugins.example/index.json"));
				}
			});
			using var client = CreateAdminClient(factory);

			object body = catalogMode
				? new { pluginId = "vapor.test-plugin", agentIds = new[] { "agent-9" } }
				: new { url = "https://pkg/direct.zip", sha256 = new string('A', 64), agentIds = new[] { "agent-9" } };
			using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", body);
			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

			await using var scope = factory.Services.CreateAsyncScope();
			var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
			var entries = await audit.QueryAsync(
				new AuditQuery(Action: "plugin_install_dispatched", Limit: 1), CancellationToken.None);
			return entries.Count == 1 ? entries[0].Details : null;
		}

		string signedIndex = """{"plugins":[{"id":"vapor.test-plugin","name":"Test","publisher":"vapor","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/s.zip","sha256":"AABB","signing":{"publicKey":"a2V5","signature":"c2ln"}}]}""";
		string bareIndex = """{"plugins":[{"id":"vapor.test-plugin","name":"Test","publisher":"vapor","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/b.zip","sha256":"CCDD"}]}""";

		IReadOnlyDictionary<string, object?>? signed = await InstallAuditAsync(signedIndex, catalogMode: true);
		Assert.NotNull(signed);
		Assert.True(((JsonElement)signed!["signed"]!).GetBoolean());
		Assert.Equal("vapor", ((JsonElement)signed["publisher"]!).GetString());

		IReadOnlyDictionary<string, object?>? bare = await InstallAuditAsync(bareIndex, catalogMode: true);
		Assert.NotNull(bare);
		Assert.False(((JsonElement)bare!["signed"]!).GetBoolean());

		// Direct mode has no catalog entry at all — never signed, whatever the index says.
		IReadOnlyDictionary<string, object?>? direct = await InstallAuditAsync(null, catalogMode: false);
		Assert.NotNull(direct);
		Assert.False(((JsonElement)direct!["signed"]!).GetBoolean());
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
			agentIds = new[] { "agent-9" }
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
		using HttpResponseMessage dispatched = await client.PostAsJsonAsync("/v1/plugins/uninstall/vapor.test-plugin", new { agentIds = new[] { "agent-9" } });

		Assert.Equal(HttpStatusCode.BadRequest, noAgents.StatusCode);
		Assert.Equal(HttpStatusCode.Accepted, dispatched.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
		using var doc = JsonDocument.Parse(await dispatched.Content.ReadAsStringAsync());
		string jobId = doc.RootElement.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
		JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);

		Assert.Equal("plugin_uninstall", created.Job.Action);
		Assert.Equal("agent:agent-9", created.Tasks[0].Target);
	}

	[Fact]
	public async Task Uninstall_WhitespaceRouteSegmentIsRejected()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		// A route segment can carry decoded whitespace (the "%20" URL form), which
		// trims to an empty plugin id — the endpoint must reject it before dispatch.
		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/uninstall/%20", new { agentIds = new[] { "agent-9" } });

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
			new Uri(factory.Server.BaseAddress, "/v1/agent/ws?agentId=agent-9&region=local"),
			CancellationToken.None);
		await SendTextAsync(ws, JsonSerializer.Serialize(new WSMessage("hello",
			new AgentHello("agent-9", "local",
				new Dictionary<string, bool> { ["plugin_install"] = true }, null),
			null, null), JsonDefaults.Options));

		using (var client = CreateAdminClient(factory))
		{
			using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/install", new
			{
				pluginId = "vapor.tunnel",
				agentIds = new[] { "agent-9" }
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
				Assert.Equal("agent-9", agents[0].GetProperty("agentId").GetString());
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
			new Uri(factory.Server.BaseAddress, "/v1/agent/ws?agentId=agent-9&region=local"),
			CancellationToken.None);
		await SendTextAsync(ws, JsonSerializer.Serialize(new WSMessage("hello",
			new AgentHello("agent-9", "local",
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
				listed = (await roster.Content.ReadAsStringAsync()).Contains("agent-9", StringComparison.Ordinal);
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
			stillListed = (await roster.Content.ReadAsStringAsync()).Contains("agent-9", StringComparison.Ordinal);
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

	[Fact]
	public async Task UpdateCheck_NotConfiguredIndex_ReturnsBadRequest()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/update-check", new { agentIds = new[] { "agent-9" } });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("not configured", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task UpdateCheck_DispatchesIndexCandidatesToNamedAgents()
	{
		var index = """{"plugins":[{"id":"vapor.beta","name":"Beta","publisher":"vapor","version":"2.0.0","apiVersion":"1.0","url":"https://pkg/b.zip","sha256":"AABB"},{"id":"vapor.alpha","name":"Alpha","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/a.zip","sha256":"CCDD"}]}""";
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler(index)),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/update-check", new
		{
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
			Assert.Equal("plugin_update_check", created.Job.Action);
			Assert.Single(created.Tasks);
			Assert.Equal($"agent:{job.GetProperty("agentId").GetString()}", created.Tasks[0].Target);

			IReadOnlyDictionary<string, object?> payload = created.Tasks[0].Payload!;
			var candidates = (JsonElement)payload["candidates"]!;
			Assert.Equal(2, candidates.GetArrayLength());
			Assert.Equal("vapor.alpha", candidates[0].GetProperty("id").GetString());
			Assert.Equal("1.0.0", candidates[0].GetProperty("version").GetString());
			Assert.False(candidates[0].TryGetProperty("publisher", out _)); // undeclared stays omitted
			Assert.Equal("vapor.beta", candidates[1].GetProperty("id").GetString());
			Assert.Equal("2.0.0", candidates[1].GetProperty("version").GetString());
			Assert.Equal("vapor", candidates[1].GetProperty("publisher").GetString());
		}
	}

	[Fact]
	public async Task UpdateCheck_DefaultsToConnectedAgentsAndCarriesTheirRegion()
	{
		var index = """{"plugins":[{"id":"vapor.alpha","name":"Alpha","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/a.zip","sha256":"CCDD"}]}""";
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler(index)),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		using CancellationTokenSource registrationCts = new();
		registry.Register(
			new AgentHello("agent-live", "us-east", new Dictionary<string, bool> { ["plugin_update_check"] = true }, null),
			new UpdateCheckNoopWebSocket(),
			registrationCts.Token);
		using var client = CreateAdminClient(factory);

		// No body at all: the optional request binds as null and the endpoint
		// defaults to every connected agent, resolving the region from the hello.
		using HttpResponseMessage response = await client.PostAsync("/v1/plugins/update-check", content: null);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		await using var scope = factory.Services.CreateAsyncScope();
		var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		string jobId = doc.RootElement.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
		JobWithTasks created = await store.GetJob(jobId, CancellationToken.None);
		Assert.Equal("plugin_update_check", created.Job.Action);
		Assert.Equal("us-east", created.Job.Region);
		Assert.Equal("agent:agent-live", created.Tasks[0].Target);
	}

	[Fact]
	public async Task UpdateCheck_WithoutConnectedAgents_ReturnsConflict()
	{
		var index = """{"plugins":[{"id":"vapor.alpha","name":"Alpha","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/a.zip","sha256":"CCDD"}]}""";
		await using var factory = CreateFactory(services =>
		{
			services.RemoveAll<PluginCatalogService>();
			services.AddSingleton(_ => new PluginCatalogService(
				new HttpClient(new FakeIndexHandler(index)),
				NullLogger<PluginCatalogService>.Instance,
				() => "https://plugins.example/index.json"));
		});
		using var client = CreateAdminClient(factory);

		// Catalog is configured, but no agentIds in the request and no connected
		// agents to default to.
		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/plugins/update-check", new { });

		Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
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
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:", AgentKeyAgentIds: new Dictionary<string, string> { ["agent-token"] = "agent-9" }));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				customize?.Invoke(services);
			});
		}
	}

	private sealed class UpdateCheckNoopWebSocket : System.Net.WebSockets.WebSocket
	{
		public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose()
		{
		}
		public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			Task.FromCanceled<System.Net.WebSockets.WebSocketReceiveResult>(cancellationToken);
		public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			Task.CompletedTask;
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
