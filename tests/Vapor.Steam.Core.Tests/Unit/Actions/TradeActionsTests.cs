using Microsoft.Extensions.Logging;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetInventoryActionTests : IDisposable
{
	private readonly Mock<ILogger<GetInventoryAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetInventoryAction(_loggerMock.Object);
		Assert.Equal("get_inventory", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new GetInventoryAction(_loggerMock.Object);
		Assert.Equal("get_inventory", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresSteamId_ReturnsErrorIfMissing()
	{
		var action = new GetInventoryAction(_loggerMock.Object);
		var session = CreateSessionWithoutWebHandler("test_account");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresWebHandler_ReturnsErrorIfMissing()
	{
		var action = new GetInventoryAction(_loggerMock.Object);
		var session = CreateSessionWithoutWebHandler("test_account");
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private BotSession CreateSessionWithoutWebHandler(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, null, null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}

public sealed class SendTradeOfferActionTests : IDisposable
{
	private readonly Mock<ILogger<SendTradeOfferAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new SendTradeOfferAction(_loggerMock.Object);
		Assert.Equal("send_trade_offer", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new SendTradeOfferAction(_loggerMock.Object);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresPartnerOrTradeUrl_ReturnsErrorIfMissing()
	{
		var action = new SendTradeOfferAction(_loggerMock.Object);
		var session = CreateSessionWithWebHandler("test_account");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("partner_steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_InvalidTradeUrl_ReturnsError()
	{
		var action = new SendTradeOfferAction(_loggerMock.Object);
		var session = CreateSessionWithWebHandler("test_account");
		var payload = new Dictionary<string, object?> { ["trade_url"] = "https://invalid.com/trade" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Invalid trade URL", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private BotSession CreateSessionWithWebHandler(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandlerLogger = new Mock<ILogger<SteamWebHandler>>(MockBehavior.Loose);
		var webHandler = new SteamWebHandler(new SteamWebHandlerConfig(), webHandlerLogger.Object);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}

public sealed class AcceptTradeOfferActionTests : IDisposable
{
	private readonly Mock<ILogger<AcceptTradeOfferAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new AcceptTradeOfferAction(_loggerMock.Object);
		Assert.Equal("accept_trade_offer", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new AcceptTradeOfferAction(_loggerMock.Object);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresTradeOfferId_ReturnsErrorIfMissing()
	{
		var action = new AcceptTradeOfferAction(_loggerMock.Object);
		var session = CreateSessionWithWebHandler("test_account");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("trade_offer_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresPartnerSteamId_ReturnsErrorIfMissing()
	{
		var action = new AcceptTradeOfferAction(_loggerMock.Object);
		var session = CreateSessionWithWebHandler("test_account");
		var payload = new Dictionary<string, object?> { ["trade_offer_id"] = "12345678" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("partner_steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private BotSession CreateSessionWithWebHandler(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandlerLogger = new Mock<ILogger<SteamWebHandler>>(MockBehavior.Loose);
		var webHandler = new SteamWebHandler(new SteamWebHandlerConfig(), webHandlerLogger.Object);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}

public sealed class DeclineTradeOfferActionTests : IDisposable
{
	private readonly Mock<ILogger<DeclineTradeOfferAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new DeclineTradeOfferAction(_loggerMock.Object);
		Assert.Equal("decline_trade_offer", action.Name);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresTradeOfferId_ReturnsErrorIfMissing()
	{
		var action = new DeclineTradeOfferAction(_loggerMock.Object);
		var session = CreateSessionWithWebHandler("test_account");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("trade_offer_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private BotSession CreateSessionWithWebHandler(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandlerLogger = new Mock<ILogger<SteamWebHandler>>(MockBehavior.Loose);
		var webHandler = new SteamWebHandler(new SteamWebHandlerConfig(), webHandlerLogger.Object);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}

public sealed class CancelTradeOfferActionTests : IDisposable
{
	private readonly Mock<ILogger<CancelTradeOfferAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new CancelTradeOfferAction(_loggerMock.Object);
		Assert.Equal("cancel_trade_offer", action.Name);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresTradeOfferId_ReturnsErrorIfMissing()
	{
		var action = new CancelTradeOfferAction(_loggerMock.Object);
		var session = CreateSessionWithWebHandler("test_account");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("trade_offer_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private BotSession CreateSessionWithWebHandler(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandlerLogger = new Mock<ILogger<SteamWebHandler>>(MockBehavior.Loose);
		var webHandler = new SteamWebHandler(new SteamWebHandlerConfig(), webHandlerLogger.Object);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}

public sealed class TradeUrlParamsTests
{
	[Fact]
	public void TryParse_ValidUrl_ReturnsParsedParams()
	{
		var url = "https://steamcommunity.com/tradeoffer/new/?partner=12345678&token=abc123";

		var result = TradeUrlParams.TryParse(url);

		Assert.NotNull(result);
		Assert.Equal(76561197972611406UL, result.PartnerSteamId); // 76561197960265728 + 12345678
		Assert.Equal("abc123", result.Token);
	}

	[Fact]
	public void TryParse_InvalidUrl_ReturnsNull()
	{
		var result = TradeUrlParams.TryParse("https://invalid.com/trade");
		Assert.Null(result);
	}

	[Fact]
	public void TryParse_NullInput_ReturnsNull()
	{
		var result = TradeUrlParams.TryParse(null!);
		Assert.Null(result);
	}

	[Fact]
	public void TryParse_EmptyInput_ReturnsNull()
	{
		var result = TradeUrlParams.TryParse("");
		Assert.Null(result);
	}

	[Fact]
	public void TryParse_UrlWithoutPartner_ReturnsNull()
	{
		var url = "https://steamcommunity.com/tradeoffer/new/?token=abc123";

		var result = TradeUrlParams.TryParse(url);

		Assert.Null(result);
	}
}
