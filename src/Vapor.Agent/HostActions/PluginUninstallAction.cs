using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Agent;

/// <summary>
/// Host action "plugin_uninstall": unloads the plugin by id and removes its
/// directory. Idempotent — uninstalling a plugin that is not loaded succeeds with
/// removed=false. The output always carries the full installed-plugin list.
/// </summary>
public sealed class PluginUninstallAction : IHostAction
{
	private readonly string _pluginsRoot;
	private readonly PluginManager? _manager;
	private readonly ILogger _logger;

	public PluginUninstallAction(string pluginsRoot, PluginManager? manager, ILogger logger)
	{
		_pluginsRoot = pluginsRoot;
		_manager = manager;
		_logger = logger;
	}

	public string Name => "plugin_uninstall";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Unloads a plugin by id and removes its directory from the agent's plugins root",
		RequiresLogin: false,
		TimeoutSeconds: 60
	);

	public async Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? pluginId = PayloadReader.GetString(payload, "pluginId") ?? PayloadReader.GetString(payload, "plugin_id");
		if (string.IsNullOrWhiteSpace(pluginId))
		{
			return new ActionResult(false, "payload field 'pluginId' is required", null);
		}

		if (_manager is null)
		{
			return new ActionResult(false, "plugin host is not initialized on this agent", null);
		}

		bool removed = await _manager.UnloadAsync(pluginId, cancellationToken).ConfigureAwait(false);
		if (removed)
		{
			RetirePluginDirectory(pluginId);
			_logger.LogInformation("Plugin {PluginId} uninstalled", pluginId);
		}

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["pluginId"] = pluginId,
			["removed"] = removed,
			["plugins"] = PluginOutput.ListLoaded(_manager)
		});
	}

	/// <summary>
	/// Removes the plugin's directory after a successful unload. The live ALC is
	/// released by then, but the CLR may still hold file locks until collection
	/// (Windows); rename-aside keeps discovery from ever seeing the directory again
	/// and deletion is best-effort.
	/// </summary>
	internal void RetirePluginDirectory(string pluginId)
	{
		string directory = Path.Combine(_pluginsRoot, pluginId);
		if (!Directory.Exists(directory))
		{
			return;
		}

		string retired = directory + $".old-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
		try
		{
			Directory.Move(directory, retired);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not move plugin directory {Directory} aside after uninstall", directory);
			return;
		}

		try
		{
			Directory.Delete(retired, recursive: true);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Deferred cleanup of uninstalled plugin directory {Directory}", retired);
		}
	}
}
