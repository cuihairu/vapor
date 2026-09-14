using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class SteamMarketClientTests
{
	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		public List<Uri> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.RequestUri!);
			return Task.FromResult(Responder(request));
		}
	}

	private static (SteamMarketClient Client, FakeHttpMessageHandler Fake) Create()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance), fake);
	}

	private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
	{
		Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
	};

	private static MyMarketListingsPage Parse(string json, int start = 0, int pageSize = 100)
	{
		using var doc = JsonDocument.Parse(json);
		return SteamMarketClient.ParseMyListings(doc.RootElement, start, pageSize);
	}

	// --- ParseMyListings variants ---

	[Fact]
	public void Parse_FallbackArrayName_Listings()
	{
		// node-steam-market-fetcher documents the array as `listings`; the
		// scraper guides as `mylistings`. Either must parse.
		const string json = """
			{
				"success": true,
				"total_count": 1,
				"listings": [
					{ "listingid": "111", "price": 100, "fee": 13, "asset_description": { "market_hash_name": "Key" } }
				]
			}
			""";

		var page = Parse(json);

		var listing = Assert.Single(page.Listings);
		Assert.Equal("111", listing.ListingId);
		Assert.Equal(100, listing.PriceCents);
		Assert.Equal(13, listing.FeeCents);
		Assert.Equal(87, listing.SellerProceedsCents);
	}

	[Fact]
	public void Parse_MissingFee_ProceedsStayNull()
	{
		const string json = """
			{ "mylistings": [ { "listingid": "42", "price": 80 } ] }
			""";

		var page = Parse(json);

		var listing = Assert.Single(page.Listings);
		Assert.Null(listing.FeeCents);
		Assert.Null(listing.SellerProceedsCents);
	}

	[Fact]
	public void Parse_EntryWithoutListingIdOrPrice_Skipped()
	{
		const string json = """
			{
				"mylistings": [
					{ "price": 50, "fee": 6 },
					{ "listingid": "43" },
					{ "listingid": "44", "price": 60, "fee": 7 }
				],
				"total_count": 3
			}
			""";

		var page = Parse(json);

		var listing = Assert.Single(page.Listings);
		Assert.Equal("44", listing.ListingId);
		// total_count reflects the raw page even though one entry was usable.
		Assert.Equal(3, page.TotalCount);
	}

	[Fact]
	public void Parse_EmptyPage_YieldsZeroesWithoutMylistingsKey()
	{
		const string json = """{ "success": 1, "num_active_listings": 0 }""";

		var page = Parse(json, start: 40, pageSize: 20);

		Assert.Empty(page.Listings);
		// No total_count: fall back to start + parsed entries.
		Assert.Equal(40, page.TotalCount);
		Assert.Equal(0, page.ActiveCount);
		Assert.Null(page.OnHoldCount);
		Assert.Null(page.ToBeConfirmedCount);
	}

	[Fact]
	public void Parse_HoldCounts_AcceptArraysOrNumbers()
	{
		const string jsonArray = """
			{ "mylistings": [], "listing_on_hold": [ {}, {} ], "listing_to_be_confirmed": [ {} ] }
			""";
		const string jsonNumbers = """
			{ "mylistings": [], "listing_on_hold": 2, "listing_to_be_confirmed": 1 }
			""";

		var fromArrays = Parse(jsonArray);
		var fromNumbers = Parse(jsonNumbers);

		Assert.Equal(2, fromArrays.OnHoldCount);
		Assert.Equal(1, fromArrays.ToBeConfirmedCount);
		Assert.Equal(2, fromNumbers.OnHoldCount);
		Assert.Equal(1, fromNumbers.ToBeConfirmedCount);
	}

	[Fact]
	public void Parse_CancelRequestedAndTimeCreated_Normalize()
	{
		const string json = """
			{
				"mylistings": [
					{ "listingid": "1", "price": 10, "cancel_requested": true, "time_created": 1760000000 },
					{ "listingid": "2", "price": 10, "cancel_requested": 0, "time_created": 0 }
				]
			}
			""";

		var page = Parse(json);

		Assert.True(page.Listings[0].CancelRequested);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), page.Listings[0].TimeCreated);
		Assert.False(page.Listings[1].CancelRequested);
		Assert.Null(page.Listings[1].TimeCreated);
	}

	// --- Client behavior ---

	private static string FixtureJson =>
		File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "market_mylistings_p1.json"));

	[Fact]
	public async Task GetMyListings_BuildsNorenderUrlWithPaging()
	{
		var (client, fake) = Create();
		fake.Responder = _ => Json(FixtureJson);

		await client.GetMyListingsAsync(start: 20, count: 10);

		var uri = Assert.Single(fake.Requests);
		Assert.Equal("https://steamcommunity.com/market/mylistings/", uri.GetLeftPart(UriPartial.Path));
		Assert.Contains("norender=1", uri.Query, StringComparison.Ordinal);
		Assert.Contains("start=20", uri.Query, StringComparison.Ordinal);
		Assert.Contains("count=10", uri.Query, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetMyListings_ClampsStartFloorAndCountCeiling()
	{
		var (client, fake) = Create();
		fake.Responder = _ => Json(FixtureJson);

		// Negative start clamps to 0; over-large count clamps to MaxCount.
		await client.GetMyListingsAsync(start: -5, count: 10000);

		var uri = Assert.Single(fake.Requests);
		Assert.Contains("start=0", uri.Query, StringComparison.Ordinal);
		Assert.Contains("count=500", uri.Query, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetMyListings_ReplaysFixtureThroughClient()
	{
		var (client, fake) = Create();
		fake.Responder = _ => Json(FixtureJson);

		var page = await client.GetMyListingsAsync();

		Assert.NotNull(page);
		Assert.Equal(3, page!.Listings.Count);
		Assert.Equal("3547123456789012345", page.Listings[0].ListingId);
		Assert.Equal(91, page.Listings[0].SellerProceedsCents);
	}

	[Fact]
	public async Task GetMyListings_LoginGateFailure_ReturnsNull()
	{
		// The endpoint is login-gated: an unauthenticated session gets a
		// redirect/error page rather than JSON.
		var (client, fake) = Create();
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.Found)
		{
			Content = new StringContent("<html>Sign in</html>", System.Text.Encoding.UTF8, "text/html")
		};

		var page = await client.GetMyListingsAsync();

		Assert.Null(page);
	}

	[Fact]
	public async Task GetMyListings_MalformedJson_ReturnsNull()
	{
		var (client, fake) = Create();
		fake.Responder = _ => Json("{ not json");

		var page = await client.GetMyListingsAsync();

		Assert.Null(page);
	}
}
