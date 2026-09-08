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
}
