using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Covers the Program.cs endpoint branches the happy-path suites don't reach:
/// auth failures on every admin route, per-route validation and not-found
/// responses, SSE streams, the agent WebSocket lifecycle, audit-persistence
/// failure handling and the live (non-JsonElement) task-output shapes.
/// </summary>
public sealed class ProgramBranchCoverageTests
{
	// ── auth matrix ──────────────────────────────────────────────────────────

	[Theory]
	[InlineData("GET", "/v1/agents")]
	[InlineData("PUT", "/v1/config/global")]
	[InlineData("PUT", "/v1/config/account/alice")]
	[InlineData("GET", "/v1/accounts/alice")]
	[InlineData("DELETE", "/v1/accounts/alice")]
	[InlineData("POST", "/v1/accounts/alice/enable")]
	[InlineData("POST", "/v1/accounts/alice/disable")]
	[InlineData("GET", "/v1/accounts/alice/inventory")]
	[InlineData("POST", "/v1/accounts/alice/confirmations/accept-all")]
	[InlineData("POST", "/v1/accounts/alice/loot")]
	[InlineData("GET", "/v1/accounts/alice/duplicates")]
	[InlineData("POST", "/v1/accounts/alice/swap-offers")]
	[InlineData("POST", "/v1/accounts/alice/licenses")]
	[InlineData("POST", "/v1/jobs")]
	[InlineData("GET", "/v1/jobs")]
	[InlineData("GET", "/v1/jobs/job-1")]
	[InlineData("POST", "/v1/jobs/job-1/cancel")]
	[InlineData("GET", "/v1/jobs/job-1/events")]
	[InlineData("GET", "/v1/jobs/events")]
	[InlineData("GET", "/v1/sessions/events")]
	[InlineData("GET", "/v1/auth/challenges/events")]
	[InlineData("GET", "/v1/auth/challenges")]
	[InlineData("POST", "/v1/auth/challenges/alice/code")]
	[InlineData("GET", "/v1/agents/status")]
	[InlineData("POST", "/v1/sessions/events")]
	[InlineData("GET", "/v1/sessions")]
	[InlineData("GET", "/v1/agent/ws")]
	public async Task AdminRoutes_WithoutToken_Return401(string method, string path)
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await SendAsync(client, method, path, body: new { });

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Bearer admin-token", HttpStatusCode.OK)]
	[InlineData("admin-token", HttpStatusCode.OK)]
	[InlineData("   ", HttpStatusCode.Unauthorized)]
	public async Task AuthorizationQueryParameter_IsAcceptedLikeHeader(string queryValue, HttpStatusCode expected)
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync($"/v1/sessions?authorization={Uri.EscapeDataString(queryValue)}");

		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, object? body)
	{
		return method switch
		{
			"GET" => await client.GetAsync(path),
			"DELETE" => await client.DeleteAsync(path),
			"POST" => await client.PostAsJsonAsync(path, body),
			_ => await client.PutAsJsonAsync(path, body)
		};
	}

	// ── config endpoints ─────────────────────────────────────────────────────

	[Fact]
	public async Task PutAccountConfig_WhitespaceName_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/config/account/%20", new { enabled = true });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("account name is required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task PutGlobalConfig_MixedSettingValueTypes_RoundTrip()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		// The audit entry's actor comes from X-Forwarded-For when present.
		client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/config/global", new
		{
			settings = new Dictionary<string, object?>
			{
				["enabled"] = true,
				["name"] = "prod",
				["missing"] = null,
				["retries"] = 3
			},
			updatedBy = "tester"
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);

		// The audit write above recorded the forwarded actor (the connection IP stays local).
		IReadOnlyList<AuditEntry> audits = await factory.AuditStore!.QueryAsync(new AuditQuery(Action: "config.global.updated"), CancellationToken.None);
		AuditEntry entry = Assert.Single(audits);
		Assert.Equal("203.0.113.7", entry.Actor);

		using HttpResponseMessage get = await client.GetAsync("/v1/config");
		string body = await get.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal(4, doc.RootElement.GetProperty("global").GetProperty("settings").EnumerateObject().Count());
	}

	[Fact]
	public async Task AuditPersistenceFailure_IsSwallowedByEndpoints()
	{
		await using BranchFactory factory = new() { AuditStore = new ThrowingAuditStore() };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Generic store failure: the endpoint still succeeds, audit loss is only logged.
		ThrowingAuditStore store = (ThrowingAuditStore)factory.AuditStore!;
		store.Throw = new InvalidOperationException("disk full");
		using HttpResponseMessage ok = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });
		Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

		// Cancellation is not swallowed: it propagates as a failed request.
		store.Throw = new OperationCanceledException();
		using HttpResponseMessage canceled = await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "offline" });
		Assert.Equal(HttpStatusCode.InternalServerError, canceled.StatusCode);
	}

	// ── account endpoints ────────────────────────────────────────────────────

	[Fact]
	public async Task PutAccount_WhitespaceName_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/accounts/%20", new { desiredState = "offline" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("account name is required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AccountMutations_OnUndeclaredAccount_Return404()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage removed = await client.DeleteAsync("/v1/accounts/ghost");
		using HttpResponseMessage enabled = await client.PostAsync("/v1/accounts/ghost/enable", content: null);
		using HttpResponseMessage disabled = await client.PostAsync("/v1/accounts/ghost/disable", content: null);

		Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, enabled.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
	}

	[Fact]
	public async Task Inventory_ValidationAndPendingResponses()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage undeclared = await client.GetAsync("/v1/accounts/ghost/inventory");
			using HttpResponseMessage emptyList = await client.GetAsync("/v1/accounts/alice/inventory?appIds=,&marketableOnly=true");
			using HttpResponseMessage pending = await client.GetAsync("/v1/accounts/alice/inventory?appIds=753&marketableOnly=true");

			Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
			Assert.Equal(HttpStatusCode.BadRequest, emptyList.StatusCode);
			Assert.Contains("at least one app", await emptyList.Content.ReadAsStringAsync());
			Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Loot_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.PostAsJsonAsync("/v1/accounts/ghost/loot", new { partnerSteamId = "76561198000000001" });
		using HttpResponseMessage badPartner = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "not-a-number" });

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badPartner.StatusCode);
		Assert.Contains("positive 64-bit SteamID", await badPartner.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Loot_WithMessageAndAppIds_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new
			{
				partnerSteamId = "76561198000000001",
				message = "  thanks!  ",
				appIds = new[] { 753, 730 }
			});

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

			// The dispatched payload carries the trimmed message and the app id list.
			IJobStore store = factory.Services.GetRequiredService<IJobStore>();
			JobWithTasks job = await store.GetJob(Assert.Single(await store.ListJobs(10, null, CancellationToken.None)).Id, CancellationToken.None);
			JsonElement payload = (JsonElement)job.Tasks[0].Payload!["message"]!;
			Assert.Equal("thanks!", payload.GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Loot_PlainSuccessWithoutConfirmationFlag_SkipsConfirm()
	{
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(new Dictionary<string, object?> { ["ok"] = true }) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76561198000000001" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
		Assert.False(doc.RootElement.TryGetProperty("mobile_confirmation", out _));
	}

	[Fact]
	public async Task Loot_RequiresConfirmationWithLiveValueTypes_AutoConfirms()
	{
		// Live bool/string values (not the JsonElement shapes a SQLite round-trip
		// produces) exercise the tolerant output readers on their native branches.
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = "43591234567890"
		};
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(output, confirmOutput: new Dictionary<string, object?> { ["confirmed"] = true }) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000001" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.True(confirm.GetProperty("confirmed").GetBoolean());
	}

	[Fact]
	public async Task Loot_RequiresConfirmationWithoutOfferId_SkipsConfirm()
	{
		Dictionary<string, object?> output = new() { ["requires_mobile_confirmation"] = true };
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(output) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000002" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.False(doc.RootElement.TryGetProperty("mobile_confirmation", out _));
	}

	[Fact]
	public async Task Loot_ConfirmFails_ReportsErrorInMobileConfirmation()
	{
		// A numeric trade_offer_id survives the JSON round-trip as a JsonElement
		// number — the fallback ToString branch of the output reader.
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = 43591234567890L
		};
		await using BranchFactory factory = new()
		{
			JobStore = MakeScriptedStore(output, confirmError: "no identity secret is stored for 'alice'")
		};
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000003" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.False(confirm.GetProperty("confirmed").GetBoolean());
		Assert.Contains("identity secret", confirm.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Duplicates_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.GetAsync("/v1/accounts/ghost/duplicates");
		using HttpResponseMessage badEntry = await client.GetAsync("/v1/accounts/alice/duplicates?appIds=abc");
		using HttpResponseMessage emptyList = await client.GetAsync("/v1/accounts/alice/duplicates?appIds=,");

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badEntry.StatusCode);
		Assert.Contains("positive app ids", await badEntry.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.BadRequest, emptyList.StatusCode);
		Assert.Contains("at least one app", await emptyList.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task SwapOffers_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.PostAsJsonAsync("/v1/accounts/ghost/swap-offers", new { partnerSteamId = "76561198000000100" });
		using HttpResponseMessage badPartner = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new { partnerSteamId = "0" });

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badPartner.StatusCode);
		Assert.Contains("positive 64-bit SteamID", await badPartner.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task SwapOffers_TradeUrlWithAllOptionalFields_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new
			{
				tradeUrl = "https://s.team/a/1234567890/abcdefgh",
				message = "swap?",
				appIds = new[] { 753 },
				keep = 1,
				maxSwaps = 5,
				send = true
			});

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

			IJobStore store = factory.Services.GetRequiredService<IJobStore>();
			JobWithTasks job = await store.GetJob(Assert.Single(await store.ListJobs(10, null, CancellationToken.None)).Id, CancellationToken.None);
			JsonElement tradeUrl = (JsonElement)job.Tasks[0].Payload!["trade_url"]!;
			Assert.StartsWith("https://s.team/a/", tradeUrl.GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task SwapOffers_SendConfirmFails_ReportsErrorInMobileConfirmation()
	{
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = "43591234567891"
		};
		await using BranchFactory factory = new()
		{
			JobStore = MakeScriptedStore(output, confirmError: "confirmation window closed")
		};
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new
		{
			partnerSteamId = "76561198000000100",
			send = true
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.False(confirm.GetProperty("confirmed").GetBoolean());
		Assert.Equal("confirmation window closed", confirm.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Licenses_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.PostAsJsonAsync("/v1/accounts/ghost/licenses", new { appIds = new[] { 753 } });
		using HttpResponseMessage badAppId = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { appIds = new[] { 0 } });

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badAppId.StatusCode);
		Assert.Contains("app_ids must be positive", await badAppId.Content.ReadAsStringAsync());
	}

	// ── jobs endpoints ───────────────────────────────────────────────────────

	[Fact]
	public async Task CreateJob_MissingFields_Return400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage noAction = await client.PostAsJsonAsync("/v1/jobs", new { targets = new[] { "alice" } });
		using HttpResponseMessage noTargets = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping" });

		Assert.Equal(HttpStatusCode.BadRequest, noAction.StatusCode);
		Assert.Contains("action is required", await noAction.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.BadRequest, noTargets.StatusCode);
		Assert.Contains("targets is required", await noTargets.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task GetJob_UnknownId_Returns404()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/jobs/does-not-exist");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		Assert.Contains("job not found", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task CancelJob_UnknownId_Returns404()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsync("/v1/jobs/does-not-exist/cancel", content: null);

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task JobEventsStream_UnknownJob_Returns404Json()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/jobs/does-not-exist/events");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		Assert.Contains("job not found", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task JobEventsStream_DeliversPublishedEvents()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", targets = new[] { "alice" } });
		Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using var stream = await client.GetStreamAsync($"/v1/jobs/{jobId}/events");
		using var reader = new StreamReader(stream, Encoding.UTF8);

		Assert.Equal("event: ready", await reader.ReadLineAsync());
		Assert.Equal("data: {}", await reader.ReadLineAsync());
		Assert.True(string.IsNullOrEmpty(await reader.ReadLineAsync()));

		await WaitForJobSubscriptionAsync(factory.Events);
		factory.Events.Publish(jobId, "task.started", new Dictionary<string, object?> { ["taskId"] = "t-1" });

		Assert.Equal("event: task.started", await reader.ReadLineAsync());
		string data = await reader.ReadLineAsync() ?? string.Empty;
		Assert.Contains("\"taskId\":\"t-1\"", data);
	}

	// ── session + challenge endpoints ─────────────────────────────────────────

	[Fact]
	public async Task SessionEventsStream_DeliversEventsPostedThroughTheApi()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using var stream = await client.GetStreamAsync("/v1/sessions/events?accountName=alice");
		using var reader = new StreamReader(stream, Encoding.UTF8);

		Assert.Equal("event: ready", await reader.ReadLineAsync());
		Assert.Equal("data: {}", await reader.ReadLineAsync());
		Assert.True(string.IsNullOrEmpty(await reader.ReadLineAsync()));
		await factory.Events.WaitForSessionSubscriptionAsync();

		using var agentClient = factory.CreateClient();
		agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
		using HttpResponseMessage post = await agentClient.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "Connected",
			state = "Connected"
		});
		Assert.Equal(HttpStatusCode.OK, post.StatusCode);

		Assert.Equal("event: session.connected", await reader.ReadLineAsync());
		string data = await reader.ReadLineAsync() ?? string.Empty;
		Assert.Contains("\"accountName\":\"alice\"", data);
	}

	[Fact]
	public async Task SessionEvents_EventTypeNormalization_Variants()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Missing event type falls back to state_changed.
		using HttpResponseMessage missing = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			state = "Connected"
		});

		// Mixed-case unknown type goes through the snake-case converter.
		using HttpResponseMessage snake = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "Two-Factor Ready",
			state = "Waiting"
		});

		// Remaining mapped names.
		using HttpResponseMessage disconnected = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "Disconnected",
			state = "Disconnected"
		});
		using HttpResponseMessage twoFactor = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "TwoFactorCodeNeeded",
			state = "ConnectingWait2FA"
		});

		Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
		Assert.Equal(HttpStatusCode.OK, snake.StatusCode);
		Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
		Assert.Equal(HttpStatusCode.OK, twoFactor.StatusCode);

		// The tracker keeps the last normalized type per account.
		using HttpResponseMessage sessions = await client.GetAsync("/v1/sessions?account=alice");
		string body = await sessions.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("2fa_required", doc.RootElement.GetProperty("sessions")[0].GetProperty("eventType").GetString());
	}

	[Fact]
	public async Task SessionEvents_MissingAccountName_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			eventType = "StateChanged",
			state = "Connected"
		});

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("accountName is required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AuthCode_SubmissionVariants()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage noCode = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { type = "email" });
		using HttpResponseMessage defaultType = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { code = "123456" });
		using HttpResponseMessage totp = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { code = "123456", type = "totp" });
		using HttpResponseMessage invalidType = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { code = "123456", type = "sms" });

		Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
		Assert.Contains("code is required", await noCode.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.OK, defaultType.StatusCode);
		Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, invalidType.StatusCode);
		Assert.Contains("type must be one of", await invalidType.Content.ReadAsStringAsync());
	}

	// ── agent WebSocket lifecycle ─────────────────────────────────────────────

	[Fact]
	public async Task AgentWs_PlainGetWithoutUpgrade_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/agent/ws?agentId=agent-9&region=us-east");

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("websocket required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AgentWs_MissingQueryParameters_Returns400()
	{
		await using BranchFactory factory = new();
		_ = factory.CreateClient();

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?authorization=agent-token");

		await Assert.ThrowsAnyAsync<Exception>(() => wsClient.ConnectAsync(uri, CancellationToken.None));
	}

	[Fact]
	public async Task AgentWs_NonHelloFirstFrame_ClosesWithPolicyViolation()
	{
		await using BranchFactory factory = new();
		_ = factory.CreateClient();

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-9&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		// First frame is not a hello: the server closes with PolicyViolation.
		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat("t-1", 0, DateTimeOffset.UtcNow)), CancellationToken.None);
		byte[] buffer = new byte[1024];
		var segment = new ArraySegment<byte>(buffer);
		System.Net.WebSockets.WebSocketReceiveResult result = await ws.ReceiveAsync(segment, CancellationToken.None);

		Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, result.MessageType);
		Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, result.CloseStatus);

		// Complete the close handshake so the server handler runs to completion.
		await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
	}

	[Fact]
	public async Task AgentWs_FullLifecycle_RegistersAcceptsResultsAndUnregisters()
	{
		var store = new SqliteJobStore(":memory:");
		await using BranchFactory factory = new() { JobStore = store };
		using var adminClient = factory.CreateClient();
		adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-9&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		await WebSocketJson.Send(ws, new WSMessage("hello", new AgentHello("agent-9", "us-east", null, null), null, null), CancellationToken.None);

		// Give the server handler a moment to process the hello and register the agent.
		await Task.Delay(200);

		// The connection shows up in the registry…
		using HttpResponseMessage status = await adminClient.GetAsync("/v1/agents/status");
		string statusBody = await status.Content.ReadAsStringAsync();
		Assert.Contains("agent-9", statusBody);

		// …heartbeat for an unknown task is swallowed…
		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat("nope", 0, DateTimeOffset.UtcNow)), CancellationToken.None);

		// …a claimed task accepts heartbeats and results over the tunnel…
		using HttpResponseMessage created = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		JobTask claimed = (await store.ClaimNextQueuedTask("us-east", CancellationToken.None))!;
		Assert.Equal(JobTaskStatus.Running, claimed.Status);

		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat(claimed.Id, claimed.Attempt, DateTimeOffset.UtcNow)), CancellationToken.None);
		await WebSocketJson.Send(ws, new WSMessage("task_result", null, null,
			new TaskResult(claimed.Id, Success: true, Error: null, Output: new Dictionary<string, object?> { ["ok"] = true }, DateTimeOffset.UtcNow, claimed.Attempt)), CancellationToken.None);

		// …a sensitive action's result is audit-logged…
		// (the sensitive-action predicate matches CamelCase action names; dispatching
		// one verbatim via POST /v1/jobs drives the audited result path).
		using HttpResponseMessage sensitiveJob = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "GetInventory", region = "us-east", targets = new[] { "alice" } });
		string sensitiveJobId;
		using (JsonDocument doc = JsonDocument.Parse(await sensitiveJob.Content.ReadAsStringAsync()))
		{
			sensitiveJobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		JobTask sensitiveClaimed = (await store.ClaimNextQueuedTask("us-east", CancellationToken.None))!;
		await WebSocketJson.Send(ws, new WSMessage("task_result", null, null,
			new TaskResult(sensitiveClaimed.Id, Success: true, Error: null, Output: new Dictionary<string, object?> { ["items"] = 0 }, DateTimeOffset.UtcNow, sensitiveClaimed.Attempt)), CancellationToken.None);

		// …a result for an unknown task is swallowed…
		await WebSocketJson.Send(ws, new WSMessage("task_result", null, null,
			new TaskResult("nope", Success: true, Error: null, Output: null, DateTimeOffset.UtcNow)), CancellationToken.None);

		// …a claimed (running) task is visible via the jobs endpoint and its cancel
		// is pushed to the connected agent…
		using HttpResponseMessage runningJob = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string runningJobId;
		using (JsonDocument doc = JsonDocument.Parse(await runningJob.Content.ReadAsStringAsync()))
		{
			runningJobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		JobTask runningClaimed = (await store.ClaimNextQueuedTask("us-east", CancellationToken.None))!;
		Assert.Equal(JobTaskStatus.Running, runningClaimed.Status);

		using HttpResponseMessage jobView = await adminClient.GetAsync($"/v1/jobs/{runningJobId}");
		Assert.Equal(HttpStatusCode.OK, jobView.StatusCode);

		using HttpResponseMessage cancelRunning = await adminClient.PostAsync($"/v1/jobs/{runningJobId}/cancel", content: null);
		Assert.Equal(HttpStatusCode.OK, cancelRunning.StatusCode);

		// …canceling a queued job enqueues the cancel on the connected agent…
		using HttpResponseMessage queuedJob = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string queuedJobId;
		using (JsonDocument doc = JsonDocument.Parse(await queuedJob.Content.ReadAsStringAsync()))
		{
			queuedJobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using HttpResponseMessage cancel = await adminClient.PostAsync($"/v1/jobs/{queuedJobId}/cancel", content: null);
		Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

		// Give the read loop a moment to process the pipeline above, then check outcomes.
		JobWithTasks finished = await store.GetJob(jobId, CancellationToken.None);
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (finished.Tasks.Single(t => t.Id == claimed.Id).Status != JobTaskStatus.Finished && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(25);
			finished = await store.GetJob(jobId, CancellationToken.None);
		}

		Assert.Equal(JobTaskStatus.Finished, finished.Tasks.Single(t => t.Id == claimed.Id).Status);

		// The sensitive action's result was audit-logged with the job id and target
		// (written right after the result lands, so poll briefly).
		AuditEntry? sensitiveEntry = null;
		deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			IReadOnlyList<AuditEntry> sensitiveAudits = await factory.AuditStore!.QueryAsync(new AuditQuery(Action: "task.result.reported"), CancellationToken.None);
			if (sensitiveAudits.Count > 0)
			{
				sensitiveEntry = Assert.Single(sensitiveAudits);
				break;
			}

			await Task.Delay(25);
		}

		Assert.NotNull(sensitiveEntry);
		Assert.Equal("alice", sensitiveEntry.AccountName);
		Assert.Equal(sensitiveJobId, sensitiveEntry.JobId);

		// Closing the socket unregisters the agent. (The server's read loop treats a
		// close frame as an IOException and tears the handler down without completing
		// the close handshake, so abort the client side instead of CloseAsync.)
		ws.Abort();
		deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage final = await adminClient.GetAsync("/v1/agents/status");
			string body = await final.Content.ReadAsStringAsync();
			if (!body.Contains("agent-9"))
			{
				break;
			}

			await Task.Delay(25);
		}

		using HttpResponseMessage afterClose = await adminClient.GetAsync("/v1/agents/status");
		Assert.DoesNotContain("agent-9", await afterClose.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AgentWs_ClientCloseFrame_TearsDownHandlerAndUnregisters()
	{
		// A close frame surfaces as an IOException from WebSocketJson.Receive: the
		// handler must still fall through to its finally (unregister + disconnected
		// event) even though the server never completes the close handshake.
		await using BranchFactory factory = new();
		using var adminClient = factory.CreateClient();
		adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-7&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		await WebSocketJson.Send(ws, new WSMessage("hello", new AgentHello("agent-7", "us-east", null, null), null, null), CancellationToken.None);
		await Task.Delay(200);

		// CloseOutputAsync sends the close frame without waiting for the server's
		// half of the handshake (which the server intentionally never completes).
		await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage status = await adminClient.GetAsync("/v1/agents/status");
			if (!(await status.Content.ReadAsStringAsync()).Contains("agent-7"))
			{
				break;
			}

			await Task.Delay(25);
		}

		using HttpResponseMessage final = await adminClient.GetAsync("/v1/agents/status");
		Assert.DoesNotContain("agent-7", await final.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AgentWs_ConnectionAbortedDuringMessage_UnregistersThroughLoopExit()
	{
		// A heartbeat that outlives the connection lets the read loop evaluate its own
		// condition after the abort (RequestAborted cancelled) and exit normally into
		// the finally — the graceful-disconnect teardown, not the exception path.
		var store = new SqliteJobStore(":memory:");
		await using BranchFactory factory = new() { JobStore = new SlowHeartbeatStore(store) };
		using var adminClient = factory.CreateClient();
		adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-6&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		await WebSocketJson.Send(ws, new WSMessage("hello", new AgentHello("agent-6", "us-east", null, null), null, null), CancellationToken.None);
		await Task.Delay(200);
		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat("t-1", 0, DateTimeOffset.UtcNow)), CancellationToken.None);

		// The server is now inside the (deliberately slow) heartbeat; aborting here
		// cancels RequestAborted before the loop's condition is evaluated again.
		await Task.Delay(150);
		ws.Abort();

		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage status = await adminClient.GetAsync("/v1/agents/status");
			if (!(await status.Content.ReadAsStringAsync()).Contains("agent-6"))
			{
				break;
			}

			await Task.Delay(25);
		}

		using HttpResponseMessage final = await adminClient.GetAsync("/v1/agents/status");
		Assert.DoesNotContain("agent-6", await final.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task CancelJob_WithQueuedTaskAndNoAgent_ReturnsOk()
	{
		var store = new SqliteJobStore(":memory:");
		await using BranchFactory factory = new() { JobStore = store };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", targets = new[] { "alice" } });
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using HttpResponseMessage cancel = await client.PostAsync($"/v1/jobs/{jobId}/cancel", content: null);

		Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		Assert.All(job.Tasks, t => Assert.Equal(JobTaskStatus.Canceled, t.Status));
	}

	// ── remaining endpoint branches ──────────────────────────────────────────

	[Fact]
	public async Task GetAgents_ReturnsRegisteredAgents()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/agents");

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.Contains("agents", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task GetJob_ReturnsJobWithTasks()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using HttpResponseMessage resp = await client.GetAsync($"/v1/jobs/{jobId}");

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.Contains(jobId, await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task PutGlobalConfig_ReplacesSettings_AndAudits()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/config/global", new
		{
			settings = new Dictionary<string, object?> { ["farmedAppIds"] = new[] { 730 } },
			updatedBy = "operator"
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(await PollAuditAsync(factory, "config.global.updated"));
	}

	[Fact]
	public async Task PutAccountConfig_ReplacesAccountSettings_AndAudits()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/config/account/bob", new
		{
			enabled = false,
			region = "eu-west",
			labels = new[] { "veteran" },
			settings = new Dictionary<string, object?> { ["k"] = "v" },
			updatedBy = "operator"
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(await PollAuditAsync(factory, "config.account.updated"));
	}

	[Fact]
	public async Task DeclineTradeOffer_InvalidOfferId_Returns400()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/nope/decline", new { });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
	}

	[Fact]
	public async Task DeclineTradeOffer_UndeclaredAccount_Returns404()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/trade-offers/42/decline", new { });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_UndeclaredAccount_Returns404()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/confirmations/accept-all", new { });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task GetDuplicates_WhenTaskStaysQueued_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using HttpClient client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			// No agent responds, so the dispatch window closes with the task queued.
			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/duplicates");

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using JsonDocument doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task SessionEvents_MixedCaseEventType_FallsBackToSnakeCase()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "TwoFactor Ready",
			state = "Connecting",
			message = "hi"
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		AuditEntry? entry = await PollAuditAsync(factory, "session.event.received");
		Assert.NotNull(entry);
	}

	[Fact]
	public async Task AcceptTradeOffer_ConfirmResultAsString_TracksConfirmation()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using CancellationTokenSource cts = new();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
			new (string Action, bool Success, Dictionary<string, object?>? Output, string? Error)[]
			{
				("accept_trade_offer", true, new Dictionary<string, object?> { ["requires_mobile_confirmation"] = true }, null),
				// A JSON string booleans survives the SQLite round-trip as a string
				// JsonElement; the tolerant output reader must still parse it.
				("confirm_trade_offer", true, new Dictionary<string, object?> { ["confirmed"] = "true" }, null),
			}, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/42/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.GetProperty("mobile_confirmation").GetProperty("confirmed").GetBoolean());
	}

	// ── helpers ──────────────────────────────────────────────────────────────

	/// <summary>Polls the audit store until at least one entry with <paramref name="action"/> lands.</summary>
	private static async Task<AuditEntry?> PollAuditAsync(BranchFactory factory, string action)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			IReadOnlyList<AuditEntry> entries = await factory.AuditStore!.QueryAsync(new AuditQuery(Action: action), CancellationToken.None);
			if (entries.Count > 0)
			{
				return entries[^1];
			}

			await Task.Delay(25);
		}

		return null;
	}

	/// <summary>
	/// Simulates an agent working through a sequence of dispatched tasks: claims
	/// each queued task by action and completes it with the scripted outcome.
	/// </summary>
	private static async Task RespondToStepsAsync(
		IJobStore store,
		ReadOnlyMemory<(string Action, bool Success, Dictionary<string, object?>? Output, string? Error)> steps,
		CancellationToken ct)
	{
		for (int i = 0; i < steps.Length; i++)
		{
			(string action, bool success, Dictionary<string, object?>? output, string? error) step = steps.Span[i];
			while (!ct.IsCancellationRequested)
			{
				JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
				if (claimed is not null && claimed.Action == step.action)
				{
					await store.SetTaskResult(
						new TaskResult(claimed.Id, step.success, step.success ? null : step.error, step.success ? step.output : null, DateTimeOffset.UtcNow),
						ct);
					break;
				}

				await Task.Delay(25, ct);
			}
		}
	}

	private static async Task WaitForJobSubscriptionAsync(BranchEventBroker broker)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (broker.ActiveJobSubscriptions == 0)
		{
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException("job SSE subscription did not start within 5s");
			}

			await Task.Delay(10);
		}
	}

	/// <summary>
	/// A store whose jobs immediately complete with a caller-supplied output
	/// dictionary. Values keep their live .NET types (no SQLite JSON round-trip),
	/// which is the point: the API layer must tolerate both shapes.
	/// </summary>
	private static ScriptedJobStore MakeScriptedStore(
		Dictionary<string, object?> output,
		Dictionary<string, object?>? confirmOutput = null,
		string? confirmError = null)
	{
		return new ScriptedJobStore(output, confirmOutput, confirmError);
	}

	private sealed class ScriptedJobStore(
		Dictionary<string, object?> taskOutput,
		Dictionary<string, object?>? confirmOutput,
		string? confirmError) : IJobStore
	{
		private sealed record ScriptedTask(CreateJobRequest Request, JobTaskStatus Status, Dictionary<string, object?>? Output, string? Error);

		private readonly Dictionary<string, ScriptedTask> _jobs = new();
		private int _counter;

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
		{
			int n = Interlocked.Increment(ref _counter);
			string jobId = $"scripted-job-{n}";
			bool confirm = string.Equals(request.Action, AccountTaskRunner.ConfirmTradeOfferAction, StringComparison.Ordinal);
			var scripted = new ScriptedTask(
				request,
				confirm && confirmError is not null ? JobTaskStatus.Failed : JobTaskStatus.Finished,
				confirm ? (confirmError is null ? confirmOutput : null) : taskOutput,
				confirm ? confirmError : null);
			lock (_jobs)
			{
				_jobs[jobId] = scripted;
			}

			return Task.FromResult(BuildJob(jobId, scripted));
		}

		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
		{
			ScriptedTask scripted;
			lock (_jobs)
			{
				if (!_jobs.TryGetValue(jobId, out scripted!))
				{
					throw new NotFoundException("job not found");
				}
			}

			return Task.FromResult(BuildJob(jobId, scripted));
		}

		private static JobWithTasks BuildJob(string jobId, ScriptedTask scripted)
		{
			CreateJobRequest request = scripted.Request;
			var job = new Job(jobId, request.Action, request.Region, request.Targets, request.Meta, JobStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			var task = new JobTask($"{jobId}-t1", jobId, request.Targets[0], request.Action, request.Region, request.Payload, scripted.Status, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, scripted.Error, scripted.Output);
			return new JobWithTasks(job, [task]);
		}

		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobTask>>([]);
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskCancel>>([]);
		public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<JobTaskStatus, int>>(new Dictionary<JobTaskStatus, int>());
		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => Task.FromResult<Job?>(null);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => Task.FromResult<JobTask?>(null);
		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	private sealed class ThrowingAuditStore : IAuditStore
	{
		/// <summary>Next exception to throw; null persists nothing and succeeds.</summary>
		public Exception? Throw { get; set; }

		public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
		{
			return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
		}

		public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditEntry>>([]);
		public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
	}

	/// <summary>Event broker with live job and session channels (unlike the shared
	/// recording broker, whose session stream is intentionally empty).</summary>
	internal sealed class BranchEventBroker : IEventBroker
	{
		private readonly object _gate = new();
		private readonly List<(Channel<Event> Channel, string JobId)> _jobSubscriptions = [];
		private readonly List<(Channel<SessionEvent> Channel, string? AccountName)> _sessionSubscriptions = [];
		private readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _jobReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public int ActiveJobSubscriptions
		{
			get { lock (_gate) { return _jobSubscriptions.Count; } }
		}

		public Task WaitForJobSubscriptionAsync() => _jobReady.Task;
		public Task WaitForSessionSubscriptionAsync() => _sessionReady.Task;

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
			var evt = new Event(Guid.NewGuid().ToString("N"), jobId, type, DateTimeOffset.UtcNow, payload);
			List<ChannelWriter<Event>> writers;
			lock (_gate)
			{
				writers = _jobSubscriptions
					.Where(sub => sub.JobId == "*" || string.Equals(sub.JobId, jobId, StringComparison.Ordinal))
					.Select(sub => sub.Channel.Writer)
					.ToList();
			}

			foreach (ChannelWriter<Event> writer in writers)
			{
				writer.TryWrite(evt);
			}
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
			var evt = new SessionEvent(Guid.NewGuid().ToString("N"), accountName, eventType, state, message, DateTimeOffset.UtcNow);
			List<ChannelWriter<SessionEvent>> writers;
			lock (_gate)
			{
				writers = _sessionSubscriptions
					.Where(sub => sub.AccountName == null || string.Equals(sub.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
					.Select(sub => sub.Channel.Writer)
					.ToList();
			}

			foreach (ChannelWriter<SessionEvent> writer in writers)
			{
				writer.TryWrite(evt);
			}
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
		{
		}

		public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
		{
			var channel = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
			lock (_gate)
			{
				_jobSubscriptions.Add((channel, jobId));
				_jobReady.TrySetResult();
			}

			try
			{
				await foreach (Event evt in channel.Reader.ReadAllAsync(cancellationToken))
				{
					yield return evt;
				}
			}
			finally
			{
				lock (_gate)
				{
					for (int i = _jobSubscriptions.Count - 1; i >= 0; i--)
					{
						if (ReferenceEquals(_jobSubscriptions[i].Channel, channel))
						{
							_jobSubscriptions.RemoveAt(i);
						}
					}
				}

				channel.Writer.TryComplete();
			}
		}

		public async IAsyncEnumerable<SessionEvent> SubscribeSessions([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			var channel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
			lock (_gate)
			{
				_sessionSubscriptions.Add((channel, accountName));
				_sessionReady.TrySetResult();
			}

			try
			{
				await foreach (SessionEvent evt in channel.Reader.ReadAllAsync(cancellationToken))
				{
					yield return evt;
				}
			}
			finally
			{
				lock (_gate)
				{
					for (int i = _sessionSubscriptions.Count - 1; i >= 0; i--)
					{
						if (ReferenceEquals(_sessionSubscriptions[i].Channel, channel))
						{
							_sessionSubscriptions.RemoveAt(i);
						}
					}
				}

				channel.Writer.TryComplete();
			}
		}

		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			await Task.CompletedTask;
			yield break;
		}
	}
}

/// <summary>
/// Factory over the real composition root with store/broker/audit overrides and
/// hosted services removed (background dispatchers would race the in-test agent
/// fakes). Exposes the overrides for assertions.
/// </summary>
/// <summary>
/// Wraps a store so heartbeat handling takes long enough for a test to abort the
/// connection mid-message, forcing the agent tunnel's read loop to re-evaluate its
/// condition after the disconnect instead of dying on a receive exception.
/// </summary>
internal sealed class SlowHeartbeatStore : IJobStore
{
	private readonly IJobStore _inner;

	public SlowHeartbeatStore(IJobStore inner)
	{
		_inner = inner;
	}

	public async Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken)
	{
		// The delay must ignore the token: the whole point is to return after the
		// abort instead of unwinding through an OperationCanceledException.
		await Task.Delay(600).ConfigureAwait(false);
		return await _inner.HeartbeatTask(taskId, attempt, cancellationToken).ConfigureAwait(false);
	}

	public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => _inner.CreateJob(request, cancellationToken);
	public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => _inner.GetJob(jobId, cancellationToken);
	public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => _inner.ListJobs(limit, account, cancellationToken);
	public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => _inner.ListRecentTasksForTarget(target, limit, cancellationToken);
	public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => _inner.GetTaskStatusCounts(cancellationToken);
	public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => _inner.ClaimNextQueuedTask(region, cancellationToken);
	public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _inner.ListDueScheduledJobs(now, limit, cancellationToken);
	public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => _inner.HasActiveChildJob(templateJobId, cancellationToken);
	public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => _inner.TriggerScheduledJob(templateJobId, nextRunAt, extraMeta, cancellationToken);
	public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => _inner.AdvanceSchedule(templateJobId, nextRunAt, cancellationToken);
	public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => _inner.RequeueTask(taskId, retryDelay, cancellationToken);
	public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => _inner.RequeueStaleRunningTasks(taskLease, cancellationToken);
	public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => _inner.SetTaskResult(result, cancellationToken);
	public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => _inner.FailRunningTask(taskId, error, cancellationToken);
	public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => _inner.CancelJob(jobId, cancellationToken);
}

internal sealed class BranchFactory : WebApplicationFactory<Program>
{
	public ProgramBranchCoverageTests.BranchEventBroker Events { get; } = new();

	/// <summary>Optional store override; a fresh :memory: SQLite store is used when unset.</summary>
	public IJobStore? JobStore { get; init; }

	/// <summary>Optional audit store override (exposed for assertions and fault injection).</summary>
	public IAuditStore? AuditStore { get; internal set; }

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		BranchFactory self = this;
		builder.UseEnvironment("Development");
		builder.ConfigureServices(services =>
		{
			services.RemoveAll<IJobStore>();
			services.RemoveAll<IEventBroker>();
			services.RemoveAll<IAuditStore>();
			services.RemoveAll<IHostedService>();
			services.RemoveAll<IHostedLifecycleService>();
			services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
			services.AddSingleton<IJobStore>(sp => self.JobStore ?? new SqliteJobStore(":memory:"));
			services.AddSingleton<IAuditStore>(sp => self.AuditStore ??= new SqliteAuditStore(":memory:"));
			services.AddSingleton<IEventBroker>(self.Events);
		});
	}
}
