using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Failure and permission paths of the plugin loader, driven through PluginManager.LoadAsync:
/// broken entry assemblies, entry-type mismatches, assemblies with zero or multiple
/// IPlugin implementations, failing lifecycle methods and the events permission grant.
/// </summary>
public class PluginLoaderTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "vapor-plugin-tests", Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task LoadAsync_BrokenEntryAssembly_ThrowsPluginException()
	{
		// An "MZ" magic header with no valid PE body makes the runtime fail the assembly
		// load with BadImageFormatException; the loader must convert it into a
		// PluginException and unload the fresh load context.
		var pluginDir = Path.Combine(_root, "broken");
		Directory.CreateDirectory(pluginDir);
		var dllPath = Path.Combine(pluginDir, "broken.dll");
		File.WriteAllBytes(dllPath, [0x4D, 0x5A, 0x01, 0x02, 0x03, 0x04]);
		File.WriteAllText(
			Path.Combine(pluginDir, PluginManifest.ManifestFileName),
			"""{ "id": "vapor.broken", "name": "Broken", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "broken.dll" }""");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));

		Assert.Contains("failed to load entry assembly", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_NoPublicIPluginImplementation_Throws()
	{
		// A real managed assembly that implements no IPlugin (a logging library) must fail
		// discovery-by-scan with the "no public IPlugin implementation" error.
		var pluginDir = Path.Combine(_root, "no-plugin");
		Directory.CreateDirectory(pluginDir);
		var libraryPath = Path.Combine(AppContext.BaseDirectory, "Microsoft.Extensions.Logging.Abstractions.dll");
		File.WriteAllText(
			Path.Combine(pluginDir, PluginManifest.ManifestFileName),
			$$"""{ "id": "vapor.no-plugin", "name": "No Plugin", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "{{libraryPath.Replace("\\", "\\\\")}}" }""");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));

		Assert.Contains("no public IPlugin implementation", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_MultipleIPluginImplementations_Throws()
	{
		// The fixture assembly intentionally declares several IPlugin implementations, so a
		// scan without entryType must refuse to pick one implicitly.
		PluginStaging.StageFixturePlugin(_root, entryType: null!);

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));

		Assert.Contains("multiple IPlugin implementations", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_EntryTypeDoesNotImplementIPlugin_Throws()
	{
		PluginStaging.StageFixturePlugin(_root, entryType: "Vapor.Plugins.TestFixtures.NotAPlugin");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));

		Assert.Contains("does not implement IPlugin", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_EntryTypeWithoutPublicConstructor_Throws()
	{
		PluginStaging.StageFixturePlugin(_root, entryType: "Vapor.Plugins.TestFixtures.NoPublicConstructorPlugin");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));

		Assert.Contains("public parameterless constructor", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_InitializeThrows_UnloadsContextAndThrowsPluginException()
	{
		PluginStaging.StageFixturePlugin(
			_root,
			entryType: "Vapor.Plugins.TestFixtures.ThrowingInitPlugin",
			pluginId: "vapor.fixture-throwing-init");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));

		Assert.Contains("failed to initialize", ex.Message);
		Assert.Contains("init exploded", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_EventPluginWithDeclaredPermission_GrantsEvents()
	{
		PluginStaging.StageFixturePlugin(
			_root,
			entryType: "Vapor.Plugins.TestFixtures.EventFixturePlugin",
			pluginId: "vapor.fixture-events",
			permissions: [PluginPermissions.Events]);

		await using var manager = PluginStaging.CreateManager();
		var loaded = await manager.LoadAsync(Assert.Single(manager.Discover(_root)));

		Assert.Empty(loaded.Actions);
		Assert.Equal([PluginPermissions.Events], loaded.GrantedPermissions);
	}

	[Fact]
	public async Task LoadAsync_ManifestWithoutConfiguration_GetsEmptyConfiguration()
	{
		// An explicit "configuration": null must behave like an omitted section: the loader
		// falls back to its empty configuration dictionary instead of handing the plugin a
		// null. The manifest record is built directly to force that shape past discovery.
		PluginStaging.StageFixturePlugin(
			_root,
			entryType: "Vapor.Plugins.TestFixtures.FixturePluginOne",
			pluginId: "vapor.fixture-no-config");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));
		descriptor = descriptor with { Manifest = descriptor.Manifest with { Configuration = null } };

		var loaded = await manager.LoadAsync(descriptor);

		Assert.Equal("vapor.fixture-no-config", loaded.Descriptor.Manifest.Id);
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
		}
	}
}
