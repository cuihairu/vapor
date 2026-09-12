using System.Text.Json.Serialization;

namespace Vapor.Steam.Core.Models;

/// <summary>
/// Price information for a game or an item.
/// </summary>
public sealed record PriceOverview
{
	/// <summary>Currency code (e.g. "USD", "CNY").</summary>
	public string Currency { get; init; } = "USD";

	/// <summary>Current price after discount, in major currency units.</summary>
	public decimal? Final { get; init; }

	/// <summary>Price before discount, in major currency units.</summary>
	public decimal? Initial { get; init; }

	/// <summary>Discount percentage (0-100).</summary>
	public int DiscountPercent { get; init; }

	/// <summary>Formatted final price as returned by Steam (display convenience).</summary>
	public string? FinalFormatted { get; init; }
}

/// <summary>
/// Metadata for a Steam game/application.
/// </summary>
public sealed record GameInfo
{
	/// <summary>The Steam AppID.</summary>
	public uint AppId { get; init; }

	/// <summary>Display name of the game.</summary>
	public string Name { get; init; } = string.Empty;

	/// <summary>Content type: "game", "dlc", "application", "music", ...</summary>
	public string? Type { get; init; }

	/// <summary>Developer name(s), joined when multiple.</summary>
	public string? Developer { get; init; }

	/// <summary>Publisher name(s), joined when multiple.</summary>
	public string? Publisher { get; init; }

	/// <summary>Release date when known.</summary>
	public DateTimeOffset? ReleaseDate { get; init; }

	/// <summary>Whether the game is free to play.</summary>
	public bool IsFree { get; init; }

	/// <summary>Whether the app requires an active Steam subscription (purchased) to use.</summary>
	public bool RequiresPurchase { get; init; }

	/// <summary>Current price overview.</summary>
	public PriceOverview? Price { get; init; }

	/// <summary>Metacritic score when available.</summary>
	public int? MetacriticScore { get; init; }

	/// <summary>Total positive+negative recommendations (reviews).</summary>
	public long? RecommendationsTotal { get; init; }

	/// <summary>Genre names.</summary>
	public IReadOnlyList<string> Genres { get; init; } = [];

	/// <summary>Category names (Single-Player, Multi-Player, Steam Cloud, ...).</summary>
	public IReadOnlyList<string> Categories { get; init; } = [];

	/// <summary>Header image URL.</summary>
	public string? HeaderImage { get; init; }

	/// <summary>Small capsule image URL.</summary>
	public string? SmallCapsuleImage { get; init; }

	/// <summary>Short description snippet (already trimmed by the source).</summary>
	public string? ShortDescription { get; init; }

	/// <summary>Supported languages as a raw comma-separated string from Steam.</summary>
	public string? SupportedLanguages { get; init; }

	/// <summary>When this snapshot was fetched (cache freshness marker).</summary>
	[JsonPropertyName("fetchedAt")]
	public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

	public static string CacheKey(uint appId) => $"game:{appId}";
}

/// <summary>
/// Market metadata for an inventory item (identified by market hash name).
/// </summary>
public sealed record ItemInfo
{
	/// <summary>The AppID the item belongs to.</summary>
	public uint AppId { get; init; }

	/// <summary>Market hash name (unique per item class).</summary>
	public string MarketHashName { get; init; } = string.Empty;

	/// <summary>Display name.</summary>
	public string? Name { get; init; }

	/// <summary>Item type description from Steam.</summary>
	public string? Type { get; init; }

	/// <summary>Icon URL.</summary>
	public string? IconUrl { get; init; }

	/// <summary>Lowest listed price observed, in major currency units.</summary>
	public decimal? LowestPrice { get; init; }

	/// <summary>Median sale price, in major currency units.</summary>
	public decimal? MedianPrice { get; init; }

	/// <summary>Currency code for the prices above.</summary>
	public string Currency { get; init; } = "USD";

	/// <summary>24h sold volume when available.</summary>
	public int? Volume24h { get; init; }

	/// <summary>When this snapshot was fetched (cache freshness marker).</summary>
	[JsonPropertyName("fetchedAt")]
	public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

	public static string CacheKey(uint appId, string marketHashName) => $"item:{appId}:{marketHashName}";
}

/// <summary>
/// Search result entry for game queries (lighter than <see cref="GameInfo"/>).
/// </summary>
public sealed record GameSearchResult
{
	public uint AppId { get; init; }

	public string Name { get; init; } = string.Empty;

	public string? Type { get; init; }

	public bool IsFree { get; init; }

	public PriceOverview? Price { get; init; }

	public string? HeaderImage { get; init; }

	public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A listing on the Steam Community Market for a given app.
/// Prices are the buyer-facing totals (item price + fees), converted to major currency units.
/// Search-aggregate results (market search render) carry <see cref="HashName"/>/
/// <see cref="SellListings"/> and leave the per-listing fields zeroed; per-listing
/// responses (item render) populate the listing/asset IDs instead.
/// </summary>
public sealed record MarketListing
{
	/// <summary>The market listing ID (0 for search aggregates, which have no individual listing).</summary>
	public ulong ListingId { get; init; }

	/// <summary>Display name of the listed item or aggregate.</summary>
	public string? Name { get; init; }

	/// <summary>Market hash name — the stable item identity for search aggregates.</summary>
	public string? HashName { get; init; }

	/// <summary>Number of active sell listings behind a search aggregate.</summary>
	public int? SellListings { get; init; }

	/// <summary>The AppID the listed asset belongs to.</summary>
	public uint AppId { get; init; }

	/// <summary>Asset ID of the listed item (0 for search aggregates).</summary>
	public ulong AssetId { get; init; }

	/// <summary>Class ID of the listed item (maps to item descriptions).</summary>
	public ulong ClassId { get; init; }

	/// <summary>Instance ID of the listed item (0 for search aggregates).</summary>
	public ulong InstanceId { get; init; }

	/// <summary>Total buyer-facing price (price + fees), in major currency units.</summary>
	public decimal? TotalPrice { get; init; }

	/// <summary>Steam currency ID of the converted price (e.g. 2001 = USD).</summary>
	public int? CurrencyId { get; init; }

	/// <summary>When this snapshot was fetched.</summary>
	public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A page of market listings with pagination metadata.
/// </summary>
public sealed record MarketListingsPage
{
	public uint AppId { get; init; }

	public IReadOnlyList<MarketListing> Listings { get; init; } = [];

	/// <summary>Total listings matching the query.</summary>
	public int TotalCount { get; init; }

	/// <summary>Offset this page starts at.</summary>
	public int Start { get; init; }

	/// <summary>Page size.</summary>
	public int PageSize { get; init; }

	public bool HasMore => Start + Listings.Count < TotalCount;

	public static string CacheKey(uint appId, int start, int count) => $"market:{appId}:{start}:{count}";
}
