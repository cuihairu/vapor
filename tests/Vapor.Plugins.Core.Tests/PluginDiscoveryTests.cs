using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginDiscoveryTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "vapor-plugin-tests", Guid.NewGuid().ToString("N"));

	[Fact]
	public void Discover_NonexistentDirectory_ReturnsEmpty()
	{
		var discovery = new PluginDiscovery();
		var results = discovery.Discover(Path.Combine(_root, "does-not-exist"));

		Assert.Empty(results);
	}

	[Fact]
	public void Discover_DirectoryWithoutManifests_ReturnsEmpty()
	{
		Directory.CreateDirectory(Path.Combine(_root, "not-a-plugin"));

		var discovery = new PluginDiscovery();
		var results = discovery.Discover(_root);

		Assert.Empty(results);
	}

	[Fact]
	public void Discover_StagedPlugin_ReturnsDescriptor()
	{
		PluginStaging.StageTestPlugin(_root);

		var discovery = new PluginDiscovery();
		var results = discovery.Discover(_root);

		var descriptor = Assert.Single(results);
		Assert.Equal("vapor.test-plugin", descriptor.Manifest.Id);
		Assert.True(File.Exists(descriptor.EntryAssemblyPath));
		Assert.Equal("vapor.test-plugin", descriptor.Info.Id);
		Assert.Equal(new Version(1, 0, 0), descriptor.Info.Version);
		Assert.Equal(new Version(1, 0), descriptor.Info.ApiVersion);
	}

	[Fact]
	public void Discover_InvalidManifest_IsSkippedAndReported()
	{
		var pluginDir = Path.Combine(_root, "broken");
		Directory.CreateDirectory(pluginDir);
		File.WriteAllText(Path.Combine(pluginDir, PluginManifest.ManifestFileName), "{ invalid");
		PluginStaging.StageTestPlugin(_root);

		var errors = new List<string>();
		var discovery = new PluginDiscovery();
		var results = discovery.Discover(_root, errors);

		Assert.Single(results);
		Assert.Single(errors);
	}

	[Fact]
	public void Discover_MissingEntryAssembly_IsSkippedAndReported()
	{
		var pluginDir = Path.Combine(_root, "no-assembly");
		Directory.CreateDirectory(pluginDir);
		File.WriteAllText(
			Path.Combine(pluginDir, PluginManifest.ManifestFileName),
			"""
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "Missing.dll" }
			""");

		var errors = new List<string>();
		var results = new PluginDiscovery().Discover(_root, errors);

		Assert.Empty(results);
		Assert.Single(errors);
		Assert.Contains("Missing.dll", errors[0]);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
			{
				Directory.Delete(_root, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Assemblies may still be locked by the load context; best-effort cleanup.
			// Windows raises UnauthorizedAccessException for directories with open files.
		}
	}
}
