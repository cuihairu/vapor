using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Contract tests: replay recorded Steam Web API responses (TestData/*.json —
/// real upstream payloads, trimmed to size) through the client's parsing paths.
/// The fixtures pin the actual response contract; a failing test here means the
/// upstream structure drifted or the parser broke, not that hand-written JSON changed.
/// </summary>
public sealed class SteamStoreApiContractTests
{
	private const string AppDetailsFixture = "appdetails_620.json";
	private const string StoreSearchFixture = "storesearch_portal.json";
	private const string MarketSearchRenderFixture = "market_search_render_730.json";

	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(Responder(request));
		}
	}

	private static (SteamStoreApiClient Client, FakeHttpMessageHandler Fake) Create()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), fake);
	}

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
	};

	private static void ReplayFixture(FakeHttpMessageHandler fake, string fileName)
	{
		string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
		fake.Responder = _ => Json(json);
	}

	[Fact]
	public async Task AppDetailsContract_ParsesRecordedPortal2Response()
	{
		var (client, fake) = Create();
		ReplayFixture(fake, AppDetailsFixture);

		var game = await client.GetGameInfoAsync(620);

		Assert.NotNull(game);
		Assert.Equal(620U, game!.AppId);
		Assert.Equal("Portal 2", game.Name);
		Assert.Equal("game", game.Type);
		Assert.Equal("Valve", game.Developer);
		Assert.Equal("Valve", game.Publisher);
		Assert.False(game.IsFree);
		Assert.True(game.RequiresPurchase);
		Assert.Equal(new DateTimeOffset(2011, 4, 18, 0, 0, 0, TimeSpan.Zero), game.ReleaseDate);
		Assert.Equal(95, game.MetacriticScore);
		Assert.Equal(390_244L, game.RecommendationsTotal);
		Assert.Equal(new[] { "Action", "Adventure" }, game.Genres);
		Assert.Equal(new[] { "Single-player", "Multi-player", "Co-op" }, game.Categories);
		Assert.NotNull(game.HeaderImage);
		Assert.False(string.IsNullOrEmpty(game.ShortDescription));

		// Real price_overview: $9.99, no discount, initial_formatted is an empty string.
		Assert.NotNull(game.Price);
		Assert.Equal("USD", game.Price!.Currency);
		Assert.Equal(9.99m, game.Price.Initial);
		Assert.Equal(9.99m, game.Price.Final);
		Assert.Equal(0, game.Price.DiscountPercent);
		Assert.Equal("$9.99", game.Price.FinalFormatted);

		// get_price shares the appdetails parsing path.
		var price = await client.GetPriceAsync(620);
		Assert.NotNull(price);
		Assert.Equal(9.99m, price.Final);
	}

	[Fact]
	public async Task StoreSearchContract_ParsesRecordedPortalResponse()
	{
		var (client, fake) = Create();
		ReplayFixture(fake, StoreSearchFixture);

		var results = await client.SearchGamesAsync("portal", limit: 50);

		Assert.Equal(3, results.Count);

		Assert.Equal(620U, results[0].AppId);
		Assert.Equal("Portal 2", results[0].Name);
		Assert.False(results[0].IsFree);
		Assert.Equal(9.99m, results[0].Price!.Final);
		Assert.Equal("app", results[0].Type);

		// Price block carries an independent discount (initial 19.99 → final 2.99).
		Assert.Equal(374040U, results[2].AppId);
		Assert.Equal("Portal Knights", results[2].Name);
		Assert.Equal(19.99m, results[2].Price!.Initial);
		Assert.Equal(2.99m, results[2].Price!.Final);

		// Recorded items also carry metascore/platforms/controller_support — unknown
		// fields must simply be ignored, not break parsing.
		Assert.NotNull(results[0].HeaderImage);
	}

	[Fact]
	public async Task MarketSearchRenderContract_ParsesRecordedCs2Response()
	{
		var (client, fake) = Create();
		ReplayFixture(fake, MarketSearchRenderFixture);

		var page = await client.GetMarketListingsAsync(730);

		// Regression guard for the 2026 contract drift: the search render payload
		// switched from listinginfo/total_rowcount to results/total_count. Before
		// the dual-path parser this response parsed to null silently.
		Assert.NotNull(page);
		Assert.Equal(730U, page!.AppId);
		Assert.Equal(284, page.TotalCount);
		Assert.Equal(3, page.Listings.Count);
		Assert.Equal(0, page.Start);
		Assert.Equal(20, page.PageSize);
		Assert.True(page.HasMore);

		var first = page.Listings[0];
		Assert.Equal("Dreams & Nightmares Case", first.Name);
		Assert.Equal("Dreams & Nightmares Case", first.HashName);
		Assert.Equal(730U, first.AppId);
		Assert.Equal(4_717_330_486UL, first.ClassId);
		Assert.Equal(1.35m, first.TotalPrice); // sell_price 135 cents
		Assert.Equal(482_308, first.SellListings);
		Assert.Equal(0UL, first.ListingId); // aggregates carry no individual listing id
		Assert.Null(first.CurrencyId);

		Assert.Equal("Revolution Case", page.Listings[2].Name);
		Assert.Equal(0.27m, page.Listings[2].TotalPrice);
	}

	[Fact]
	public async Task LegacyListingInfoContract_IsStillParsed()
	{
		// The pre-2026 search render shape (listinginfo + total_rowcount) kept here
		// as a synthetic fixture so the fallback parsing path stays covered even
		// after the live endpoint moved on.
		string legacyJson = """
		{
			"success": true,
			"start": 0,
			"pagesize": 1,
			"total_rowcount": 12345,
			"listinginfo": {
				"1001": {
					"listingid": "1001",
					"asset": { "appid": 730, "contextid": "2", "id": "9001", "classid": "111", "instanceid": "222", "amount": "1" },
					"converted_price": 1234,
					"converted_fee": 50,
					"converted_publisher_fee": 0,
					"converted_currencyid": 2001
				}
			}
		}
		""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(legacyJson);

		var page = await client.GetMarketListingsAsync(730);

		Assert.NotNull(page);
		Assert.Equal(12345, page!.TotalCount);
		MarketListing listing = Assert.Single(page.Listings);
		Assert.Equal(1001UL, listing.ListingId);
		Assert.Equal(12.84m, listing.TotalPrice);
		Assert.Equal(2001, listing.CurrencyId);
	}
}
