namespace Vapor.Plugins.Core;

/// <summary>
/// One entry of a manifest's optional <c>dependencies</c> list: the plugin id that must
/// be discovered and enabled first, plus an optional SemVer constraint on the API version
/// that dependency declares (same rule shape as the host API check: major must match,
/// the provider's minor must be at least the requested one).
/// </summary>
public sealed record PluginDependency
{
	/// <summary>Manifest id of the required plugin.</summary>
	public string? PluginId { get; init; }

	/// <summary>Optional API version the dependency must implement (e.g. "1.0").</summary>
	public string? ApiVersion { get; init; }
}
