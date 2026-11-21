using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Achievement endpoint tests: read, unlock and reset payloads.</summary>
public sealed partial class AccountApiTests
{
	[Fact]
	public async Task GetAchievements_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/achievements?appId=400");

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task GetAchievements_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/ghost/achievements?appId=400");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("0")]
	[InlineData("%20")]
	public async Task GetAchievements_InvalidAppId_Returns400(string appId)
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.GetAsync($"/v1/accounts/alice/achievements?appId={appId}");

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
	}

	[Fact]
	public async Task GetAchievements_AgentReportsFinished_ReturnsAchievements()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstAchievementsTaskAsync(store, success: true, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/achievements?appId=400");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.NotEmpty(doc.RootElement.GetProperty("job_id").GetString()!);
		Assert.Equal(400, doc.RootElement.GetProperty("app_id").GetInt32());
		JsonElement output = doc.RootElement.GetProperty("achievements");
		Assert.Equal(1, output.GetProperty("total_count").GetInt32());
		JsonElement achievements = output.GetProperty("achievements");
		Assert.Equal(1, achievements.GetArrayLength());
		Assert.Equal("ACH_NAME_ONE", achievements[0].GetProperty("api_name").GetString());
		Assert.True(achievements[0].GetProperty("unlocked").GetBoolean());
	}

	[Fact]
	public async Task GetAchievements_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstAchievementsTaskAsync(store, success: false, cts.Token));

		using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/achievements?appId=400");
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
	}

	[Fact]
	public async Task GetAchievements_StillPending_Returns202WithJobId()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/achievements?appId=400");

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
	public async Task UnlockAchievements_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage unlock = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400, names = new[] { "ACH_ONE" } });
		using HttpResponseMessage reset = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/reset", new { appId = 400, names = new[] { "ACH_ONE" }, confirm = true });

		Assert.Equal(HttpStatusCode.Unauthorized, unlock.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, reset.StatusCode);
	}

	[Fact]
	public async Task UnlockAchievements_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/achievements/unlock", new { appId = 400, names = new[] { "ACH_ONE" } });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task UnlockAchievements_MissingOrEmptyNames_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage noNames = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400 });
		using HttpResponseMessage emptyNames = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400, names = Array.Empty<string>() });
		using HttpResponseMessage blankNames = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400, names = new[] { "  " } });
		using HttpResponseMessage noAppId = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { names = new[] { "ACH_ONE" } });

		Assert.Equal(HttpStatusCode.BadRequest, noNames.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, emptyNames.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, blankNames.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, noAppId.StatusCode);
	}

	[Fact]
	public async Task ResetAchievements_MissingConfirm_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage noConfirm = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/reset", new { appId = 400, names = new[] { "ACH_ONE" } });
		using HttpResponseMessage falseConfirm = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/reset", new { appId = 400, names = new[] { "ACH_ONE" }, confirm = false });

		Assert.Equal(HttpStatusCode.BadRequest, noConfirm.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, falseConfirm.StatusCode);
	}

	[Fact]
	public async Task UnlockAchievements_AgentReportsFinished_ReturnsResultAndAudits()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstAchievementWriteTaskAsync(store, AccountTaskRunner.UnlockAchievementsAction, success: true, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400, names = new[] { "ACH_ONE", "ACH_TWO" } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal(400, doc.RootElement.GetProperty("app_id").GetInt32());
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal(1, result.GetProperty("succeeded_count").GetInt32());
		Assert.True(result.GetProperty("verified").GetBoolean());

		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=achievement.unlock");
		audit.EnsureSuccessStatusCode();
		string auditBody = await audit.Content.ReadAsStringAsync();
		using var auditDoc = JsonDocument.Parse(auditBody);
		Assert.True(auditDoc.RootElement.GetProperty("total").GetInt32() >= 1);
	}

	[Fact]
	public async Task ResetAchievements_AgentReportsFinished_PayloadCarriesConfirmAndNames()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		Dictionary<string, object?>? claimedPayload = null;
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(async () =>
		{
			claimedPayload = await RespondToFirstAchievementWriteTaskAsync(store, AccountTaskRunner.ResetAchievementsAction, success: true, cts.Token);
		});

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/reset", new { appId = 400, names = new[] { "ACH_ONE" }, confirm = true });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(claimedPayload);
		// Values come back through the store's JSON round-trip, so compare textually.
		Assert.Equal("400", claimedPayload!["app_id"]?.ToString());
		string[] names = claimedPayload["names"] switch
		{
			List<string> list => list.ToArray(),
			JsonElement { ValueKind: JsonValueKind.Array } arr => arr.EnumerateArray().Select(e => e.GetString()!).ToArray(),
			_ => throw new Xunit.Sdk.XunitException("names payload has an unexpected shape")
		};
		Assert.Equal(new[] { "ACH_ONE" }, names);
		Assert.Equal("True", claimedPayload["confirm"]?.ToString());

		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=achievement.reset");
		audit.EnsureSuccessStatusCode();
	}

	[Fact]
	public async Task UnlockAchievements_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstAchievementWriteTaskAsync(store, AccountTaskRunner.UnlockAchievementsAction, success: false, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400, names = new[] { "ACH_ONE" } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
	}

	[Fact]
	public async Task UnlockAchievements_StillPending_Returns202WithJobId()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 400, names = new[] { "ACH_ONE" } });

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

	/// <summary>Plays the agent side: claims the queued get_achievements task and reports a result.</summary>
	private static Task RespondToFirstAchievementsTaskAsync(IJobStore store, bool success, CancellationToken ct)
	{
		return RespondToFirstTaskAsync(
			store,
			"get_achievements",
			success,
			new Dictionary<string, object?>
			{
				["steam_id"] = "76561198000000000",
				["app_id"] = 400u,
				["unlocked_count"] = 1,
				["total_count"] = 1,
				["achievements"] = new List<Dictionary<string, object?>>
				{
					new() { ["api_name"] = "ACH_NAME_ONE", ["display_name"] = "One", ["description"] = "The first.", ["unlocked"] = true, ["icon_url"] = "https://example/one.jpg" }
				}
			},
			"agent refused",
			ct);
	}

	private static Task<Dictionary<string, object?>?> RespondToFirstAchievementWriteTaskAsync(
		IJobStore store,
		string action,
		bool success,
		CancellationToken ct)
	{
		return RespondToFirstTaskAsync(
			store,
			action,
			success,
			new Dictionary<string, object?>
			{
				["app_id"] = 400u,
				["unlock"] = action == AccountTaskRunner.UnlockAchievementsAction,
				["requested_count"] = 1,
				["results"] = new List<Dictionary<string, object?>>
				{
					new() { ["name"] = "ACH_NAME_ONE", ["success"] = true, ["detail"] = null }
				},
				["succeeded_count"] = 1,
				["failed_count"] = 0,
				["verified"] = true
			},
			"agent refused",
			ct);
	}
}
