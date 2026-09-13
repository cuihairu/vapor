using Vapor.Steam.Core.Models;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Edge coverage for the trade model surface: rarely-asserted record properties,
/// the TradeAsset.FromInventoryItem conversion, and the remaining TradeUrlParams
/// parse failures.
/// </summary>
public sealed class TradeModelsEdgeTests
{
	[Fact]
	public void InventoryItem_CarriesAllDescriptionProperties()
	{
		var item = new InventoryItem
		{
			Commodity = 1,
			MarketabilityDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
			IconUrl = "icon-hash",
			IconUrlLarge = "icon-hash-large"
		};

		Assert.Equal(1, item.Commodity);
		Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), item.MarketabilityDate);
		Assert.Equal("icon-hash", item.IconUrl);
		Assert.Equal("icon-hash-large", item.IconUrlLarge);
	}

	[Fact]
	public void ItemTag_CarriesAllProperties()
	{
		var tag = new ItemTag
		{
			InternalName = "hero",
			Name = "Hero",
			Category = "hero",
			CategoryName = "Hero category",
			Color = "ff0000"
		};

		Assert.Equal("hero", tag.InternalName);
		Assert.Equal("Hero", tag.Name);
		Assert.Equal("hero", tag.Category);
		Assert.Equal("Hero category", tag.CategoryName);
		Assert.Equal("ff0000", tag.Color);
	}

	[Fact]
	public void TradeAsset_FromInventoryItem_UsesDefaultContextAndResetsCurrency()
	{
		var item = new InventoryItem
		{
			AppId = 753,
			AssetId = 42,
			ClassId = 11,
			InstanceId = 22,
			Amount = 3
		};

		TradeAsset asset = TradeAsset.FromInventoryItem(item);

		Assert.Equal(753u, asset.AppId);
		Assert.Equal(2UL, asset.ContextId);
		Assert.Equal(42UL, asset.AssetId);
		Assert.Equal(11UL, asset.ClassId);
		Assert.Equal(22UL, asset.InstanceId);
		Assert.Equal(3, asset.Amount);
		Assert.False(asset.IsCurrency);
	}

	[Fact]
	public void InventoryResponse_TracksTotalCount()
	{
		var response = new InventoryResponse { TotalInventoryCount = 77 };

		Assert.Equal(77, response.TotalInventoryCount);
	}

	[Fact]
	public void TradeOffersResponse_CarriesNextCursorTime()
	{
		var at = new DateTimeOffset(2026, 9, 13, 1, 2, 3, TimeSpan.Zero);

		Assert.Equal(at, new TradeOffersResponse { NextCursorTime = at }.NextCursorTime);
	}

	[Fact]
	public void TradeUrlParams_TokenWithoutValue_ParsesAsEmptyToken()
	{
		var result = TradeUrlParams.TryParse("https://steamcommunity.com/tradeoffer/new/?partner=12345678&token");

		Assert.NotNull(result);
		Assert.Equal(76561197972611406UL, result.PartnerSteamId);
		Assert.Equal(string.Empty, result.Token);
	}

	[Fact]
	public void TradeUrlParams_NonNumericPartner_ReturnsNull()
	{
		Assert.Null(TradeUrlParams.TryParse("https://steamcommunity.com/tradeoffer/new/?partner=abc"));
	}

	[Fact]
	public void TradeUrlParams_MalformedUrl_ReturnsNull()
	{
		Assert.Null(TradeUrlParams.TryParse("http://[::steamcommunity.com"));
	}
}
