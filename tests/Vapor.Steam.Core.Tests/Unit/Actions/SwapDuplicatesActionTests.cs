using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class SwapDuplicatesActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;
	private const ulong PartnerSteamId = 76561198000000100UL;

	private readonly Mock<ILogger<SwapDuplicatesAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new SwapDuplicatesAction(_loggerMock.Object);
		Assert.Equal("swap_duplicates", action.Name);
	}

	[Fact]
	public void Metadata_RequiresLogin()
	{
		var action = new SwapDuplicatesAction(_loggerMock.Object);
		Assert.True(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresPartnerOrTradeUrl()
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

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_url"] = "https://invalid.com/trade" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Invalid trade URL", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_SelfSwap_Fails()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("must differ", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_InvalidParameterRanges_Fail()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var tooManyApps = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = new object[] { 730, 440, 570, 252490, 2183900, 232090 }
			},
			CancellationToken.None);
		var badKeep = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["keep"] = "0" },
			CancellationToken.None);
		var badMaxSwaps = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["max_swaps"] = "101" },
			CancellationToken.None);

		Assert.Contains("limited to 5", tooManyApps.Error, StringComparison.Ordinal);
		Assert.Contains("keep", badKeep.Error, StringComparison.Ordinal);
		Assert.Contains("max_swaps", badMaxSwaps.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_DryRunByDefault_MatchesWithoutSending()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		SetupComplementaryInventories(clientMock);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(true, result.Output!["dry_run"]);
		Assert.Equal(1, result.Output["give_count"]);
		Assert.Equal(1, result.Output["receive_count"]);

		var matches = Assert.IsType<Dictionary<string, object?>[]>(result.Output["matches"]);
		var match = Assert.Single(matches);
		var give = Assert.IsType<Dictionary<string, object?>>(match["give"]);
		var receive = Assert.IsType<Dictionary<string, object?>>(match["receive"]);
		Assert.Equal(3UL, give["asset_id"]);
		Assert.Equal("Card A", give["name"]);
		Assert.Equal("6", give["context_id"]);
		Assert.Equal(11UL, receive["asset_id"]);
		Assert.Equal("Card B", receive["name"]);

		// Dry run never talks to the offer endpoint.
		clientMock.Verify(c => c.SendTradeOfferAsync(
			It.IsAny<ulong>(),
			It.IsAny<IReadOnlyList<TradeAsset>>(),
			It.IsAny<IReadOnlyList<TradeAsset>>(),
			It.IsAny<string?>(),
			It.IsAny<string?>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_SendTrue_SendsSymmetricOffer()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		SetupComplementaryInventories(clientMock);
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				PartnerSteamId,
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 999UL, RequiresMobileConfirmation = true });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["send"] = true,
				["message"] = "1:1 card swap"
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("999", result.Output!["trade_offer_id"]);
		Assert.Equal(true, result.Output["requires_mobile_confirmation"]);
		Assert.Equal(false, result.Output["dry_run"]);

		clientMock.Verify(c => c.SendTradeOfferAsync(
			PartnerSteamId,
			It.Is<IReadOnlyList<TradeAsset>>(items => items.Count == 1
				&& items[0].AssetId == 3UL && items[0].AppId == 753u && items[0].ContextId == 6UL),
			It.Is<IReadOnlyList<TradeAsset>>(items => items.Count == 1
				&& items[0].AssetId == 11UL && items[0].AppId == 753u && items[0].ContextId == 6UL),
			null,
			"1:1 card swap",
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_SendFailure_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		SetupComplementaryInventories(clientMock);
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
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["send"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("partner blocked trade offers", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoComplementaryDuplicates_Fails()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		// Both sides hold the same duplicate: nothing complementary to swap.
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 2, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }
				]
			});
		clientMock
			.Setup(c => c.GetInventoryAsync(PartnerSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 10, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 11, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }
				]
			});
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no complementary duplicates", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_PartnerInventoryFailure_ReportsPartnerSide()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		clientMock
			.Setup(c => c.GetInventoryAsync(PartnerSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = false, Error = "inventory is private" });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("partner inventory", result.Error, StringComparison.Ordinal);
		Assert.Contains("inventory is private", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_PaginatesBothInventories()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.SetupSequence(c => c.GetInventoryAsync(OwnSteamId, 753, 6, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }],
				HasMore = true,
				LastAssetId = 1
			})
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 3, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }
				]
			});
		clientMock
			.SetupSequence(c => c.GetInventoryAsync(PartnerSteamId, 753, 6, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 10, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" }],
				HasMore = true,
				LastAssetId = 10
			})
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 11, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" }
				]
			});
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["give_count"]);
		clientMock.Verify(c => c.GetInventoryAsync(OwnSteamId, 753, 6, 1UL, It.IsAny<CancellationToken>()), Times.Once);
		clientMock.Verify(c => c.GetInventoryAsync(PartnerSteamId, 753, 6, 10UL, It.IsAny<CancellationToken>()), Times.Once);
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

	[Fact]
	public async Task ExecuteAsync_InvalidPartnerSteamId_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var junk = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = "not-a-number" },
			CancellationToken.None);
		var zero = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = "0" },
			CancellationToken.None);

		Assert.All(new[] { junk, zero }, r =>
		{
			Assert.False(r.Success);
			Assert.Equal("Invalid partner_steam_id parameter", r.Error);
		});
	}

	[Fact]
	public async Task ExecuteAsync_EmptyAppIdsList_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		// A provided but unusable app_ids list leaves nothing to scan — refused
		// instead of silently falling back to the 753 default.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = new object[] { 0, true }
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("app_ids must contain at least one app id when provided", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_TradeUrl_ExtractsPartnerAndToken_SendsWithMessage()
	{
		const ulong urlPartner = 76561197972611406UL; // account id 12345678
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
					new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 3, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }
				]
			});
		clientMock
			.Setup(c => c.GetInventoryAsync(urlPartner, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 10, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" },
					new InventoryItem { AssetId = 11, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" }
				]
			});
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new TradeOfferResult { Success = true, TradeOfferId = 555, RequiresMobileConfirmation = true });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["trade_url"] = "https://steamcommunity.com/tradeoffer/new/?partner=12345678&token=t0k3n",
				["send"] = true,
				["message"] = "swap?"
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(false, result.Output!["dry_run"]);
		Assert.Equal("555", result.Output["trade_offer_id"]);
		Assert.Equal(true, result.Output["requires_mobile_confirmation"]);
		clientMock.Verify(c => c.SendTradeOfferAsync(
			urlPartner,
			It.IsAny<IReadOnlyList<TradeAsset>>(),
			It.IsAny<IReadOnlyList<TradeAsset>>(),
			"t0k3n",
			"swap?",
			It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_KeepAndMaxSwaps_ValueShapes_AreAccepted()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		// Three copies per side, keep=2 → exactly one spare pair to swap.
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 3, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 5, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }
				]
			});
		clientMock
			.Setup(c => c.GetInventoryAsync(PartnerSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 10, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" },
					new InventoryItem { AssetId = 11, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" },
					new InventoryItem { AssetId = 12, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" }
				]
			});
		var session = CreateSession(CreateWebHandler());

		// long, JsonElement number, and JsonElement numeric string shapes all parse.
		var viaLong = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["keep"] = 2L, ["max_swaps"] = 5L },
			CancellationToken.None);
		var viaElements = await action.ExecuteAsync(
			session,
			System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
				"""{"partner_steam_id":"76561198000000100","keep":2,"max_swaps":"5"}""")!,
			CancellationToken.None);

		Assert.All(new[] { viaLong, viaElements }, r =>
		{
			Assert.True(r.Success);
			Assert.Equal(2, r.Output!["keep"]);
			Assert.Equal(1, r.Output["give_count"]);
		});
	}

	[Fact]
	public async Task ExecuteAsync_RateLimited_Fails()
	{
		var (clientMock, actionWithLimiter) = CreateActionWithLimiter(CreateExhaustedRateLimiter());
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		SetupComplementaryInventories(clientMock);
		var session = CreateSession(CreateWebHandler());

		var result = await actionWithLimiter.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["send"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error, StringComparison.OrdinalIgnoreCase);
		// The offer endpoint is never reached.
		clientMock.Verify(c => c.SendTradeOfferAsync(
			It.IsAny<ulong>(),
			It.IsAny<IReadOnlyList<TradeAsset>>(),
			It.IsAny<IReadOnlyList<TradeAsset>>(),
			It.IsAny<string?>(),
			It.IsAny<string?>(),
			It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_SendThrows_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		SetupComplementaryInventories(clientMock);
		clientMock
			.Setup(c => c.SendTradeOfferAsync(
				It.IsAny<ulong>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<IReadOnlyList<TradeAsset>>(),
				It.IsAny<string?>(),
				It.IsAny<string?>(),
				It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("offer endpoint down"));
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["send"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("offer endpoint down", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsValueShapes_AreAllUnderstood()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		var scannedApps = new List<uint>();
		clientMock
			.Setup(c => c.GetInventoryAsync(It.IsAny<ulong>(), It.IsAny<uint>(), It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.Callback<ulong, uint, ulong, ulong?, CancellationToken>((_, app, _, _, _) => scannedApps.Add(app))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		var session = CreateSession(CreateWebHandler());

		// Empty inventories mean no matches — the assertion is on which apps got
		// scanned per value shape.
		var viaJsonArray = await action.ExecuteAsync(
			session,
			System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
				"""{"partner_steam_id":"76561198000000100","app_ids":[753, "730", 440, true, 0]}""")!,
			CancellationToken.None);
		var viaList = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = new List<object?> { 753L, 570.0, 570.5, "252490", null }
			},
			CancellationToken.None);
		var viaSingleValue = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString(), ["app_ids"] = 730u },
			CancellationToken.None);

		Assert.All(new[] { viaJsonArray, viaList, viaSingleValue }, r =>
		{
			Assert.False(r.Success);
			Assert.Contains("no complementary duplicates", r.Error, StringComparison.Ordinal);
		});
		// Each call loads the own inventory's apps first, then the partner's.
		// Whole-number doubles parse like loot/get_inventory (570.0 -> 570),
		// while fractional ones (570.5) and null are skipped.
		Assert.Equal(new[] { 753u, 730u, 440u, 753u, 730u, 440u }, scannedApps.Take(6).ToArray());
		Assert.Equal(new[] { 753u, 570u, 252490u, 753u, 570u, 252490u }, scannedApps.Skip(6).Take(6).ToArray());
		Assert.Equal(new[] { 730u, 730u }, scannedApps.Skip(12).ToArray());
	}

	private (Mock<ISteamTradeClient> ClientMock, SwapDuplicatesAction Action) CreateActionWithLimiter(TradeRateLimiter limiter)
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		var action = new SwapDuplicatesAction(
			_loggerMock.Object,
			_ => clientMock.Object,
			limiter);
		return (clientMock, action);
	}

	/// <summary>
	/// A limiter whose single window slot and concurrency slot are both held by an
	/// unreleased lease, so any action acquire times out and returns null.
	/// </summary>
	private static TradeRateLimiter CreateExhaustedRateLimiter()
	{
		var limiter = new TradeRateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(5),
			MaxConcurrentOperations = 1,
			AcquireTimeout = TimeSpan.FromMilliseconds(100)
		});
		_ = limiter.AcquireAsync("test_account").GetAwaiter().GetResult();
		return limiter;
	}

	private static void SetupComplementaryInventories(Mock<ISteamTradeClient> clientMock)
	{
		// Own: two copies of A (one spare). Partner: two copies of B (one spare).
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 3, AppId = 753, ClassId = 100, Tradable = true, MarketHashName = "Card A" }
				]
			});
		clientMock
			.Setup(c => c.GetInventoryAsync(PartnerSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 10, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" },
					new InventoryItem { AssetId = 11, AppId = 753, ClassId = 200, Tradable = true, MarketHashName = "Card B" }
				]
			});
	}

	private (SwapDuplicatesAction Action, Mock<ISteamTradeClient> ClientMock) CreateActionWithMock()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		var action = new SwapDuplicatesAction(
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
