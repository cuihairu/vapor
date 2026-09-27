using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// Smoke tests that boot the real composition root (the top-level statements in
// Program.cs) with environment-driven configuration, exercising the startup
// wiring that the service-replacing test factories remove: the SQLite store
// lambdas, the webhook sink registration, both branches of the notification
// event filter, OpenTelemetry registration, and the Swagger pipeline toggle.
// xUnit runs the tests of one class sequentially, which keeps the process-wide
// environment variables of these tests from interleaving; the shared
// non-parallel collection additionally keeps them off the rest of the run
// (see ProcessGlobalTracingCollection).
[Collection(ProcessGlobalTracingCollection.Name)]
public sealed class CompositionRootSmokeTests
{
	[Theory]
	[InlineData(true, "job.created, job.failed ,")]
	[InlineData(false, null)]
	public async Task Factory_BootsFromEnvironment_AndServesConfiguredPipeline(bool enableSwagger, string? webhookEvents)
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-cp-{Guid.NewGuid():N}.db");
		string auditDbPath = Path.Combine(Path.GetTempPath(), $"vapor-audit-{Guid.NewGuid():N}.db");
		string configDbPath = Path.Combine(Path.GetTempPath(), $"vapor-config-{Guid.NewGuid():N}.db");
		string crawlDbPath = Path.Combine(Path.GetTempPath(), $"vapor-crawl-{Guid.NewGuid():N}.db");
		Dictionary<string, string?> env = new()
		{
			["Vapor_ADMIN_API_KEY"] = "admin-token",
			["Vapor_AGENT_API_KEYS"] = "agent-token",
			["Vapor_DB_PATH"] = dbPath,
			["Vapor_AUDIT_DB_PATH"] = auditDbPath,
			["Vapor_CONFIG_DB_PATH"] = configDbPath,
			["Vapor_CRAWL_DB_PATH"] = crawlDbPath,
			["Vapor_ENABLE_SWAGGER"] = enableSwagger ? "true" : "false",
			// Discard-protocol port: connects fail immediately, and with zero
			// retries the notification sink never spins on delivery.
			["Vapor_WEBHOOK_NOTIFICATIONS_URL"] = "http://127.0.0.1:9/hook",
			["Vapor_WEBHOOK_NOTIFICATIONS_EVENTS"] = webhookEvents,
			["Vapor_WEBHOOK_NOTIFICATIONS_MAX_RETRIES"] = "0",
			["Vapor_WEBHOOK_NOTIFICATIONS_RETRY_BASE_DELAY_MS"] = "10",
			["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317",
		};

		foreach ((string key, string? value) in env)
		{
			Environment.SetEnvironmentVariable(key, value);
		}

		try
		{
			await using RawFactory factory = new();
			using HttpClient client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

			using HttpResponseMessage health = await client.GetAsync("/healthz");
			Assert.Equal(HttpStatusCode.OK, health.StatusCode);

			// The real SqliteJobStore lambda was used, so the database file exists.
			Assert.True(File.Exists(dbPath));
			// Same proof for the declared-state persistence lambda.
			Assert.True(File.Exists(configDbPath));
			// Same proof for the crawl store (and that no boot falls back to the
			// process-CWD default, where state would leak across runs).
			Assert.True(File.Exists(crawlDbPath));

			// The metrics endpoint renders the per-sink notification counters when a
			// sink is registered at startup.
			using HttpResponseMessage metrics = await client.GetAsync("/metrics");
			Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
			Assert.Contains("vapor_controlplane_notifications_total{sink=\"webhook\"", await metrics.Content.ReadAsStringAsync());

			// The swagger pipeline is wired only when the env toggle is on.
			using HttpResponseMessage swaggerDoc = await client.GetAsync("/swagger/v1/swagger.json");
			Assert.Equal(enableSwagger ? HttpStatusCode.OK : HttpStatusCode.NotFound, swaggerDoc.StatusCode);

			if (enableSwagger)
			{
				using HttpResponseMessage swaggerUi = await client.GetAsync("/swagger");
				Assert.Equal(HttpStatusCode.OK, swaggerUi.StatusCode);

				// Swashbuckle 10 / Microsoft.OpenApi 2 migration anchor: the
				// bearer scheme and the global security application must survive
				// document generation — a silent drop would hollow out the
				// OpenAPI contract without breaking anything else.
				string doc = await swaggerDoc.Content.ReadAsStringAsync();
				using var json = JsonDocument.Parse(doc);
				Assert.True(json.RootElement.TryGetProperty("components", out var components)
					&& components.TryGetProperty("securitySchemes", out var schemes)
					&& schemes.TryGetProperty("bearer", out _), doc);
				Assert.True(json.RootElement.TryGetProperty("security", out _), doc);
			}
		}
		finally
		{
			foreach (string key in env.Keys)
			{
				Environment.SetEnvironmentVariable(key, null);
			}

			await DisposeDbFileAsync(dbPath);
			await DisposeDbFileAsync(auditDbPath);
			await DisposeDbFileAsync(configDbPath);
			await DisposeDbFileAsync(crawlDbPath);
		}
	}

	[Fact]
	public async Task Factory_WiresTheRateLimiterFromEnvironment()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-cp-{Guid.NewGuid():N}.db");
		string auditDbPath = Path.Combine(Path.GetTempPath(), $"vapor-audit-{Guid.NewGuid():N}.db");
		string configDbPath = Path.Combine(Path.GetTempPath(), $"vapor-config-{Guid.NewGuid():N}.db");
		string crawlDbPath = Path.Combine(Path.GetTempPath(), $"vapor-crawl-{Guid.NewGuid():N}.db");
		Dictionary<string, string?> env = new()
		{
			["Vapor_ADMIN_API_KEY"] = "admin-token",
			["Vapor_AGENT_API_KEYS"] = "agent-token",
			["Vapor_DB_PATH"] = dbPath,
			["Vapor_AUDIT_DB_PATH"] = auditDbPath,
			["Vapor_CONFIG_DB_PATH"] = configDbPath,
			["Vapor_CRAWL_DB_PATH"] = crawlDbPath,
			["Vapor_ENABLE_SWAGGER"] = "false",
			// The limiter is constructed from the env-loaded startup config in
			// Program.cs — this is the wiring the service-replacing factories
			// bypass, so prove it end to end here.
			["Vapor_API_RATE_LIMIT_PER_MINUTE"] = "2",
		};

		foreach ((string key, string? value) in env)
		{
			Environment.SetEnvironmentVariable(key, value);
		}

		try
		{
			await using RawFactory factory = new();
			using HttpClient client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

			Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);
			Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);
			Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/agents")).StatusCode);
		}
		finally
		{
			foreach (string key in env.Keys)
			{
				Environment.SetEnvironmentVariable(key, null);
			}

			await DisposeDbFileAsync(dbPath);
			await DisposeDbFileAsync(auditDbPath);
			await DisposeDbFileAsync(configDbPath);
			await DisposeDbFileAsync(crawlDbPath);
		}
	}

	[Fact]
	public async Task Factory_WiresTheFaultInjectorIntoTheEdgePipeline()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-cp-{Guid.NewGuid():N}.db");
		string auditDbPath = Path.Combine(Path.GetTempPath(), $"vapor-audit-{Guid.NewGuid():N}.db");
		string configDbPath = Path.Combine(Path.GetTempPath(), $"vapor-config-{Guid.NewGuid():N}.db");
		string crawlDbPath = Path.Combine(Path.GetTempPath(), $"vapor-crawl-{Guid.NewGuid():N}.db");
		Dictionary<string, string?> env = new()
		{
			["Vapor_ADMIN_API_KEY"] = "admin-token",
			["Vapor_AGENT_API_KEYS"] = "agent-token",
			["Vapor_DB_PATH"] = dbPath,
			["Vapor_AUDIT_DB_PATH"] = auditDbPath,
			["Vapor_CONFIG_DB_PATH"] = configDbPath,
			["Vapor_CRAWL_DB_PATH"] = crawlDbPath,
			["Vapor_ENABLE_SWAGGER"] = "false",
		};

		foreach ((string key, string? value) in env)
		{
			Environment.SetEnvironmentVariable(key, value);
		}

		try
		{
			await using RawFactory factory = new();
			using HttpClient client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

			// Disarmed by default; the injector resolves from the real root.
			Assert.Equal("[]", (await client.GetStringAsync("/v1/faults")).Trim());

			// Arm through the real pipeline and prove the edge middleware enforces
			// the drill while the fault endpoints themselves stay reachable.
			using HttpResponseMessage armed = await client.PostAsJsonAsync("/v1/faults", new { kind = "api-request", mode = "error", route = "/v1/agents", httpStatus = 503 });
			Assert.Equal(HttpStatusCode.Created, armed.StatusCode);
			Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/v1/agents")).StatusCode);

			string metrics = await client.GetStringAsync("/metrics");
			Assert.Contains("vapor_controlplane_fault_injections_total{kind=\"api-request\",mode=\"error\"} 1", metrics);
			Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/v1/faults")).StatusCode);
			Assert.Contains("vapor_controlplane_faults_armed 0", await client.GetStringAsync("/metrics"));
		}
		finally
		{
			foreach (string key in env.Keys)
			{
				Environment.SetEnvironmentVariable(key, null);
			}

			await DisposeDbFileAsync(dbPath);
			await DisposeDbFileAsync(auditDbPath);
			await DisposeDbFileAsync(configDbPath);
			await DisposeDbFileAsync(crawlDbPath);
		}
	}

	// The §9 restart drill: what the operator used to have to redo by hand —
	// re-declaring every account and every setting after a control-plane
	// restart — must now survive it through the declared-state database. Two
	// RawFactory lifecycles against the same files are the "restart".
	[Fact]
	public async Task Factory_RehydratesDeclaredStateAfterRestart()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-cp-{Guid.NewGuid():N}.db");
		string auditDbPath = Path.Combine(Path.GetTempPath(), $"vapor-audit-{Guid.NewGuid():N}.db");
		string configDbPath = Path.Combine(Path.GetTempPath(), $"vapor-config-{Guid.NewGuid():N}.db");
		string crawlDbPath = Path.Combine(Path.GetTempPath(), $"vapor-crawl-{Guid.NewGuid():N}.db");
		Dictionary<string, string?> env = new()
		{
			["Vapor_ADMIN_API_KEY"] = "admin-token",
			["Vapor_AGENT_API_KEYS"] = "agent-token",
			["Vapor_DB_PATH"] = dbPath,
			["Vapor_AUDIT_DB_PATH"] = auditDbPath,
			["Vapor_CONFIG_DB_PATH"] = configDbPath,
			["Vapor_CRAWL_DB_PATH"] = crawlDbPath,
			["Vapor_ENABLE_SWAGGER"] = "false",
		};

		foreach ((string key, string? value) in env)
		{
			Environment.SetEnvironmentVariable(key, value);
		}

		try
		{
			await using (RawFactory first = new())
			{
				HttpClient client = first.CreateClient();
				client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

				using HttpResponseMessage declared = await client.PutAsJsonAsync("/v1/accounts/alice", new
				{
					enabled = true,
					desiredState = "idle",
					idleApps = new[] { "730" },
					region = "us-east",
					note = "keep my farm",
				});
				Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
				using HttpResponseMessage doomed = await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "offline" });
				Assert.Equal(HttpStatusCode.OK, doomed.StatusCode);
				Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/v1/accounts/bob")).StatusCode);
				using HttpResponseMessage global = await client.PutAsJsonAsync("/v1/config/global", new { settings = new { theme = "dark" }, updatedBy = "operator" });
				Assert.Equal(HttpStatusCode.OK, global.StatusCode);
			}

			await using (RawFactory second = new())
			{
				HttpClient client = second.CreateClient();
				client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

				using HttpResponseMessage accounts = await client.GetAsync("/v1/accounts");
				Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);
				using var accountsJson = JsonDocument.Parse(await accounts.Content.ReadAsStringAsync());
				JsonElement specs = accountsJson.RootElement.GetProperty("accounts");
				// alice rehydrated with her declaration; bob stays deleted (the
				// delete write-through survives too, not just the upserts).
				Assert.Single(specs.EnumerateArray().Select(e => e.GetProperty("accountName").GetString()));
				Assert.Equal("alice", specs[0].GetProperty("accountName").GetString());
				Assert.Equal("us-east", specs[0].GetProperty("region").GetString());
				Assert.Equal(1, specs[0].GetProperty("version").GetProperty("version").GetInt32());

				using HttpResponseMessage config = await client.GetAsync("/v1/config");
				Assert.Equal(HttpStatusCode.OK, config.StatusCode);
				using var configJson = JsonDocument.Parse(await config.Content.ReadAsStringAsync());
				JsonElement settings = configJson.RootElement.GetProperty("global").GetProperty("settings");
				Assert.Equal("dark", settings.GetProperty("theme").GetString());
				// The version counter continues from the persisted value, not from 1.
				Assert.Equal(2, configJson.RootElement.GetProperty("global").GetProperty("version").GetProperty("version").GetInt32());
			}
		}
		finally
		{
			foreach (string key in env.Keys)
			{
				Environment.SetEnvironmentVariable(key, null);
			}

			await DisposeDbFileAsync(dbPath);
			await DisposeDbFileAsync(auditDbPath);
			await DisposeDbFileAsync(configDbPath);
			await DisposeDbFileAsync(crawlDbPath);
		}
	}

	private static async Task DisposeDbFileAsync(string path)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				File.Delete(path);
				return;
			}
			catch (IOException)
			{
				// The connection pool may still hold the file briefly after dispose.
				await Task.Delay(50);
			}
		}
	}

	private sealed class RawFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Production");
		}
	}
}
