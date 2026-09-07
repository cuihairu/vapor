using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class SteamStoreApiClientTests
{
	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(Responder(request));
		}
	}

	private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
	{
		Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
	};

	private static (SteamStoreApiClient Client, FakeHttpMessageHandler Fake) Create()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance), fake);
	}

	[Fact]
	public async Task GetGameInfoAsync_ParsesAppDetails()
	{
		string json = """
		{
			"730": {
				"success": true,
				"data": {
					"type": "game",
					"name": "Counter-Strike 2",
					"steam_appid": 730,
					"required_age": 0,
					"is_free": true,
					"developers": ["Valve"],
					"publishers": ["Valve"],
					"release_date": { "coming_soon": false, "date": "21 Aug, 2012" },
					"price_overview": null,
					"genres": [ { "id": "1", "description": "Action" } ],
					"categories": [ { "id": "1", "description": "Multi-player" } ],
					"recommendations": { "total": 8000000 },
					"header_image": "https://example.test/header.jpg",
					"short_description": "A free-to-play FPS."
				}
			}
		}
		""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(HttpStatusCode.OK, json);

		var game = await client.GetGameInfoAsync(730);

		Assert.NotNull(game);
		Assert.Equal(730U, game!.AppId);
		Assert.Equal("Counter-Strike 2", game.Name);
		Assert.Equal("game", game.Type);
		Assert.Equal("Valve", game.Developer);
		Assert.True(game.IsFree);
		Assert.True(game.RequiresPurchase == false);
		Assert.Equal(8_000_000L, game.RecommendationsTotal);
		Assert.Equal(["Action"], game.Genres);
		Assert.Equal(["Multi-player"], game.Categories);
		Assert.Null(game.Price);
		Assert.NotNull(game.ReleaseDate);
	}

	[Fact]
	public async Task GetGameInfoAsync_WithPriceOverview_ParsesPrice()
	{
		string json = """
		{
			"620": {
				"success": true,
				"data": {
					"type": "game",
					"name": "Portal 2",
					"steam_appid": 620,
					"is_free": false,
					"price_overview": {
						"currency": "USD",
						"initial": 1999,
						"final": 999,
						"discount_percent": 50,
						"initial_formatted": "$19.99",
						"final_formatted": "$9.99"
					}
				}
			}
		}
		""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(HttpStatusCode.OK, json);

		var game = await client.GetGameInfoAsync(620);

		Assert.NotNull(game);
		Assert.NotNull(game!.Price);
		Assert.Equal("USD", game.Price.Currency);
		Assert.Equal(19.99m, game.Price.Initial);
		Assert.Equal(9.99m, game.Price.Final);
		Assert.Equal(50, game.Price.DiscountPercent);
		Assert.Equal("$9.99", game.Price.FinalFormatted);
	}

	[Fact]
	public async Task GetGameInfoAsync_WhenSuccessFalse_ReturnsNull()
	{
		string json = """{ "999999999": { "success": false } }""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(HttpStatusCode.OK, json);

		var game = await client.GetGameInfoAsync(999999999);

		Assert.Null(game);
	}

	[Fact]
	public async Task GetGameInfoAsync_WhenHttpFails_ReturnsNull()
	{
		var (client, fake) = Create();
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

		var game = await client.GetGameInfoAsync(730);

		Assert.Null(game);
	}

	[Fact]
	public async Task SearchGamesAsync_ParsesItemsAndAppliesLimit()
	{
		string json = """
		{
			"total": 3,
			"items": [
				{ "type": "app", "name": "Counter-Strike 2", "id": 730, "tiny_image": "https://example.test/730.jpg" },
				{ "type": "app", "name": "Counter-Strike: Source", "id": 240, "price": { "currency": "USD", "final": 999, "initial": 999, "discount_percent": 0, "final_formatted": "$9.99" } },
				{ "type": "app", "name": "Counter-Strike: Condition Zero", "id": 80, "price": { "currency": "USD", "final": 499, "initial": 499, "discount_percent": 0 } }
			]
		}
		""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(HttpStatusCode.OK, json);

		var results = await client.SearchGamesAsync("counter", limit: 2);

		Assert.Equal(2, results.Count);
		Assert.Equal("Counter-Strike 2", results[0].Name);
		Assert.True(results[0].IsFree); // no price block
		Assert.False(results[1].IsFree);
		Assert.Equal(9.99m, results[1].Price!.Final);
	}

	[Fact]
	public async Task SearchGamesAsync_WithEmptyTerm_Throws()
	{
		var (client, _) = Create();

		await Assert.ThrowsAsync<ArgumentException>(() => client.SearchGamesAsync(""));
	}

	[Fact]
	public async Task GetMarketListingsAsync_ParsesListingInfo()
	{
		string json = """
		{
			"success": true,
			"start": 0,
			"pagesize": 2,
			"total_rowcount": 12345,
			"listinginfo": {
				"1001": {
					"listingid": "1001",
					"asset": { "currency": 0, "appid": 730, "contextid": "2", "id": "9001", "classid": "111", "instanceid": "222", "amount": "1" },
					"converted_price": 1234,
					"converted_fee": 50,
					"converted_publisher_fee": 0,
					"converted_currencyid": 2001
				},
				"1002": {
					"listingid": "1002",
					"asset": { "appid": 730, "contextid": "2", "id": "9002", "classid": "333", "instanceid": "444", "amount": "1" },
					"converted_price": 2500,
					"converted_fee": 100,
					"converted_publisher_fee": 25,
					"converted_currencyid": 2001
				}
			}
		}
		""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(HttpStatusCode.OK, json);

		var page = await client.GetMarketListingsAsync(730, start: 0, count: 2);

		Assert.NotNull(page);
		Assert.Equal(12345, page!.TotalCount);
		Assert.Equal(2, page.Listings.Count);

		Assert.Equal(1001UL, page.Listings[0].ListingId);
		Assert.Equal(9001UL, page.Listings[0].AssetId);
		Assert.Equal(111UL, page.Listings[0].ClassId);
		Assert.Equal(222UL, page.Listings[0].InstanceId);
		Assert.Equal(12.84m, page.Listings[0].TotalPrice); // (1234+50)/100
		Assert.Equal(2001, page.Listings[0].CurrencyId);

		Assert.Equal(26.25m, page.Listings[1].TotalPrice); // (2500+100+25)/100
		Assert.True(page.HasMore);
	}

	[Fact]
	public async Task GetMarketListingsAsync_WhenHttpFails_ReturnsNull()
	{
		var (client, fake) = Create();
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

		var page = await client.GetMarketListingsAsync(730);

		Assert.Null(page);
	}

	[Fact]
	public async Task GetPriceAsync_DelegatesToGameInfo()
	{
		string json = """
		{
			"620": {
				"success": true,
				"data": {
					"name": "Portal 2", "steam_appid": 620, "is_free": false,
					"price_overview": { "currency": "USD", "initial": 1999, "final": 999, "discount_percent": 50, "final_formatted": "$9.99" }
				}
			}
		}
		""";

		var (client, fake) = Create();
		fake.Responder = _ => Json(HttpStatusCode.OK, json);

		var price = await client.GetPriceAsync(620);

		Assert.NotNull(price);
		Assert.Equal(9.99m, price!.Final);
	}
}
