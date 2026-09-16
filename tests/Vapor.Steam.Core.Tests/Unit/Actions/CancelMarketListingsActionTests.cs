using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class CancelMarketListingsActionTests : IDisposable
{
	// GET /market/mylistings → the fixture page (3 listings: CS2 103c,
	// Steam coupon 30c, TF2 key 250c). POST /market/removelisting/* →
	// 2xx except the ids in FailIds (simulating one failed cancellation).
	private sealed class MarketFakeHandler : HttpMessageHandler
	{
		public List<string> PostedListingIds { get; } = [];
		public required HashSet<string> FailIds { get; init; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.StartsWith("/market/removelisting/", StringComparison.Ordinal))
			{
				string listingId = request.RequestUri.AbsolutePath["/market/removelisting/".Length..];
				PostedListingIds.Add(listingId);
				bool ok = !FailIds.Contains(listingId);
				return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
				{
					Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
				});
			}

			string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "market_mylistings_p1.json"));
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(fixture, System.Text.Encoding.UTF8, "application/json")
			});
		}
	}

	private readonly Mock<ILogger<CancelMarketListingsAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new CancelMarketListingsAction(_loggerMock.Object);

		Assert.Equal("cancel_market_listings", action.Name);
		Assert.True(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_DryRunByDefault_PreviewsWithoutCancelRequests()
	{
		var (action, fake, session) = CreateAction();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_id"] = 730 }, CancellationToken.None);

		Assert.True(result.Success);
		Assert.True(Assert.IsType<bool>(result.Output!["dry_run"]));
		Assert.Equal(1, result.Output["matched"]);
		Assert.Equal(3, result.Output["scanned"]);
		Assert.Null(result.Output["succeeded"]);
		Assert.Null(result.Output["failed"]);
		var listings = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["listings"]);
		Assert.Equal("3547123456789012345", listings[0]["listing_id"]);
		Assert.True(Assert.IsType<bool>(listings[0]["would_cancel"]));
		Assert.Empty(fake.PostedListingIds);
	}

	[Fact]
	public async Task ExecuteAsync_RealRun_CancelsEachMatchAndSummarizes()
	{
		var (action, fake, session) = CreateAction();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = 730, ["dry_run"] = false, ["delay_ms"] = 0 },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["matched"]);
		Assert.Equal(1, result.Output["succeeded"]);
		Assert.Equal(0, result.Output["failed"]);
		Assert.Equal(["3547123456789012345"], fake.PostedListingIds);
	}

	[Fact]
	public async Task ExecuteAsync_SingleFailure_DoesNotAbortBatch()
	{
		var (action, fake, session) = CreateAction();
		fake.FailIds.Add("3547123456789012346");

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["min_price_cents"] = 1, ["dry_run"] = false, ["delay_ms"] = 0 },
			CancellationToken.None);

		Assert.True(result.Success, result.Error ?? "no error");
		Assert.Equal(3, result.Output!["matched"]);
		Assert.Equal(2, result.Output["succeeded"]);
		Assert.Equal(1, result.Output["failed"]);
		var listings = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["listings"]);
		Assert.False(Assert.IsType<bool>(listings[1]["succeeded"]));
		Assert.Equal(3, fake.PostedListingIds.Count);
	}

	[Fact]
	public async Task ExecuteAsync_HashNameFilter_MatchesExactly()
	{
		var (action, _, session) = CreateAction();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["market_hash_name"] = "Mann Co. Supply Crate Key" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["matched"]);
		var listings = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["listings"]);
		Assert.Equal("3547123456789012347", listings[0]["listing_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_PriceRangeFilter_IsInclusive()
	{
		var (action, _, session) = CreateAction();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["min_price_cents"] = 30, ["max_price_cents"] = 103 },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["matched"]);
	}

	[Fact]
	public async Task ExecuteAsync_AgeFilter_MatchesOldListings()
	{
		var (action, _, session) = CreateAction();

		// Fixture listings are stamped 2025-10: one second old is "younger"
		// than nothing here, everything matches; int.MaxValue seconds (~68
		// years) marks everything as too recent. Both stay independent of the
		// current wall clock (and inside the int range of GetInt32).
		var oldEnough = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["older_than_seconds"] = 1 }, CancellationToken.None);
		var tooRecent = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["older_than_seconds"] = int.MaxValue }, CancellationToken.None);

		Assert.Equal(3, oldEnough.Output!["matched"]);
		Assert.Equal(0, tooRecent.Output!["matched"]);
	}

	[Fact]
	public async Task ExecuteAsync_RealRunWithoutAnyFilter_IsRefused()
	{
		var (action, fake, session) = CreateAction();
		// The control plane endpoint guards this too, but the action must not
		// rely on that: a direct dispatch with dry_run=false and no filter is
		// refused before any request goes out.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["dry_run"] = false, ["delay_ms"] = 0 },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("refusing", result.Error ?? string.Empty, StringComparison.Ordinal);
		Assert.Empty(fake.PostedListingIds);
	}

	[Fact]
	public async Task ExecuteAsync_InvertedPriceRange_IsRefused()
	{
		var (action, _, session) = CreateAction();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["min_price_cents"] = 500, ["max_price_cents"] = 100 },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("min_price_cents", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_ListingFetchFailure_ReturnsError()
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			new FailingHandler());
		var action = new CancelMarketListingsAction(
			_loggerMock.Object,
			_ => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance));
		var session = CreateSession(webHandler);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = 730 },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("logged on", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_IsRefused()
	{
		var action = new CancelMarketListingsAction(_loggerMock.Object);

		var result = await action.ExecuteAsync(CreateNullSession(), new Dictionary<string, object?> { ["app_id"] = 730 }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler not available", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NegativePriceRange_IsRefused()
	{
		var (action, fake, session) = CreateAction();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["min_price_cents"] = -1 }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("must not be negative", result.Error ?? string.Empty, StringComparison.Ordinal);
		Assert.Empty(fake.PostedListingIds);
	}

	[Fact]
	public async Task ExecuteAsync_RealRunWithPacing_DelayBetweenCancellations()
	{
		var (action, fake, session) = CreateAction();

		// delay_ms=1 keeps the pacing path exercised without slowing the test:
		// three matched listings → two 1ms pauses between the cancellations.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["min_price_cents"] = 1, ["dry_run"] = false, ["delay_ms"] = 1 },
			CancellationToken.None);

		Assert.True(result.Success, result.Error ?? "no error");
		Assert.Equal(3, result.Output!["succeeded"]);
		Assert.Equal(3, fake.PostedListingIds.Count);
	}

	[Fact]
	public async Task ExecuteAsync_CanceledDuringRun_ReportsCanceled()
	{
		var (action, fake, session) = CreateAction();
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = 730, ["dry_run"] = false, ["delay_ms"] = 0 },
			cts.Token);

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
		Assert.Empty(fake.PostedListingIds);
	}

	[Fact]
	public async Task ExecuteAsync_TransportThrows_SurfacesExceptionMessage()
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			new ThrowingHandler());
		var action = new CancelMarketListingsAction(
			_loggerMock.Object,
			_ => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance));
		var session = CreateSession(webHandler);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_id"] = 730 }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("transport broke", result.Error);
	}

	// A session without a web handler for the webHandler-null guard; separate
	// from CreateSession (which exists to hand the sessions to Dispose).
	private BotSession CreateNullSession()
	{
		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password"),
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			_sessionLoggerMock.Object,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);
		_sessions.Add(session);
		return session;
	}

	private (CancelMarketListingsAction Action, MarketFakeHandler Fake, BotSession Session) CreateAction()
	{
		var fake = new MarketFakeHandler { FailIds = [] };
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		// Cancellations need a session id on the handler (echoed in the POST body).
		webHandler.SetSessionCookies("session-123", "token");
		var action = new CancelMarketListingsAction(
			_loggerMock.Object,
			_ => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance));
		var session = CreateSession(webHandler);
		return (action, fake, session);
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

	private sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("transport broke");
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
