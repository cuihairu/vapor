using System.Text.Json;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Contract tests: replay the mylistings fixture (TestData/market_mylistings_p1.json)
/// through the parser. The live page is login-gated (anonymous requests don't see
/// own listings), so the fixture is a *constructed* skeleton — not a verbatim
/// recording. Its structure is cross-confirmed (2026-09-14) against three
/// independent consumers of the live endpoint:
///   - cs2.sh, "How to Scrape Official Steam Community Market Listings"
///     (mylistings[] entries: listingid / price / fee / game_appid / contextid /
///     assetid + asset_description; top-level listing_on_hold,
///     listing_to_be_confirmed; hovers blob)
///   - SnaBe/node-steam-market-fetcher, index.d.ts (GetMyListingsResponse:
///     num_active_listings, assets[appid][contextid][assetid] table,
///     PersonalMarketListing price/fee/currencyid/time_created)
///   - zevnda/steam-game-idler, inventory/market.rs (num_active_listings as
///     paging total, assets-table join, hovers string scan)
/// Two variants are deliberately both present in the fixture: entries with an
/// inline asset_description and one joined via the assets table. A failing test
/// here means the upstream structure drifted or the parser broke — re-record a
/// real response (authenticated capture) and update the fixture.
/// </summary>
public sealed class SteamMarketMyListingsContractTests
{
	private static MyMarketListingsPage Replay()
	{
		string json = File.ReadAllText(
			Path.Combine(AppContext.BaseDirectory, "TestData", "market_mylistings_p1.json"));
		using var doc = JsonDocument.Parse(json);
		return SteamMarketClient.ParseMyListings(doc.RootElement, start: 0, pageSize: 100);
	}

	[Fact]
	public void Contract_ParsesListingsWithPricingAndAssetSummary()
	{
		var page = Replay();

		Assert.Equal(3, page.Listings.Count);

		// CS2 skin: full entry with inline asset_description.
		var ak = page.Listings[0];
		Assert.Equal("3547123456789012345", ak.ListingId);
		Assert.Equal(730U, ak.AppId);
		Assert.Equal("2", ak.ContextId);
		Assert.Equal("51234567890", ak.AssetId);
		Assert.Equal("4593095276", ak.ClassId);
		Assert.Equal("AK-47 | Redline (Field-Tested)", ak.MarketHashName);
		Assert.Equal("AK-47 | Redline (Field-Tested)", ak.MarketName);
		Assert.Equal("Counter-Strike 2", ak.GameName);
		Assert.Equal(103, ak.PriceCents);
		Assert.Equal(12, ak.FeeCents);
		Assert.Equal(91, ak.SellerProceedsCents);
		Assert.Equal("1", ak.CurrencyId);
		Assert.Equal("economy/image/fWfeUw/360fx360f", ak.IconUrl);
		Assert.NotNull(ak.TimeCreated);
		Assert.False(ak.CancelRequested);

		// Steam commodity: minimal entry, proceeds still derived from fee.
		var coupon = page.Listings[1];
		Assert.Equal("3547123456789012346", coupon.ListingId);
		Assert.Equal(753U, coupon.AppId);
		Assert.Equal("Summer 2026 Coupon", coupon.MarketHashName);
		Assert.Equal(30, coupon.PriceCents);
		Assert.Equal(4, coupon.FeeCents);
		Assert.Equal(26, coupon.SellerProceedsCents);
	}

	[Fact]
	public void Contract_JoinsAssetsTableWhenDescriptionMissing()
	{
		var page = Replay();

		// TF2 key: no inline asset_description — names come from the assets table.
		var key = page.Listings[2];
		Assert.Equal("3547123456789012347", key.ListingId);
		Assert.Equal(440U, key.AppId);
		Assert.Equal("6677889900", key.AssetId);
		Assert.Equal("Mann Co. Supply Crate Key", key.MarketHashName);
		Assert.Equal("Mann Co. Supply Crate Key", key.MarketName);
		Assert.Equal(250, key.PriceCents);
		Assert.Equal(38, key.FeeCents);
		Assert.Equal(212, key.SellerProceedsCents);
		Assert.True(key.CancelRequested);
	}

	[Fact]
	public void Contract_ExposesPagingTotalsAndHoldCounts()
	{
		var page = Replay();

		Assert.Equal(0, page.Start);
		Assert.Equal(100, page.PageSize);
		Assert.Equal(3, page.TotalCount);
		Assert.Equal(2, page.ActiveCount);
		Assert.Equal(1, page.OnHoldCount);
		Assert.Equal(2, page.ToBeConfirmedCount);
	}
}
