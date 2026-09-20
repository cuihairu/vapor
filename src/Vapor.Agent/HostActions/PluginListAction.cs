using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Agent;

/// <summary>
/// Host action "plugin_list": reports the currently-loaded plugins (and the plugins
/// root) so the ControlPlane can rebuild its inventory mirror on demand.
/// </summary>
public sealed class PluginListAction : IHostAction
{
	private readonly string _pluginsRoot;
	private readonly PluginManager? _manager;

	public PluginListAction(string pluginsRoot, PluginManager? manager)
	{
		_pluginsRoot = pluginsRoot;
		_manager = manager;
	}

	public string Name => "plugin_list";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Lists the plugins currently loaded on this agent",
		RequiresLogin: false,
		TimeoutSeconds: 15
	);

	public Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var plugins = PluginOutput.ListLoaded(_manager);
		return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
		{
			["directory"] = _pluginsRoot,
			["count"] = plugins.Count,
			["plugins"] = plugins
		}));
	}
}
