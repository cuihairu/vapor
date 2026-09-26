using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// The API-edge middleware in Program.cs: RED recording keyed by the route
// pattern (bounded cardinality) and the opt-in per-key rate limiter on /v1.
// Each fact builds its own factory so limiter and metric state never leak
// between tests.
public sealed class ApiEdgeMiddlewareTests
{
	private sealed class TestFactory(int rateLimitPerMinute) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(_ => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(_ => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				// The limiter instance comes from the env-loaded startup config in
				// Program.cs; the test replaces it to drive both of its modes.
				services.RemoveAll<ApiKeyRateLimiter>();
				services.AddSingleton(new ApiKeyRateLimiter(rateLimitPerMinute));
			});
		}
	}

	private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
	{
		HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}

	[Fact]
	public async Task V1Requests_BeyondTheKeyLimit_Get429WithRetryAfterAndErrorBody()
	{
		await using TestFactory factory = new(2);
		using HttpClient client = AdminClient(factory);

		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);

		using HttpResponseMessage rejected = await client.GetAsync("/v1/agents");
		Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
		Assert.NotNull(rejected.Headers.RetryAfter);
		Assert.InRange(rejected.Headers.RetryAfter.Delta!.Value.TotalSeconds, 1, 60);

		string body = await rejected.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.StartsWith("rate limit exceeded: retry after", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task RateLimiting_SeparatesTheAnonymousBucketFromNamedKeys()
	{
		await using TestFactory factory = new(1);
		using HttpClient admin = AdminClient(factory);
		using HttpClient anonymous = factory.CreateClient();

		// No Authorization header → shared anonymous bucket: the first request
		// is admitted at the edge (and rejected by auth inside the endpoint),
		// the second is stopped at the edge. The admin key is unaffected.
		Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/agents")).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync("/v1/agents")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/v1/agents")).StatusCode);
	}

	[Fact]
	public async Task NonV1Routes_AreNeverRateLimited()
	{
		await using TestFactory factory = new(1);
		using HttpClient client = factory.CreateClient();

		for (int i = 0; i < 3; i++)
		{
			Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
		}

		// /metrics stays scrapeable even with the anonymous bucket exhausted.
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/metrics")).StatusCode);
	}

	[Fact]
	public async Task DisabledLimiter_NeverRejects()
	{
		await using TestFactory factory = new(0);
		using HttpClient client = factory.CreateClient();

		for (int i = 0; i < 5; i++)
		{
			Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/agents")).StatusCode);
		}
	}

	[Fact]
	public async Task Metrics_ExposeTheREDFamiliesIncludingRejections()
	{
		await using TestFactory factory = new(1);
		using HttpClient client = AdminClient(factory);

		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);
		Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/agents")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);

		using HttpResponseMessage metrics = await client.GetAsync("/metrics");
		Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
		string exposition = await metrics.Content.ReadAsStringAsync();
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/agents\",status=\"200\"} 1", exposition);
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/agents\",status=\"429\"} 1", exposition);
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/healthz\",status=\"200\"} 1", exposition);
		Assert.Contains("vapor_controlplane_http_request_duration_seconds_count{method=\"GET\",route=\"/v1/agents\"} 2", exposition);
		Assert.Contains("vapor_controlplane_rate_limited_total 1", exposition);
	}

	[Fact]
	public async Task RouteLabelsUseThePattern_NotTheConcretePath()
	{
		await using TestFactory factory = new(0);
		using HttpClient client = AdminClient(factory);

		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/jobs/not-a-real-job")).StatusCode);

		using HttpResponseMessage metrics = await client.GetAsync("/metrics");
		Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
		string exposition = await metrics.Content.ReadAsStringAsync();
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/jobs/{jobId}\",status=\"404\"} 1", exposition);
		Assert.DoesNotContain("not-a-real-job", exposition);
	}

	[Fact]
	public async Task UnmatchedPaths_AreNotRecorded()
	{
		await using TestFactory factory = new(0);
		using HttpClient client = factory.CreateClient();

		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/definitely-not-a-route")).StatusCode);

		using HttpResponseMessage metrics = await client.GetAsync("/metrics");
		Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
		Assert.DoesNotContain("definitely-not-a-route", await metrics.Content.ReadAsStringAsync());
	}
}
