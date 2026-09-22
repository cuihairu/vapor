using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Logging;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Constructor guard clauses (ArgumentNullException arms) plus the
/// null-overload-default arms (config ?? new Config()) that the happy-path
/// tests never touch, collected in one place.
/// </summary>
public sealed class GuardClauseTests
{
	[Fact]
	public void SteamAchievementsClient_NullDependencies_ThrowWithParamName()
	{
		Assert.Equal("webHandler", Assert.Throws<ArgumentNullException>(
			() => new SteamAchievementsClient(null!, null!)).ParamName);
		Assert.Equal("logger", Assert.Throws<ArgumentNullException>(
			() => new SteamAchievementsClient(CreateWebHandler(), null!)).ParamName);
	}

	[Fact]
	public void SteamBadgesClient_NullDependencies_ThrowWithParamName()
	{
		Assert.Equal("webHandler", Assert.Throws<ArgumentNullException>(
			() => new SteamBadgesClient(null!, null!)).ParamName);
		Assert.Equal("logger", Assert.Throws<ArgumentNullException>(
			() => new SteamBadgesClient(CreateWebHandler(), null!)).ParamName);
	}

	[Fact]
	public void SteamProfileGamesClient_NullDependencies_ThrowWithParamName()
	{
		Assert.Equal("webHandler", Assert.Throws<ArgumentNullException>(
			() => new SteamProfileGamesClient(null!, null!)).ParamName);
		Assert.Equal("logger", Assert.Throws<ArgumentNullException>(
			() => new SteamProfileGamesClient(CreateWebHandler(), null!)).ParamName);
	}

	[Fact]
	public void SteamWebHandler_NullConfig_FallsBackToDefaults()
	{
		// Both constructors tolerate a null config by design (defaults apply).
		using var withoutInner = new SteamWebHandler(null!, NullLogger<SteamWebHandler>.Instance);
		Assert.NotNull(withoutInner);
		using var withInner = new SteamWebHandler(null!, NullLogger<SteamWebHandler>.Instance, new HttpClientHandler());
		Assert.NotNull(withInner);
	}

	[Fact]
	public void RedactingLoggerProvider_NullInner_ThrowsWithParamName()
	{
		Assert.Equal("inner", Assert.Throws<ArgumentNullException>(
			() => new RedactingLoggerProvider(null!)).ParamName);
	}

	[Fact]
	public void SteamTimeSynchronizer_NullServerTimeQuery_ThrowsWithParamName()
	{
		Assert.Equal("serverTimeQuery", Assert.Throws<ArgumentNullException>(
			() => new SteamTimeSynchronizer(null!)).ParamName);
	}

	[Fact]
	public void RedisVaporCache_NullOptions_FallsBackToDefaults()
	{
		var multiplexer = Moq.Mock.Of<StackExchange.Redis.IConnectionMultiplexer>();
		using var cache = new RedisVaporCache(multiplexer, options: null);
		Assert.NotNull(cache);
	}

	[Fact]
	public void TradeRateLimiter_NullOptions_FallsBackToDefaults()
	{
		using var limiter = new TradeRateLimiter(options: null);
		Assert.NotNull(limiter);
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);
}
