using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetCardDropsActionTests : IDisposable
{
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetCardDropsAction(NullLogger<GetCardDropsAction>.Instance);
		Assert.Equal("get_card_drops", action.Name);
	}

	[Fact]
	public void Metadata_RequiresLogin()
	{
		var action = new GetCardDropsAction(NullLogger<GetCardDropsAction>.Instance);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(120, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public void Constructor_Default_WiresRealBadgesClient()
	{
		// The public constructor wires the real badges client factory without
		// touching the network.
		var action = new GetCardDropsAction(NullLogger<GetCardDropsAction>.Instance);
		Assert.Equal("get_card_drops", action.Name);
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

		public int RequestCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			return Task.FromResult(Responder(request));
		}
	}

	private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
	};

	private static string Page1Path => Path.Combine(AppContext.BaseDirectory, "TestData", "badges_page_p1.html");

	private static string Page2Path => Path.Combine(AppContext.BaseDirectory, "TestData", "badges_page_p2.html");

	private static GetCardDropsAction CreateAction(SteamWebHandler webHandler, IVaporCache? cache = null) =>
		new(
			NullLogger<GetCardDropsAction>.Instance,
			handler => new SteamBadgesClient(handler, NullLogger<SteamBadgesClient>.Instance),
			cache);

	/// <summary>Serves the page fixtures per ?p= so pagination gets real pages.</summary>
	private void ServePagedBadges(FakeHttpMessageHandler fake) =>
		fake.Responder = request => Html(File.ReadAllText(
			request.RequestUri!.Query.Contains("p=2", StringComparison.Ordinal) ? Page2Path : Page1Path));

	[Fact]
	public async Task ExecuteAsync_WithoutSteamId_ReturnsError()
	{
		var (webHandler, _) = CreateWebHandler();
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(CreateSession(webHandler), new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.StartsWith("steam_id parameter is required", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutSteamId_ResolvesOwnSteamIdFromCookies()
	{
		var (webHandler, fake) = CreateWebHandler();
		webHandler.SetSessionCookies("session-1", "76561197960265728%7C%7Ctoken");
		ServePagedBadges(fake);
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(CreateSession(webHandler), new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("76561197960265728", result.Output!["steam_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutSteamId_AndUnusableCookies_ReturnsError()
	{
		var (webHandler, _) = CreateWebHandler();
		webHandler.SetSessionCookies("session-1", "not-a-steamid%7C%7Ctoken");
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(CreateSession(webHandler), new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.StartsWith("steam_id parameter is required", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithNonNumericSteamId_ReturnsError()
	{
		var (webHandler, _) = CreateWebHandler();
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["steam_id"] = "gabelogannewell" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid steam_id parameter", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_ReturnsError()
	{
		var action = CreateAction(CreateWebHandler().WebHandler);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler: null),
			new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithDrops_ReturnsSortedOutput()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServePagedBadges(fake);
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" },
			CancellationToken.None);

		Assert.True(result.Success);

		// Sorted by remaining drops descending: HL2 (6), L4D2 (2), Portal 2 (1).
		var drops = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["drops"]);
		Assert.Equal(3, drops.Count);
		Assert.Equal(220U, drops[0]["app_id"]);
		Assert.Equal(6, drops[0]["drops_remaining"]);
		Assert.Equal("Half-Life 2", drops[0]["name"]);
		Assert.Equal(550U, drops[1]["app_id"]);
		Assert.Equal(620U, drops[2]["app_id"]);

		Assert.Equal(3, result.Output!["apps_with_drops"]);
		Assert.Equal(9, result.Output!["total_drops_remaining"]);
		Assert.Equal("76561198000000000", result.Output!["steam_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_WhenFetchFails_ReturnsError()
	{
		var (webHandler, fake) = CreateWebHandler();
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.NotNull(result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_SecondCall_ServedFromCache()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServePagedBadges(fake);
		using var cache = new MemoryVaporCache();
		var action = CreateAction(webHandler, cache);
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" };
		var session = CreateSession(webHandler);

		await action.ExecuteAsync(session, payload, CancellationToken.None);
		await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.Equal(2, fake.RequestCount); // pages 1+2 fetched once, second call served from cache
	}

	[Fact]
	public async Task ExecuteAsync_ForceRefresh_BypassesCache()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServePagedBadges(fake);
		using var cache = new MemoryVaporCache();
		var action = CreateAction(webHandler, cache);
		var session = CreateSession(webHandler);

		await action.ExecuteAsync(session, new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" }, CancellationToken.None);
		await action.ExecuteAsync(session, new Dictionary<string, object?> { ["steam_id"] = "76561198000000000", ["force_refresh"] = true }, CancellationToken.None);

		Assert.Equal(4, fake.RequestCount); // pages 1+2 refetched on force_refresh
	}

	[Fact]
	public async Task ExecuteAsync_CacheDisabled_FetchesEveryCall()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServePagedBadges(fake);
		using var cache = new MemoryVaporCache();
		var action = CreateAction(webHandler, cache);
		var session = CreateSession(webHandler);
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000", ["cache_ttl_seconds"] = 0 };

		await action.ExecuteAsync(session, payload, CancellationToken.None);
		await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.Equal(4, fake.RequestCount); // pages 1+2 on each call, cache bypassed
	}
}
