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

	// --- Cache invalidation & force refresh ---

	[Fact]
	public async Task GetGameInfo_ForceRefresh_BypassesCacheAndRepopulates()
	{
		var first = SampleGame();
		first = first with { Name = "Old Name" };
		var refreshed = SampleGame() with { Name = "New Name" };

		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.SetupSequence(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(first)
			.ReturnsAsync(refreshed);

		using var cache = new MemoryVaporCache();
		var action = new GetGameInfoAction(
			NullLogger<GetGameInfoAction>.Instance,
			_ => clientMock.Object,
			cache);

		var session = CreateSession();

		var cached = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_id"] = "730" }, CancellationToken.None);
		var forced = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_id"] = "730", ["force_refresh"] = true }, CancellationToken.None);

		Assert.True(cached.Success);
		Assert.True(forced.Success);
		clientMock.Verify(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()), Times.Exactly(2));

		// The forced fetch repopulated the entry, so a follow-up read stays cached.
		var again = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_id"] = "730" }, CancellationToken.None);
		Assert.True(again.Success);
		Assert.Equal("New Name", ((GameInfo)again.Output!["game"]!).Name);
		clientMock.Verify(c => c.GetGameInfoAsync(730U, "us", It.IsAny<CancellationToken>()), Times.Exactly(2));
	}

	[Fact]
	public async Task GetPrice_StaleWhileRevalidate_ServesInstantlyAfterFreshTtl()
	{
		var price = new PriceOverview
		{
			Currency = "USD",
			Initial = 1999,
			Final = 999,
			FinalFormatted = "$9.99"
		};
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetPriceAsync(730U, "us", It.IsAny<CancellationToken>()))
			.ReturnsAsync(price);

		var clock = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
		using var cache = new MemoryVaporCache(new MemoryVaporCacheOptions { DefaultTtl = null }, () => clock);
		var action = new GetPriceAction(
			NullLogger<GetPriceAction>.Instance,
			_ => clientMock.Object,
			cache);

		var session = CreateSession();
		var payload = new Dictionary<string, object?> { ["app_id"] = "730" };

		// First call fills the cache (3-minute fresh tier).
		Assert.True((await action.ExecuteAsync(session, payload, CancellationToken.None)).Success);
		clientMock.Verify(c => c.GetPriceAsync(730U, "us", It.IsAny<CancellationToken>()), Times.Once);

		// After the fresh TTL but within the stale window, the cached price is served
		// and the refresh runs in the background (which the mock serves as well).
		clock += TimeSpan.FromMinutes(5);
		var stale = await action.ExecuteAsync(session, payload, CancellationToken.None);
		Assert.True(stale.Success);
		Assert.Equal(1, cache.StaleHits);
		clientMock.Verify(c => c.GetPriceAsync(730U, "us", It.IsAny<CancellationToken>()), Times.AtMost(2));
	}

	[Fact]
	public async Task InvalidateCache_ByPrefix_RemovesOnlyMatchingEntries()
	{
		using var cache = new MemoryVaporCache();
		await cache.SetAsync("price:730:us", new CacheProbe());
		await cache.SetAsync("game:730", new CacheProbe());

		var action = new InvalidateCacheAction(NullLogger<InvalidateCacheAction>.Instance, cache);
		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["prefix"] = "price:730" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["removed"]);
		Assert.Equal(1, cache.Count); // game:730 survives
	}

	[Fact]
	public async Task InvalidateCache_ClearAll_RemovesEverything()
	{
		using var cache = new MemoryVaporCache();
		await cache.SetAsync("price:730:us", new CacheProbe());
		await cache.SetAsync("game:730", new CacheProbe());

		var action = new InvalidateCacheAction(NullLogger<InvalidateCacheAction>.Instance, cache);
		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["clear_all"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["removed"]);
		Assert.Equal(0, cache.Count);
		Assert.Equal(true, result.Output["cleared_all"]);
	}

	[Fact]
	public async Task InvalidateCache_WithoutPrefixOrClearAll_Fails()
	{
		var action = new InvalidateCacheAction(NullLogger<InvalidateCacheAction>.Instance, new MemoryVaporCache());
		var result = await action.ExecuteAsync(CreateSession(), new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("prefix", result.Error);
	}

	[Fact]
	public async Task InvalidateCache_WithoutConfiguredCache_Fails()
	{
		var action = new InvalidateCacheAction(NullLogger<InvalidateCacheAction>.Instance, cache: null);
		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["clear_all"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("No cache", result.Error);
	}

	private sealed class CacheProbe
	{
	}

	// --- exception paths (missing web session, store client failures) ---

	[Fact]
	public async Task GetGameInfo_WithoutWebHandler_ReturnsError()
	{
		var action = new GetGameInfoAction(NullLogger<GetGameInfoAction>.Instance, _ => throw new UnreachableFactory());

		var result = await action.ExecuteAsync(
			CreateSessionWithoutWebHandler(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task SearchGames_WithoutWebHandler_ReturnsError()
	{
		var action = new SearchGamesAction(NullLogger<SearchGamesAction>.Instance, _ => throw new UnreachableFactory());

		var result = await action.ExecuteAsync(
			CreateSessionWithoutWebHandler(),
			new Dictionary<string, object?> { ["term"] = "cs2" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task GetPrice_WithoutWebHandler_ReturnsError()
	{
		var action = new GetPriceAction(NullLogger<GetPriceAction>.Instance, _ => throw new UnreachableFactory());

		var result = await action.ExecuteAsync(
			CreateSessionWithoutWebHandler(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task GetMarketListings_WithoutWebHandler_ReturnsError()
	{
		var action = new GetMarketListingsAction(NullLogger<GetMarketListingsAction>.Instance, _ => throw new UnreachableFactory());

		var result = await action.ExecuteAsync(
			CreateSessionWithoutWebHandler(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task GetMarketListings_WithInvalidAppId_ReturnsError()
	{
		var action = new GetMarketListingsAction(NullLogger<GetMarketListingsAction>.Instance);

		var missing = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?>(),
			CancellationToken.None);
		var zero = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "0" },
			CancellationToken.None);

		Assert.All(new[] { missing, zero }, r =>
		{
			Assert.False(r.Success);
			Assert.Equal("Valid app_id is required", r.Error);
		});
	}

	[Fact]
	public async Task GetPrice_WithInvalidAppId_ReturnsError()
	{
		var action = new GetPriceAction(
			NullLogger<GetPriceAction>.Instance,
			_ => throw new InvalidOperationException("should not be called"));

		var missing = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?>(),
			CancellationToken.None);
		var garbage = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "abc" },
			CancellationToken.None);

		Assert.All(new[] { missing, garbage }, r =>
		{
			Assert.False(r.Success);
			Assert.Equal("Valid app_id is required", r.Error);
		});
	}

	[Fact]
	public async Task GetMarketListings_WhenClientReturnsNullPage_ReturnsError()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetMarketListingsAsync(730U, 0, 20, It.IsAny<CancellationToken>()))
			.ReturnsAsync((MarketListingsPage?)null);

		var action = new GetMarketListingsAction(
			NullLogger<GetMarketListingsAction>.Instance,
			_ => clientMock.Object);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_id"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Market listings request failed for app 730", result.Error);
	}

	[Fact]
	public void InvalidateCache_Metadata_DescribesAction()
	{
		var action = new InvalidateCacheAction(NullLogger<InvalidateCacheAction>.Instance, new MemoryVaporCache());

		Assert.Equal("cache_invalidate", action.Name);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(10, action.Metadata.TimeoutSeconds);
	}

	// The store client factory throwing exercises the generic catch (Exception)
	// arm — the InvalidOperationException-filtered arm does not match because a
	// web handler IS present.
	[Theory]
	[InlineData("game_info")]
	[InlineData("search")]
	[InlineData("price")]
	[InlineData("market_listings")]
	public async Task StoreClientFactoryThrowing_ReportsExceptionMessage(string actionKind)
	{
		ActionResult result = actionKind switch
		{
			"game_info" => await new GetGameInfoAction(
				NullLogger<GetGameInfoAction>.Instance,
				_ => throw new InvalidOperationException("store API down")).ExecuteAsync(
				CreateSession(), new Dictionary<string, object?> { ["app_id"] = "730" }, CancellationToken.None),
			"search" => await new SearchGamesAction(
				NullLogger<SearchGamesAction>.Instance,
				_ => throw new InvalidOperationException("store API down")).ExecuteAsync(
				CreateSession(), new Dictionary<string, object?> { ["term"] = "cs2" }, CancellationToken.None),
			"price" => await new GetPriceAction(
				NullLogger<GetPriceAction>.Instance,
				_ => throw new InvalidOperationException("store API down")).ExecuteAsync(
				CreateSession(), new Dictionary<string, object?> { ["app_id"] = "730" }, CancellationToken.None),
			_ => await new GetMarketListingsAction(
				NullLogger<GetMarketListingsAction>.Instance,
				_ => throw new InvalidOperationException("store API down")).ExecuteAsync(
				CreateSession(), new Dictionary<string, object?> { ["app_id"] = "730" }, CancellationToken.None)
		};

		Assert.False(result.Success);
		Assert.Equal("store API down", result.Error);
	}

	private sealed class UnreachableFactory : Exception
	{
	}

	private BotSession CreateSessionWithoutWebHandler()
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, null, null);
		_sessions.Add(session);
		return session;
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
