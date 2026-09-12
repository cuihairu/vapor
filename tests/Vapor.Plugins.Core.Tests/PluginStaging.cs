using Xunit;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Helpers to stage plugin directories on disk for discovery/loading tests.
/// </summary>
internal static class PluginStaging
{
	public static string TestPluginAssemblyPath =>
		Path.Combine(AppContext.BaseDirectory, "Vapor.Plugins.TestPlugin.dll");

	/// <summary>
	/// Creates a temp plugin root containing one plugin directory with the test plugin DLL
	/// and a plugin.json manifest.
	/// </summary>
	public static string StageTestPlugin(
		string rootPath,
		string pluginDirName = "test-plugin",
		string apiVersion = "1.0",
		string pluginId = "vapor.test-plugin",
		IReadOnlyDictionary<string, string>? configuration = null,
		string? entryType = null,
		IReadOnlyList<string>? permissions = null,
		string? trust = null)
	{
		var pluginDir = Path.Combine(rootPath, pluginDirName);
		Directory.CreateDirectory(pluginDir);
		File.Copy(TestPluginAssemblyPath, Path.Combine(pluginDir, "Vapor.Plugins.TestPlugin.dll"), overwrite: true);

		var manifest = new Dictionary<string, object?>
		{
			["id"] = pluginId,
			["name"] = "Vapor Test Plugin",
			["version"] = "1.0.0",
			["apiVersion"] = apiVersion,
			["description"] = "staged test plugin",
			["entryAssembly"] = "Vapor.Plugins.TestPlugin.dll",
			["entryType"] = entryType,
			// Default to declaring everything the test plugin implements so tests that do
			// not care about permissions keep their capabilities.
			["permissions"] = permissions ?? PluginPermissions.All,
			["trust"] = trust,
			["configuration"] = configuration
		};

		File.WriteAllText(
			Path.Combine(pluginDir, PluginManifest.ManifestFileName),
			JsonSerializer.Serialize(manifest));

		return pluginDir;
	}

	public static PluginManager CreateManager(PluginManagerOptions? options = null) =>
		new(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance,
			options);

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
