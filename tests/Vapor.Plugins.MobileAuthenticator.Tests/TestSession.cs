using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

/// <summary>Builds BotSession instances for action tests (no network activity).</summary>
internal static class TestSession
{
	public static BotSession Create(bool withWebHandler = true)
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var credentials = new AccountCredentials("test_account", "test_password");
		var handler = withWebHandler
			? new SteamWebHandler(new SteamWebHandlerConfig(), NullLogger<SteamWebHandler>.Instance)
			: null;

		return new BotSession(
			"test_account",
			credentials,
			registry,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: handler,
			eventCallback: null);
	}
}
