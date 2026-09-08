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
	private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"Vapor.Plugins.Core",
		"Vapor.Steam.Core",
		"Vapor.Protocol"
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
