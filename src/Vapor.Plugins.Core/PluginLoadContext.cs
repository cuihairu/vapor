using System.Reflection;
using System.Runtime.Loader;

namespace Vapor.Plugins.Core;

/// <summary>
/// Collectible <see cref="AssemblyLoadContext"/> used to isolate a single plugin.
/// Dependencies are resolved via <see cref="AssemblyDependencyResolver"/> from the plugin
/// directory, except the Vapor contract assemblies which are always shared with the host
/// so plugin types remain assignment-compatible with host types.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
	/// <summary>
	/// Assemblies that must be shared between host and plugins (type-identity boundary).
	/// </summary>
	/// <remarks>
	/// <see cref="Api.IPluginHostServices"/> exposes <c>ILoggerFactory</c>, so
	/// Microsoft.Extensions.Logging.Abstractions is part of the contract surface too: when a
	/// published plugin directory carries its own copy, the plugin's view of that interface
	/// gets a second type identity and the JIT rejects the cross-context call with a
	/// MissingMethodException (<c>get_LoggerFactory</c>) even though reflection finds the
	/// member. It must bind to the host's copy like the Vapor contract assemblies do.
	/// </remarks>
	private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"Vapor.Plugins.Core",
		"Vapor.Steam.Core",
		"Vapor.Protocol",
		"Microsoft.Extensions.Logging.Abstractions"
	};

	private readonly AssemblyDependencyResolver _resolver;

	public PluginLoadContext(string pluginName, string entryAssemblyPath)
		: base($"vapor-plugin:{pluginName}", isCollectible: true)
	{
		_resolver = new AssemblyDependencyResolver(entryAssemblyPath);
	}

	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
		{
			// Fall back to the default (host) context to preserve type identity.
			return null;
		}

		var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
		return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
	}

	protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
	{
		var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
		return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
	}
}
