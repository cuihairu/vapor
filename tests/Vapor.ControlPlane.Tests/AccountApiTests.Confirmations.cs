using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Confirmation endpoint tests: accept-all with type/operation filters.</summary>
public sealed partial class AccountApiTests
{
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
}
