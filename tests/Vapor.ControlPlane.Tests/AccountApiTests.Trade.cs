using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Trade offer endpoint tests: listing, accept/decline, confirmation chaining.</summary>
public sealed partial class AccountApiTests
{
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
}
