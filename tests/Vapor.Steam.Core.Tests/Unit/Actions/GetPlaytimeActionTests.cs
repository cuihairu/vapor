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

public sealed class GetPlaytimeActionTests : IDisposable
{
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetPlaytimeAction(NullLogger<GetPlaytimeAction>.Instance);
		Assert.Equal("get_playtime", action.Name);
	}

	[Fact]
	public void Metadata_RequiresLogin()
	{
		var action = new GetPlaytimeAction(NullLogger<GetPlaytimeAction>.Instance);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(120, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public void Constructor_Default_WiresRealGamesClient()
	{
		// The public constructor wires the real games client factory without
		// touching the network.
		var action = new GetPlaytimeAction(NullLogger<GetPlaytimeAction>.Instance);
		Assert.Equal("get_playtime", action.Name);
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

	private static string GamesTabPath => Path.Combine(AppContext.BaseDirectory, "TestData", "games_tab_all.html");

	private static GetPlaytimeAction CreateAction(SteamWebHandler webHandler, IVaporCache? cache = null) =>
		new(
			NullLogger<GetPlaytimeAction>.Instance,
			handler => new SteamProfileGamesClient(handler, NullLogger<SteamProfileGamesClient>.Instance),
			cache);

	[Fact]
	public async Task ExecuteAsync_PositiveCacheTtl_BuildsOverrideWindow()
	{
		// A positive cache_ttl_seconds is the arm between disable (0) and the
		// default: the override window is computed and the fetch succeeds.
		var (webHandler, fake) = CreateWebHandler();
		ServeGamesTab(fake);
		var action = CreateAction(webHandler);
		var payload = new Dictionary<string, object?>
		{
			["steam_id"] = "76561197960265728",
			["cache_ttl_seconds"] = 90
		};

		var result = await action.ExecuteAsync(CreateSession(webHandler), payload, CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal("76561197960265728", result.Output!["steam_id"]);
	}

	private void ServeGamesTab(FakeHttpMessageHandler fake) =>
		fake.Responder = _ => Html(File.ReadAllText(GamesTabPath));

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
		ServeGamesTab(fake);
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(CreateSession(webHandler), new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("76561197960265728", result.Output!["steam_id"]);
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
	public async Task ExecuteAsync_WithPlaytimes_ReturnsSortedOutput()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServeGamesTab(fake);
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" },
			CancellationToken.None);

		Assert.True(result.Success);

		// Sorted by hours descending, zero-hour games at the tail in appid
		// order: HL2 (1234.5), Portal 2 (36.7), Killing Floor (0.2), TF2, CS2.
		var playtimes = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["playtimes"]);
		Assert.Equal(5, playtimes.Count);
		Assert.Equal(220U, playtimes[0]["app_id"]);
		Assert.Equal(1234.5, playtimes[0]["hours"]);
		Assert.Equal("Half-Life 2", playtimes[0]["name"]);
		Assert.Equal(620U, playtimes[1]["app_id"]);
		Assert.Equal(1250U, playtimes[2]["app_id"]);
		Assert.Equal(440U, playtimes[3]["app_id"]);
		Assert.Equal(730U, playtimes[4]["app_id"]);

		Assert.Equal(5, result.Output!["games_count"]);
		Assert.Equal(1271.4, result.Output!["total_hours"]);
		Assert.Equal("76561198000000000", result.Output!["steam_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithGamesFilter_ReturnsOnlyQueriedApps()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServeGamesTab(fake);
		var action = CreateAction(webHandler);

		var result = await action.ExecuteAsync(
			CreateSession(webHandler),
			new Dictionary<string, object?> { ["steam_id"] = "76561198000000000", ["games"] = "220,730" },
			CancellationToken.None);

		Assert.True(result.Success);
		var playtimes = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["playtimes"]);
		Assert.Equal(2, playtimes.Count);
		Assert.Equal(220U, playtimes[0]["app_id"]); // 1234.5 hours first
		Assert.Equal(730U, playtimes[1]["app_id"]); // zero hours last
		Assert.Equal(2, result.Output!["games_count"]);
		Assert.Equal(1234.5, result.Output!["total_hours"]);
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
		ServeGamesTab(fake);
		using var cache = new MemoryVaporCache();
		var action = CreateAction(webHandler, cache);
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" };
		var session = CreateSession(webHandler);

		await action.ExecuteAsync(session, payload, CancellationToken.None);
		await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.Equal(1, fake.RequestCount); // single heavy games-tab fetch, second call served from cache
	}

	[Fact]
	public async Task ExecuteAsync_ForceRefresh_BypassesCache()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServeGamesTab(fake);
		using var cache = new MemoryVaporCache();
		var action = CreateAction(webHandler, cache);
		var session = CreateSession(webHandler);

		await action.ExecuteAsync(session, new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" }, CancellationToken.None);
		await action.ExecuteAsync(session, new Dictionary<string, object?> { ["steam_id"] = "76561198000000000", ["force_refresh"] = true }, CancellationToken.None);

		Assert.Equal(2, fake.RequestCount);
	}

	[Fact]
	public async Task ExecuteAsync_CacheDisabled_FetchesEveryCall()
	{
		var (webHandler, fake) = CreateWebHandler();
		ServeGamesTab(fake);
		using var cache = new MemoryVaporCache();
		var action = CreateAction(webHandler, cache);
		var session = CreateSession(webHandler);
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000", ["cache_ttl_seconds"] = 0 };

		await action.ExecuteAsync(session, payload, CancellationToken.None);
		await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.Equal(2, fake.RequestCount);
	}
}
