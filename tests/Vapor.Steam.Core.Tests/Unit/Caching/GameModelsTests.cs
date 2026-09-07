using System.Text.Json;
using System.Text.Json.Serialization;
using Vapor.Steam.Core.Models;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Caching;

public sealed class GameModelsTests
{
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	[Fact]
	public void GameInfo_SerializesWithCamelCase_AndRoundTrips()
	{
		var game = new GameInfo
		{
			AppId = 730,
			Name = "Counter-Strike 2",
			Type = "game",
			Developer = "Valve",
			Publisher = "Valve",
			IsFree = true,
			MetacriticScore = 83,
			Genres = ["Action", "FPS"],
			Categories = ["Multi-Player", "Steam Cloud"],
			Price = new PriceOverview { Currency = "USD", Final = 0m, DiscountPercent = 0 },
			FetchedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)
		};

		string json = JsonSerializer.Serialize(game, Options);

		Assert.Contains("\"appId\":730", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\"marketHashName\"", json, StringComparison.Ordinal);

		var roundTripped = JsonSerializer.Deserialize<GameInfo>(json, Options);

		Assert.NotNull(roundTripped);
		Assert.Equal(game.AppId, roundTripped.AppId);
		Assert.Equal(game.Name, roundTripped.Name);
		Assert.Equal(game.IsFree, roundTripped.IsFree);
		Assert.Equal(game.MetacriticScore, roundTripped.MetacriticScore);
		Assert.Equal(game.Genres, roundTripped.Genres);
		Assert.Equal(game.Price!.Final, roundTripped.Price!.Final);
		Assert.Equal(game.FetchedAt, roundTripped.FetchedAt);
	}

	[Fact]
	public void ItemInfo_SerializesWithCamelCase_AndRoundTrips()
	{
		var item = new ItemInfo
		{
			AppId = 730,
			MarketHashName = "AK-47 | Redline (Field-Tested)",
			Name = "AK-47 | Redline",
			Type = "★ Mil-Spec Rifle",
			LowestPrice = 25.34m,
			MedianPrice = 25.10m,
			Currency = "USD",
			Volume24h = 1234
		};

		string json = JsonSerializer.Serialize(item, Options);

		Assert.Contains("\"marketHashName\":\"AK-47 | Redline (Field-Tested)\"", json, StringComparison.Ordinal);

		var roundTripped = JsonSerializer.Deserialize<ItemInfo>(json, Options);

		Assert.NotNull(roundTripped);
		Assert.Equal(item.MarketHashName, roundTripped.MarketHashName);
		Assert.Equal(item.LowestPrice, roundTripped.LowestPrice);
		Assert.Equal(item.Volume24h, roundTripped.Volume24h);
	}

	[Fact]
	public void CacheKeys_AreStable()
	{
		Assert.Equal("game:730", GameInfo.CacheKey(730));
		Assert.Equal("item:730:AK-47 | Redline", ItemInfo.CacheKey(730, "AK-47 | Redline"));
	}

	[Fact]
	public void GameSearchResult_IncludesCoreFields()
	{
		var result = new GameSearchResult
		{
			AppId = 570,
			Name = "Dota 2",
			Type = "game",
			IsFree = true,
			Price = new PriceOverview { Currency = "USD", Final = 0m }
		};

		string json = JsonSerializer.Serialize(result, Options);

		Assert.Contains("\"appId\":570", json, StringComparison.Ordinal);
		Assert.Contains("\"name\":\"Dota 2\"", json, StringComparison.Ordinal);
	}
}
