using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class CreateMarketListingActionTests : IDisposable
{
	// POST /market/sellitem/ → the fixture success reply unless PostResponder
	// overrides it; POSTed bodies are captured for contract assertions.
	private sealed class MarketFakeHandler : HttpMessageHandler
	{
		public List<string> PostedBodies { get; } = [];
		public Func<HttpResponseMessage>? PostResponder { get; set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.StartsWith("/market/sellitem", StringComparison.Ordinal))
			{
				PostedBodies.Add(request.Content is null
					? string.Empty
					: request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
				return Task.FromResult(PostResponder?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(
						File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "market_sellitem_response.json")),
						System.Text.Encoding.UTF8, "application/json")
				});
			}

			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	private readonly Mock<ILogger<CreateMarketListingAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new CreateMarketListingAction(_loggerMock.Object);

		Assert.Equal("create_market_listing", action.Name);
		Assert.True(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_DryRunByDefault_ReportsPlanWithoutRequests()
	{
		var (action, fake, session) = CreateAction(agentSwitchOn: false);

		var result = await action.ExecuteAsync(session, DryRunPayload(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.True(Assert.IsType<bool>(result.Output!["dry_run"]));
		Assert.True(Assert.IsType<bool>(result.Output["would_list"]));
		var pricing = Assert.IsType<Dictionary<string, object?>>(result.Output["pricing"]);
		// 91 seller → fees 4 + 9 → 104 buyer.
		Assert.Equal(91, pricing["seller_proceeds_cents"]);
		Assert.Equal(4, pricing["steam_fee_cents"]);
		Assert.Equal(9, pricing["publisher_fee_cents"]);
		Assert.Equal(104, pricing["buyer_price_cents"]);
		Assert.Empty(fake.PostedBodies);
	}

	[Fact]
	public async Task ExecuteAsync_BuyerPrice_WalksDownToSellerProceeds()
	{
		var payload = DryRunPayload();
		payload.Remove("seller_proceeds_cents");
		payload["buyer_price_cents"] = 115;

		var result = await RunAsync(payload);

		Assert.True(result.Success, result.Error ?? "no error");
		var pricing = Assert.IsType<Dictionary<string, object?>>(result.Output!["pricing"]);
		Assert.Equal(100, pricing["seller_proceeds_cents"]);
		Assert.Equal(115, pricing["buyer_price_cents"]);
	}

	[Fact]
	public async Task ExecuteAsync_BothPrices_IsRefused()
	{
		var payload = DryRunPayload();
		payload["buyer_price_cents"] = 115;

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("exactly one", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MissingPrice_IsRefused()
	{
		var payload = DryRunPayload();
		payload.Remove("seller_proceeds_cents");

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("price is required", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("seller_proceeds_cents", 0)]
	[InlineData("seller_proceeds_cents", -5)]
	[InlineData("buyer_price_cents", 0)]
	public async Task ExecuteAsync_NonPositivePrice_IsRefused(string priceKey, int priceValue)
	{
		var payload = DryRunPayload();
		payload.Remove("seller_proceeds_cents");
		payload[priceKey] = priceValue;

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("at least 1", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MissingAssetId_IsRefused()
	{
		var payload = DryRunPayload();
		payload.Remove("asset_id");

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("asset_id is required", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_RealRunWithoutAgentSwitch_IsRefused()
	{
		var (action, fake, session) = CreateAction(agentSwitchOn: false);
		var payload = DryRunPayload();
		payload["send"] = true;

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("AGENT_MARKET_LISTINGS_ENABLED", result.Error ?? string.Empty, StringComparison.Ordinal);
		Assert.Empty(fake.PostedBodies);
	}

	[Fact]
	public async Task ExecuteAsync_RealRun_PostsSellerPriceAndReportsConfirmations()
	{
		var (action, fake, session) = CreateAction(agentSwitchOn: true);
		var payload = DryRunPayload();
		payload["send"] = true;

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success, result.Error ?? "no error");
		string body = Assert.Single(fake.PostedBodies);
		// The sellitem price field carries the seller amount; the session id is echoed.
		Assert.Contains("price=91", body, StringComparison.Ordinal);
		Assert.Contains("sessionid=session-123", body, StringComparison.Ordinal);
		Assert.False(Assert.IsType<bool>(result.Output!["dry_run"]));
		Assert.True(Assert.IsType<bool>(result.Output["success"]));
		Assert.True(Assert.IsType<bool>(result.Output["needs_mobile_confirmation"]));
		Assert.False(Assert.IsType<bool>(result.Output["needs_email_confirmation"]));
	}

	[Fact]
	public async Task ExecuteAsync_RejectedBySteam_SurfacesMessageAsError()
	{
		// Steam rejects listings (rate limiting included) with success=false and
		// a human-readable message — the task fails, keeping the full reply.
		var fake = new MarketFakeHandler
		{
			PostResponder = () => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					"""{ "success": false, "message": "You cannot list this item. Rate limit exceeded." }""",
					System.Text.Encoding.UTF8, "application/json")
			}
		};
		var (action, _, session) = CreateAction(agentSwitchOn: true, fake);
		var result = await action.ExecuteAsync(session, RealPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Rate limit", result.Error ?? string.Empty, StringComparison.Ordinal);
		var output = Assert.IsType<Dictionary<string, object?>>(result.Output);
		Assert.False(Assert.IsType<bool>(output["success"]));
		Assert.Contains("Rate limit", Assert.IsType<string>(output["message"]), StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_RequestLayerFailure_ReturnsError()
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			new FailingHandler());
		var action = new CreateMarketListingAction(
			_loggerMock.Object,
			marketListingsEnabled: true,
			_ => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance));
		var session = CreateSession(webHandler);

		var result = await action.ExecuteAsync(session, RealPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to create market listing", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_IsRefused()
	{
		var action = new CreateMarketListingAction(_loggerMock.Object);
		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password"),
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			_sessionLoggerMock.Object,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);
		_sessions.Add(session);

		var result = await action.ExecuteAsync(session, DryRunPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler not available", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MissingAppId_IsRefused()
	{
		var payload = DryRunPayload();
		payload.Remove("app_id");

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("app_id is required", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MissingContextId_IsRefused()
	{
		var payload = DryRunPayload();
		payload.Remove("context_id");

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("context_id is required", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_AmountBelowOne_IsRefused()
	{
		var payload = DryRunPayload();
		payload["amount"] = 0;

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("amount must be at least 1", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_BuyerPriceBelowMinimumListingPrice_IsRefused()
	{
		// Fees floor at 1 cent each, so a 2-cent buyer price is unreachable.
		var payload = DryRunPayload();
		payload.Remove("seller_proceeds_cents");
		payload["buyer_price_cents"] = 2;

		var result = await RunAsync(payload);

		Assert.False(result.Success);
		Assert.Contains("below the minimum listing price", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_EmailConfirmationRequired_ReportsEmailDomain()
	{
		var fake = new MarketFakeHandler
		{
			PostResponder = () => new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					"""{ "success": true, "needs_email_confirmation": true, "email_domain": "example.com" }""",
					System.Text.Encoding.UTF8, "application/json")
			}
		};
		var (action, _, session) = CreateAction(agentSwitchOn: true, fake);

		var result = await action.ExecuteAsync(session, RealPayload(), CancellationToken.None);

		Assert.True(result.Success, result.Error ?? "no error");
		Assert.True(Assert.IsType<bool>(result.Output!["needs_email_confirmation"]));
		Assert.Equal("example.com", result.Output["email_domain"]);
	}

	[Fact]
	public async Task ExecuteAsync_CanceledDuringRealRun_ReportsCanceled()
	{
		// The web handler rethrows cancellation untouched, so the action's
		// cancellation guard sees the caller's own canceled token.
		var fake = new MarketFakeHandler
		{
			PostResponder = () => throw new OperationCanceledException("send aborted")
		};
		var (action, _, session) = CreateAction(agentSwitchOn: true, fake);
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var result = await action.ExecuteAsync(session, RealPayload(), cts.Token);

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_UnexpectedExceptionDuringRealRun_SurfacesMessage()
	{
		var fake = new MarketFakeHandler
		{
			PostResponder = () => throw new InvalidOperationException("market client blew up")
		};
		var (action, _, session) = CreateAction(agentSwitchOn: true, fake);

		var result = await action.ExecuteAsync(session, RealPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("market client blew up", result.Error);
	}

	// Helpers ---------------------------------------------------------------

	private static Dictionary<string, object?> DryRunPayload() => new()
	{
		["app_id"] = 730,
		["context_id"] = "6",
		["asset_id"] = "35471234567",
		["seller_proceeds_cents"] = 91
	};

	private static Dictionary<string, object?> RealPayload()
	{
		var payload = DryRunPayload();
		payload["send"] = true;
		return payload;
	}

	// Default wiring: fresh fake handler, session id set (cancellations and
	// listings both need it), agent switch per parameter.
	private (CreateMarketListingAction Action, MarketFakeHandler Fake, BotSession Session) CreateAction(
		bool agentSwitchOn,
		MarketFakeHandler? fake = null)
	{
		fake ??= new MarketFakeHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		webHandler.SetSessionCookies("session-123", "token");
		var action = new CreateMarketListingAction(
			_loggerMock.Object,
			agentSwitchOn,
			_ => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance));
		var session = CreateSession(webHandler);
		return (action, fake, session);
	}

	private async Task<ActionResult> RunAsync(Dictionary<string, object?> payload)
	{
		var (action, _, session) = CreateAction(agentSwitchOn: false);
		return await action.ExecuteAsync(session, payload, CancellationToken.None);
	}

	private BotSession CreateSession(SteamWebHandler webHandler)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private sealed class FailingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
