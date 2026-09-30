using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Plugins.GameAccess;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

public sealed class GetItemDetailsActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;

	private readonly Mock<ILogger<GetItemDetailsAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new GetItemDetailsAction(_loggerMock.Object);
		Assert.Equal("get_item_details", action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		var action = new GetItemDetailsAction(_loggerMock.Object);
		Assert.Equal("get_item_details", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_AssetIdWithoutResolvableSteamId_ReturnsError()
	{
		// Item ids need an inventory to match against: with no steam_id and a
		// handler whose cookies carry no SteamID, the call fails with the
		// explicit steam_id requirement (matches get_inventory's wording).
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["asset_ids"] = new List<ulong> { 1 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresWebHandler_ReturnsErrorIfMissing()
	{
		var action = new GetItemDetailsAction(_loggerMock.Object);
		var session = CreateSessionWithoutWebHandler("test_account");
		var payload = new Dictionary<string, object?> { ["steam_id"] = "76561198000000000" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_NoIds_ReturnsError()
	{
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("asset_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_AssetId_MatchesExactItem()
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
					new InventoryItem { AssetId = 2, ClassId = 101, InstanceId = 201, AppId = 730, Name = "M4A4", MarketHashName = "M4A4 | Howl", Tradable = false, Marketable = false }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = new List<ulong> { 1 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["matched_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Assert.Single(items);
		Assert.Equal("1", items[0]["asset_id"]);
		Assert.Equal("AK-47", items[0]["name"]);
	}

	[Fact]
	public async Task ExecuteAsync_ClassId_MatchesAllItemsOfKind()
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
					new InventoryItem { AssetId = 2, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline", Tradable = true, Marketable = true },
					new InventoryItem { AssetId = 3, ClassId = 101, InstanceId = 201, AppId = 730, Name = "M4A4", MarketHashName = "M4A4 | Howl", Tradable = false, Marketable = false }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["class_ids"] = new List<ulong> { 100 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["matched_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Assert.Equal(2, items.Count);
	}

	[Fact]
	public async Task ExecuteAsync_WithValue_AttachesPrices()
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
					new InventoryItem { AssetId = 2, ClassId = 101, InstanceId = 201, AppId = 730, Name = "M4A4", MarketHashName = "M4A4 | Howl", Tradable = false, Marketable = false }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "AK-47 | Redline", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "$10.50", "$11.00", "1,234", null));
		priceMock
			.Setup(m => m.GetItemPriceAsync(730u, "M4A4 | Howl", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(false, null, null, null, "not listed on the market"));

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = new List<ulong> { 1, 2 }, ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.Equal(1, valuation["priced_count"]);
		Assert.Equal(1, valuation["failed_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"]);
		Assert.Equal(2, items.Count);
		Assert.Equal("$10.50", items[0]["lowest_price"]);
		Assert.Equal("not listed on the market", items[1]["error"]);
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
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline" }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);
		session.SteamWebHandler!.SetSessionCookies("session-1", $"{OwnSteamId}%7C%7Ctoken");

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["asset_ids"] = new List<ulong> { 1 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(OwnSteamId.ToString(), result.Output!["steam_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_NotMarketableItem_ReportsErrorWithoutPriceLookup()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 730, Name = "Name Tag", MarketHashName = null, Marketable = false }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		var (action, session) = CreateAction(clientMock.Object, priceMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = new List<ulong> { 1 }, ["value"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output!["valuation"]);
		Assert.Equal(0, valuation["priced_count"]);
		Assert.Equal(0, valuation["failed_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(valuation["items"]);
		Assert.Single(items);
		Assert.Null(items[0]["market_hash_name"]);
		Assert.Equal("not marketable", items[0]["error"]);
		priceMock.Verify(
			m => m.GetItemPriceAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<uint?>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_SingleScalarAssetId_MatchesItem()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 7, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline" }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = 7UL },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["matched_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_ZeroScalarId_YieldsNoIds()
	{
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = 0UL },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("asset_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_UnparseableScalarId_YieldsNoIds()
	{
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = "junk" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("asset_id", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_MixedIdEntries_ParsesAllSupportedShapes()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 10, ClassId = 100, InstanceId = 200, AppId = 730, Name = "AK-47", MarketHashName = "AK-47 | Redline" }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		// ulong / long / int / uint / JSON number / JSON string / CLR string all
		// parse; negatives, zero, null and non-numeric junk are dropped silently.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["asset_ids"] = new List<object?>
				{
					10UL,
					4L,
					5,
					6u,
					JsonSerializer.SerializeToElement(7),
					JsonSerializer.SerializeToElement("8"),
					JsonSerializer.SerializeToElement("junk"),
					"9",
					"junk",
					-1L,
					"0",
					null,
					new object()
				}
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["matched_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Assert.Equal("10", items[0]["asset_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_AppContextCurrencyOverrides_FlowIntoCalls()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 570u, 570UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, ClassId = 100, InstanceId = 200, AppId = 570, Name = "Ember Spirit", MarketHashName = "Ember Spirit Item" }
				]
			});

		var priceMock = new Mock<IItemPriceClient>(MockBehavior.Loose);
		priceMock
			.Setup(m => m.GetItemPriceAsync(570u, "Ember Spirit Item", 1u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ItemPriceResult(true, "₽12,34", "₽13,00", "7", null));

		var (action, session) = CreateAction(clientMock.Object, priceMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_id"] = "570",
				["context_id"] = "570",
				["asset_ids"] = new List<ulong> { 1 },
				["value"] = true,
				["currency"] = "1"
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(570u, result.Output!["app_id"]);
		Assert.Equal("570", result.Output!["context_id"]);
		var valuation = Assert.IsType<Dictionary<string, object?>>(result.Output["valuation"]);
		Assert.Equal(1u, valuation["currency"]);
		Assert.Equal(1, valuation["priced_count"]);
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
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = new List<ulong> { 1 } },
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
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["asset_ids"] = new List<ulong> { 1 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("community down", result.Error);
	}

	private (GetItemDetailsAction Action, BotSession Session) CreateAction(
		ISteamTradeClient tradeClient,
		IItemPriceClient? priceClient = null)
	{
		var action = new GetItemDetailsAction(
			_loggerMock.Object,
			_ => tradeClient,
			_ => priceClient ?? new Mock<IItemPriceClient>(MockBehavior.Loose).Object);
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
