using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Static console pages: the read-only dashboard must stay reachable and — by
/// contract — free of any write verb, so the page can never grow mutation
/// calls unnoticed.
/// </summary>
public sealed class DashboardStaticTests
{
	[Fact]
	public async Task DashboardHtml_IsServedWithoutAuth()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/dashboard.html");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string html = await response.Content.ReadAsStringAsync();
		Assert.Contains("Vapor Dashboard", html, StringComparison.Ordinal);
		Assert.Contains("/v1/accounts", html, StringComparison.Ordinal);
	}

	[Fact]
	public void DashboardHtml_ContainsNoWriteVerbs()
	{
		string path = FindRepoFile("src/Vapor.ControlPlane/wwwroot/dashboard.html");
		string html = File.ReadAllText(path);

		// The page is read-only: data flows in via GET fetches and SSE. Assert on
		// the uppercase verb literals used by fetch/axios-style calls so a future
		// edit that adds a mutation fails here.
		Assert.DoesNotContain("POST", html, StringComparison.Ordinal);
		Assert.DoesNotContain("PUT", html, StringComparison.Ordinal);
		Assert.DoesNotContain("DELETE", html, StringComparison.Ordinal);
	}

	[Fact]
	public void DashboardHtml_LinksWithAdminConsole()
	{
		string dashboard = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/dashboard.html"));
		string admin = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/admin.html"));

		Assert.Contains("href=\"/admin.html\"", dashboard, StringComparison.Ordinal);
		Assert.Contains("href=\"/dashboard.html\"", admin, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GameDataHtml_IsServedWithoutAuth()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/gamedata.html");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string html = await response.Content.ReadAsStringAsync();
		Assert.Contains("游戏数据字典", html, StringComparison.Ordinal);
		Assert.Contains("/v1/crawl/plans", html, StringComparison.Ordinal);
	}

	[Fact]
	public void GameDataHtml_ContainsNoWriteVerbs()
	{
		string html = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/gamedata.html"));

		// Same read-only contract as the dashboard: the game-data page may only
		// GET; crawl plans are triggered through the admin REST surface.
		Assert.DoesNotContain("POST", html, StringComparison.Ordinal);
		Assert.DoesNotContain("PUT", html, StringComparison.Ordinal);
		Assert.DoesNotContain("DELETE", html, StringComparison.Ordinal);
	}

	[Fact]
	public void GameDataHtml_LinksWithBothConsoles()
	{
		string gamedata = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/gamedata.html"));
		string dashboard = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/dashboard.html"));
		string admin = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/admin.html"));

		Assert.Contains("href=\"/dashboard.html\"", gamedata, StringComparison.Ordinal);
		Assert.Contains("href=\"/admin.html\"", gamedata, StringComparison.Ordinal);
		Assert.Contains("href=\"/gamedata.html\"", dashboard, StringComparison.Ordinal);
		Assert.Contains("href=\"/gamedata.html\"", admin, StringComparison.Ordinal);
	}

	[Fact]
	public void GameDataHtml_DocumentsAllFiveModels()
	{
		string html = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/gamedata.html"));

		foreach (string model in new[] { "GameInfo", "PriceOverview", "GameSearchResult", "MarketListing", "MarketListingsPage" })
		{
			Assert.Contains(model, html, StringComparison.Ordinal);
		}

		// The old six-model docs listed ItemInfo, a record with zero production
		// references — removed 2026-09-16. Keep it from creeping back in.
		Assert.DoesNotContain("ItemInfo", html, StringComparison.Ordinal);

		Assert.Contains("get_game_info_batch", html, StringComparison.Ordinal);
	}

	[Fact]
	public void AdminHtml_QrLoginButton_DispatchesLoginJobWithQrPayload()
	{
		// Guards the deferred feature-matrix item: the sessions-panel button must
		// POST the same /v1/jobs body the docs describe, carrying the camelCase
		// `qrLogin` flag AgentTaskExecutor reads (password-less QR credentials;
		// the rotating challenge URL then lands in the auth-challenge panel).
		string html = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/admin.html"));

		Assert.Contains("id=\"qrLoginButton\"", html, StringComparison.Ordinal);
		Assert.Contains("startQrLogin", html, StringComparison.Ordinal);
		Assert.Contains("action: \"login\"", html, StringComparison.Ordinal);
		Assert.Contains("payload: { qrLogin: true }", html, StringComparison.Ordinal);
	}

	[Fact]
	public void AdminHtml_DestructiveWrites_RequireExplicitConfirmation()
	{
		string html = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/admin.html"));

		// todo §31 red line ②: every irreversible write gates on a native
		// confirm/prompt whose text names the consequence. Spot-check one anchor
		// phrase per destructive channel so an edit that drops the gate fails
		// here instead of silently shipping a one-click write.
		Assert.Contains("此操作不可逆：将删除账户", html, StringComparison.Ordinal); // delete account (prompt with exact name)
		Assert.Contains("资产转移不可逆", html, StringComparison.Ordinal); // trade offer accept
		Assert.Contains("不可逆。确认发送", html, StringComparison.Ordinal); // loot
		Assert.Contains("发送 1:1 换卡报价", html, StringComparison.Ordinal); // swap offers send
		Assert.Contains("真实撤单将取消所有匹配的挂单", html, StringComparison.Ordinal); // market cancel
		Assert.Contains("ToS 灰区操作", html, StringComparison.Ordinal); // market listing create
		Assert.Contains("积分消费不可逆", html, StringComparison.Ordinal); // points shop force claim
		Assert.Contains("已沉淀的采集结果会保留", html, StringComparison.Ordinal); // crawl plan delete
		Assert.Contains("项成就置为已解锁", html, StringComparison.Ordinal); // achievement unlock gate (§33)
		Assert.Contains("此操作不可逆：将清零账户", html, StringComparison.Ordinal); // achievement reset, confirm #1 (§33: destructive, double-gated in the UI)
		Assert.Contains("再次确认：向账户", html, StringComparison.Ordinal); // achievement reset, confirm #2
	}

	[Fact]
	public void AdminHtml_ConfigPanel_NeverDisplaysSensitiveSettingValues()
	{
		string html = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/admin.html"));

		// todo §31 P5 contract: password-class settings keys render masked; the
		// input stays empty and an untouched empty input submits the stored
		// value, so secrets never round-trip through the DOM.
		Assert.Contains("const SENSITIVE_KEY_PATTERN = /password|secret|token|key|credential/i;", html, StringComparison.Ordinal);
		Assert.Contains("••• 已设（输入新值覆盖，留空保留）", html, StringComparison.Ordinal);
		Assert.Contains("updatedBy: \"admin-console\"", html, StringComparison.Ordinal);
	}

	[Fact]
	public void AdminHtml_StandingPanel_ShowsBadgesQuarantineAndManualCheck()
	{
		string html = File.ReadAllText(FindRepoFile("src/Vapor.ControlPlane/wwwroot/admin.html"));

		// §38 P2 contract: the account list pulls the standing snapshot, renders
		// a clean/restricted/banned badge plus a quarantine flag, and the manual
		// "体检" button hits the forced-check endpoint (results land only via the
		// orchestrator's own settle path, never by dispatching a raw job).
		Assert.Contains("/v1/orchestration/standing", html, StringComparison.Ordinal);
		Assert.Contains("data-standing-check", html, StringComparison.Ordinal);
		Assert.Contains("/standing-check", html, StringComparison.Ordinal);
		Assert.Contains("已隔离", html, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RootPath_RedirectsToAdminConsole()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using HttpResponseMessage response = await client.GetAsync("/");

		Assert.Equal(HttpStatusCode.Found, response.StatusCode);
		Assert.Equal("/admin.html", response.Headers.Location?.ToString());
	}

	/// <summary>Walks up from the test run directory until the repo root is found.</summary>
	private static string FindRepoFile(string relativePath)
	{
		string? repoRoot = AppContext.BaseDirectory;
		while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "Vapor.sln")))
		{
			repoRoot = Path.GetDirectoryName(repoRoot);
		}

		Assert.False(repoRoot is null, "could not locate the repository root from the test run directory");
		string fullPath = Path.Combine(repoRoot, relativePath);
		Assert.True(File.Exists(fullPath), $"expected static page missing: {fullPath}");
		return fullPath;
	}

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
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
