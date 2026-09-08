using Microsoft.Extensions.Logging;

namespace Vapor.Plugins.Core;

/// <summary>
/// Stable identity/metadata of a plugin, as declared in its manifest.
/// </summary>
public sealed record PluginInfo(
	string Id,
	string Name,
	Version Version,
	Version ApiVersion,
	string? Description = null
);

/// <summary>
/// A Vapor plugin. Plugins are discovered via a plugin.json manifest, loaded into an
/// isolated collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and can
/// contribute actions, commands and web API routes through the capability interfaces in
/// <c>Vapor.Plugins.Core</c>.
/// </summary>
public interface IPlugin
{
	/// <summary>Identity and metadata of this plugin.</summary>
	PluginInfo Info { get; }

	/// <summary>
	/// Called once after the plugin assembly is loaded. Plugins should acquire resources
	/// and read their configuration here.
	/// </summary>
	Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

	/// <summary>
	/// Called before the plugin's assembly load context is unloaded. Plugins must release
	/// all resources and stop background work so the context can be collected.
	/// </summary>
	Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Initialization context handed to a plugin. Exposes the plugin's own info, its manifest
/// configuration, and host services.
/// </summary>
public interface IPluginContext
{
	PluginInfo Info { get; }

	/// <summary>Configuration values declared in the plugin manifest.</summary>
	IReadOnlyDictionary<string, string> Configuration { get; }

	IPluginHostServices Host { get; }
}

/// <summary>
/// Services the host exposes to plugins.
/// </summary>
public interface IPluginHostServices
{
	ILoggerFactory LoggerFactory { get; }

	/// <summary>The host service provider (e.g. the Agent's DI container).</summary>
	IServiceProvider Services { get; }
}
