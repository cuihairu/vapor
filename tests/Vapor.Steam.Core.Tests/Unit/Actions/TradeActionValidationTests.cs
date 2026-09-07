using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class TradeActionValidationTests : IDisposable
{
	private static readonly ulong PartnerSteamId = 76561197960265728UL + 4242;
	private static readonly ulong OwnSteamId = 76561197960265728UL + 1111;

	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	private BotSession CreateSession(string accountName = "test_account")
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandlerLogger = new Mock<ILogger<SteamWebHandler>>(MockBehavior.Loose);
		var webHandler = new SteamWebHandler(new SteamWebHandlerConfig(), webHandlerLogger.Object);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private static Mock<ISteamTradeClient> CreateTradeClientMock()
	{
		var mock = new Mock<ISteamTradeClient>(MockBehavior.Strict);
		mock.Setup(c => c.GetOwnSteamId()).Returns(OwnSteamId);
		return mock;
	}

	private static TradeOffer Offer(
		ulong tradeOfferId = 500,
		bool isOurOffer = false,
		TradeOfferState state = TradeOfferState.Active,
		uint accountIdOther = 4242,
		DateTimeOffset? expires = null) =>
		new()
		{
			TradeOfferId = tradeOfferId,
			AccountIdOther = accountIdOther,
			IsOurOffer = isOurOffer,
			State = state,
			TimeCreated = DateTimeOffset.UtcNow.AddHours(-1),
			TimeExpires = expires
		};

	private static InventoryResponse InventoryOf(params InventoryItem[] items) =>
		new()
		{
			Success = true,
			Items = items,
			AppId = 730,
			ContextId = 2
		};

	private static InventoryItem TradableItem(ulong assetId, int amount = 1) =>
		new() { AssetId = assetId, ClassId = assetId + 1, AppId = 730, Amount = amount, Tradable = true };

	// --- SendTradeOffer: ownership verification ---

	[Fact]
	public async Task SendTradeOffer_WithOwnedTradableAssets_Succeeds()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 730, 2, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(InventoryOf(TradableItem(10), TradableItem(11)));
		clientMock
			.Setup(c => c.SendTradeOfferAsync(PartnerSteamId, It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 900 });

		var action = new SendTradeOfferAction(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["items_to_give"] = new List<Dictionary<string, object?>> { new() { ["asset_id"] = "10" } }
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(true, result.Output?.GetValueOrDefault("ownership_verified"));

		clientMock.Verify(c => c.SendTradeOfferAsync(PartnerSteamId, It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task SendTradeOffer_WithUnknownAsset_FailsWithOwnershipError()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 730, 2, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(InventoryOf(TradableItem(10)));

		var action = new SendTradeOfferAction(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["items_to_give"] = new List<Dictionary<string, object?>> { new() { ["asset_id"] = "999" } }
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("was not found", result.Error ?? "", StringComparison.OrdinalIgnoreCase);

		clientMock.Verify(
			c => c.SendTradeOfferAsync(It.IsAny<ulong>(), It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task SendTradeOffer_WithUntradableAsset_Fails()
	{
		var clientMock = CreateTradeClientMock();
		var untradable = TradableItem(10) with { Tradable = false };
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 730, 2, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(InventoryOf(untradable));

		var action = new SendTradeOfferAction(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["items_to_give"] = new List<Dictionary<string, object?>> { new() { ["asset_id"] = "10" } }
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not tradable", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task SendTradeOffer_WithoutResolvableSteamId_FailsWithSkipHint()
	{
		var clientMock = CreateTradeClientMock();
		clientMock.Setup(c => c.GetOwnSteamId()).Returns((ulong?)null);

		var action = new SendTradeOfferAction(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["items_to_give"] = new List<Dictionary<string, object?>> { new() { ["asset_id"] = "10" } }
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("skip_verification", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task SendTradeOffer_WithSkipVerification_SkipsInventoryCheck()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.SendTradeOfferAsync(PartnerSteamId, It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<IReadOnlyList<TradeAsset>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 901 });

		var action = new SendTradeOfferAction(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["skip_verification"] = true,
			["items_to_give"] = new List<Dictionary<string, object?>> { new() { ["asset_id"] = "999" } }
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.True(result.Success);
		clientMock.Verify(
			c => c.GetInventoryAsync(It.IsAny<ulong>(), It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	// --- AcceptTradeOffer: state machine verification ---

	[Fact]
	public async Task AcceptTradeOffer_WithActiveReceivedOfferFromExpectedPartner_Succeeds()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(expires: DateTimeOffset.UtcNow.AddHours(1)) });
		clientMock
			.Setup(c => c.AcceptTradeOfferAsync(500, PartnerSteamId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 500 });

		var action = new AcceptTradeOfferAction(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["trade_offer_id"] = "500",
			["partner_steam_id"] = PartnerSteamId.ToString()
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.True(result.Success);
		clientMock.Verify(c => c.AcceptTradeOfferAsync(500, PartnerSteamId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task AcceptTradeOffer_WithOfferSentByUs_Fails()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(isOurOffer: true) });

		var action = new AcceptTradeOfferAction(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["trade_offer_id"] = "500",
			["partner_steam_id"] = PartnerSteamId.ToString()
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("sent by us", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
		clientMock.Verify(
			c => c.AcceptTradeOfferAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task AcceptTradeOffer_WithMismatchedPartner_Fails()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(accountIdOther: 7777) });

		var action = new AcceptTradeOfferAction(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["trade_offer_id"] = "500",
			["partner_steam_id"] = PartnerSteamId.ToString()
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("expected", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AcceptTradeOffer_WithTerminalState_Fails()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(state: TradeOfferState.Accepted) });

		var action = new AcceptTradeOfferAction(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["trade_offer_id"] = "500",
			["partner_steam_id"] = PartnerSteamId.ToString()
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Accepted", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AcceptTradeOffer_WithVerifyStateDisabled_SkipsLookup()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.AcceptTradeOfferAsync(500, PartnerSteamId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 500 });

		var action = new AcceptTradeOfferAction(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["trade_offer_id"] = "500",
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["verify_state"] = false
		};

		var result = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.True(result.Success);
		clientMock.Verify(
			c => c.GetTradeOfferAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	// --- DeclineTradeOffer ---

	[Fact]
	public async Task DeclineTradeOffer_WithActiveReceivedOffer_Succeeds()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer() });
		clientMock
			.Setup(c => c.DeclineTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 500 });

		var action = new DeclineTradeOfferAction(
			NullLogger<DeclineTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["trade_offer_id"] = "500" },
			CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task DeclineTradeOffer_WithOurOwnOffer_FailsAndSuggestsCancel()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(isOurOffer: true) });

		var action = new DeclineTradeOfferAction(
			NullLogger<DeclineTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["trade_offer_id"] = "500" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cancel", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	// --- CancelTradeOffer ---

	[Fact]
	public async Task CancelTradeOffer_WithSentActiveOffer_Succeeds()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(isOurOffer: true, expires: DateTimeOffset.UtcNow.AddHours(1)) });
		clientMock
			.Setup(c => c.CancelTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 500 });

		var action = new CancelTradeOfferAction(
			NullLogger<CancelTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["trade_offer_id"] = "500" },
			CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task CancelTradeOffer_WithReceivedOffer_FailsAndSuggestsDecline()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(isOurOffer: false) });

		var action = new CancelTradeOfferAction(
			NullLogger<CancelTradeOfferAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["trade_offer_id"] = "500" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("decline", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	// --- Rate limiting integration ---

	[Fact]
	public async Task TradeActions_WithExhaustedRateLimit_FailWithRateLimitError()
	{
		var clientMock = CreateTradeClientMock();
		clientMock
			.Setup(c => c.GetTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOffer = Offer(isOurOffer: true, expires: DateTimeOffset.UtcNow.AddHours(1)) });
		clientMock
			.Setup(c => c.CancelTradeOfferAsync(500, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 500 });

		DateTimeOffset frozenClock = DateTimeOffset.UtcNow;
		using var limiter = new TradeRateLimiter(
			new TradeRateLimiterOptions
			{
				MaxOperationsPerWindow = 1,
				Window = TimeSpan.FromMinutes(10),
				AcquireTimeout = TimeSpan.FromMilliseconds(200)
			},
			() => frozenClock);

		var action = new CancelTradeOfferAction(
			NullLogger<CancelTradeOfferAction>.Instance,
			_ => clientMock.Object,
			limiter);

		var first = await action.ExecuteAsync(
			CreateSession("acct"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "500" },
			CancellationToken.None);

		var second = await action.ExecuteAsync(
			CreateSession("acct"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "500" },
			CancellationToken.None);

		Assert.True(first.Success);
		Assert.False(second.Success);
		Assert.Contains("rate limit", second.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}
}

public sealed class TradeUrlParamsExtendedTests
{
	[Fact]
	public void TryParse_With64BitPartnerSteamId_DoesNotOffsetAgain()
	{
		var url = "https://steamcommunity.com/tradeoffer/new/?partner=76561198000000000&token=tok";

		var result = TradeUrlParams.TryParse(url);

		Assert.NotNull(result);
		Assert.Equal(76561198000000000UL, result.PartnerSteamId);
		Assert.Equal("tok", result.Token);
	}

	[Fact]
	public void TryParse_With32BitPartnerSteamId_AppliesBaseOffset()
	{
		var url = "https://steamcommunity.com/tradeoffer/new/?partner=12345678&token=abc";

		var result = TradeUrlParams.TryParse(url);

		Assert.NotNull(result);
		Assert.Equal(76561197972611406UL, result.PartnerSteamId);
	}
}
