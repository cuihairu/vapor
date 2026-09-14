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

	public static string FixtureAssemblyPath =>
		Path.Combine(AppContext.BaseDirectory, "Vapor.Plugins.TestFixtures.dll");

	/// <summary>
	/// Stages the broken/multi-plugin fixture assembly with a manifest pinning a specific
	/// entryType (the assembly intentionally has several IPlugin implementations, so a
	/// scan without entryType always fails and cannot be used for successful loads).
	/// </summary>
	public static string StageFixturePlugin(
		string rootPath,
		string entryType,
		string pluginId = "vapor.fixture",
		string? pluginDirName = null,
		IReadOnlyList<string>? permissions = null,
		IReadOnlyDictionary<string, string>? configuration = null)
	{
		var pluginDir = Path.Combine(rootPath, pluginDirName ?? pluginId);
		Directory.CreateDirectory(pluginDir);
		File.Copy(FixtureAssemblyPath, Path.Combine(pluginDir, "Vapor.Plugins.TestFixtures.dll"), overwrite: true);

		var manifest = new Dictionary<string, object?>
		{
			["id"] = pluginId,
			["name"] = "Fixture Plugin",
			["version"] = "1.0.0",
			["apiVersion"] = "1.0",
			["description"] = "staged plugin fixture",
			["entryAssembly"] = "Vapor.Plugins.TestFixtures.dll",
			["entryType"] = entryType,
			["permissions"] = permissions,
			["trust"] = null,
			["configuration"] = configuration
		};

		File.WriteAllText(
			Path.Combine(pluginDir, PluginManifest.ManifestFileName),
			JsonSerializer.Serialize(manifest));

		return pluginDir;
	}

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
