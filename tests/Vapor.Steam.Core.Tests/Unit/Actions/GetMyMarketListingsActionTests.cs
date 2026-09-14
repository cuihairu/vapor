using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetMyMarketListingsActionTests : IDisposable
{
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

	private readonly Mock<ILogger<GetMyMarketListingsAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_IsDistinctFromPublicMarketSearch()
	{
		var action = new GetMyMarketListingsAction(_loggerMock.Object);

		Assert.Equal("get_my_market_listings", action.Name);
		Assert.NotEqual("get_market_listings", action.Name);
	}

	[Fact]
	public void Metadata_RequiresLogin()
	{
		var action = new GetMyMarketListingsAction(_loggerMock.Object);

		Assert.Equal("get_my_market_listings", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_ReturnsError()
	{
		var action = new GetMyMarketListingsAction(_loggerMock.Object);
		var session = CreateSession(webHandler: null);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_Success_MapsPageToOutput()
	{
		var (action, fake) = CreateActionWithFake();
		fake.Responder = _ => JsonResponse(FixtureJson);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(0, result.Output!["start"]);
		Assert.Equal(100, result.Output["count"]);
		Assert.Equal(3, result.Output["total_count"]);
		Assert.Equal(2, result.Output["active_count"]);
		Assert.Equal(1, result.Output["on_hold_count"]);
		Assert.Equal(2, result.Output["to_be_confirmed_count"]);

		var listings = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["listings"]);
		Assert.Equal(3, listings.Count);
		Assert.Equal("3547123456789012345", listings[0]["listing_id"]);
		Assert.Equal(103, listings[0]["price_cents"]);
		Assert.Equal(91, listings[0]["seller_proceeds_cents"]);
		Assert.Equal("AK-47 | Redline (Field-Tested)", listings[0]["market_hash_name"]);
		Assert.True(Assert.IsType<bool>(listings[2]["cancel_requested"]));
	}

	[Fact]
	public async Task ExecuteAsync_PayloadStartAndCount_ArePassedThrough()
	{
		var (action, fake) = CreateActionWithFake();
		fake.Responder = _ => JsonResponse(FixtureJson);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["start"] = 40, ["count"] = 25 },
			CancellationToken.None);

		Assert.True(result.Success);
		var uri = Assert.Single(fake.Requests);
		Assert.Contains("start=40", uri.Query, StringComparison.Ordinal);
		Assert.Contains("count=25", uri.Query, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_FetchFailure_ReturnsError()
	{
		var (action, fake) = CreateActionWithFake();
		// Login-gated endpoint answers a redirect when the session is not logged on.
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.Found);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("logged on", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_WhenClientThrows_ReturnsError()
	{
		var action = new GetMyMarketListingsAction(
			_loggerMock.Object,
			_ => throw new InvalidOperationException("circuit breaker is open"));
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("circuit breaker is open", result.Error);
	}

	private (GetMyMarketListingsAction Action, FakeHttpMessageHandler Fake) CreateActionWithFake()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		var action = new GetMyMarketListingsAction(
			_loggerMock.Object,
			_ => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance));
		return (action, fake);
	}

	private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
	};

	private static string FixtureJson =>
		File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "market_mylistings_p1.json"));

	private BotSession CreateSession(SteamWebHandler? webHandler)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
