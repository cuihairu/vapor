namespace Vapor.Plugins.Core;

/// <summary>
/// A discovered plugin: its manifest plus resolved on-disk locations.
/// </summary>
public sealed record PluginDescriptor(
	PluginManifest Manifest,
	string Directory,
	string ManifestPath,
	string EntryAssemblyPath
)
{
	/// <summary>The plugin identity declared by the manifest.</summary>
	public PluginInfo Info => new(
		Id: Manifest.Id,
		Name: Manifest.Name,
		Version: PluginApi.TryParseVersion(Manifest.Version, out var v) ? v : new Version(0, 0),
		ApiVersion: PluginApi.TryParseVersion(Manifest.ApiVersion, out var a) ? a : new Version(0, 0),
		Description: Manifest.Description
	);

	/// <summary>Declared trust level (<see cref="PluginTrust.Unknown"/> when not declared).</summary>
	public PluginTrust Trust => Manifest.Trust switch
	{
		"community" => PluginTrust.Community,
		"official" => PluginTrust.Official,
		_ => PluginTrust.Unknown
	};

	/// <summary>Declared permission names (empty when not declared).</summary>
	public IReadOnlyList<string> Permissions => Manifest.Permissions ?? [];
}
