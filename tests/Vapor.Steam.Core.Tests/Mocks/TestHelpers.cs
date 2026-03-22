using Vapor.Steam.Core;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Mocks;

/// <summary>
/// Helper methods for creating test objects.
/// </summary>
public static class TestHelpers
{
	/// <summary>
	/// Creates a mock BotSession for testing.
	/// </summary>
	public static BotSession CreateMockSession(ISteamClientManager? steamClientManager = null)
	{
		var actionRegistry = new ActionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<ActionRegistry>.Instance);
		var credentials = new AccountCredentials(
			AccountName: "test_account",
			Password: "test_password"
		);

		return new BotSession(
			"test_account",
			credentials,
			actionRegistry,
			Microsoft.Extensions.Logging.Abstractions.NullLogger<BotSession>.Instance,
			steamClientManager ?? new MockSteamClientManager(),
			steamWebHandler: null,
			eventCallback: null
		);
	}

	/// <summary>
	/// Creates a mock IActionRegistry for testing.
	/// </summary>
	public static IActionRegistry CreateMockActionRegistry()
	{
		return new ActionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<ActionRegistry>.Instance);
	}
}
