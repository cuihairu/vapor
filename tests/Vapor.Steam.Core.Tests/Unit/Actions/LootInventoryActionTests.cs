using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class LootInventoryActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;
	private const ulong PartnerSteamId = 76561198000000100UL;

	private readonly Mock<ILogger<LootInventoryAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new LootInventoryAction(_loggerMock.Object);
		Assert.Equal("loot_inventory", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new LootInventoryAction(_loggerMock.Object);
		Assert.Equal("loot_inventory", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(120, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresPartnerOrTradeUrl_ReturnsErrorIfMissing()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("partner_steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_InvalidTradeUrl_ReturnsError()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());
		var payload = new Dictionary<string, object?> { ["trade_url"] = "https://invalid.com/trade" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Invalid trade URL", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_LootsOnlyTradableItems_AndSendsOffer()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, AppId = 753, Tradable = true },
					new InventoryItem { AssetId = 2, AppId = 753, Tradable = false },
					new InventoryItem { AssetId = 3, AppId = 753, Tradable = true, TradabilityDate = DateTimeOffset.UtcNow.AddDays(7) }
				]
			});
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				PartnerSteamId,
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 999UL });
		var session = CreateSession(CreateWebHandler());
		var payload = new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("999", result.Output!["trade_offer_id"]);
		// Only asset 1 ships: asset 2 is untradable, asset 3 is under a future tradability lock.
		Assert.Equal(1, result.Output["item_count"]);
		Assert.Equal(false, result.Output["requires_mobile_confirmation"]);

		var scanned = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["apps_scanned"]);
		Assert.Equal(753u, scanned[0]["app_id"]);
		Assert.Equal("6", scanned[0]["context_id"]);
		Assert.Equal(1, scanned[0]["tradable_items"]);

		clientMock.Verify(c => c.SendTradeOfferAsync(
			PartnerSteamId,
			It.Is<IReadOnlyList<TradeAsset>>(items => items.Count == 1
				&& items[0].AppId == 753 && items[0].ContextId == 6UL && items[0].AssetId == 1UL),
			It.Is<IReadOnlyList<TradeAsset>>(items => items.Count == 0),
			null,
			It.IsAny<string?>(),
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_DefaultsToCommunityApp753Context6()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 5, AppId = 753, Tradable = true }] });
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 1000UL });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		clientMock.Verify(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()), Times.Once);
		clientMock.Verify(c => c.GetInventoryAsync(It.IsAny<ulong>(), It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsOverride_UsesGameContext2ForNonCommunityApps()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 730, 2, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 7, AppId = 730, Tradable = true, Amount = 1 }] });
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 1001UL });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["app_ids"] = new object[] { 730 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["item_count"]);
		clientMock.Verify(c => c.GetInventoryAsync(OwnSteamId, 730, 2, null, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_PaginatesUntilNoMore()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.SetupSequence(c => c.GetInventoryAsync(OwnSteamId, 753, 6, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 10, AppId = 753, Tradable = true }], HasMore = true, LastAssetId = 10 })
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 11, AppId = 753, Tradable = true }] });
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 1002UL });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["item_count"]);
		clientMock.Verify(c => c.GetInventoryAsync(OwnSteamId, 753, 6, 10UL, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_MobileConfirmationRequired_ReportsInOutput()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 20, AppId = 753, Tradable = true }] });
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 1003UL, RequiresMobileConfirmation = true });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(true, result.Output!["requires_mobile_confirmation"]);
	}

	[Fact]
	public async Task ExecuteAsync_NoTradableItems_Fails()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 30, AppId = 753, Tradable = false }] });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no tradable items", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_InventoryFailure_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = false, Error = "inventory endpoint exploded" });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to load inventory for app 753", result.Error, StringComparison.Ordinal);
		Assert.Contains("inventory endpoint exploded", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_SendFailure_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 40, AppId = 753, Tradable = true }] });
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = false, Error = "partner blocked trade offers" });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("partner blocked trade offers", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoOwnSteamId_Fails()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns((ulong?)null);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("own SteamID", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_TooManyApps_ReturnsError()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());
		var payload = new Dictionary<string, object?>
		{
			["partner_steam_id"] = PartnerSteamId.ToString(),
			["app_ids"] = new object[] { 730, 440, 570, 252490, 2183900, 232090 }
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("limited to 5", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoWebHandler_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(webHandler: null);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private (LootInventoryAction Action, Mock<ISteamTradeClient> ClientMock) CreateActionWithMock()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		var action = new LootInventoryAction(
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
