using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vapor.Plugins.Core;

/// <summary>
/// Orchestrates the plugin lifecycle: discovery, compatibility checks, isolated loading,
/// and unloading.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
	private readonly ConcurrentDictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
	private readonly IPluginHostServices _hostServices;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<PluginManager> _logger;
	private readonly PluginManagerOptions _options;
	private readonly PluginDiscovery _discovery;
	private bool _disposed;

	public PluginManager(
		IPluginHostServices hostServices,
		ILoggerFactory loggerFactory,
		PluginManagerOptions? options = null)
	{
		_hostServices = hostServices ?? throw new ArgumentNullException(nameof(hostServices));
		_loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		_logger = loggerFactory.CreateLogger<PluginManager>();
		_options = options ?? new PluginManagerOptions();
		_discovery = new PluginDiscovery(loggerFactory.CreateLogger<PluginDiscovery>());
	}

	/// <summary>Currently loaded plugins.</summary>
	public IReadOnlyList<LoadedPlugin> LoadedPlugins => _loaded.Values.ToArray();

	/// <summary>Raised after a plugin has been loaded and initialized.</summary>
	public event EventHandler<PluginLoadedEventArgs>? PluginLoaded;

	/// <summary>
	/// Raised before a plugin is shut down and unloaded. Hosts should remove any registered
	/// contributions (actions, routes) here.
	/// </summary>
	public event EventHandler<PluginLoadedEventArgs>? PluginUnloading;

	/// <summary>Scans a directory for plugins without loading them.</summary>
	public IReadOnlyList<PluginDescriptor> Discover(string pluginsDirectory, IList<string>? errors = null)
	{
		return _discovery.Discover(pluginsDirectory, errors);
	}

	/// <summary>
	/// Discovers and loads all compatible plugins under <paramref name="pluginsDirectory"/>.
	/// </summary>
	public async Task<PluginLoadReport> LoadAllAsync(string pluginsDirectory, CancellationToken cancellationToken = default)
	{
		var errors = new List<string>();
		var descriptors = Discover(pluginsDirectory, errors);
		var loaded = new List<LoadedPlugin>();
		var failures = new List<string>(errors);

		foreach (var descriptor in descriptors)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				loaded.Add(await LoadAsync(descriptor, cancellationToken).ConfigureAwait(false));
			}
			catch (PluginException ex) when (!_options.ThrowOnLoadFailure)
			{
				_logger.LogError(ex, "Failed to load plugin {PluginId}", descriptor.Manifest.Id);
				failures.Add(ex.Message);
			}
		}

		return new PluginLoadReport(loaded, failures);
	}

	/// <summary>
	/// Loads a single discovered plugin. Throws <see cref="PluginException"/> when the
	/// plugin is incompatible, already loaded, or fails to initialize.
	/// </summary>
	public async Task<LoadedPlugin> LoadAsync(PluginDescriptor descriptor, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(descriptor);

		var info = descriptor.Info;

		if (!PluginApi.IsCompatible(info.ApiVersion, out var reason))
		{
			throw new PluginException($"Plugin '{info.Id}' is not compatible with this host: {reason}");
		}

		if (descriptor.Trust < _options.MinimumTrust)
		{
			throw new PluginException(
				$"Plugin '{info.Id}' trust level '{descriptor.Trust}' is below the host minimum '{_options.MinimumTrust}'");
		}

		if (_loaded.ContainsKey(info.Id))
		{
			throw new PluginException($"Plugin '{info.Id}' is already loaded");
		}

		var plugin = await PluginLoader.LoadAsync(descriptor, _hostServices, _logger, _options, cancellationToken).ConfigureAwait(false);

		if (!_loaded.TryAdd(info.Id, plugin))
		{
			await SafeShutdownAsync(plugin, cancellationToken).ConfigureAwait(false);
			plugin.MarkUnloaded();
			throw new PluginException($"Plugin '{info.Id}' is already loaded");
		}

		try
		{
			PluginLoaded?.Invoke(this, new PluginLoadedEventArgs(plugin));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "PluginLoaded handler for {PluginId} threw", info.Id);
		}

		return plugin;
	}

	/// <summary>
	/// Shuts down and unloads a loaded plugin. Returns false when no plugin with the given
	/// id is loaded. After this returns, <see cref="LoadedPlugin.UnloadTracker"/> can be
	/// used to verify the load context is collected.
	/// </summary>
	public async Task<bool> UnloadAsync(string pluginId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

		if (!_loaded.TryRemove(pluginId, out var plugin))
		{
			return false;
		}

		_logger.LogInformation("Unloading plugin {PluginId}", pluginId);

		try
		{
			PluginUnloading?.Invoke(this, new PluginLoadedEventArgs(plugin));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "PluginUnloading handler for {PluginId} threw", pluginId);
		}

		await SafeShutdownAsync(plugin, cancellationToken).ConfigureAwait(false);

		plugin.MarkUnloaded();

		_logger.LogInformation("Plugin {PluginId} unloaded", pluginId);
		return true;
	}

	private async Task SafeShutdownAsync(LoadedPlugin plugin, CancellationToken cancellationToken)
	{
		try
		{
			await plugin.Instance.ShutdownAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Plugin {PluginId} threw during shutdown", plugin.Info.Id);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		foreach (var pluginId in _loaded.Keys)
		{
			try
			{
				await UnloadAsync(pluginId).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to unload plugin {PluginId} during disposal", pluginId);
			}
		}
	}
}
