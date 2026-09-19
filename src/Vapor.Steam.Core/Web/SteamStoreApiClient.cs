using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Steam Store / Community Market data source used by the data actions.
/// </summary>
public interface ISteamStoreApiClient
{
	/// <summary>Fetches full app details (store appdetails API).</summary>
	Task<GameInfo?> GetGameInfoAsync(uint appId, string country = "us", CancellationToken cancellationToken = default);

	/// <summary>Searches the store catalog by free-text term.</summary>
	Task<IReadOnlyList<GameSearchResult>> SearchGamesAsync(string term, int limit = 20, string country = "us", CancellationToken cancellationToken = default);

	/// <summary>Fetches the current price overview for an app.</summary>
	Task<PriceOverview?> GetPriceAsync(uint appId, string country = "us", CancellationToken cancellationToken = default);

	/// <summary>Fetches one page of Community Market listings for an app.</summary>
	Task<MarketListingsPage?> GetMarketListingsAsync(uint appId, int start = 0, int count = 20, CancellationToken cancellationToken = default);

	/// <summary>Claims a free store sub for the logged-on account (checkout addlicense). Returns null on transport failure.</summary>
	Task<StorePurchaseResult?> AddFreeLicenseAsync(uint subId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a store checkout purchase call. <see cref="Success"/> covers the
/// codes that mean the sub is on the account afterwards (granted or already owned);
/// <see cref="PurchaseResultDetail"/> carries the raw Steam EPurchaseResultDetail value.
/// </summary>
public sealed record StorePurchaseResult(bool Success, int PurchaseResultDetail)
{
	/// <summary>EPurchaseResultDetail: the purchase went through.</summary>
	public const int Ok = 1;
	/// <summary>EPurchaseResultDetail: the sub was already on the account.</summary>
	public const int AlreadyPurchased = 15;
}

/// <summary>
/// Default <see cref="ISteamStoreApiClient"/> implementation backed by public
/// store.steampowered.com / steamcommunity.com endpoints via <see cref="SteamWebHandler"/>.
/// </summary>
public sealed class SteamStoreApiClient : ISteamStoreApiClient
{
	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamStoreApiClient> _logger;

	public SteamStoreApiClient(SteamWebHandler webHandler, ILogger<SteamStoreApiClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger;
	}

	public async Task<GameInfo?> GetGameInfoAsync(uint appId, string country = "us", CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(country);

		var url = new Uri(
			$"https://store.steampowered.com/api/appdetails?appids={appId}&cc={Uri.EscapeDataString(country)}&l=english");

		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning("appdetails request failed for {AppId}: {StatusCode}", appId, response.StatusCode);
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(response.Body);
			if (!doc.RootElement.TryGetProperty(appId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var appRoot) ||
				!appRoot.TryGetProperty("success", out var successElem) || !successElem.GetBoolean())
			{
				return null;
			}

			if (!appRoot.TryGetProperty("data", out var data))
			{
				return null;
			}

			return ParseGameInfo(appId, data);
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to parse appdetails response for {AppId}", appId);
			return null;
		}
	}

	public async Task<IReadOnlyList<GameSearchResult>> SearchGamesAsync(string term, int limit = 20, string country = "us", CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(term);
		ArgumentException.ThrowIfNullOrEmpty(country);

		int cappedLimit = Math.Clamp(limit, 1, 50);
		var url = new Uri(
			$"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc={Uri.EscapeDataString(country)}");

		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning("storesearch request failed for term {Term}: {StatusCode}", term, response.StatusCode);
			return [];
		}

		try
		{
			using var doc = JsonDocument.Parse(response.Body);
			var results = new List<GameSearchResult>();

			if (doc.RootElement.TryGetProperty("items", out var items))
			{
				foreach (var item in items.EnumerateArray())
				{
					if (results.Count >= cappedLimit)
					{
						break;
					}

					results.Add(ParseSearchResult(item));
				}
			}

			return results;
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to parse storesearch response for term {Term}", term);
			return [];
		}
	}

	public async Task<PriceOverview?> GetPriceAsync(uint appId, string country = "us", CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(country);

		var game = await GetGameInfoAsync(appId, country, cancellationToken).ConfigureAwait(false);
		return game?.Price;
	}

	public async Task<MarketListingsPage?> GetMarketListingsAsync(uint appId, int start = 0, int count = 20, CancellationToken cancellationToken = default)
	{
		int cappedStart = Math.Max(start, 0);
		int cappedCount = Math.Clamp(count, 1, 100);

		var url = new Uri(
			$"https://steamcommunity.com/market/search/render/?appid={appId}&norender=1&count={cappedCount}&start={cappedStart}");

		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning("market search request failed for app {AppId}: {StatusCode}", appId, response.StatusCode);
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(response.Body);

			// Current search-render contract: `results` is an array of per-commodity
			// aggregates (hash_name / sell_price / asset_description) with `total_count`.
			// Legacy responses instead expose the `listinginfo` object of individual
			// listings — keep parsing both so either upstream shape yields data.
			if (doc.RootElement.TryGetProperty("results", out var results) &&
				results.ValueKind == JsonValueKind.Array)
			{
				return ParseMarketSearchPage(appId, doc.RootElement, results, cappedStart, cappedCount);
			}

			if (!doc.RootElement.TryGetProperty("listinginfo", out var listingInfo) ||
				listingInfo.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var listings = new List<MarketListing>();
			foreach (var listingProperty in listingInfo.EnumerateObject())
			{
				var listing = ParseMarketListing(appId, listingProperty.Value);
				if (listing != null)
				{
					listings.Add(listing);
				}
			}

			int totalCount = doc.RootElement.TryGetProperty("total_rowcount", out var totalElem)
				&& totalElem.TryGetInt32(out int total)
					? total
					: cappedStart + listings.Count;

			return new MarketListingsPage
			{
				AppId = appId,
				Listings = listings,
				TotalCount = totalCount,
				Start = cappedStart,
				PageSize = cappedCount
			};
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to parse market search response for app {AppId}", appId);
			return null;
		}
	}

	public async Task<StorePurchaseResult?> AddFreeLicenseAsync(uint subId, CancellationToken cancellationToken = default)
	{
		// The addlicense endpoint is the same call the store's own "Add to account"
		// button makes; it needs the session cookies (sessionid + steamLoginSecure)
		// the web handler already carries, and answers with a flat JSON verdict.
		var url = new Uri($"https://store.steampowered.com/checkout/addlicense/{subId}");
		var headers = new Dictionary<string, string> { ["Referer"] = $"https://store.steampowered.com/sub/{subId}/" };

		var response = await _webHandler.PostAsync(url, content: null, headers, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning("addlicense request failed for sub {SubId}: {StatusCode}", subId, response.StatusCode);
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(response.Body);
			if (!doc.RootElement.TryGetProperty("purchaseresultdetail", out var detailElem) ||
				!detailElem.TryGetInt32(out int detail))
			{
				_logger.LogWarning("addlicense response for sub {SubId} carries no purchaseresultdetail", subId);
				return null;
			}

			bool success = detail is StorePurchaseResult.Ok or StorePurchaseResult.AlreadyPurchased;
			_logger.LogInformation("addlicense for sub {SubId}: detail {Detail} (success: {Success})", subId, detail, success);
			return new StorePurchaseResult(success, detail);
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to parse addlicense response for sub {SubId}", subId);
			return null;
		}
	}

	private static GameInfo ParseGameInfo(uint appId, JsonElement data)
	{
		List<string> genres = [];
		if (data.TryGetProperty("genres", out var genresElem))
		{
			foreach (var genre in genresElem.EnumerateArray())
			{
				if (genre.TryGetProperty("description", out var description) && description.GetString() is { } name)
				{
					genres.Add(name);
				}
			}
		}

		List<string> categories = [];
		if (data.TryGetProperty("categories", out var categoriesElem))
		{
			foreach (var category in categoriesElem.EnumerateArray())
			{
				if (category.TryGetProperty("description", out var description) && description.GetString() is { } name)
				{
					categories.Add(name);
				}
			}
		}

		DateTimeOffset? releaseDate = null;
		if (data.TryGetProperty("release_date", out var releaseDateElem) &&
			releaseDateElem.TryGetProperty("date", out var releaseDateStr) &&
			DateTime.TryParse(releaseDateStr.GetString(), out var parsed))
		{
			releaseDate = new DateTimeOffset(parsed, TimeSpan.Zero);
		}

		long? recommendations = null;
		if (data.TryGetProperty("recommendations", out var recommendationsElem) &&
			recommendationsElem.TryGetProperty("total", out var totalElem) &&
			totalElem.TryGetInt64(out long total))
		{
			recommendations = total;
		}

		bool isFree = data.TryGetProperty("is_free", out var isFreeElem) && isFreeElem.GetBoolean();

		return new GameInfo
		{
			AppId = data.TryGetProperty("steam_appid", out var appIdElem) ? appIdElem.GetUInt32() : appId,
			Name = data.GetStringProperty("name") ?? string.Empty,
			Type = data.GetStringProperty("type"),
			Developer = JoinDescriptions(data, "developers"),
			Publisher = JoinDescriptions(data, "publishers"),
			ReleaseDate = releaseDate,
			IsFree = isFree,
			RequiresPurchase = !isFree,
			Price = ParsePriceOverview(data.GetPropertyOrNull("price_overview")),
			MetacriticScore = data.TryGetProperty("metacritic", out var metacriticElem) &&
							  metacriticElem.TryGetProperty("score", out var scoreElem) &&
							  scoreElem.TryGetInt32(out int score)
				? score
				: null,
			RecommendationsTotal = recommendations,
			Genres = genres,
			Categories = categories,
			HeaderImage = data.GetStringProperty("header_image"),
			SmallCapsuleImage = data.GetStringProperty("small_capsule") ?? data.GetStringProperty("capsule_image"),
			ShortDescription = data.GetStringProperty("short_description"),
			SupportedLanguages = data.GetStringProperty("supported_languages")
		};
	}

	private static GameSearchResult ParseSearchResult(JsonElement item)
	{
		// storesearch results do not carry an explicit free flag; absence of a
		// price block is the practical signal used here.
		bool hasPrice = item.TryGetProperty("price", out _);

		return new GameSearchResult
		{
			AppId = item.TryGetProperty("id", out var idElem) ? idElem.GetUInt32() : 0,
			Name = item.GetStringProperty("name") ?? string.Empty,
			Type = item.GetStringProperty("type"),
			IsFree = !hasPrice,
			Price = ParsePriceOverview(item.GetPropertyOrNull("price")),
			HeaderImage = item.GetStringProperty("tiny_image")
		};
	}

	private static MarketListing? ParseMarketListing(uint appId, JsonElement listing)
	{
		if (!listing.TryGetProperty("listingid", out var listingIdElem) ||
			!ulong.TryParse(listingIdElem.GetString(), out ulong listingId))
		{
			return null;
		}

		ulong assetId = 0, classId = 0, instanceId = 0;
		if (listing.TryGetProperty("asset", out var asset))
		{
			assetId = GetUlong(asset, "id");
			classId = GetUlong(asset, "classid");
			instanceId = GetUlong(asset, "instanceid");
		}

		long? convertedPrice = GetLongOrNull(listing, "converted_price");
		long? convertedFee = GetLongOrNull(listing, "converted_fee");
		long? publisherFee = GetLongOrNull(listing, "converted_publisher_fee");
		int? currencyId = GetIntOrNull(listing, "converted_currencyid");

		decimal? totalPrice = null;
		if (convertedPrice.HasValue)
		{
			decimal total = convertedPrice.Value + (convertedFee ?? 0) + (publisherFee ?? 0);
			totalPrice = total / 100m;
		}

		return new MarketListing
		{
			ListingId = listingId,
			AppId = appId,
			AssetId = assetId,
			ClassId = classId,
			InstanceId = instanceId,
			TotalPrice = totalPrice,
			CurrencyId = currencyId,
			FetchedAt = DateTimeOffset.UtcNow
		};
	}

	private static MarketListingsPage ParseMarketSearchPage(uint appId, JsonElement root, JsonElement results, int start, int pageSize)
	{
		var listings = new List<MarketListing>();
		foreach (var result in results.EnumerateArray())
		{
			var listing = ParseMarketSearchResult(appId, result);
			if (listing != null)
			{
				listings.Add(listing);
			}
		}

		int totalCount = root.TryGetProperty("total_count", out var totalElem) && totalElem.TryGetInt32(out int total)
			? total
			: start + listings.Count;

		return new MarketListingsPage
		{
			AppId = appId,
			Listings = listings,
			TotalCount = totalCount,
			Start = start,
			PageSize = pageSize
		};
	}

	private static MarketListing? ParseMarketSearchResult(uint appId, JsonElement result)
	{
		// The hash name is the stable identity of a market aggregate; entries
		// without it are not actionable for callers.
		if (result.GetStringProperty("hash_name") is not { } hashName)
		{
			return null;
		}

		ulong classId = 0;
		if (result.TryGetProperty("asset_description", out var asset))
		{
			classId = GetUlong(asset, "classid");
		}

		decimal? totalPrice = null;
		if (result.TryGetProperty("sell_price", out var sellPriceElem) && sellPriceElem.TryGetInt64(out long sellPriceCents))
		{
			totalPrice = sellPriceCents / 100m;
		}

		int? sellListings = result.TryGetProperty("sell_listings", out var sellListingsElem) && sellListingsElem.TryGetInt32(out int sellListingsCount)
			? sellListingsCount
			: null;

		return new MarketListing
		{
			Name = result.GetStringProperty("name"),
			HashName = hashName,
			AppId = appId,
			ClassId = classId,
			SellListings = sellListings,
			TotalPrice = totalPrice,
			FetchedAt = DateTimeOffset.UtcNow
		};
	}

	private static PriceOverview? ParsePriceOverview(JsonElement? element)
	{
		if (element == null || element.Value.ValueKind != JsonValueKind.Object)
		{
			return null;
		}

		var price = element.Value;
		return new PriceOverview
		{
			Currency = price.GetStringProperty("currency") ?? "USD",
			Initial = TryGetDecimal(price, "initial", out decimal initial) ? initial / 100m : null,
			Final = TryGetDecimal(price, "final", out decimal final) ? final / 100m : null,
			DiscountPercent = price.TryGetProperty("discount_percent", out var discountElem) && discountElem.TryGetInt32(out int discount)
				? discount
				: 0,
			FinalFormatted = price.GetStringProperty("final_formatted")
		};
	}

	private static string? JoinDescriptions(JsonElement data, string propertyName)
	{
		if (!data.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		List<string> values = [];
		foreach (var entry in array.EnumerateArray())
		{
			if (entry.GetString() is { } value)
			{
				values.Add(value);
			}
		}

		return values.Count > 0 ? string.Join(", ", values) : null;
	}

	private static ulong GetUlong(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out var value) && ulong.TryParse(value.GetString(), out ulong parsed)
			? parsed
			: 0;
	}

	private static long? GetLongOrNull(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out long parsed)
			? parsed
			: null;
	}

	private static int? GetIntOrNull(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out int parsed)
			? parsed
			: null;
	}

	private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
	{
		if (element.TryGetProperty(propertyName, out var property))
		{
			switch (property.ValueKind)
			{
				case JsonValueKind.Number:
					if (property.TryGetInt64(out long asLong))
					{
						value = asLong;
						return true;
					}

					return property.TryGetDecimal(out value);
				case JsonValueKind.String when decimal.TryParse(property.GetString(), out value):
					return true;
			}
		}

		value = 0;
		return false;
	}
}

internal static class JsonElementExtensions
{
	public static string? GetStringProperty(this JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	public static JsonElement? GetPropertyOrNull(this JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out var value) ? value : null;
}
