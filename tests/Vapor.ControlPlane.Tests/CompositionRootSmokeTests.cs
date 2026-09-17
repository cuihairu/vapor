using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
		Dictionary<string, string?> env = new()
		{
			["Vapor_ADMIN_API_KEY"] = "admin-token",
			["Vapor_AGENT_API_KEYS"] = "agent-token",
			["Vapor_DB_PATH"] = dbPath,
			["Vapor_AUDIT_DB_PATH"] = auditDbPath,
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
