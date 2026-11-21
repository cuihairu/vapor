using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>License endpoint tests: app/sub id dispatch and validation.</summary>
public sealed partial class AccountApiTests
{
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
}
