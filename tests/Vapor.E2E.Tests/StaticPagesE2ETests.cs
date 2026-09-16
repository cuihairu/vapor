using System.Net;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// Guards the admin/dashboards static pages against deployment-form regressions. The
/// E2E control plane runs the bare bin dll with the test runner's working directory —
/// exactly the launch shape where the default WebRoot (ContentRoot, = cwd for a dll
/// launch) finds no wwwroot and every page 404s. Both halves of the fix are pinned
/// here: the csproj copies wwwroot next to the bin output, and Program.cs anchors the
/// static file provider on AppContext.BaseDirectory. No other test in the suite ever
/// requests a page, which is how a fleet-wide 404 survived 2060 green tests.
/// </summary>
[Collection("E2E")]
public sealed class StaticPagesE2ETests
{
	private readonly E2EStack _stack;

	public StaticPagesE2ETests(E2EStack stack)
	{
		_stack = stack;
	}

	[Theory]
	[InlineData("/admin.html", "text/html")]
	[InlineData("/dashboard.html", "text/html")]
	[InlineData("/gamedata.html", "text/html")]
	[InlineData("/favicon.svg", "image/svg+xml")]
	public async Task StaticPages_AnonymousRequestFromForeignCwd_Served(string path, string contentTypePrefix)
	{
		using var response = await _stack.Http.GetAsync(path);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.NotNull(response.Content.Headers.ContentType);
		Assert.StartsWith(contentTypePrefix, response.Content.Headers.ContentType.MediaType, StringComparison.OrdinalIgnoreCase);

		string body = await response.Content.ReadAsStringAsync();
		Assert.False(string.IsNullOrWhiteSpace(body), $"{path} served an empty body");
	}

	[Fact]
	public async Task RootUrl_AnonymousRequest_LandsOnAdminUi()
	{
		// The shared HttpClient follows redirects, so this asserts the whole hop:
		// / must resolve (a bare-dll 404 of admin.html would surface here as 404)
		// and land on the admin page rather than an error body.
		using var response = await _stack.Http.GetAsync("/");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.False(string.IsNullOrWhiteSpace(body));
		Assert.Contains("<!DOCTYPE html", body, StringComparison.OrdinalIgnoreCase);
	}
}
