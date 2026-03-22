using SteamKit2;

namespace Vapor.Steam.Core.Tests.RealSteam;

/// <summary>
/// Utility class for simulating Steam callbacks in tests.
/// This helps test various login scenarios without requiring actual Steam connections.
/// </summary>
public static class SteamCallbackSimulator
{
	/// <summary>
	/// Creates a mock LoggedOnCallback with the specified result.
	/// </summary>
	public static SteamUser.LoggedOnCallback CreateLoggedOnCallback(EResult result)
	{
		// We use reflection or a mock approach since SteamUser.LoggedOnCallback
		// doesn't have a public constructor
		// For now, return null - the actual implementation would use a mocking library
		// or create the callback through SteamKit2's internal mechanisms

		throw new NotImplementedException("SteamCallbackSimulator requires a mocking framework like Moq or NSubstitute");
	}

	/// <summary>
	/// Simulates an auth code required scenario.
	/// </summary>
	public static SteamUser.LoggedOnCallback CreateAuthCodeRequiredCallback()
	{
		return CreateLoggedOnCallback(EResult.AccountLogonDenied);
	}

	/// <summary>
	/// Simulates a 2FA required scenario.
	/// </summary>
	public static SteamUser.LoggedOnCallback CreateTwoFactorRequiredCallback()
	{
		return CreateLoggedOnCallback(EResult.AccountLoginDeniedNeedTwoFactor);
	}

	/// <summary>
	/// Simulates a successful login.
	/// </summary>
	public static SteamUser.LoggedOnCallback CreateSuccessCallback()
	{
		return CreateLoggedOnCallback(EResult.OK);
	}
}
