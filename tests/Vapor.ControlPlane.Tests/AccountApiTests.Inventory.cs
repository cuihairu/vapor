using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Inventory endpoint tests: app-id dispatch and parameter validation.</summary>
public sealed partial class AccountApiTests
{
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
}
