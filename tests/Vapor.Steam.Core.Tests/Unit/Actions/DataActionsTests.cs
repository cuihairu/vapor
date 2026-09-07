using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class DataActionsTests : IDisposable
{
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

	private static GameInfo SampleGame(uint appId = 730) => new()
	{
		AppId = appId,
		Name = "Counter-Strike 2",
		Type = "game",
		IsFree = true
	};

	// --- GetGameInfoAction ---

	[Fact]
	public async Task GetGameInfo_WithValidAppId_ReturnsGame()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SampleGame());

		var action = new GetGameInfoAction(
			NullLogger<GetGameInfoAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.True(result.Success);
		var game = Assert.IsType<GameInfo>(result.Output!["game"]);
		Assert.Equal("Counter-Strike 2", game.Name);
	}

	[Fact]
	public async Task GetGameInfo_WithInvalidAppId_ReturnsError()
	{
		var action = new GetGameInfoAction(
			NullLogger<GetGameInfoAction>.Instance,
			_ => throw new InvalidOperationException("should not be called"));

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "abc" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("app_id", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GetGameInfo_WithUnknownApp_ReturnsError()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(999U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync((GameInfo?)null);

		var action = new GetGameInfoAction(
			NullLogger<GetGameInfoAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "999" },
			CancellationToken.None);

		Assert.False(result.Success);
	}

	[Fact]
	public async Task GetGameInfo_WithCache_ServesSecondCallFromCache()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SampleGame());

		using var cache = new MemoryVaporCache(new MemoryVaporCacheOptions { DefaultTtl = TimeSpan.FromMinutes(5) });
		var action = new GetGameInfoAction(
			NullLogger<GetGameInfoAction>.Instance,
			_ => clientMock.Object,
			cache);

		var session = CreateSession();
		var payload = new Dictionary<string, object?> { ["app_id"] = "730" };

		var first = await action.ExecuteAsync(session, payload, CancellationToken.None);
		var second = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(first.Success);
		Assert.True(second.Success);
		clientMock.Verify(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()), Times.Once);
		Assert.Equal(1, cache.Count);
	}

	[Fact]
	public async Task GetGameInfo_WithCacheDisabledByPayload_BypassesCache()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SampleGame());

		using var cache = new MemoryVaporCache();
		var action = new GetGameInfoAction(
			NullLogger<GetGameInfoAction>.Instance,
			_ => clientMock.Object,
			cache);

		var session = CreateSession();
		var payload = new Dictionary<string, object?> { ["app_id"] = "730", ["cache_ttl_seconds"] = 0 };

		await action.ExecuteAsync(session, payload, CancellationToken.None);
		await action.ExecuteAsync(session, payload, CancellationToken.None);

		clientMock.Verify(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()), Times.Exactly(2));
		Assert.Equal(0, cache.Count);
	}

	// --- SearchGamesAction ---

	[Fact]
	public async Task SearchGames_WithTerm_ReturnsResults()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.SearchGamesAsync("portal", 20, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<GameSearchResult>
			{
				new() { AppId = 620, Name = "Portal 2" },
				new() { AppId = 400, Name = "Portal" }
			});

		var action = new SearchGamesAction(
			NullLogger<SearchGamesAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["term"] = "portal" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total_count"]);
	}

	[Fact]
	public async Task SearchGames_WithoutTerm_ReturnsError()
	{
		var action = new SearchGamesAction(
			NullLogger<SearchGamesAction>.Instance,
			_ => throw new InvalidOperationException("should not be called"));

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?>(),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("term", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
	}

	// --- GetPriceAction ---

	[Fact]
	public async Task GetPrice_WithAppId_ReturnsPrice()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetPriceAsync(620U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PriceOverview { Currency = "USD", Final = 9.99m, Initial = 19.99m, DiscountPercent = 50, FinalFormatted = "$9.99" });

		var action = new GetPriceAction(
			NullLogger<GetPriceAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "620" },
			CancellationToken.None);

		Assert.True(result.Success);
		var price = Assert.IsType<PriceOverview>(result.Output!["price"]);
		Assert.Equal(9.99m, price.Final);
	}

	[Fact]
	public async Task GetPrice_WhenNoPriceAvailable_ReturnsError()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetPriceAsync(730U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync((PriceOverview?)null);

		var action = new GetPriceAction(
			NullLogger<GetPriceAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
	}

	// --- GetMarketListingsAction ---

	[Fact]
	public async Task GetMarketListings_WithAppId_ReturnsPage()
	{
		var page = new MarketListingsPage
		{
			AppId = 730,
			Start = 0,
			PageSize = 2,
			TotalCount = 100,
			Listings = new List<MarketListing>
			{
				new() { ListingId = 1, AppId = 730, TotalPrice = 12.84m },
				new() { ListingId = 2, AppId = 730, TotalPrice = 26.25m }
			}
		};
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetMarketListingsAsync(730U, 0, 20, It.IsAny<CancellationToken>()))
			.ReturnsAsync(page);

		var action = new GetMarketListingsAction(
			NullLogger<GetMarketListingsAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(100, result.Output!["total_count"]);
		Assert.Equal(true, result.Output["has_more"]);
	}

	[Fact]
	public async Task GetMarketListings_WithCache_ServesSecondCallFromCache()
	{
		var page = new MarketListingsPage
		{
			AppId = 730,
			Start = 0,
			PageSize = 20,
			TotalCount = 1,
			Listings = [new MarketListing { ListingId = 42, AppId = 730 }]
		};
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetMarketListingsAsync(730U, 0, 20, It.IsAny<CancellationToken>()))
			.ReturnsAsync(page);

		using var cache = new MemoryVaporCache();
		var action = new GetMarketListingsAction(
			NullLogger<GetMarketListingsAction>.Instance,
			_ => clientMock.Object,
			cache);

		var session = CreateSession();
		var payload = new Dictionary<string, object?> { ["app_id"] = "730" };

		await action.ExecuteAsync(session, payload, CancellationToken.None);
		await action.ExecuteAsync(session, payload, CancellationToken.None);

		clientMock.Verify(c => c.GetMarketListingsAsync(730U, 0, 20, It.IsAny<CancellationToken>()), Times.Once);
	}

	// --- Metadata ---

	[Theory]
	[InlineData("get_game_info")]
	[InlineData("search_games")]
	[InlineData("get_price")]
	[InlineData("get_market_listings")]
	public void DataActions_HaveExpectedNames(string expected)
	{
		IAction[] actions =
		[
			new GetGameInfoAction(NullLogger<GetGameInfoAction>.Instance),
			new SearchGamesAction(NullLogger<SearchGamesAction>.Instance),
			new GetPriceAction(NullLogger<GetPriceAction>.Instance),
			new GetMarketListingsAction(NullLogger<GetMarketListingsAction>.Instance)
		];

		Assert.Contains(actions, a => a.Name == expected);
	}

	[Fact]
	public void DataActions_DoNotRequireLogin()
	{
		IAction[] actions =
		[
			new GetGameInfoAction(NullLogger<GetGameInfoAction>.Instance),
			new SearchGamesAction(NullLogger<SearchGamesAction>.Instance),
			new GetPriceAction(NullLogger<GetPriceAction>.Instance),
			new GetMarketListingsAction(NullLogger<GetMarketListingsAction>.Instance)
		];

		Assert.All(actions, a => Assert.False(a.Metadata.RequiresLogin));
	}
}
