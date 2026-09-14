using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// One of the account's own Steam Community Market listings, as reported by the
/// mylistings page. Amounts are in the wallet currency's smallest unit (cents):
/// <see cref="PriceCents"/> is what the buyer pays, <see cref="FeeCents"/> the
/// combined Steam + publisher fee, and <see cref="SellerProceedsCents"/> what
/// the seller receives (price − fee, null when the response omits the fee).
/// </summary>
public sealed record MyMarketListing(
	string ListingId,
	uint AppId,
	string ContextId,
	string AssetId,
	string? ClassId,
	string? MarketHashName,
	string? MarketName,
	string? GameName,
	int PriceCents,
	int? FeeCents,
	int? SellerProceedsCents,
	string? CurrencyId,
	string? IconUrl,
	DateTimeOffset? TimeCreated,
	bool CancelRequested);

/// <summary>One page of the account's own listings plus the totals needed for paging.</summary>
public sealed record MyMarketListingsPage(
	IReadOnlyList<MyMarketListing> Listings,
	int Start,
	int PageSize,
	int TotalCount,
	int? ActiveCount,
	int? OnHoldCount,
	int? ToBeConfirmedCount);

/// <summary>
/// Fetches and parses the account's own community market listings
/// (<c>steamcommunity.com/market/mylistings</c>). The page is login-gated
/// (session cookies carried by the <see cref="SteamWebHandler"/>) and has no
/// official Web API equivalent; it answers with JSON when <c>norender=1</c>
/// is passed. This is the read side of the market loop — the control plane's
/// <c>GET /v1/accounts/{name}/market/listings</c> endpoint dispatches the
/// get_my_market_listings action, which goes through here.
/// </summary>
public sealed class SteamMarketClient
{
	// Defensive cap so a bogus count can't turn one dispatch into a huge scrape.
	private const int MaxCount = 500;

	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamMarketClient> _logger;

	public SteamMarketClient(SteamWebHandler webHandler, ILogger<SteamMarketClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>Fetches one page of the account's own listings. Null on request or parse failure.</summary>
	public async Task<MyMarketListingsPage?> GetMyListingsAsync(
		int start = 0,
		int count = 100,
		CancellationToken cancellationToken = default)
	{
		int cappedStart = Math.Max(start, 0);
		int cappedCount = Math.Clamp(count, 1, MaxCount);

		var url = new Uri(
			$"https://steamcommunity.com/market/mylistings/?norender=1&start={cappedStart.ToString(CultureInfo.InvariantCulture)}&count={cappedCount.ToString(CultureInfo.InvariantCulture)}");

		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning("mylistings request failed: {StatusCode}", response.StatusCode);
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(response.Body);
			return ParseMyListings(doc.RootElement, cappedStart, cappedCount);
		}
		catch (JsonException ex)
		{
			_logger.LogError(ex, "Failed to parse mylistings response");
			return null;
		}
	}

	/// <summary>
	/// Cancels one own market listing. Same POST the market page's cancel
	/// button issues: the session id must be echoed in the form body, with
	/// XHR-style headers. True on any 2xx; false on failure (429 rate limits
	/// are already retried with backoff by the web handler's resilience).
	/// </summary>
	public async Task<bool> CancelListingAsync(string listingId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrEmpty(listingId))
		{
			throw new ArgumentException("Listing id is required", nameof(listingId));
		}

		if (!_webHandler.TryGetSessionId(out string? sessionId) || string.IsNullOrEmpty(sessionId))
		{
			_logger.LogWarning("cancel listing {ListingId}: no session id on the web handler (is the session logged on?)", listingId);
			return false;
		}

		var url = new Uri($"https://steamcommunity.com/market/removelisting/{Uri.EscapeDataString(listingId)}");
		// Referer/Origin for community hosts are added by the web handler;
		// only the XHR marker is cancel-specific.
		var headers = new Dictionary<string, string>
		{
			["X-Requested-With"] = "XMLHttpRequest"
		};

		var content = new FormUrlEncodedContent(
		[
			new KeyValuePair<string, string>("sessionid", sessionId)
		]);

		var response = await _webHandler.PostAsync(url, content, headers, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess)
		{
			_logger.LogWarning("cancel listing {ListingId} failed: {StatusCode}", listingId, response.StatusCode);
			return false;
		}

		_logger.LogInformation("Canceled market listing {ListingId}", listingId);
		return true;
	}

	/// <summary>
	/// Parses one mylistings response. The live page is login-gated (like the
	/// badges page), so the shape is pinned by the market_mylistings_p1.json
	/// fixture, whose skeleton is cross-confirmed (2026-09-14) against three
	/// independent consumers of the endpoint (the cs2.sh scraping guide,
	/// node-steam-market-fetcher's index.d.ts, zevnda/steam-game-idler's
	/// market.rs). Two tolerances keep either observed variant parsing: the
	/// entries are read from <c>mylistings</c> with <c>listings</c> as a
	/// fallback array name, and an entry without an inline
	/// <c>asset_description</c> is joined against the top-level <c>assets</c>
	/// table by asset id. Entries without a usable listing id or price are
	/// skipped rather than emitted with made-up values.
	/// </summary>
	internal static MyMarketListingsPage ParseMyListings(JsonElement root, int start, int pageSize)
	{
		Dictionary<string, JsonElement> assetsById = BuildAssetTable(root);
		var listings = new List<MyMarketListing>();

		JsonElement? entries = GetArrayProperty(root, "mylistings") ?? GetArrayProperty(root, "listings");
		if (entries is not null)
		{
			foreach (JsonElement entry in entries.Value.EnumerateArray())
			{
				var listing = ParseListing(entry, assetsById);
				if (listing is not null)
				{
					listings.Add(listing);
				}
			}
		}

		return new MyMarketListingsPage(
			listings,
			start,
			pageSize,
			GetInt32OrNull(root, "total_count") ?? start + listings.Count,
			GetInt32OrNull(root, "num_active_listings"),
			GetCount(root, "listing_on_hold"),
			GetCount(root, "listing_to_be_confirmed"));
	}

	private static MyMarketListing? ParseListing(JsonElement entry, Dictionary<string, JsonElement> assetsById)
	{
		string? listingId = entry.GetStringProperty("listingid");
		int? price = GetInt32OrNull(entry, "price");
		if (string.IsNullOrEmpty(listingId) || price is null)
		{
			return null;
		}

		// Names/icon come from the inline asset_description when present,
		// otherwise from the top-level assets table joined by asset id.
		JsonElement? description = entry.GetPropertyOrNull("asset_description");
		string? assetId = entry.GetStringProperty("assetid")
			?? GetStringOrNull(entry.GetPropertyOrNull("asset"), "id");
		if (description is null && assetId is not null && assetsById.TryGetValue(assetId, out JsonElement joined))
		{
			description = joined;
		}

		JsonElement? asset = entry.GetPropertyOrNull("asset");
		int appIdValue = GetInt32OrNull(entry, "game_appid")
			?? GetInt32OrNull(description, "appid")
			?? GetInt32OrNull(asset, "appid")
			?? 0;
		int? fee = GetInt32OrNull(entry, "fee");
		long? timeCreated = GetInt64OrNull(entry, "time_created");

		return new MyMarketListing(
			listingId,
			(uint)Math.Max(appIdValue, 0),
			entry.GetStringProperty("contextid") ?? GetStringOrNull(asset, "contextid") ?? string.Empty,
			assetId ?? string.Empty,
			entry.GetStringProperty("classid") ?? GetStringOrNull(description, "classid"),
			GetStringOrNull(description, "market_hash_name"),
			GetStringOrNull(description, "market_name"),
			entry.GetStringProperty("game_name"),
			price.Value,
			fee,
			fee is null ? null : price.Value - fee.Value,
			GetStringOrNumber(entry, "currencyid"),
			GetStringOrNull(description, "icon_url"),
			timeCreated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(timeCreated.Value) : null,
			GetBoolValue(entry, "cancel_requested"));
	}

	// assets[appid][contextid][assetid] → asset object. Keyed by asset id only:
	// asset ids are globally unique 64-bit ids and the join key the listings
	// carry is the plain asset id.
	private static Dictionary<string, JsonElement> BuildAssetTable(JsonElement root)
	{
		var table = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		if (root.TryGetProperty("assets", out JsonElement byApp) && byApp.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty appProperty in byApp.EnumerateObject())
			{
				if (appProperty.Value.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				foreach (JsonProperty contextProperty in appProperty.Value.EnumerateObject())
				{
					if (contextProperty.Value.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					foreach (JsonProperty assetProperty in contextProperty.Value.EnumerateObject())
					{
						table[assetProperty.Name] = assetProperty.Value;
					}
				}
			}
		}

		return table;
	}

	private static JsonElement? GetArrayProperty(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.Array
			? property
			: null;
	}

	// `listing_on_hold` / `listing_to_be_confirmed` arrive as arrays in the
	// observed responses; accept a bare number too in case Steam flattens them.
	private static int? GetCount(JsonElement root, string propertyName)
	{
		if (!root.TryGetProperty(propertyName, out JsonElement property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.Array => property.GetArrayLength(),
			JsonValueKind.Number when property.TryGetInt32(out int parsed) => parsed,
			_ => null
		};
	}

	private static int? GetInt32OrNull(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out JsonElement property)
			&& property.ValueKind == JsonValueKind.Number
			&& property.TryGetInt32(out int parsed)
				? parsed
				: null;
	}

	private static int? GetInt32OrNull(JsonElement? element, string propertyName) =>
		element is null ? null : GetInt32OrNull(element.Value, propertyName);

	private static long? GetInt64OrNull(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out JsonElement property)
			&& property.ValueKind == JsonValueKind.Number
			&& property.TryGetInt64(out long parsed)
				? parsed
				: null;
	}

	private static string? GetStringOrNull(JsonElement? element, string propertyName) =>
		element is null ? null : element.Value.GetStringProperty(propertyName);

	// Some numeric fields arrive as JSON numbers in one variant and strings in
	// another (currencyid does across endpoints) — accept either.
	private static string? GetStringOrNumber(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.String => property.GetString(),
			JsonValueKind.Number => property.GetRawText(),
			_ => null
		};
	}

	private static bool GetBoolValue(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return false;
		}

		return property.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Number => property.TryGetInt32(out int parsed) && parsed != 0,
			_ => false
		};
	}
}
