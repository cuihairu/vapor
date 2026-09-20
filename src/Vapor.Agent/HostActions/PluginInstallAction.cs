using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Agent;

/// <summary>
/// Host action "plugin_install": installs a plugin package (zip) named by URL and
/// mandatory SHA-256 checksum, validates it against the manifest and hot-loads it.
/// The output always carries the full installed-plugin list so the ControlPlane can
/// mirror this agent's inventory wholesale.
/// </summary>
public sealed class PluginInstallAction : IHostAction
{
	private readonly PluginPackageInstaller _installer;
	private readonly PluginManager? _manager;
	private readonly ILogger _logger;

	public PluginInstallAction(PluginPackageInstaller installer, PluginManager? manager, ILogger logger)
	{
		_installer = installer;
		_manager = manager;
		_logger = logger;
	}

	public string Name => "plugin_install";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Installs a plugin package from a URL after verifying its SHA-256 checksum and hot-loads it",
		RequiresLogin: false,
		TimeoutSeconds: 300
	);

	public async Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? url = PayloadReader.GetString(payload, "url");
		string? sha256 = PayloadReader.GetString(payload, "sha256") ?? PayloadReader.GetString(payload, "sha_256");
		string? pluginId = PayloadReader.GetString(payload, "pluginId") ?? PayloadReader.GetString(payload, "plugin_id");
		string? version = PayloadReader.GetString(payload, "version");

		if (string.IsNullOrWhiteSpace(url))
		{
			return new ActionResult(false, "payload field 'url' (package location) is required", null);
		}

		if (string.IsNullOrWhiteSpace(sha256))
		{
			return new ActionResult(false, "payload field 'sha256' (64-hex-digit package digest) is required", null);
		}

		if (_manager is null)
		{
			return new ActionResult(false, "plugin host is not initialized on this agent", null);
		}

		var result = await _installer.InstallAsync(url, sha256, pluginId, version, _manager, cancellationToken).ConfigureAwait(false);
		if (!result.Success)
		{
			_logger.LogWarning("plugin_install failed: {Error}", result.Error);
			return new ActionResult(false, result.Error, new Dictionary<string, object?>
			{
				["url"] = url,
				["plugins"] = PluginOutput.ListLoaded(_manager)
			});
		}

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["pluginId"] = result.PluginId,
			["version"] = result.Version,
			["replaced"] = result.Replaced,
			["actions"] = result.Actions,
			["plugins"] = PluginOutput.ListLoaded(_manager)
		});
	}
}
