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

// Shares the static AccountTaskRunner.WaitWindow/PollInterval knobs with
// AccountApiTests (see AccountTaskWaitWindowCollection).
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed class AdminEndpointTests
{
	[Fact]
	public async Task EnableAccount_SetsEnabledTrue()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/test-account-enable",
			new { desiredState = "online" });

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		JsonElement spec = (await ReadJson(put)).GetProperty("spec");

		Assert.Equal("test-account-enable", spec.GetProperty("accountName").GetString());
		Assert.True(spec.GetProperty("enabled").GetBoolean());
	}

	[Fact]
	public async Task DisableAccount_SetsEnabledFalse()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/test-account-disable",
			new { enabled = false, desiredState = "offline" });

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		JsonElement spec = (await ReadJson(put)).GetProperty("spec");

		Assert.Equal("test-account-disable", spec.GetProperty("accountName").GetString());
		Assert.False(spec.GetProperty("enabled").GetBoolean());
	}

	[Fact]
	public async Task StandingCheck_ViewReflectsSeededQuarantine()
	{
		await using var factory = CreateFactory();
		DesiredStateReconciler reconciler = factory.Services.GetRequiredService<DesiredStateReconciler>();

		// Never checked ⇒ never quarantined.
		Assert.Null(reconciler.GetStandingView("standing-test"));

		reconciler.SetStandingForTests("standing-test", quarantined: true, "banned");
		AccountStandingView? view = reconciler.GetStandingView("standing-test");

		Assert.NotNull(view);
		Assert.True(view.Quarantined);
		Assert.Equal("banned", view.Standing);
	}

	[Fact]
	public async Task Proxy_OnUndeclaredAccount_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/accounts/no-such-account/proxy",
			new { proxy = "socks5://user:pass@proxy.example.com:1080" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Proxy_WithoutPinnedAgent_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/test-proxy-unpinned",
			new { desiredState = "offline" });
		Assert.Equal(HttpStatusCode.OK, put.StatusCode);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/accounts/test-proxy-unpinned/proxy",
			new { proxy = "socks5://user:pass@proxy.example.com:1080" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("no pinned agent", body);
	}

	[Fact]
	public async Task Proxy_PinnedAgentNotConnected_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = CreateClient(factory);

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/test-proxy-offline",
			new { agentId = "ghost-agent", desiredState = "offline" });
		Assert.Equal(HttpStatusCode.OK, put.StatusCode);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/accounts/test-proxy-offline/proxy",
			new { proxy = "" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("not connected", body);
	}

	private static HttpClient CreateClient(TestFactory factory)
	{
		HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}

	private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

	private static TestFactory CreateFactory() => new();

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
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				// The standing view and the proxy gate are pure dispatch-time
				// reads — background reconciler/scheduler loops would only race
				// the seeded state.
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
