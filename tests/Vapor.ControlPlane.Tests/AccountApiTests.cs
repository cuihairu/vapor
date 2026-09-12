using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vapor.ControlPlane;
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

	private static TestFactory CreateFactory()
	{
		return new TestFactory();
	}

	private sealed class TestFactory : WebApplicationFactory<Program>
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
			});
		}
	}
}
