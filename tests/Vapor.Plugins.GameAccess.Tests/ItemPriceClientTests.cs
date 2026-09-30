using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.GameAccess;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

public sealed class ItemPriceClientTests
{
	private static SteamWebResponse Respond(HttpStatusCode statusCode, string? body) =>
		new(statusCode, (int)statusCode, new Dictionary<string, string>(), body);

	[Fact]
	public void Constructor_NullDelegate_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => new ItemPriceClient((Func<Uri, CancellationToken, Task<SteamWebResponse>>)null!));
	}

	[Fact]
	public void Constructor_NullWebHandler_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => new ItemPriceClient((SteamWebHandler)null!));
	}

	[Fact]
	public void Constructor_WithWebHandler_AcceptsHandler()
	{
		var client = new ItemPriceClient(CreateWebHandler());
		Assert.NotNull(client);
	}

	[Fact]
	public async Task GetItemPriceAsync_Success_ReturnsAllFieldsAndBuildsPriceoverviewUrl()
	{
		Uri? captured = null;
		var client = new ItemPriceClient((url, _) =>
		{
			captured = url;
			return Task.FromResult(Respond(HttpStatusCode.OK, """{"success":true,"lowest_price":"$0.05","median_price":"$0.06","volume":"1,234"}"""));
		});

		ItemPriceResult result = await client.GetItemPriceAsync(730u, "AK-47 | Redline", null, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal("$0.05", result.LowestPrice);
		Assert.Equal("$0.06", result.MedianPrice);
		Assert.Equal("1,234", result.Volume);
		Assert.NotNull(captured);
		Assert.Equal(
			"https://steamcommunity.com/market/priceoverview/?appid=730&market_hash_name=AK-47%20%7C%20Redline",
			captured!.AbsoluteUri);
	}

	[Fact]
	public async Task GetItemPriceAsync_Success_WithCurrencyAndMissingOrNonStringFields()
	{
		Uri? captured = null;
		var client = new ItemPriceClient((url, _) =>
		{
			captured = url;
			return Task.FromResult(Respond(HttpStatusCode.OK, """{"success":true,"lowest_price":5}"""));
		});

		ItemPriceResult result = await client.GetItemPriceAsync(570u, "EMBER Spirit", 1u, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.LowestPrice);
		Assert.Null(result.MedianPrice);
		Assert.Null(result.Volume);
		Assert.NotNull(captured);
		Assert.Equal(
			"https://steamcommunity.com/market/priceoverview/?appid=570&currency=1&market_hash_name=EMBER%20Spirit",
			captured!.AbsoluteUri);
	}

	[Fact]
	public async Task GetItemPriceAsync_EmptyMarketHashName_Throws()
	{
		var client = new ItemPriceClient((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, "{}")));

		await Assert.ThrowsAsync<ArgumentException>(
			() => client.GetItemPriceAsync(730u, "", null, CancellationToken.None));
	}

	[Fact]
	public async Task GetItemPriceAsync_OperationCanceled_Propagates()
	{
		var client = new ItemPriceClient((_, _) => throw new OperationCanceledException());

		await Assert.ThrowsAsync<OperationCanceledException>(
			() => client.GetItemPriceAsync(730u, "AK-47 | Redline", null, CancellationToken.None));
	}

	[Fact]
	public async Task GetItemPriceAsync_RequestThrows_ReturnsError()
	{
		var client = new ItemPriceClient((_, _) => throw new HttpRequestException("socket gone"));

		ItemPriceResult result = await client.GetItemPriceAsync(730u, "AK-47 | Redline", null, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Null(result.LowestPrice);
		Assert.Equal("request failed: socket gone", result.Error);
	}

	[Fact]
	public async Task GetItemPriceAsync_NonSuccessStatus_ReturnsHttpError()
	{
		var client = new ItemPriceClient((_, _) => Task.FromResult(Respond(HttpStatusCode.InternalServerError, "oops")));

		ItemPriceResult result = await client.GetItemPriceAsync(730u, "AK-47 | Redline", null, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("HTTP 500", result.Error);
	}

	[Fact]
	public async Task GetItemPriceAsync_EmptyBody_ReturnsError()
	{
		var client = new ItemPriceClient((_, _) => Task.FromResult(Respond(HttpStatusCode.OK, null)));

		ItemPriceResult result = await client.GetItemPriceAsync(730u, "AK-47 | Redline", null, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("empty response body", result.Error);
	}

	[Fact]
	public void Parse_MalformedJson_ReturnsError()
	{
		ItemPriceResult result = ItemPriceClient.Parse("{not json");

		Assert.False(result.Success);
		Assert.Equal("malformed JSON response", result.Error);
	}

	[Fact]
	public void Parse_NonObjectRoot_ReturnsError()
	{
		ItemPriceResult result = ItemPriceClient.Parse("[]");

		Assert.False(result.Success);
		Assert.Equal("unexpected JSON shape", result.Error);
	}

	[Fact]
	public void Parse_SuccessFalse_ReturnsNotListed()
	{
		ItemPriceResult result = ItemPriceClient.Parse("""{"success":false}""");

		Assert.False(result.Success);
		Assert.Equal("not listed on the market", result.Error);
	}

	[Fact]
	public void Parse_MissingSuccess_ReturnsNotListed()
	{
		ItemPriceResult result = ItemPriceClient.Parse("{}");

		Assert.False(result.Success);
		Assert.Equal("not listed on the market", result.Error);
	}

	[Fact]
	public void BuildUrl_WithCurrency_EscapesMarketHashName()
	{
		Uri url = ItemPriceClient.BuildUrl(730u, "StatTrak™ AK-47 | Redline", 5u);

		Assert.Equal(
			"https://steamcommunity.com/market/priceoverview/?appid=730&currency=5&market_hash_name=StatTrak%E2%84%A2%20AK-47%20%7C%20Redline",
			url.AbsoluteUri);
	}

	[Fact]
	public void BuildUrl_WithoutCurrency_OmitsCurrencyParameter()
	{
		Uri url = ItemPriceClient.BuildUrl(570u, "EMBER Spirit", null);

		Assert.Equal(
			"https://steamcommunity.com/market/priceoverview/?appid=570&market_hash_name=EMBER%20Spirit",
			url.AbsoluteUri);
	}

	[Fact]
	public void CacheKey_WithCurrency_IncludesCurrency()
	{
		Assert.Equal("itemprice:730:5:AK-47 | Redline", ItemPriceClient.CacheKey(730u, "AK-47 | Redline", 5u));
	}

	[Fact]
	public void CacheKey_WithoutCurrency_UsesDefault()
	{
		Assert.Equal("itemprice:570:default:EMBER Spirit", ItemPriceClient.CacheKey(570u, "EMBER Spirit", null));
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);
}
