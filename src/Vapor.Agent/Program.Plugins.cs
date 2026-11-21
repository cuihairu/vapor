using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Utilities;

internal partial class Program
{
	static async Task<(PluginManager Manager, PluginEventDispatcher Events, string PluginsDirectory)> LoadPluginsAsync(IServiceProvider services, IActionRegistry actionRegistry, ILogger logger)
	{
		var pluginsDir = Environment.GetEnvironmentVariable("VAPOR_PLUGINS_DIR");
		if (string.IsNullOrWhiteSpace(pluginsDir))
		{
			pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
		}

		// The plugin host is always up so runtime installs (plugin_install) have a
		// place to land even on an agent that started with no plugins directory.
		Directory.CreateDirectory(pluginsDir);

		var loggerFactory = services.GetRequiredService<ILoggerFactory>();
		var eventDispatcher = new PluginEventDispatcher(loggerFactory);
		eventDispatcher.Start(services.GetRequiredService<ISessionManager>());

		// CA2000 suppressed: ownership of the manager transfers to the caller along with
		// eventDispatcher; it is disposed there, not in this scope.
#pragma warning disable CA2000
		var manager = new PluginManager(
			new DefaultPluginHostServices(loggerFactory, services),
			loggerFactory);

		manager.PluginLoaded += (_, e) =>
		{
			foreach (var action in e.Plugin.Actions)
			{
				actionRegistry.Register(action);
			}

			if (e.Plugin.Instance is IEventPlugin eventPlugin && e.Plugin.GrantedPermissions.Contains(PluginPermissions.Events))
			{
				eventDispatcher.Add(eventPlugin);
			}
		};
		manager.PluginUnloading += (_, e) =>
		{
			foreach (var action in e.Plugin.Actions)
			{
				actionRegistry.Unregister(action.Name);
			}

			if (e.Plugin.Instance is IEventPlugin eventPlugin && e.Plugin.GrantedPermissions.Contains(PluginPermissions.Events))
			{
				eventDispatcher.Remove(eventPlugin);
			}
		};

		var report = await manager.LoadAllAsync(pluginsDir);
		foreach (var failure in report.Failures)
		{
			logger.LogWarning("Plugin load failure: {Failure}", SensitiveDataRedactor.Redact(failure));
		}

		logger.LogInformation(
			"Plugins loaded: {LoadedCount}, failures: {FailureCount}, event subscribers: {EventSubscriberCount}",
			report.Loaded.Count,
			report.Failures.Count,
			eventDispatcher.SubscriberCount);
		return (manager, eventDispatcher, pluginsDir);
#pragma warning restore CA2000
	}
}
