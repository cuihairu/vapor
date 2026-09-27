using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// The /v1/faults admin surface (roadmap §8 fault-injection drills): validation
// arms of the POST body, the panic button, and the two e2e enforcement paths —
// api-request injection in the edge middleware (including the /v1/faults
// exemption that keeps the drill observable) and task-dispatch injection riding
// the real requeue machinery. Each fact builds its own factory so injector
// state never leaks between tests.
public sealed class FaultApiTests
{
	private sealed class TestFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
			});
		}
	}

	private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
	{
		HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}

	private static async Task<FaultView> ArmAsync(HttpClient client, object body)
	{
		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/faults", body);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		FaultView? view = await response.Content.ReadFromJsonAsync<FaultView>();
		Assert.NotNull(view);
		return view;
	}

	[Fact]
	public async Task Faults_AuthRequired_AllFourEndpoints()
	{
		await using TestFactory factory = new();
		using HttpClient client = factory.CreateClient(); // no bearer token

		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/faults")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/v1/faults", new { kind = "api-request", mode = "error" })).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/v1/faults/fault-x")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/v1/faults")).StatusCode);
	}

	[Fact]
	public async Task GetFaults_EmptyByDefault()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);

		using HttpResponseMessage response = await client.GetAsync("/v1/faults");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());
	}

	[Theory]
	[InlineData(null, "error", null, "kind must be")]
	[InlineData("chaos", "error", null, "kind must be")]
	[InlineData("task-dispatch", "chaos", null, "mode must be")]
	[InlineData("api-request", null, null, "mode must be")]
	[InlineData("task-dispatch", "error", "route", "route/method selectors")]
	[InlineData("task-dispatch", "error", "method", "route/method selectors")]
	[InlineData("api-request", "error", "action", "action/region selectors")]
	[InlineData("api-request", "error", "region", "action/region selectors")]
	public async Task PostFaults_InvalidBody_Returns400WithMessage(string? kind, string? mode, string? selector, string expectedMessage)
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		object body = new
		{
			kind,
			mode,
			route = selector == "route" ? "/v1/agents" : null,
			method = selector == "method" ? "GET" : null,
			action = selector == "action" ? "login" : null,
			region = selector == "region" ? "local" : null,
		};

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/faults", body);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
		Assert.StartsWith(expectedMessage, error);
	}

	[Theory]
	[InlineData("httpStatus", 350, "httpStatus must be 400..599")]
	[InlineData("httpStatus", 700, "httpStatus must be 400..599")]
	[InlineData("delayMs", 0, "delayMs must be 1..60000")]
	[InlineData("delayMs", 99999, "delayMs must be 1..60000")]
	[InlineData("budget", 0, "budget must be 1..1000")]
	[InlineData("budget", 5000, "budget must be 1..1000")]
	[InlineData("ttlSeconds", 0, "ttlSeconds must be 1..3600")]
	[InlineData("ttlSeconds", 7200, "ttlSeconds must be 1..3600")]
	public async Task PostFaults_OutOfRangeBounds_Returns400(string field, int value, string expectedMessage)
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		// Build the JSON manually so the theory can target any bound field.
		string json = $$"""{ "kind": "api-request", "mode": "error", "{{field}}": {{value}} }""";

		using HttpResponseMessage response = await client.PostAsync("/v1/faults", new StringContent(json, null, "application/json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
		Assert.StartsWith(expectedMessage, error);
	}

	[Fact]
	public async Task PostFaults_MissingBody_Returns400()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);

		using HttpResponseMessage response = await client.PostAsync("/v1/faults", new StringContent("{}", null, "application/json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task PostFaults_ValidBody_AppliesDefaultsAndLists()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/faults", new { kind = "task-dispatch", mode = "delay", action = "login" });

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		FaultView? view = await response.Content.ReadFromJsonAsync<FaultView>();
		Assert.NotNull(view);
		Assert.Equal($"/v1/faults/{view.Id}", response.Headers.Location?.ToString());
		Assert.Equal("task-dispatch", view.Kind);
		Assert.Equal("delay", view.Mode);
		Assert.Equal("login", view.Action);
		Assert.Null(view.Region);
		Assert.Equal(500, view.HttpStatus);
		Assert.Equal(1000, view.DelayMs);
		Assert.Equal(1, view.BudgetRemaining);
		Assert.Equal(0, view.Fired);
		Assert.True(view.ExpiresAt - view.CreatedAt >= TimeSpan.FromMinutes(14));

		// GET lists the armed fault, and DELETE removes it (404 on repeat).
		FaultView[] listed = (await client.GetFromJsonAsync<FaultView[]>("/v1/faults"))!;
		FaultView armed = Assert.Single(listed);
		Assert.Equal(view.Id, armed.Id);

		Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/v1/faults/{view.Id}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/v1/faults/{view.Id}")).StatusCode);
		FaultView[] after = (await client.GetFromJsonAsync<FaultView[]>("/v1/faults"))!;
		Assert.Empty(after);
	}

	[Fact]
	public async Task DeleteAllFaults_PanicButton_ClearsEverything()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		await ArmAsync(client, new { kind = "api-request", mode = "error" });
		await ArmAsync(client, new { kind = "task-dispatch", mode = "delay" });

		ClearFaultsResponse? cleared = await client.DeleteFromJsonAsync<ClearFaultsResponse>("/v1/faults");

		Assert.NotNull(cleared);
		Assert.Equal(2, cleared.Removed);
		FaultView[] after = (await client.GetFromJsonAsync<FaultView[]>("/v1/faults"))!;
		Assert.Empty(after);
	}

	[Fact]
	public async Task ApiRequestErrorFault_InjectsIntoMatchedRoute_ThenSelfHeals()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);

		FaultView fault = await ArmAsync(client, new { kind = "api-request", mode = "error", route = "/v1/agents", httpStatus = 503, budget = 2 });

		// Two matched requests fail with the injected status and an identifying body.
		for (int i = 0; i < 2; i++)
		{
			using HttpResponseMessage injected = await client.GetAsync("/v1/agents");
			Assert.Equal(HttpStatusCode.ServiceUnavailable, injected.StatusCode);
			string error = (await injected.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
			Assert.Equal($"fault injection {fault.Id}", error);
		}

		// Budget exhausted: the route works again without touching anything.
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);

		// The drill's own control surface was never subject to the fault.
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/faults")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
		FaultView[] listed = (await client.GetFromJsonAsync<FaultView[]>("/v1/faults"))!;
		Assert.Empty(listed);
	}

	[Fact]
	public async Task ApiRequestErrorFault_NonMatchingRouteIsUntouched()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		await ArmAsync(client, new { kind = "api-request", mode = "error", route = "/v1/jobs", httpStatus = 503, budget = 5 });

		Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/agents")).StatusCode);
		FaultView[] listed = (await client.GetFromJsonAsync<FaultView[]>("/v1/faults"))!;
		Assert.Equal(5, Assert.Single(listed).BudgetRemaining); // budget not consumed by the unmatched route
	}

	[Fact]
	public async Task ApiRequestDelayFault_AddsLatencyAndRecordsInflatedDuration()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		await ArmAsync(client, new { kind = "api-request", mode = "delay", route = "/v1/agents", delayMs = 300, budget = 1 });

		Stopwatch stopwatch = Stopwatch.StartNew();
		using HttpResponseMessage response = await client.GetAsync("/v1/agents");
		stopwatch.Stop();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode); // delay, not failure
		Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(300), $"elapsed {stopwatch.Elapsed} should include the injected 300 ms delay");

		// The duration family for the route carries the inflated sum (>= 0.3s).
		string metrics = await client.GetStringAsync("/metrics");
		string sumLine = metrics.Split('\n').Single(l => l.StartsWith("vapor_controlplane_http_request_duration_seconds_sum{method=\"GET\",route=\"/v1/agents\"} ", StringComparison.Ordinal));
		double sumSeconds = double.Parse(sumLine[(sumLine.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
		Assert.True(sumSeconds >= 0.3, $"duration sum {sumSeconds}s should include the injected 300 ms delay");
	}

	[Fact]
	public async Task FaultInjection_IsObservableInMetrics()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		await ArmAsync(client, new { kind = "api-request", mode = "error", route = "/v1/agents", httpStatus = 503, budget = 1 });
		await ArmAsync(client, new { kind = "task-dispatch", mode = "delay" });

		using HttpResponseMessage injected = await client.GetAsync("/v1/agents");
		Assert.Equal(HttpStatusCode.ServiceUnavailable, injected.StatusCode);

		string metrics = await client.GetStringAsync("/metrics");
		Assert.Contains("vapor_controlplane_fault_injections_total{kind=\"api-request\",mode=\"error\"} 1", metrics);
		Assert.Contains("vapor_controlplane_faults_armed 1", metrics);
		// The injected 503 is recorded in the RED family like any other response.
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/agents\",status=\"503\"} 1", metrics);
	}

	[Fact]
	public async Task TaskDispatchFaultSelection_RejectedOnTheApiRequestPlane()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);

		// task-dispatch selector on the api-request kind: rejected so the fault
		// cannot silently never fire.
		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/faults", new { kind = "api-request", mode = "error", action = "login" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task FaultDrills_WriteAuditEntries()
	{
		await using TestFactory factory = new();
		using HttpClient client = AdminClient(factory);
		FaultView fault = await ArmAsync(client, new { kind = "api-request", mode = "error" });
		await client.DeleteAsync($"/v1/faults/{fault.Id}");
		await client.DeleteAsync("/v1/faults");

		// The action filter is exact-match, so each drill action is queried by name.
		foreach (string action in new[] { "faults.enable", "faults.disable", "faults.clear" })
		{
			using JsonDocument doc = JsonDocument.Parse(await client.GetStringAsync($"/v1/audit/logs?action={action}"));
			Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 1, $"{action} should be persisted");
			Assert.Equal(action, doc.RootElement.GetProperty("logs")[0].GetProperty("action").GetString());
		}
	}
}
