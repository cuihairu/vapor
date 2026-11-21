using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Item collection endpoint tests: loot, duplicates and swap offers.</summary>
public sealed partial class AccountApiTests
{
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
}
