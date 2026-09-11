using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Monitoring.Tests;

/// <summary>Builds BotSession instances for action tests (no network activity).</summary>
internal static class TestSession
{
	public static BotSession Create()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var credentials = new AccountCredentials("test_account", "test_password");

		return new BotSession(
			"test_account",
			credentials,
			registry,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);
	}
}

/// <summary>Plugin context stub with configurable configuration values and host services.</summary>
internal sealed class StubPluginContext(
	PluginInfo info,
	IServiceProvider services,
	IReadOnlyDictionary<string, string>? configuration = null) : IPluginContext
{
	public PluginInfo Info { get; } = info;

	public IReadOnlyDictionary<string, string> Configuration { get; } =
		configuration ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public IPluginHostServices Host { get; } = new DefaultPluginHostServices(NullLoggerFactory.Instance, services);
}

/// <summary>Service provider stub returning pre-registered instances.</summary>
internal sealed class StubServiceProvider(IReadOnlyDictionary<Type, object>? services = null) : IServiceProvider
{
	private readonly IReadOnlyDictionary<Type, object> _services = services ?? new Dictionary<Type, object>();

	public object? GetService(Type serviceType) =>
		_services.TryGetValue(serviceType, out var value) ? value : null;
}
