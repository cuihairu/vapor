namespace Vapor.Steam.Core.Caching;

/// <summary>
/// Tiered freshness policy for store data actions: each data kind gets a fresh
/// TTL and a stale-while-revalidate grace window sized to how often the source
/// actually changes. Within the stale window requests are served instantly from
/// cache while a background refresh updates the entry, so slow Steam round
/// trips never sit on the caller's path unless data is missing outright.
/// </summary>
public static class SteamCacheTtl
{
	/// <summary>Game details (appdetails) change rarely.</summary>
	public static readonly TimeSpan GameInfo = TimeSpan.FromMinutes(30);
	public static readonly TimeSpan GameInfoStale = TimeSpan.FromHours(2);

	/// <summary>Store search results are near-static catalog data.</summary>
	public static readonly TimeSpan Search = TimeSpan.FromHours(1);
	public static readonly TimeSpan SearchStale = TimeSpan.FromHours(6);

	/// <summary>Prices are the fastest-changing tier: 3 minutes fresh, then served
	/// stale while background refreshes keep them current.</summary>
	public static readonly TimeSpan Price = TimeSpan.FromMinutes(3);
	public static readonly TimeSpan PriceStale = TimeSpan.FromMinutes(15);

	/// <summary>Market listing pages change moderately.</summary>
	public static readonly TimeSpan MarketListings = TimeSpan.FromMinutes(5);
	public static readonly TimeSpan MarketListingsStale = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Stale window applied when a payload overrides the fresh TTL with
	/// <c>cache_ttl_seconds</c>: 4x keeps the same freshness ratio tiers have.
	/// </summary>
	public static TimeSpan DefaultStaleWindowFor(TimeSpan ttl) => ttl + TimeSpan.FromTicks(ttl.Ticks * 3);
}
