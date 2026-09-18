using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetAchievementsActionTests : IDisposable
{
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);
		Assert.Equal("get_achievements", action.Name);
	}

	[Fact]
	public async Task MissingAppId_Fails()
	{
		var (webHandler, _) = CreateWebHandler();
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(CreateSession(webHandler), new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("app_id", result.Error);
	}

	[Fact]
	public async Task ZeroAppId_Fails()
	{
		var (webHandler, _) = CreateWebHandler();
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(CreateSession(webHandler), new Dictionary<string, object?> { ["app_id"] = "0" }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("app_id", result.Error);
	}

	[Fact]
	public async Task ExplicitSteamId_FetchesAndProjectsOutput()
	{
		var (webHandler, fake) = CreateWebHandler();
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(SampleAchievementsHtml(), Encoding.UTF8, "text/html")
		};
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["app_id"] = "400", ["steam_id"] = "76561198000000000" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal(400U, result.Output!["app_id"]);
		Assert.Equal(1, result.Output["total_count"]);
		Assert.Contains("/stats/400", fake.Requests.Single().ToString());
	}

	[Fact]
	public async Task SteamIdFromSessionCookie_UsedWhenParamMissing()
	{
		var (webHandler, fake) = CreateWebHandler();
		webHandler.SetSessionCookies("sessionid-1", "76561198000000000%7C%7C%7C%7Ctokendata");
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(SampleAchievementsHtml(), Encoding.UTF8, "text/html")
		};
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["app_id"] = "400" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("76561198000000000", result.Output!["steam_id"]!.ToString());
	}

	[Fact]
	public async Task NoSteamIdAndNoCookie_Fails()
	{
		var (webHandler, _) = CreateWebHandler();
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["app_id"] = "400" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam_id", result.Error);
	}

	[Fact]
	public async Task FetchFailure_PropagatesAsTaskFailure()
	{
		var (webHandler, fake) = CreateWebHandler();
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["app_id"] = "400", ["steam_id"] = "76561198000000000" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to get achievements", result.Error);
	}

	[Fact]
	public void Metadata_DescribesTheReadAction()
	{
		// The factory constructor is the test seam — exercising it here also
		// keeps the metadata honest without ever touching the factory.
		var action = new GetAchievementsAction(
			NullLogger<GetAchievementsAction>.Instance,
			_ => throw new InvalidOperationException("the achievement client factory must not be touched by metadata"));

		Assert.Equal("get_achievements", action.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(120, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task MissingWebHandler_Fails()
	{
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler: null),
			new Dictionary<string, object?> { ["app_id"] = "400" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task NonNumericSteamId_Fails()
	{
		var (webHandler, fake) = CreateWebHandler();
		fake.Responder = _ => throw new InvalidOperationException("a malformed steam_id must not reach the page fetch");
		var action = new GetAchievementsAction(NullLogger<GetAchievementsAction>.Instance);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["app_id"] = "400", ["steam_id"] = "not-a-steam-id" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Invalid steam_id", result.Error);
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	private BotSession CreateSession(SteamWebHandler? webHandler = null)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private static (SteamWebHandler WebHandler, FakeHttpMessageHandler Fake) CreateWebHandler()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (webHandler, fake);
	}

	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		public List<Uri> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.RequestUri!);
			return Task.FromResult(Responder(request));
		}
	}

	private static string SampleAchievementsHtml() => """
		<div id="topSummaryAchievements"><div>1 of 1 (100%) achievements earned:</div></div>
		<div id="personalAchieve" class="achievements_list ">
			<div role="button" class="achieveRow">
				<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/only_ach.jpg"></div>
				<div class="achieveTxtHolder"><div class="achieveTxt">
					<h3 class="ellipsis">Only One</h3>
					<h5 class="ellipsis">The only achievement.</h5>
				</div></div>
			</div>
		</div>
		""";
}
