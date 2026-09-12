namespace Vapor.Plugins.Core;

/// <summary>Options controlling <see cref="PluginManager"/> behavior.</summary>
public sealed record PluginManagerOptions
{
	/// <summary>
	/// When true, a failing plugin aborts <see cref="PluginManager.LoadAllAsync"/>. When
	/// false (default), failures are logged and collected into
	/// <see cref="PluginLoadReport.Failures"/> while other plugins continue loading.
	/// </summary>
	public bool ThrowOnLoadFailure { get; init; }

	/// <summary>
	/// Minimum trust level a plugin must declare to be loaded (default: <see cref="PluginTrust.Unknown"/>,
	/// i.e. no gate). Plugins below the minimum are refused before any plugin code runs.
	/// </summary>
	public PluginTrust MinimumTrust { get; init; } = PluginTrust.Unknown;

	/// <summary>
	/// When true, a plugin implementing a capability interface without declaring the matching
	/// permission fails to load. When false (default, minimal-trust), the undeclared capability
	/// is stripped — it is never registered — and a warning is logged.
	/// </summary>
	public bool RequirePermissionsDeclared { get; init; }
}

/// <summary>Outcome of a bulk plugin load.</summary>
public sealed record PluginLoadReport(
	IReadOnlyList<LoadedPlugin> Loaded,
	IReadOnlyList<string> Failures
);

public sealed class PluginLoadedEventArgs(LoadedPlugin plugin) : EventArgs
{
	public LoadedPlugin Plugin { get; } = plugin;
}
