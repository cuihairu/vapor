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

public sealed class CrawlApiTests
{
	// ── auth: every crawl endpoint is admin-guarded ──

	[Theory]
	[InlineData("GET", "/v1/crawl/plans")]
	[InlineData("POST", "/v1/crawl/plans")]
	[InlineData("GET", "/v1/crawl/plans/p1")]
	[InlineData("PUT", "/v1/crawl/plans/p1")]
	[InlineData("DELETE", "/v1/crawl/plans/p1")]
	[InlineData("POST", "/v1/crawl/plans/p1/trigger")]
	[InlineData("GET", "/v1/crawl/plans/p1/runs")]
	[InlineData("GET", "/v1/crawl/results")]
	public async Task CrawlEndpoints_WithoutToken_Return401(string method, string uri)
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, new HttpMethod(method), uri, body: new { }, token: null);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	// ── create ──

	public static IEnumerable<object?[]> InvalidCreatePayloads => new List<object?[]>
	{
		new object?[] { new { appIds = new List<string> { "570" } } },                                     // missing name
		new object?[] { new { name = "x" } },                                                              // missing app_ids
		new object?[] { new { name = "x", appIds = new List<string>() } },                                 // empty app_ids
		new object?[] { new { name = "x", appIds = new List<string> { "abc" } } },                         // non-numeric entry
		new object?[] { new { name = "x", appIds = new List<string> { "0" } } },                           // zero appid
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, shardSize = 0 } },          // shard below 1
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, shardSize = 201 } },        // shard above 200
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, intervalMs = -1 } },        // negative pacing
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, intervalMs = 60001 } },     // pacing above 60s
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, intervalSeconds = 3 } },    // below 5s minimum
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, cron = "not-a-cron" } },    // malformed cron
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, cron = "*/5 * * * *", intervalSeconds = 60 } }, // mutually exclusive
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, accounts = new List<string> { "ghost" } } },    // undeclared account
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, overrides = new Dictionary<string, string> { ["570"] = "ghost" } } }, // undeclared override target
		new object?[] { new { name = "x", appIds = new List<string> { "570" }, overrides = new Dictionary<string, string> { ["zz"] = "alice" } } },  // non-numeric override key
	};

	[Theory]
	[MemberData(nameof(InvalidCreatePayloads))]
	public async Task CreatePlan_InvalidRequest_Returns400(object payload)
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		SeedAccounts(factory);

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans", body: payload);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task CreatePlan_OverPlanLimit_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		var payload = new
		{
			name = "too big",
			appIds = Enumerable.Range(1, 501).Select(i => i.ToString()).ToList()
		};

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans", body: payload);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task CreatePlan_OneShot_IsDueImmediately()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		SeedAccounts(factory);

		var payload = new
		{
			name = "Top free",
			appIds = new List<string> { "570", "730" },
			accounts = new List<string> { "alice" },
			overrides = new Dictionary<string, string> { ["730"] = "bob" },
			shardSize = 10,
			cc = "de"
		};

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans", body: payload);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement plan = doc.RootElement;
		Assert.False(string.IsNullOrWhiteSpace(plan.GetProperty("id").GetString()));
		Assert.Equal("Top free", plan.GetProperty("name").GetString());
		Assert.Equal(2, plan.GetProperty("appIds").GetArrayLength());
		Assert.Equal(JsonValueKind.Number, plan.GetProperty("appIds")[0].ValueKind);
		Assert.Equal("bob", plan.GetProperty("overrides").GetProperty("730").GetString());
		Assert.Equal("de", plan.GetProperty("cc").GetString());
		Assert.True(plan.TryGetProperty("nextRunAt", out _), "one-shot plans must be armed immediately");

		HttpResponseMessage list = await SendAsync(client, HttpMethod.Get, "/v1/crawl/plans");
		using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
		Assert.Equal(1, listDoc.RootElement.GetProperty("plans").GetArrayLength());
	}

	[Fact]
	public async Task CreatePlan_RecurringStartNowFalse_LeavesCursorUnset()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		var payload = new { name = "nightly", appIds = new List<string> { "570" }, cron = "0 3 * * *", startNow = false };

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans", body: payload);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.False(doc.RootElement.TryGetProperty("nextRunAt", out _), "StartNow=false must not arm the schedule");
	}

	[Fact]
	public async Task CreatePlan_RecurringWithoutStartNow_ArmsImmediately()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		var payload = new { name = "hourly", appIds = new List<string> { "570" }, intervalSeconds = 3600 };

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans", body: payload);

		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(doc.RootElement.TryGetProperty("nextRunAt", out _), "recurring plans default to StartNow=true");
	}

	// ── get / update / delete ──

	[Fact]
	public async Task GetPlan_Missing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/v1/crawl/plans/nope");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task UpdatePlan_MergesOmittedFieldsAndRearmsOnStartNow()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		string planId = await CreatePlanAsync(client, new
		{
			name = "orig",
			appIds = new List<string> { "570", "730" },
			intervalSeconds = 60,
			startNow = false
		});

		HttpResponseMessage rename = await SendAsync(client, HttpMethod.Put, $"/v1/crawl/plans/{planId}", body: new { name = "Renamed" });
		Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
		using var renamed = JsonDocument.Parse(await rename.Content.ReadAsStringAsync());
		Assert.Equal("Renamed", renamed.RootElement.GetProperty("name").GetString());
		Assert.Equal(2, renamed.RootElement.GetProperty("appIds").GetArrayLength()); // untouched by the rename
		Assert.Equal(60, renamed.RootElement.GetProperty("intervalSeconds").GetInt32());
		Assert.False(renamed.RootElement.TryGetProperty("nextRunAt", out _), "cursor stays put without StartNow");

		HttpResponseMessage rearm = await SendAsync(client, HttpMethod.Put, $"/v1/crawl/plans/{planId}", body: new { startNow = true });
		Assert.Equal(HttpStatusCode.OK, rearm.StatusCode);
		using var rearmed = JsonDocument.Parse(await rearm.Content.ReadAsStringAsync());
		Assert.True(rearmed.RootElement.TryGetProperty("nextRunAt", out _), "StartNow=true re-arms the cursor");
	}

	[Fact]
	public async Task UpdatePlan_Missing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Put, "/v1/crawl/plans/nope", body: new { name = "x" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task DeletePlan_RemovesPlanButKeepsResults()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		string planId = await CreatePlanAsync(client, new { name = "doomed", appIds = new List<string> { "570" } });
		SqliteCrawlStore crawl = factory.Services.GetRequiredService<SqliteCrawlStore>();
		await crawl.AddResultAsync(MakeRow(planId, "run-1"));

		HttpResponseMessage deleted = await SendAsync(client, HttpMethod.Delete, $"/v1/crawl/plans/{planId}");
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		HttpResponseMessage get = await SendAsync(client, HttpMethod.Get, $"/v1/crawl/plans/{planId}");
		Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

		HttpResponseMessage results = await SendAsync(client, HttpMethod.Get, $"/v1/crawl/results?planId={planId}");
		using var resultsDoc = JsonDocument.Parse(await results.Content.ReadAsStringAsync());
		Assert.Equal(1, resultsDoc.RootElement.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task DeletePlan_Missing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Delete, "/v1/crawl/plans/nope");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	// ── trigger / runs ──

	[Fact]
	public async Task TriggerPlan_RearmsCursor()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		string planId = await CreatePlanAsync(client, new { name = "manual", appIds = new List<string> { "570" }, startNow = false });

		HttpResponseMessage trigger = await SendAsync(client, HttpMethod.Post, $"/v1/crawl/plans/{planId}/trigger");

		Assert.Equal(HttpStatusCode.Accepted, trigger.StatusCode);
		HttpResponseMessage get = await SendAsync(client, HttpMethod.Get, $"/v1/crawl/plans/{planId}");
		using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.True(doc.RootElement.TryGetProperty("nextRunAt", out _), "trigger must re-arm the due cursor");
	}

	[Fact]
	public async Task TriggerPlan_Disabled_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		string planId = await CreatePlanAsync(client, new { name = "paused", appIds = new List<string> { "570" } });

		await SendAsync(client, HttpMethod.Put, $"/v1/crawl/plans/{planId}", body: new { enabled = false });
		HttpResponseMessage trigger = await SendAsync(client, HttpMethod.Post, $"/v1/crawl/plans/{planId}/trigger");

		Assert.Equal(HttpStatusCode.BadRequest, trigger.StatusCode);
	}

	[Fact]
	public async Task TriggerPlan_Missing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans/nope/trigger");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task ListRuns_AggregatedNewestFirst()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		string planId = await CreatePlanAsync(client, new { name = "runs", appIds = new List<string> { "570" } });
		SqliteCrawlStore crawl = factory.Services.GetRequiredService<SqliteCrawlStore>();
		await crawl.AddResultAsync(MakeRow(planId, "run-1", fetchedAtMs: 1000));
		await crawl.AddResultAsync(MakeRow(planId, "run-1", appId: 730, ok: false, error: "boom", fetchedAtMs: 1010));
		await crawl.AddResultAsync(MakeRow(planId, "run-2", fetchedAtMs: 2000));

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, $"/v1/crawl/plans/{planId}/runs");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement runs = doc.RootElement.GetProperty("runs");
		Assert.Equal(2, runs.GetArrayLength());
		Assert.Equal("run-2", runs[0].GetProperty("runId").GetString());
		Assert.Equal(1, runs[0].GetProperty("total").GetInt32());
		Assert.Equal("run-1", runs[1].GetProperty("runId").GetString());
		Assert.Equal(2, runs[1].GetProperty("total").GetInt32());
		Assert.Equal(1, runs[1].GetProperty("ok").GetInt32());
		Assert.Equal(1, runs[1].GetProperty("failed").GetInt32());
	}

	[Fact]
	public async Task ListRuns_MissingPlan_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/v1/crawl/plans/nope/runs");

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	// ── results ──

	[Fact]
	public async Task Results_FiltersPaginateAndExposeData()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		string planId = await CreatePlanAsync(client, new { name = "results", appIds = new List<string> { "570", "730", "440" } });
		SqliteCrawlStore crawl = factory.Services.GetRequiredService<SqliteCrawlStore>();
		var data = JsonDocument.Parse("""{"name":"Dota 2","is_free":true}""").RootElement.Clone();
		await crawl.AddResultAsync(MakeRow(planId, "run-1", appId: 570, account: "alice", ok: true, data: data));
		await crawl.AddResultAsync(MakeRow(planId, "run-1", appId: 730, account: "bob", ok: false, error: "boom"));
		await crawl.AddResultAsync(MakeRow(planId, "run-2", appId: 440, account: "alice", ok: true));

		// failures only
		HttpResponseMessage failures = await SendAsync(client, HttpMethod.Get, "/v1/crawl/results?ok=false");
		using var failuresDoc = JsonDocument.Parse(await failures.Content.ReadAsStringAsync());
		Assert.Equal(1, failuresDoc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal(730, failuresDoc.RootElement.GetProperty("results")[0].GetProperty("appId").GetInt32());
		Assert.Equal("boom", failuresDoc.RootElement.GetProperty("results")[0].GetProperty("error").GetString());

		// by account
		HttpResponseMessage byAccount = await SendAsync(client, HttpMethod.Get, "/v1/crawl/results?account=alice");
		using var byAccountDoc = JsonDocument.Parse(await byAccount.Content.ReadAsStringAsync());
		Assert.Equal(2, byAccountDoc.RootElement.GetProperty("total").GetInt32());

		// by run + paging: total reflects the filter, page carries one row
		HttpResponseMessage paged = await SendAsync(client, HttpMethod.Get, "/v1/crawl/results?runId=run-1&limit=1&offset=0");
		using var pagedDoc = JsonDocument.Parse(await paged.Content.ReadAsStringAsync());
		Assert.Equal(2, pagedDoc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal(1, pagedDoc.RootElement.GetProperty("results").GetArrayLength());
		Assert.Equal(1, pagedDoc.RootElement.GetProperty("limit").GetInt32());

		// payload data survives the round trip as a camelCase JSON object
		HttpResponseMessage byApp = await SendAsync(client, HttpMethod.Get, "/v1/crawl/results?appId=570");
		using var byAppDoc = JsonDocument.Parse(await byApp.Content.ReadAsStringAsync());
		JsonElement dataOut = byAppDoc.RootElement.GetProperty("results")[0].GetProperty("data");
		Assert.Equal("Dota 2", dataOut.GetProperty("name").GetString());
		Assert.True(dataOut.GetProperty("is_free").GetBoolean());
	}

	// ── plumbing ──

	private static CrawlTestFactory CreateFactory() => new();

	private static void SeedAccounts(CrawlTestFactory factory)
	{
		AccountStore accounts = factory.Services.GetRequiredService<AccountStore>();
		accounts.Upsert("alice", enabled: true, AccountDesiredState.Idle, null, "us-east", null, null);
		accounts.Upsert("bob", enabled: true, AccountDesiredState.Idle, null, "eu-west", null, null);
	}

	private static async Task<string> CreatePlanAsync(HttpClient client, object payload)
	{
		HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/v1/crawl/plans", body: payload);
		Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return doc.RootElement.GetProperty("id").GetString()!;
	}

	private static CrawlResultRow MakeRow(
		string planId,
		string runId,
		uint appId = 570,
		string account = "alice",
		bool ok = true,
		string? error = null,
		long fetchedAtMs = 1000,
		JsonElement? data = null) => new(
		Id: 0,
		PlanId: planId,
		RunId: runId,
		AppId: appId,
		Account: account,
		JobId: "job-1",
		Ok: ok,
		Error: error,
		Data: data,
		FetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs));

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string uri, object? body = null, string? token = "admin-token")
	{
		using var request = new HttpRequestMessage(method, uri);
		if (token != null)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		if (body != null)
		{
			request.Content = JsonContent.Create(body);
		}

		return await client.SendAsync(request);
	}

	private sealed class CrawlTestFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.RemoveAll<SqliteCrawlStore>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.AddSingleton<SqliteCrawlStore>(sp => new SqliteCrawlStore(":memory:"));

				// The crawl worker would claim freshly created plans and dispatch
				// jobs no fake agent answers; these tests exercise the REST surface.
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
