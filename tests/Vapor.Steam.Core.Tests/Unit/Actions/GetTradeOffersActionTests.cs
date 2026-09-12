using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetTradeOffersActionTests : IDisposable
{
	private readonly Mock<ILogger<GetTradeOffersAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetTradeOffersAction(_loggerMock.Object);
		Assert.Equal("get_trade_offers", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new GetTradeOffersAction(_loggerMock.Object);
		Assert.Equal("get_trade_offers", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_ReturnsError()
	{
		var action = new GetTradeOffersAction(_loggerMock.Object);
		var session = CreateSession(webHandler: null);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_Success_MapsOffersToOutput()
	{
		ulong expectedId = 43591234567890UL;
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetTradeOffersAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOffersResponse
			{
				Success = true,
				SentOffers =
				[
					new TradeOffer
					{
						TradeOfferId = expectedId,
						AccountIdOther = 76561198000000001UL,
						IsOurOffer = true,
						State = TradeOfferState.Active,
						ItemsToGiveCount = 1,
						ItemsToReceiveCount = 0,
						TimeCreated = DateTimeOffset.Parse("2026-09-12T06:00:00Z"),
						ItemsToGive = [new TradeAsset { AppId = 730, ContextId = 2, AssetId = 111, ClassId = 222, InstanceId = 333, Amount = 1 }]
					}
				],
				ReceivedOffers =
				[
					new TradeOffer
					{
						TradeOfferId = 43591234567891UL,
						AccountIdOther = 76561198000000002UL,
						IsOurOffer = false,
						State = TradeOfferState.Active,
						Message = "1:1 for my knife",
						TimeExpires = DateTimeOffset.Parse("2026-09-19T06:00:00Z")
					}
				]
			});
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["sent_count"]);
		Assert.Equal(1, result.Output["received_count"]);

		var sent = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["sent_offers"]);
		Dictionary<string, object?> offer = sent[0];
		Assert.Equal(expectedId.ToString(System.Globalization.CultureInfo.InvariantCulture), offer["trade_offer_id"]);
		Assert.Equal("76561198000000001", offer["partner_steam_id"]);
		Assert.Equal("Active", offer["state"]);
		Assert.True(Assert.IsType<bool>(offer["is_our_offer"]));
		Assert.Equal(1, offer["items_to_give_count"]);

		var items = Assert.IsType<List<Dictionary<string, object?>>>(offer["items_to_give"]);
		Assert.Equal(730u, items[0]["app_id"]);
		Assert.Equal("111", items[0]["asset_id"]);

		var received = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["received_offers"]);
		Assert.Equal("1:1 for my knife", received[0]["message"]);
		Assert.NotNull(received[0]["time_expires"]);
	}

	[Fact]
	public async Task ExecuteAsync_ActiveOnlyDefaultsTrue_AndIsPassedThrough()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetTradeOffersAsync(true, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOffersResponse { Success = true });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(true, result.Output!["active_only"]);
		clientMock.Verify(c => c.GetTradeOffersAsync(true, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_ActiveOnlyFalse_IsPassedThrough()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetTradeOffersAsync(false, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOffersResponse { Success = true });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["active_only"] = false },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(false, result.Output!["active_only"]);
		Assert.Equal(0, result.Output["sent_count"]);
		Assert.Equal(0, result.Output["received_count"]);
		clientMock.Verify(c => c.GetTradeOffersAsync(false, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_Failure_ReturnsErrorFromClient()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetTradeOffersAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOffersResponse { Success = false, Error = "Failed to get API key" });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Failed to get API key", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WhenClientThrows_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetTradeOffersAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("circuit breaker is open"));
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("circuit breaker is open", result.Error);
	}

	private (GetTradeOffersAction Action, Mock<ISteamTradeClient> ClientMock) CreateActionWithMock()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		var action = new GetTradeOffersAction(
			_loggerMock.Object,
			_ => clientMock.Object);
		return (action, clientMock);
	}

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
