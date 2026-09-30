using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Plugins.GameAccess;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

public sealed class GetGameInventoryActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;

	private readonly Mock<ILogger<GetGameInventoryAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetGameInventoryAction(_loggerMock.Object);
		Assert.Equal("get_game_inventory", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new GetGameInventoryAction(_loggerMock.Object);
		Assert.Equal("get_game_inventory", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_MissingSteamIdWithoutWebSession_ReturnsError()
	{
		var action = new GetGameInventoryAction(_loggerMock.Object);
		var session = CreateSessionWithoutWebHandler("test_account");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresWebHandler_ReturnsErrorIfMissing()
	{
		var action = new GetGameInventoryAction(_loggerMock.Object);
		var session = CreateSessionWithoutWebHandler("test_account");
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_AggregatesItemsIntoStacks()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Tradable = true, Marketable = true, Amount = 1 },
					new InventoryItem { AssetId = 2, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Tradable = true, Marketable = true, Amount = 1 },
					new InventoryItem { AssetId = 3, ClassId = 101, InstanceId = 201, AppId = 730, Name = "M4A4", MarketHashName = "M4A4 | Howl", Tradable = false, Marketable = false, Amount = 1 }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(3, result.Output!["total_items"]);
		Assert.Equal(2, result.Output!["stack_count"]);
		var stacks = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["stacks"]);
		Assert.Equal(2, stacks.Count);
		// Largest stack first.
		Assert.Equal("100", stacks[0]["class_id"]);
		Assert.Equal(2, stacks[0]["count"]);
		Assert.Equal(2, stacks[0]["tradable_count"]);
		Assert.Equal(2, stacks[0]["marketable_count"]);
		Assert.Equal("101", stacks[1]["class_id"]);
		Assert.Equal(1, stacks[1]["count"]);
		Assert.Equal(0, stacks[1]["tradable_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_TradableOnly_FiltersBeforeAggregation()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Tradable = true, Marketable = true },
					new InventoryItem { AssetId = 2, ClassId = 101, InstanceId = 201, AppId = 730, Tradable = false, Marketable = false }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["tradable_only"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["total_items"]);
		Assert.Equal(1, result.Output!["stack_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_MarketableOnly_FiltersBeforeAggregation()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Marketable = true },
					new InventoryItem { AssetId = 2, ClassId = 101, InstanceId = 201, AppId = 730, Marketable = false }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["marketable_only"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["total_items"]);
		Assert.Equal(1, result.Output!["stack_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_AttachesValuation()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Tradable = true, Marketable = true }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "AK-47 | Redline", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$10.50", "$11.00", "1,234", null));

		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(730u, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PriceOverview { Currency = "USD", Final = 30.0m, Initial = 30.0m, DiscountPercent = 0, FinalFormatted = "$29.99" });

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.Equal(1, valuation["priced_stacks"]);
		Assert.Equal(0, valuation["unpriced_stacks"]);
		Assert.False((bool)valuation["truncated"]!);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"]);
		Assert.Single(items);
		Assert.Equal("$10.50", items[0]["lowest_price"]);
		Assert.Equal("$11.00", items[0]["median_price"]);
		Assert.Equal("1,234", items[0]["volume"]);
		var gamePrice = Assert.IsType<Dictionary<string, object?>>(valuation["game_price"]);
		Assert.Equal(730u, gamePrice["app_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_PriceFailure_DoesNotSinkValuation()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Tradable = true, Marketable = true },
					new InventoryItem { AssetId = 2, ClassId = 101, InstanceId = 201, AppId = 730, Name = "Name Tag", Marketable = false }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "AK-47 | Redline", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(false, null, null, null, "not listed on the market"));

		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(730u, "us", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("store unavailable"));

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.Equal(0, valuation["priced_stacks"]);
		Assert.Equal(1, valuation["unpriced_stacks"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"]);
		Assert.Equal("not listed on the market", items[0]["error"]);
		// App price failure is supplementary — reported but not fatal.
		var gamePrice = Assert.IsType<Dictionary<string, object?>>(valuation["game_price"]);
		Assert.NotNull(gamePrice["error"]);
	}

	[Fact]
	public async Task ExecuteAsync_InvalidSteamId_ReturnsError()
	{
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = "not-a-number" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesSteamIdFromSessionCookies()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });

		var (action, session) = CreateAction(clientMock.Object);
		session.SteamWebHandler!.SetSessionCookies("session-1", $"{OwnSteamId}%7C%7Ctoken");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(OwnSteamId.ToString(), result.Output!["steam_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_UnresolvableSteamIdWithoutParam_ReturnsError()
	{
		// Real handler without cookies: nothing to resolve the own SteamID from.
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cookies", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_AppContextCurrencyPayloadOverrides_FlowIntoCalls()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 570u, 570UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 570, Name = "Ember Spirit", MarketHashName = "Ember Spirit Item", Marketable = true }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(570u, "Ember Spirit Item", 1u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "₽12,34", "₽13,00", "7", null));

		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(570u, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PriceOverview { Currency = "USD", Final = 10.0m, Initial = 10.0m, DiscountPercent = 0, FinalFormatted = "$9.99" });

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_id"] = "570",
				["context_id"] = "570",
				["value"] = true,
				["currency"] = "1"
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(570u, result.Output!["app_id"]);
		Assert.Equal("570", result.Output!["context_id"]);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output["valuation"]);
		Assert.Equal(1u, valuation["currency"]);
		Assert.Equal("₽12,34", Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"])[0]["lowest_price"]);
	}

	[Fact]
	public async Task ExecuteAsync_FetchError_ReturnsError()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = false });

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.False(string.IsNullOrEmpty(result.Error));
	}

	[Fact]
	public async Task ExecuteAsync_TradeClientThrows_ReturnsError()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("community down"));

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("community down", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_CacheHit_SkipsPriceAndStoreLookups()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Marketable = true }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		var cacheMock = new Mock<IVaporCache>(MockBehavior.Loose);
		cacheMock
			.Setup(m => m.GetAsync<ItemPriceResult>(ItemPriceClient.CacheKey(730u, "AK-47 | Redline", null), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$12.00", "$13.00", "55", null));
		cacheMock
			.Setup(m => m.GetAsync<PriceOverview>("price:730:us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PriceOverview { Currency = "USD", Final = 30.0m, Initial = 30.0m, DiscountPercent = 0, FinalFormatted = "$29.99" });

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object, cacheMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.Equal(1, valuation["priced_stacks"]);
		Assert.Equal("$12.00", Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"])[0]["lowest_price"]);
		priceMock.Verify(
			m => m.GetItemPriceAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<uint?>(), It.IsAny<CancellationToken>()),
			Times.Never);
		storeMock.Verify(
			m => m.GetPriceAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_CacheMiss_StoresPricesUnderTtl()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Marketable = true }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "AK-47 | Redline", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$10.50", "$11.00", "1,234", null));
		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(730u, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PriceOverview { Currency = "USD", Final = 30.0m, Initial = 30.0m, DiscountPercent = 0, FinalFormatted = "$29.99" });
		var cacheMock = new Mock<IVaporCache>(MockBehavior.Loose);

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object, cacheMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		cacheMock.Verify(
			m => m.SetAsync(ItemPriceClient.CacheKey(730u, "AK-47 | Redline", null), It.IsAny<ItemPriceResult>(), SteamCacheTtl.Price, It.IsAny<CancellationToken>()),
			Times.Once);
		cacheMock.Verify(
			m => m.SetAsync("price:730:us", It.IsAny<PriceOverview>(), SteamCacheTtl.Price, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_GamePriceCanceled_FailsAction()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Marketable = true }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "AK-47 | Redline", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$10.50", "$11.00", "1,234", null));
		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(730u, "us", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException());

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cancel", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_GamePriceUnavailable_LeavesGamePriceEmpty()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Marketable = true }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "AK-47 | Redline", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$10.50", "$11.00", "1,234", null));
		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(730u, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync((PriceOverview?)null);

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.Null(valuation["game_price"]);
		Assert.Equal(1, valuation["priced_stacks"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_TruncatesValuationAtCap()
	{
		var items = new List<InventoryItem>();
		for (uint i = 0; i < GetGameInventoryAction.MaxValuationStacks + 1; i++)
		{
			items.Add(new InventoryItem
			{
				AssetId = i + 1,
				ClassId = 1000 + i,
				InstanceId = 2000 + i,
				AppId = 730,
				Name = $"Item {i}",
				MarketHashName = $"Item {i}",
				Marketable = true
			});
		}

		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = items });

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<uint?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$1.00", "$1.10", "3", null));
		var storeMock = new Mock<ISteamStoreApiClient>(MockBehavior.Loose);
		storeMock
			.Setup(m => m.GetPriceAsync(730u, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PriceOverview { Currency = "USD", Final = 30.0m, Initial = 30.0m, DiscountPercent = 0, FinalFormatted = "$29.99" });

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object, storeMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.True((bool)valuation["truncated"]!);
		Assert.Equal(GetGameInventoryAction.MaxValuationStacks, valuation["priced_stacks"]);
		Assert.Equal(1, valuation["unpriced_stacks"]);
		Assert.Equal(GetGameInventoryAction.MaxValuationStacks, Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"]).Count);
	}

	private (GetGameInventoryAction Action, BotSession Session) CreateAction(
		ISteamTradeClient tradeClient,
		IItemPriceClient? priceClient = null,
		ISteamStoreApiClient? storeClient = null,
		IVaporCache? cache = null)
	{
		var action = new GetGameInventoryAction(
			_loggerMock.Object,
			_ => tradeClient,
			_ => priceClient ?? new Mock<IItemPriceClient>(MockBehavior.Loose).Object,
			_ => storeClient ?? new Mock<ISteamStoreApiClient>(MockBehavior.Loose).Object,
			cache);
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandler = CreateWebHandler();
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return (action, session);
	}

	private BotSession CreateSessionWithoutWebHandler(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null, null, null);
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
