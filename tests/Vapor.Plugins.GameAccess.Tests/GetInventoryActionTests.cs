using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Plugins.GameAccess;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

public sealed class GetInventoryActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;

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
	public async Task ExecuteAsync_MissingSteamIdWithoutWebSession_ReturnsError()
	{
		// steam_id defaults to the session's own id; with neither a steam_id nor a
		// web session to resolve it from, the call fails with a clear error.
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

	[Fact]
	public async Task ExecuteAsync_MultiApp_UsesLootContextRulesAndMergesItems()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 753u, 6UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 1, AppId = 753, Tradable = true, Marketable = true }] });
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 2, AppId = 730, Marketable = true }] });

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["app_ids"] = new List<uint> { 753, 730 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total_count"]);
		var apps = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["apps"]);
		Assert.Equal(2, apps.Count);
		Assert.Equal("6", apps[0]["context_id"]);
		Assert.Equal("2", apps[1]["context_id"]);
		Assert.Equal(1, apps[0]["item_count"]);
		Assert.Equal(1, apps[1]["item_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Assert.Equal(2, items.Count);
	}

	[Fact]
	public async Task ExecuteAsync_MultiApp_TradableOnly_FiltersItems()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 753u, 6UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
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

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_ids"] = new List<uint> { 753 },
				["tradable_only"] = true
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_MultiApp_MarketableOnly_FiltersItems()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 753u, 6UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, AppId = 753, Marketable = true },
					new InventoryItem { AssetId = 2, AppId = 753, Marketable = false }
				]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_ids"] = new List<uint> { 753 },
				["marketable_only"] = true
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["total_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_MultiApp_OverLimit_Fails()
	{
		var (action, session) = CreateAction(new Mock<ISteamTradeClient>(MockBehavior.Loose).Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_ids"] = new List<uint> { 1, 2, 3, 4, 5, 6 }
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("limited to 5", result.Error ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MultiApp_SteamIdDefaultsFromSessionCookies()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 753u, 6UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });

		var (action, session) = CreateAction(clientMock.Object, withCookies: true);

		// No steam_id in the payload — the session cookies carry it.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 753 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(OwnSteamId.ToString(), result.Output!["steam_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_MultiApp_ClientFailure_ReturnsError()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 753u, 6UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = false, Error = "inventory is private" });

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["app_ids"] = new List<uint> { 753 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("inventory is private", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MultiApp_PayloadAfterJsonRoundTrip_IsUnderstood()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 753u, 6UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });

		var (action, session) = CreateAction(clientMock.Object);

		Dictionary<string, object?> payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
			$"{{\"steam_id\":\"{OwnSteamId}\",\"app_ids\":[753],\"tradable_only\":true}}")!;

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_SingleApp_ClassicOutputShapeKept()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetInventoryAsync(OwnSteamId, 730u, 2UL, It.IsAny<ulong?>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 9, AppId = 730, ClassId = 11, InstanceId = 22, MarketHashName = "AK-47", Marketable = true }]
			});

		var (action, session) = CreateAction(clientMock.Object);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["app_id"] = "730" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(730u, result.Output!["app_id"]);
		Assert.Equal(2UL, result.Output["context_id"]);
		Assert.Equal(1, result.Output["total_count"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Dictionary<string, object?> item = Assert.Single(items);
		Assert.Equal("9", item["asset_id"]);
		Assert.Equal("AK-47", item["market_hash_name"]);
		Assert.Equal(true, item["marketable"]);
	}

	private (GetInventoryAction Action, BotSession Session) CreateAction(ISteamTradeClient tradeClient, bool withCookies = false)
	{
		var action = new GetInventoryAction(_loggerMock.Object, _ => tradeClient);
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandler = withCookies ? CreateWebHandlerWithCookies() : CreateWebHandler();
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

	private static SteamWebHandler CreateWebHandlerWithCookies()
	{
		var webHandler = CreateWebHandler();
		webHandler.SetSessionCookies("session-1", $"{OwnSteamId}%7C%7Ctoken");
		return webHandler;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
