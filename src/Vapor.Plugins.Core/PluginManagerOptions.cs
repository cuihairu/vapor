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
