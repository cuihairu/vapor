using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Vapor.Plugins.Core;

/// <summary>
/// Loads a discovered plugin into an isolated collectible load context and initializes it.
/// </summary>
internal static class PluginLoader
{
	private static readonly IReadOnlyDictionary<string, string> EmptyConfiguration =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	public static async Task<LoadedPlugin> LoadAsync(
		PluginDescriptor descriptor,
		IPluginHostServices hostServices,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var info = descriptor.Info;
		logger.LogInformation(
			"Loading plugin {PluginId} v{PluginVersion} (API {ApiVersion}) from {PluginDirectory}",
			info.Id, info.Version, info.ApiVersion, descriptor.Directory);

		var loadContext = new PluginLoadContext(info.Id, descriptor.EntryAssemblyPath);

		Assembly entryAssembly;
		try
		{
			entryAssembly = loadContext.LoadFromAssemblyPath(descriptor.EntryAssemblyPath);
		}
		catch (Exception ex) when (ex is FileLoadException or BadImageFormatException)
		{
			loadContext.Unload();
			throw new PluginException($"Plugin '{info.Id}': failed to load entry assembly '{manifestEntry(descriptor)}': {ex.Message}", ex);
		}

		IPlugin instance;
		try
		{
			instance = CreatePluginInstance(entryAssembly, descriptor);
		}
		catch
		{
			loadContext.Unload();
			throw;
		}

		var context = new DefaultPluginContext(info, descriptor.Manifest.Configuration ?? EmptyConfiguration, hostServices);
		try
		{
			await instance.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			loadContext.Unload();
			throw new PluginException($"Plugin '{info.Id}' failed to initialize: {ex.Message}", ex);
		}

		var actions = instance is IActionPlugin actionPlugin ? actionPlugin.GetActions().ToArray() : [];
		var commands = instance is ICommandPlugin commandPlugin ? commandPlugin.GetCommands().ToArray() : [];
		var routes = instance is IWebApiPlugin webApiPlugin ? webApiPlugin.GetRoutes().ToArray() : [];

		logger.LogInformation(
			"Plugin {PluginId} loaded: {ActionCount} action(s), {CommandCount} command(s), {RouteCount} route(s)",
			info.Id, actions.Length, commands.Length, routes.Length);

		return new LoadedPlugin(descriptor, instance, loadContext, actions, commands, routes);
	}

	private static string manifestEntry(PluginDescriptor descriptor) => descriptor.Manifest.EntryAssembly;

	private static IPlugin CreatePluginInstance(Assembly entryAssembly, PluginDescriptor descriptor)
	{
		var info = descriptor.Info;

		if (!string.IsNullOrWhiteSpace(descriptor.Manifest.EntryType))
		{
			var pluginType = entryAssembly.GetType(descriptor.Manifest.EntryType, throwOnError: false);
			if (pluginType is null)
			{
				throw new PluginException(
					$"Plugin '{info.Id}': entry type '{descriptor.Manifest.EntryType}' not found in {descriptor.Manifest.EntryAssembly}");
			}

			return Instantiate(pluginType, info);
		}

		var pluginTypes = entryAssembly.GetTypes()
			.Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.IsPublic)
			.ToArray();

		return pluginTypes switch
		{
			{ Length: 0 } => throw new PluginException(
				$"Plugin '{info.Id}': no public IPlugin implementation found in {descriptor.Manifest.EntryAssembly}"),
			{ Length: > 1 } => throw new PluginException(
				$"Plugin '{info.Id}': multiple IPlugin implementations found; specify 'entryType' in the manifest"),
			_ => Instantiate(pluginTypes[0], info)
		};
	}

	private static IPlugin Instantiate(Type pluginType, PluginInfo info)
	{
		if (!typeof(IPlugin).IsAssignableFrom(pluginType))
		{
			throw new PluginException($"Plugin '{info.Id}': type '{pluginType.FullName}' does not implement IPlugin");
		}

		try
		{
			return (IPlugin)(Activator.CreateInstance(pluginType)
				?? throw new PluginException($"Plugin '{info.Id}': failed to create instance of '{pluginType.FullName}'"));
		}
		catch (MissingMethodException ex)
		{
			throw new PluginException(
				$"Plugin '{info.Id}': type '{pluginType.FullName}' must have a public parameterless constructor", ex);
		}
	}

	private sealed class DefaultPluginContext(
		PluginInfo info,
		IReadOnlyDictionary<string, string> configuration,
		IPluginHostServices host) : IPluginContext
	{
		public PluginInfo Info { get; } = info;

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } = host;
	}
}
