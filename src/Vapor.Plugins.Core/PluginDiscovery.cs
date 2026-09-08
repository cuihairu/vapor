using Microsoft.Extensions.Logging;

namespace Vapor.Plugins.Core;

/// <summary>
/// Discovers plugins on disk. Convention: the plugins root contains one subdirectory per
/// plugin, each holding a plugin.json manifest next to the plugin assemblies.
/// </summary>
public sealed class PluginDiscovery
{
	private readonly ILogger<PluginDiscovery>? _logger;

	public PluginDiscovery(ILogger<PluginDiscovery>? logger = null)
	{
		_logger = logger;
	}

	/// <summary>
	/// Scans <paramref name="pluginsDirectory"/> for plugin manifests. Invalid manifests are
	/// skipped and reported through <paramref name="errors"/>.
	/// </summary>
	public IReadOnlyList<PluginDescriptor> Discover(string pluginsDirectory, IList<string>? errors = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

		var results = new List<PluginDescriptor>();

		if (!System.IO.Directory.Exists(pluginsDirectory))
		{
			_logger?.LogWarning("Plugins directory {PluginsDirectory} does not exist; no plugins discovered", pluginsDirectory);
			return results;
		}

		foreach (var directory in System.IO.Directory.EnumerateDirectories(pluginsDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
		{
			var manifestPath = Path.Combine(directory, PluginManifest.ManifestFileName);
			if (!File.Exists(manifestPath))
			{
				continue;
			}

			try
			{
				var manifest = PluginManifest.Load(manifestPath);
				var entryAssemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly));

				if (!File.Exists(entryAssemblyPath))
				{
					throw new PluginException(
						$"Plugin '{manifest.Id}' entry assembly '{manifest.EntryAssembly}' not found in {directory}");
				}

				results.Add(new PluginDescriptor(manifest, directory, manifestPath, entryAssemblyPath));
			}
			catch (PluginException ex)
			{
				_logger?.LogWarning(ex, "Skipping invalid plugin in {PluginDirectory}", directory);
				errors?.Add(ex.Message);
			}
		}

		_logger?.LogInformation("Discovered {PluginCount} plugin(s) in {PluginsDirectory}", results.Count, pluginsDirectory);
		return results;
	}
}
