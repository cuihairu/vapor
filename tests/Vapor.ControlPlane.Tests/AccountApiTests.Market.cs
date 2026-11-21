using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Market endpoint tests: listing, cancel and create flows.</summary>
public sealed partial class AccountApiTests
{
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
	public async Task PostMarketListingsCancel_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new { appId = 730 });

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task PostMarketListingsCancel_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new { appId = 730 });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task PostMarketListingsCancel_DryRunDefault_AgentReportsFinished_ReturnsPreview()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstMarketCancelTaskAsync(store, success: true, dryRun: true, cts.Token));

		// No dry_run in the body: the default preview run.
		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new { appId = 730 });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.True(result.GetProperty("dry_run").GetBoolean());
		Assert.Equal(2, result.GetProperty("matched").GetInt32());
		// Dry runs carry no success counters at all.
		bool succeededAbsentOrNull = !result.TryGetProperty("succeeded", out JsonElement succeededElem)
			|| succeededElem.ValueKind == JsonValueKind.Null;
		Assert.True(succeededAbsentOrNull);
	}

	[Fact]
	public async Task PostMarketListingsCancel_RealRunWithoutFilter_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		// A real run (dry_run=false) with no filter must be refused up front —
		// no job is created and the agent is never involved.
		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new { dryRun = false });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
	}

	[Fact]
	public async Task PostMarketListingsCancel_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstMarketCancelTaskAsync(store, success: false, dryRun: false, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new { appId = 730, dryRun = false });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("agent refused", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task PostMarketListingsCancel_EveryFilter_ReachesTheAgentPayload()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Dictionary<string, object?>? dispatchedPayload = null;
		Task responder = Task.Run(async () =>
		{
			dispatchedPayload = await RespondToFirstMarketCancelTaskAsync(store, success: true, dryRun: false, cts.Token);
		});

		// All four filters at once: each must land in the payload as its own key.
		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new
		{
			appId = 730,
			marketHashName = "  AK-47 | Redline (Field-Tested)  ",
			minPriceCents = 100,
			maxPriceCents = 250,
			olderThanSeconds = 3600,
			dryRun = false
		});
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(dispatchedPayload);
		Assert.Equal("730", Scalar(dispatchedPayload!["app_id"]));
		// The name filter is trimmed before dispatch.
		Assert.Equal("AK-47 | Redline (Field-Tested)", Scalar(dispatchedPayload["market_hash_name"]));
		Assert.Equal("100", Scalar(dispatchedPayload["min_price_cents"]));
		Assert.Equal("250", Scalar(dispatchedPayload["max_price_cents"]));
		Assert.Equal("3600", Scalar(dispatchedPayload["older_than_seconds"]));
		Assert.Equal("false", Scalar(dispatchedPayload["dry_run"]));
	}

	[Fact]
	public async Task PostMarketListingsCancel_StillPending_Returns202WithJobId()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings/cancel", new { appId = 730 });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
			Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("job_id").GetString()));
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task PostMarketListingCreate_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings", new { appId = 730, assetId = "111" });

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task PostMarketListingCreate_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings", new { appId = 730, assetId = "111" });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task PostMarketListingCreate_SendWithoutAccountSwitch_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		// Real listing creation is the per-account opt-in: default off, and the
		// endpoint refuses before any job is created.
		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/market/listings", new { appId = 730, contextId = "6", assetId = "111", send = true });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("marketListingsEnabled", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PostMarketListingCreate_DryRunDefault_AgentReportsFinished()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Dictionary<string, object?>? dispatchedPayload = null;
		Task responder = Task.Run(async () =>
		{
			dispatchedPayload = await RespondToFirstMarketCreateTaskAsync(store, success: true, cts.Token);
		});

		// No send in the body: the default dry-run pricing plan.
		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/market/listings",
			new { appId = 730, contextId = "6", assetId = "3547...", sellerProceedsCents = 91 });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.True(result.GetProperty("dry_run").GetBoolean());
		Assert.NotNull(dispatchedPayload);
		// The payload round-trips the SQLite job store, so numbers come back as
		// JsonElements — compare through the scalar helper.
		Assert.Equal("730", Scalar(dispatchedPayload!["app_id"]));
		Assert.Equal("6", Scalar(dispatchedPayload["context_id"]));
		Assert.Equal("91", Scalar(dispatchedPayload["seller_proceeds_cents"]));
		// The dry-run default must not leak a send flag into the payload.
		Assert.False(dispatchedPayload.ContainsKey("send"));
	}

	[Fact]
	public async Task PostMarketListingCreate_SendEnabled_AgentReportsFinished()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", marketListingsEnabled = true });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Dictionary<string, object?>? dispatchedPayload = null;
		Task responder = Task.Run(async () =>
		{
			dispatchedPayload = await RespondToFirstMarketCreateTaskAsync(store, success: true, cts.Token);
		});

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/market/listings",
			new { appId = 753, contextId = "6", assetId = "3548...", amount = 1, buyerPriceCents = 115, send = true });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(dispatchedPayload);
		Assert.Equal("true", Scalar(dispatchedPayload!["send"]));
		Assert.Equal("115", Scalar(dispatchedPayload["buyer_price_cents"]));
		// The response echoes the spec with the switch on.
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
	}

	[Fact]
	public async Task PostMarketListingCreate_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstMarketCreateTaskAsync(store, success: false, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/market/listings",
			new { appId = 730, contextId = "6", assetId = "3547...", sellerProceedsCents = 91 });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("agent refused", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task PostMarketListingCreate_StillPending_Returns202WithJobId()
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
				"/v1/accounts/alice/market/listings",
				new { appId = 730, contextId = "6", assetId = "3547...", sellerProceedsCents = 91 });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
			Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("job_id").GetString()));
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
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

	/// <summary>Plays the agent side: claims the queued cancel_market_listings task, reports a
	/// result and returns the dispatched payload so tests can assert what the endpoint sent.</summary>
	private static Task<Dictionary<string, object?>?> RespondToFirstMarketCancelTaskAsync(IJobStore store, bool success, bool dryRun, CancellationToken ct)
	{
		var output = new Dictionary<string, object?>
		{
			["dry_run"] = dryRun,
			["matched"] = 2,
			["scanned"] = 5,
			["succeeded"] = dryRun ? null : 2,
			["failed"] = dryRun ? null : 0,
			["listings"] = new List<Dictionary<string, object?>>
			{
				new() { ["listing_id"] = "3547123456789012345", ["market_hash_name"] = "AK-47 | Redline (Field-Tested)", ["price_cents"] = 103, ["would_cancel"] = true },
				new() { ["listing_id"] = "3547123456789012347", ["market_hash_name"] = "Mann Co. Supply Crate Key", ["price_cents"] = 250, ["would_cancel"] = true }
			}
		};
		if (!dryRun)
		{
			// Real runs report success/failure per listing instead of would_cancel.
			output["listings"] = new List<Dictionary<string, object?>>
			{
				new() { ["listing_id"] = "3547123456789012345", ["succeeded"] = true },
				new() { ["listing_id"] = "3547123456789012347", ["succeeded"] = true }
			};
		}

		return RespondToFirstTaskAsync(store, "cancel_market_listings", success, output, "agent refused", ct);
	}

	/// <summary>
	/// Plays the agent side for create_market_listing: claims the queued task,
	/// reports the dry-run pricing-plan output and returns the dispatched
	/// payload so tests can assert what the endpoint actually sent.
	/// </summary>
	private static async Task<Dictionary<string, object?>?> RespondToFirstMarketCreateTaskAsync(IJobStore store, bool success, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == "create_market_listing")
			{
				Dictionary<string, object?>? payload = claimed.Payload is null
					? null
					: new Dictionary<string, object?>(claimed.Payload);
				await store.SetTaskResult(
					new TaskResult(
						claimed.Id,
						success,
						success ? null : "agent refused",
						success
							? new Dictionary<string, object?>
							{
								["dry_run"] = true,
								["would_list"] = true,
								["asset"] = new Dictionary<string, object?> { ["app_id"] = 730u, ["context_id"] = "6", ["asset_id"] = "3547...", ["amount"] = 1 },
								["pricing"] = new Dictionary<string, object?>
								{
									["seller_proceeds_cents"] = 91,
									["steam_fee_cents"] = 4,
									["publisher_fee_cents"] = 9,
									["buyer_price_cents"] = 104
								}
							}
							: null,
						DateTimeOffset.UtcNow),
					ct);
				return payload;
			}

			await Task.Delay(25, ct);
		}

		return null;
	}
}
