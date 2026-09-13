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
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
