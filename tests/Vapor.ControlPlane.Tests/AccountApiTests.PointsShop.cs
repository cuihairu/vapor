using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Points shop endpoint tests: balance summary and claim payloads.</summary>
public sealed partial class AccountApiTests
{
	[Fact]
	public async Task PointsShopSummary_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/points-shop/summary");

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task PointsShopSummary_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/ghost/points-shop/summary");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task PointsShopSummary_InvalidDefinitionId_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/points-shop/summary?definitionIds=abc");

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("definition_ids", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PointsShopSummary_AgentReportsFinished_ReturnsBalance()
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
			dispatchedPayload = await RespondToFirstPointsShopTaskAsync(
				store, "get_points_shop_summary",
				new Dictionary<string, object?> { ["points"] = 1500L, ["points_earned"] = 2000L, ["points_spent"] = 500L },
				cts.Token);
		});

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/points-shop/summary?definitionIds=91000,91001&freeOnly=true");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal(1500L, doc.RootElement.GetProperty("points_shop").GetProperty("points").GetInt64());
		Assert.NotNull(dispatchedPayload);
		var ids = Assert.IsType<JsonElement>(dispatchedPayload!["definition_ids"]);
		Assert.Equal(2, ids.GetArrayLength());
		Assert.Equal("91000", Scalar(ids.EnumerateArray().First()));
		Assert.Equal("91001", Scalar(ids.EnumerateArray().Last()));
		Assert.Equal("true", Scalar(dispatchedPayload["free_only"]));
	}

	[Fact]
	public async Task PointsShopSummary_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(store, "get_points_shop_summary", success: false, null, "agent refused", cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/points-shop/summary?definitionIds=91000");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("agent refused", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task PointsShopSummary_StillPending_Returns202WithJobId()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/points-shop/summary?definitionIds=91000");

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
	public async Task PointsShopClaim_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/points-shop/claim", new { definitionIds = new[] { 91000u } });

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task PointsShopClaim_MissingDefinitionIds_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/points-shop/claim", new { });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("definition_ids is required", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PointsShopClaim_AgentReportsFinished_PayloadPassthrough()
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
			dispatchedPayload = await RespondToFirstPointsShopTaskAsync(
				store, "claim_points_shop_items",
				new Dictionary<string, object?>
				{
					["force"] = true,
					["requested"] = 2,
					["succeeded"] = 2,
					["failed"] = 0,
					["results"] = new List<object?>
					{
						new Dictionary<string, object?> { ["defid"] = 91000u, ["success"] = true, ["result"] = "OK" },
						new Dictionary<string, object?> { ["defid"] = 91001u, ["success"] = true, ["result"] = "OK" }
					},
					["points_after"] = 800L
				},
				cts.Token);
		});

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/points-shop/claim",
			new { definitionIds = new[] { 91000u, 91001u }, force = true });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal(2, result.GetProperty("succeeded").GetInt32());
		Assert.Equal(800L, result.GetProperty("points_after").GetInt64());
		Assert.NotNull(dispatchedPayload);
		var ids = Assert.IsType<JsonElement>(dispatchedPayload!["definition_ids"]);
		Assert.Equal(2, ids.GetArrayLength());
		Assert.Equal("true", Scalar(dispatchedPayload["force"]));
	}

	[Fact]
	public async Task PointsShopClaim_ForceOmitted_PayloadHasNoForceKey()
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
			dispatchedPayload = await RespondToFirstPointsShopTaskAsync(
				store, "claim_points_shop_items",
				new Dictionary<string, object?> { ["succeeded"] = 1, ["failed"] = 0 },
				cts.Token);
		});

		// No force in the body: free-only default must reach the agent as an
		// absent key, not as an explicit false masquerading as a choice.
		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/points-shop/claim",
			new { definitionIds = new[] { 91000u } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(dispatchedPayload);
		Assert.False(dispatchedPayload!.ContainsKey("force"));
	}

	[Fact]
	public async Task PointsShopClaim_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/points-shop/claim", new { definitionIds = new[] { 91000u } });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task PointsShopClaim_ZeroDefinitionId_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/points-shop/claim",
			new { definitionIds = new[] { 91000u, 0u } });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("definition_ids must be positive", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PointsShopClaim_DuplicateIds_AgentReceivesDistinctList()
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
			dispatchedPayload = await RespondToFirstPointsShopTaskAsync(
				store, "claim_points_shop_items",
				new Dictionary<string, object?> { ["succeeded"] = 1, ["failed"] = 0 },
				cts.Token);
		});

		// Duplicates are the caller's typo, not two redemptions: dedupe on dispatch.
		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/points-shop/claim",
			new { definitionIds = new[] { 91000u, 91001u, 91000u } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(dispatchedPayload);
		var ids = Assert.IsType<JsonElement>(dispatchedPayload!["definition_ids"]);
		Assert.Equal(2, ids.GetArrayLength());
	}

	[Fact]
	public async Task PointsShopClaim_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(store, "claim_points_shop_items", success: false, null, "agent refused", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/accounts/alice/points-shop/claim",
			new { definitionIds = new[] { 91000u } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("agent refused", body, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task PointsShopClaim_StillPending_Returns202WithJobId()
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
				"/v1/accounts/alice/points-shop/claim",
				new { definitionIds = new[] { 91000u } });

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

	/// <summary>
	/// Plays the agent side for the points shop endpoints: claims the queued
	/// task for <paramref name="action"/>, reports <paramref name="output"/>
	/// and returns the dispatched payload so tests can assert what the endpoint
	/// actually sent.
	/// </summary>
	private static async Task<Dictionary<string, object?>?> RespondToFirstPointsShopTaskAsync(
		IJobStore store,
		string action,
		Dictionary<string, object?> output,
		CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == action)
			{
				Dictionary<string, object?>? payload = claimed.Payload is null
					? null
					: new Dictionary<string, object?>(claimed.Payload);
				await store.SetTaskResult(
					new TaskResult(claimed.Id, true, null, output, DateTimeOffset.UtcNow),
					ct);
				return payload;
			}

			await Task.Delay(25, ct);
		}

		return null;
	}
}
