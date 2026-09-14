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

public sealed class AccountApiTests
{
	[Fact]
	public async Task Accounts_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage list = await client.GetAsync("/v1/accounts");
		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });

		Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_CreatesSpecAndReturnsIt()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "idle",
			idleApps = new[] { "730", "570" },
			region = "us-east",
			agentId = "agent-1",
			note = "farm bot"
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal("alice", spec.GetProperty("accountName").GetString());
		Assert.Equal("idle", spec.GetProperty("desiredState").GetString());
		Assert.Equal(2, spec.GetProperty("idleApps").GetArrayLength());
		Assert.Equal("us-east", spec.GetProperty("region").GetString());
		Assert.Equal("agent-1", spec.GetProperty("agentId").GetString());
		Assert.Equal(1, spec.GetProperty("version").GetProperty("version").GetInt32());
	}

	[Fact]
	public async Task PutAccount_InvalidIdleApp_ReturnsBadRequest()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			desiredState = "idle",
			idleApps = new[] { "not-a-number" }
		});

		Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_FarmState_AcceptsIdleAppsAsExclusionList()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "farm",
			idleApps = new[] { "730" }
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal("farm", spec.GetProperty("desiredState").GetString());
		Assert.Equal(1, spec.GetProperty("idleApps").GetArrayLength());
	}

	[Fact]
	public async Task PutAccount_ReplacesExistingSpec()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });
		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", region = "eu-west" });

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal(2, spec.GetProperty("version").GetProperty("version").GetInt32());
		Assert.Equal("offline", spec.GetProperty("desiredState").GetString());
		Assert.Equal("eu-west", spec.GetProperty("region").GetString());

		using HttpResponseMessage list = await client.GetAsync("/v1/accounts");
		string listBody = await list.Content.ReadAsStringAsync();
		Assert.Contains("\"accountName\":\"alice\"", listBody);
	}

	[Fact]
	public async Task GetAccount_ReturnsSpecAndMissingReturns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });

		using HttpResponseMessage found = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.OK, found.StatusCode);
		string body = await found.Content.ReadAsStringAsync();
		Assert.Contains("\"accountName\":\"alice\"", body);

		using HttpResponseMessage missing = await client.GetAsync("/v1/accounts/ghost");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task ListAccounts_FiltersByStateRegionAndAgent()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" }, region = "us-east", agentId = "agent-1" });
		await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "online", region = "us-east" });
		await client.PutAsJsonAsync("/v1/accounts/carol", new { desiredState = "offline", region = "eu-west", agentId = "agent-1" });

		using HttpResponseMessage byState = await client.GetAsync("/v1/accounts?state=idle");
		string byStateBody = await byState.Content.ReadAsStringAsync();
		Assert.Contains("alice", byStateBody);
		Assert.DoesNotContain("bob", byStateBody);
		Assert.DoesNotContain("carol", byStateBody);

		using HttpResponseMessage byRegion = await client.GetAsync("/v1/accounts?region=us-east");
		string byRegionBody = await byRegion.Content.ReadAsStringAsync();
		Assert.Contains("alice", byRegionBody);
		Assert.Contains("bob", byRegionBody);
		Assert.DoesNotContain("carol", byRegionBody);

		using HttpResponseMessage byAgent = await client.GetAsync("/v1/accounts?agent=agent-1");
		string byAgentBody = await byAgent.Content.ReadAsStringAsync();
		Assert.Contains("alice", byAgentBody);
		Assert.DoesNotContain("bob", byAgentBody);
		Assert.Contains("carol", byAgentBody);

		using HttpResponseMessage invalidState = await client.GetAsync("/v1/accounts?state=hopping");
		Assert.Equal(HttpStatusCode.BadRequest, invalidState.StatusCode);
	}

	[Fact]
	public async Task DisableAndEnable_ToggleWithoutTouchingDesiredState()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" } });

		using HttpResponseMessage disabled = await client.PostAsJsonAsync("/v1/accounts/alice/disable", new { });
		Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
		using var disabledDoc = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
		Assert.False(disabledDoc.RootElement.GetProperty("spec").GetProperty("enabled").GetBoolean());
		Assert.Equal("idle", disabledDoc.RootElement.GetProperty("spec").GetProperty("desiredState").GetString());

		using HttpResponseMessage enabled = await client.PostAsJsonAsync("/v1/accounts/alice/enable", new { });
		Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
		using var enabledDoc = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
		Assert.True(enabledDoc.RootElement.GetProperty("spec").GetProperty("enabled").GetBoolean());

		using HttpResponseMessage missing = await client.PostAsJsonAsync("/v1/accounts/ghost/disable", new { });
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task DeleteAccount_RemovesSpecAndIsAudited()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });

		using HttpResponseMessage deleted = await client.DeleteAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		using HttpResponseMessage missing = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=account.spec.updated");
		Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
		string auditBody = await audit.Content.ReadAsStringAsync();
		using var auditDoc = JsonDocument.Parse(auditBody);
		Assert.True(auditDoc.RootElement.GetProperty("total").GetInt32() >= 1);

		using HttpResponseMessage deletedAudit = await client.GetAsync("/v1/audit/logs?action=account.spec.removed");
		string deletedBody = await deletedAudit.Content.ReadAsStringAsync();
		using var deletedDoc = JsonDocument.Parse(deletedBody);
		Assert.Equal(1, deletedDoc.RootElement.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task GetAccount_ReturnsAggregateViewWithSessionChallengeAndTasks()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" }, region = "us-east" });

		// A session snapshot + a pending 2FA challenge via the normal event endpoint.
		using HttpResponseMessage evt = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "state_changed",
			state = "ConnectingWait2FA",
			message = "need 2fa"
		});
		Assert.Equal(HttpStatusCode.OK, evt.StatusCode);

		// A job targeting alice so the aggregate view has recent tasks.
		using HttpResponseMessage job = await client.PostAsJsonAsync("/v1/jobs", new { action = "login", targets = new[] { "alice" } });
		Assert.Equal(HttpStatusCode.Accepted, job.StatusCode);

		using HttpResponseMessage view = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.OK, view.StatusCode);

		string body = await view.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;

		Assert.Equal("idle", root.GetProperty("spec").GetProperty("desiredState").GetString());
		Assert.Equal("ConnectingWait2FA", root.GetProperty("session").GetProperty("state").GetString());
		Assert.Equal("2fa_required", root.GetProperty("pendingChallenge").GetProperty("challengeType").GetString());
		Assert.Equal(1, root.GetProperty("recentTasks").GetArrayLength());
		Assert.Equal("login", root.GetProperty("recentTasks")[0].GetProperty("action").GetString());
	}

	[Fact]
	public async Task GetAccount_MissingReturns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage missing = await client.GetAsync("/v1/accounts/ghost");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task ListJobs_SupportsAccountFilter()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PostAsJsonAsync("/v1/jobs", new { action = "login", targets = new[] { "alice" } });
		await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", targets = new[] { "bob" } });

		using HttpResponseMessage filtered = await client.GetAsync("/v1/jobs?account=alice");
		Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
		string body = await filtered.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(1, doc.RootElement.GetProperty("jobs").GetArrayLength());
		Assert.Equal("login", doc.RootElement.GetProperty("jobs")[0].GetProperty("action").GetString());
	}

	[Fact]
	public async Task ListSessions_SupportsAccountFilter()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PostAsJsonAsync("/v1/sessions/events", new { accountName = "alice", eventType = "state_changed", state = "Connected" });
		await client.PostAsJsonAsync("/v1/sessions/events", new { accountName = "bob", eventType = "state_changed", state = "Disconnected" });

		using HttpResponseMessage filtered = await client.GetAsync("/v1/sessions?account=alice");
		Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
		string body = await filtered.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(1, doc.RootElement.GetProperty("sessions").GetArrayLength());
		Assert.Equal("alice", doc.RootElement.GetProperty("sessions")[0].GetProperty("accountName").GetString());
	}

	[Fact]
	public async Task GetTradeOffers_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/trade-offers");

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task GetTradeOffers_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/trade-offers");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task GetTradeOffers_AgentReportsFinished_ReturnsOffers()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTradeOffersTaskAsync(store, success: true, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/trade-offers");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.NotEmpty(doc.RootElement.GetProperty("job_id").GetString()!);
		JsonElement offers = doc.RootElement.GetProperty("offers");
		Assert.Equal(1, offers.GetProperty("received_count").GetInt32());
		Assert.Equal("43591234567890", offers.GetProperty("received_offers")[0].GetProperty("trade_offer_id").GetString());
		Assert.Equal("Active", offers.GetProperty("received_offers")[0].GetProperty("state").GetString());
	}

	[Fact]
	public async Task GetTradeOffers_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTradeOffersTaskAsync(store, success: false, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/trade-offers");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("agent refused", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GetTradeOffers_StillPending_Returns202WithJobId()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/trade-offers");

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task AcceptTradeOffer_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage accept = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });
		using HttpResponseMessage decline = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/decline", new { });

		Assert.Equal(HttpStatusCode.Unauthorized, accept.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, decline.StatusCode);
	}

	[Fact]
	public async Task GetMarketListings_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/market/listings");

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task GetMarketListings_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/market/listings");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task GetMarketListings_AgentReportsFinished_ReturnsListings()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstMarketListingsTaskAsync(store, success: true, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/market/listings");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.NotEmpty(doc.RootElement.GetProperty("job_id").GetString()!);
		JsonElement page = doc.RootElement.GetProperty("market_listings");
		Assert.Equal(3, page.GetProperty("total_count").GetInt32());
		JsonElement listings = page.GetProperty("listings");
		Assert.Equal(1, listings.GetArrayLength());
		Assert.Equal("3547123456789012345", listings[0].GetProperty("listing_id").GetString());
		Assert.Equal(103, listings[0].GetProperty("price_cents").GetInt32());
		Assert.Equal(91, listings[0].GetProperty("seller_proceeds_cents").GetInt32());
	}

	[Fact]
	public async Task GetMarketListings_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstMarketListingsTaskAsync(store, success: false, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/market/listings");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("agent refused", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GetMarketListings_StillPending_Returns202WithJobId()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/market/listings");

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task AcceptTradeOffer_InvalidRequest_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage badOfferId = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/not-a-number/accept", new { partnerSteamId = "76561198000000001" });
		using HttpResponseMessage missingPartner = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { });

		Assert.Equal(HttpStatusCode.BadRequest, badOfferId.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, missingPartner.StatusCode);
	}

	[Fact]
	public async Task AcceptTradeOffer_AgentReportsFinished_ReturnsResult()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "accept_trade_offer", success: true,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890", ["requires_mobile_confirmation"] = false },
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("43591234567890", doc.RootElement.GetProperty("result").GetProperty("trade_offer_id").GetString());
	}

	[Fact]
	public async Task AcceptTradeOffer_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "accept_trade_offer", success: false, null, "offer is no longer active", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("no longer active", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DeclineTradeOffer_AgentReportsFinished_ReturnsResult()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "decline_trade_offer", success: true,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" },
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/decline", new { });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("43591234567890", doc.RootElement.GetProperty("result").GetProperty("trade_offer_id").GetString());
	}

	[Fact]
	public async Task AcceptTradeOffer_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task AcceptTradeOffer_RequiresConfirmation_AutoConfirms()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("accept_trade_offer", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "43591234567890",
					["requires_mobile_confirmation"] = true
				}, null),
			new FakeAgentStep("confirm_trade_offer", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "43591234567890",
					["confirmation_id"] = "111",
					["confirmed"] = true
				}, null)
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.GetProperty("result").GetProperty("requires_mobile_confirmation").GetBoolean());
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.True(confirm.GetProperty("attempted").GetBoolean());
		Assert.True(confirm.GetProperty("confirmed").GetBoolean());
		Assert.NotEmpty(confirm.GetProperty("job_id").GetString()!);
	}

	[Fact]
	public async Task AcceptTradeOffer_ConfirmFails_ReportsErrorButAcceptSucceeded()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("accept_trade_offer", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "43591234567890",
					["requires_mobile_confirmation"] = true
				}, null),
			new FakeAgentStep("confirm_trade_offer", false, null, "no identity secret is stored for 'alice'")
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		// The accept itself succeeded; the confirmation failure is reported honestly
		// inside mobile_confirmation instead of failing the whole request.
		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.True(confirm.GetProperty("attempted").GetBoolean());
		Assert.False(confirm.GetProperty("confirmed").GetBoolean());
		Assert.Contains("identity secret", confirm.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AcceptTradeOffer_NoConfirmationNeeded_SkipsConfirm()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("accept_trade_offer", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "43591234567890",
					["requires_mobile_confirmation"] = false
				}, null)
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/43591234567890/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		// mobile_confirmation stays absent (null, dropped by WhenWritingNull serialization).
		Assert.False(doc.RootElement.TryGetProperty("mobileConfirmation", out _) ||
					 doc.RootElement.TryGetProperty("mobile_confirmation", out _));
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_AgentReportsFinished_ReturnsResult()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "confirm_all_confirmations", success: true,
			new Dictionary<string, object?>
			{
				["operation"] = "allow",
				["total"] = 2,
				["succeeded"] = 2,
				["failed"] = 0,
				["results"] = new List<Dictionary<string, object?>>
				{
					new() { ["confirmation_id"] = "1", ["type"] = "trade", ["succeeded"] = true },
					new() { ["confirmation_id"] = "2", ["type"] = "market", ["succeeded"] = true }
				}
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/confirmations/accept-all", new { });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal(2, result.GetProperty("total").GetInt32());
		Assert.Equal(2, result.GetProperty("succeeded").GetInt32());
		Assert.Equal(0, result.GetProperty("failed").GetInt32());
		Assert.Equal(2, result.GetProperty("results").GetArrayLength());

		// The dispatched payload carries only operation/type — never a secret.
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		JobTask task = Assert.Single(job.Tasks);
		Assert.Equal("confirm_all_confirmations", task.Action);
		Assert.Equal("allow", PayloadValue(task.Payload!, "operation"));
		Assert.Equal("all", PayloadValue(task.Payload, "type"));
		Assert.DoesNotContain("identity", string.Join(',', task.Payload!.Keys), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_TypeFilter_PassesThroughToTask()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "confirm_all_confirmations", success: true,
			new Dictionary<string, object?> { ["operation"] = "cancel", ["total"] = 1, ["succeeded"] = 1, ["failed"] = 0, ["results"] = new List<Dictionary<string, object?>>() },
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/confirmations/accept-all", new { operation = "cancel", type = "market" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("cancel", doc.RootElement.GetProperty("result").GetProperty("operation").GetString());

		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		JobTask task = Assert.Single(job.Tasks);
		Assert.Equal("cancel", PayloadValue(task.Payload!, "operation"));
		Assert.Equal("market", PayloadValue(task.Payload, "type"));
	}

	/// <summary>Normalizes a task payload value (may be a JsonElement after the SQLite round-trip) to a string.</summary>
	private static string PayloadValue(IReadOnlyDictionary<string, object?>? payload, string key)
	{
		if (payload is null || !payload.TryGetValue(key, out object? raw))
		{
			return string.Empty;
		}

		return raw switch
		{
			JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
			JsonElement n => n.GetRawText(),
			string s => s,
			_ => string.Empty
		};
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "confirm_all_confirmations", success: false, null,
			"no identity secret is stored for 'alice'", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/confirmations/accept-all", new { });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("identity secret", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_InvalidType_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/confirmations/accept-all", new { type = "bogus" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("type must be 'all', 'trade' or 'market'", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_InvalidOperation_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/confirmations/accept-all", new { operation = "bogus" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("operation must be 'allow' or 'cancel'", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/confirmations/accept-all", new { });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Loot_AgentReportsFinished_AutoConfirms()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("loot_inventory", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "998877665544",
					["partner_steam_id"] = "76561198000000100",
					["item_count"] = 3,
					["requires_mobile_confirmation"] = true
				}, null),
			new FakeAgentStep("confirm_trade_offer", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "998877665544",
					["confirmation_id"] = "55",
					["confirmed"] = true
				}, null)
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76561198000000100" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal("998877665544", result.GetProperty("trade_offer_id").GetString());
		Assert.Equal(3, result.GetProperty("item_count").GetInt32());
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.True(confirm.GetProperty("attempted").GetBoolean());
		Assert.True(confirm.GetProperty("confirmed").GetBoolean());
		Assert.NotEmpty(confirm.GetProperty("job_id").GetString()!);

		// The confirm follow-up is its own job and carries only the offer id from the loot output.
		string confirmJobId = confirm.GetProperty("job_id").GetString()!;
		Assert.Equal("998877665544", await PayloadValueFromTask(store, confirmJobId, 0, "trade_offer_id"));
	}

	[Fact]
	public async Task Loot_NoConfirmationNeeded_SkipsConfirm()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "loot_inventory", success: true,
			new Dictionary<string, object?>
			{
				["trade_offer_id"] = "998877665544",
				["item_count"] = 1,
				["requires_mobile_confirmation"] = false
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { tradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=1&token=abc" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.False(doc.RootElement.TryGetProperty("mobileConfirmation", out _) ||
					 doc.RootElement.TryGetProperty("mobile_confirmation", out _));

		// The loot task received the trade URL, not a steam id.
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("https://steamcommunity.com/tradeoffer/new/?partner=1&token=abc", await PayloadValueFromTask(store, jobId, 0, "trade_url"));
	}

	[Fact]
	public async Task Loot_AgentFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "loot_inventory", success: false, null,
			"no tradable items to loot in the scanned apps", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76561198000000100" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("no tradable items", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Loot_MissingPartner_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("partner_steam_id", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Loot_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76561198000000100" });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Licenses_AppIds_DispatchesAndReturnsFinished()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "add_license", success: true,
			new Dictionary<string, object?>
			{
				["app_ids"] = new List<uint> { 12345 },
				["sub_ids"] = new List<uint>(),
				["apps_result"] = "OK",
				["granted_app_ids"] = new List<uint> { 12345 }
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { appIds = new[] { 12345 } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal("OK", result.GetProperty("apps_result").GetString());
		Assert.Equal(1, result.GetProperty("granted_app_ids").GetArrayLength());

		// The add_license task carried the requested app ids.
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("[12345]", await PayloadValueFromTask(store, jobId, 0, "app_ids"));
	}

	[Fact]
	public async Task Licenses_SubIds_DispatchesBothIdKinds()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "add_license", success: true,
			new Dictionary<string, object?>
			{
				["app_ids"] = new List<uint>(),
				["sub_ids"] = new List<uint> { 42666, 42667 },
				["purchases"] = new List<object>()
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { appIds = new[] { 12345 }, subIds = new[] { 42666, 42667 } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;

		// The payload carried the sub ids; app ids ride along too (the client path claims them).
		Assert.Equal("[42666,42667]", await PayloadValueFromTask(store, jobId, 0, "sub_ids"));
		Assert.Equal("[12345]", await PayloadValueFromTask(store, jobId, 0, "app_ids"));
	}

	[Fact]
	public async Task Licenses_MissingBoth_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("app_ids or sub_ids", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Licenses_NonPositiveIds_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { subIds = new[] { 0 } });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("positive", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Licenses_AgentFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "add_license", success: false, null,
			"one or more free-license requests failed", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { appIds = new[] { 12345 } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("free-license", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Licenses_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { appIds = new[] { 12345 } });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Inventory_AppIds_DispatchesAndReturnsFinished()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "get_inventory", success: true,
			new Dictionary<string, object?>
			{
				["steam_id"] = "76561198000000042",
				["total_count"] = 2,
				["items"] = new List<object>(),
				["apps"] = new List<object>
				{
					new Dictionary<string, object?> { ["app_id"] = 753u, ["context_id"] = "6", ["item_count"] = 2 }
				}
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/inventory?appIds=753,730");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement inventory = doc.RootElement.GetProperty("inventory");
		Assert.Equal(2, inventory.GetProperty("total_count").GetInt32());
		Assert.Equal(1, inventory.GetProperty("apps").GetArrayLength());

		// The get_inventory task carried the requested app ids and the filter.
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("[753,730]", await PayloadValueFromTask(store, jobId, 0, "app_ids"));
	}

	[Fact]
	public async Task Inventory_SingleAppParams_ArePassedThrough()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "get_inventory", success: true,
			new Dictionary<string, object?> { ["total_count"] = 0, ["items"] = new List<object>() },
			null, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/inventory?appId=730&contextId=2&steamId=76561198000000042&tradableOnly=true");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("730", await PayloadValueFromTask(store, jobId, 0, "app_id"));
		Assert.Equal("2", await PayloadValueFromTask(store, jobId, 0, "context_id"));
		Assert.Equal("76561198000000042", await PayloadValueFromTask(store, jobId, 0, "steam_id"));
		Assert.Equal("true", await PayloadValueFromTask(store, jobId, 0, "tradable_only"));
	}

	[Fact]
	public async Task Inventory_InvalidAppIds_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/inventory?appIds=abc");

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("positive app ids", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Inventory_UnknownAccount_Returns404()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/ghost/inventory?appIds=753");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task Inventory_AgentFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "get_inventory", success: false, null,
			"inventory is private", cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/inventory?appIds=753");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("inventory is private", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Inventory_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/inventory?appIds=753");

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Duplicates_AppIdsAndKeep_DispatchAndReturnFinished()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "find_duplicates", success: true,
			new Dictionary<string, object?>
			{
				["steam_id"] = "76561198000000042",
				["keep"] = 1,
				["excess_count"] = 1,
				["duplicates"] = new List<object>
				{
					new Dictionary<string, object?>
					{
						["app_id"] = 753u, ["class_id"] = 100UL, ["excess_count"] = 1, ["excess_asset_ids"] = new List<object> { 2UL }
					}
				}
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/duplicates?appIds=753,730&keep=2");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement duplicates = doc.RootElement.GetProperty("duplicates");
		Assert.Equal(1, duplicates.GetProperty("excess_count").GetInt32());
		Assert.Equal(1, duplicates.GetProperty("duplicates").GetArrayLength());

		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("[753,730]", await PayloadValueFromTask(store, jobId, 0, "app_ids"));
		Assert.Equal("2", await PayloadValueFromTask(store, jobId, 0, "keep"));
	}

	[Fact]
	public async Task Duplicates_InvalidKeep_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/duplicates?keep=0");

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("keep", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Duplicates_AgentFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "find_duplicates", success: false, null,
			"inventory is private", cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/duplicates");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("inventory is private", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SwapOffers_DryRunDefault_DispatchesWithoutSending()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "swap_duplicates", success: true,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = "76561198000000100",
				["dry_run"] = true,
				["matches"] = new List<object>(),
				["give_count"] = 1,
				["receive_count"] = 1
			},
			null, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/swap-offers",
			new { partnerSteamId = "76561198000000100", keep = 2, maxSwaps = 30, message = "swap?" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal(1, doc.RootElement.GetProperty("result").GetProperty("give_count").GetInt32());
		Assert.False(doc.RootElement.TryGetProperty("mobile_confirmation", out var mobile) && mobile.ValueKind != JsonValueKind.Null);

		// No send flag reaches the task: the dry run is the default and the options ride along.
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("76561198000000100", await PayloadValueFromTask(store, jobId, 0, "partner_steam_id"));
		Assert.Equal("2", await PayloadValueFromTask(store, jobId, 0, "keep"));
		Assert.Equal("30", await PayloadValueFromTask(store, jobId, 0, "max_swaps"));
		Assert.Equal("swap?", await PayloadValueFromTask(store, jobId, 0, "message"));
		Assert.Equal("", await PayloadValueFromTask(store, jobId, 0, "send"));
	}

	[Fact]
	public async Task SwapOffers_SendTrue_AutoConfirms()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("swap_duplicates", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "887766554433",
					["partner_steam_id"] = "76561198000000100",
					["give_count"] = 2,
					["receive_count"] = 2,
					["dry_run"] = false,
					["requires_mobile_confirmation"] = true
				}, null),
			new FakeAgentStep("confirm_trade_offer", true, new Dictionary<string, object?>
				{
					["trade_offer_id"] = "887766554433",
					["confirmed"] = true
				}, null)
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/swap-offers",
			new { partnerSteamId = "76561198000000100", send = true });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("887766554433", doc.RootElement.GetProperty("result").GetProperty("trade_offer_id").GetString());
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.True(confirm.GetProperty("attempted").GetBoolean());
		Assert.True(confirm.GetProperty("confirmed").GetBoolean());

		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("true", await PayloadValueFromTask(store, jobId, 0, "send"));
	}

	[Fact]
	public async Task SwapOffers_MissingPartner_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new { });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("partner_steam_id", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SwapOffers_AgentFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "swap_duplicates", success: false, null,
			"no complementary duplicates found", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/swap-offers",
			new { partnerSteamId = "76561198000000100" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("no complementary duplicates", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SwapOffers_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync(
				"/v1/accounts/alice/swap-offers",
				new { partnerSteamId = "76561198000000100" });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	/// <summary>Reads one payload value from the n-th task of a job (normalized to string).</summary>
	private static async Task<string> PayloadValueFromTask(IJobStore store, string jobId, int taskIndex, string key)
	{
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		JobTask task = job.Tasks[taskIndex];
		return PayloadValue(task.Payload, key);
	}

	/// <summary>Plays the agent side: claims and completes a fixed sequence of tasks, in order.</summary>
	private sealed record FakeAgentStep(string Action, bool Success, Dictionary<string, object?>? Output, string? Error);

	private static async Task RespondToStepsAsync(IJobStore store, FakeAgentStep[] steps, CancellationToken ct)
	{
		int index = 0;
		while (index < steps.Length && !ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == steps[index].Action)
			{
				FakeAgentStep step = steps[index];
				await store.SetTaskResult(
					new TaskResult(claimed.Id, step.Success, step.Success ? null : step.Error, step.Success ? step.Output : null, DateTimeOffset.UtcNow),
					ct);
				index++;
				continue;
			}

			await Task.Delay(25, ct);
		}
	}

	/// <summary>Plays the agent side: claims the queued get_trade_offers task and reports a result.</summary>
	private static Task RespondToFirstTradeOffersTaskAsync(IJobStore store, bool success, CancellationToken ct)
	{
		return RespondToFirstTaskAsync(
			store,
			"get_trade_offers",
			success,
			new Dictionary<string, object?>
			{
				["received_count"] = 1,
				["received_offers"] = new List<Dictionary<string, object?>>
				{
					new() { ["trade_offer_id"] = "43591234567890", ["state"] = "Active" }
				}
			},
			"agent refused",
			ct);
	}

	/// <summary>Plays the agent side: claims the queued get_my_market_listings task and reports a result.</summary>
	private static Task RespondToFirstMarketListingsTaskAsync(IJobStore store, bool success, CancellationToken ct)
	{
		return RespondToFirstTaskAsync(
			store,
			"get_my_market_listings",
			success,
			new Dictionary<string, object?>
			{
				["start"] = 0,
				["count"] = 100,
				["total_count"] = 3,
				["active_count"] = 2,
				["on_hold_count"] = 1,
				["to_be_confirmed_count"] = 2,
				["listings"] = new List<Dictionary<string, object?>>
				{
					new()
					{
						["listing_id"] = "3547123456789012345",
						["app_id"] = 730u,
						["price_cents"] = 103,
						["fee_cents"] = 12,
						["seller_proceeds_cents"] = 91,
						["market_hash_name"] = "AK-47 | Redline (Field-Tested)"
					}
				}
			},
			"agent refused",
			ct);
	}

	/// <summary>Plays the agent side: claims the first queued task for <paramref name="action"/> and reports a result.</summary>
	private static async Task RespondToFirstTaskAsync(
		IJobStore store,
		string action,
		bool success,
		Dictionary<string, object?>? output,
		string? error,
		CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == action)
			{
				await store.SetTaskResult(
					new TaskResult(claimed.Id, success, success ? null : error, success ? output : null, DateTimeOffset.UtcNow),
					ct);
				return;
			}

			await Task.Delay(25, ct);
		}
	}

	private static TestFactory CreateFactory(bool removeHosted = false)
	{
		return new TestFactory(removeHosted);
	}

	private sealed class TestFactory(bool removeHosted) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				if (removeHosted)
				{
					// The synchronous trade-offers endpoint races its own fake agent
					// responder; background dispatchers would claim/fail the task first.
					services.RemoveAll<IHostedService>();
					services.RemoveAll<IHostedLifecycleService>();
				}
			});
		}
	}
}
