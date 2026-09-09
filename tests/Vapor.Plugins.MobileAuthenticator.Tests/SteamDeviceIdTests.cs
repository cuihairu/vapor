using Xunit;
using Vapor.Plugins.MobileAuthenticator;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class SteamDeviceIdTests
{
	[Fact]
	public void FromSteamId_MatchesKnownVector()
	{
		Assert.Equal(
			"android:5c9df5a2-d7de-1e2c-8fc8-766523ca130f",
			SteamDeviceId.FromSteamId(76561198000000000UL));
	}

	[Fact]
	public void FromSteamId_IsDeterministic()
	{
		Assert.Equal(SteamDeviceId.FromSteamId(76561197960265728UL), SteamDeviceId.FromSteamId(76561197960265728UL));
	}

	[Fact]
	public void FromSteamId_DiffersPerAccount()
	{
		Assert.NotEqual(SteamDeviceId.FromSteamId(76561197960265728UL), SteamDeviceId.FromSteamId(76561198000000000UL));
	}
}
