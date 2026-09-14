using Vapor.Steam.Core.Caching;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Caching;

public sealed class SteamCacheTtlTests
{
	[Fact]
	public void DefaultStaleWindowFor_IsFourTimesTheFreshTtl()
	{
		Assert.Equal(TimeSpan.FromMinutes(12), SteamCacheTtl.DefaultStaleWindowFor(TimeSpan.FromMinutes(3)));
		Assert.Equal(TimeSpan.FromHours(4), SteamCacheTtl.DefaultStaleWindowFor(TimeSpan.FromHours(1)));
		Assert.Equal(TimeSpan.Zero, SteamCacheTtl.DefaultStaleWindowFor(TimeSpan.Zero));
	}

	[Fact]
	public void TierConstants_KeepTheStaleWindowRatiosOfTheirFreshTtls()
	{
		Assert.Equal(TimeSpan.FromMinutes(30), SteamCacheTtl.GameInfo);
		Assert.Equal(TimeSpan.FromHours(2), SteamCacheTtl.GameInfoStale);
		Assert.Equal(TimeSpan.FromHours(1), SteamCacheTtl.Search);
		Assert.Equal(TimeSpan.FromHours(6), SteamCacheTtl.SearchStale);
		Assert.Equal(TimeSpan.FromMinutes(3), SteamCacheTtl.Price);
		Assert.Equal(TimeSpan.FromMinutes(15), SteamCacheTtl.PriceStale);
		Assert.Equal(TimeSpan.FromMinutes(5), SteamCacheTtl.MarketListings);
		Assert.Equal(TimeSpan.FromMinutes(30), SteamCacheTtl.MarketListingsStale);
	}
}
