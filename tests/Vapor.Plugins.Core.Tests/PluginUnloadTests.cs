using Xunit;
using System.Runtime.CompilerServices;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginUnloadTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "vapor-plugin-tests", Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task UnloadAsync_CallsShutdownAndRemovesPlugin()
	{
		var markerDir = Path.Combine(_root, "markers");
		PluginStaging.StageTestPlugin(_root, configuration: new Dictionary<string, string> { ["markerDir"] = markerDir });

		await using var manager = PluginStaging.CreateManager();
		var plugin = await manager.LoadAsync(Assert.Single(manager.Discover(_root)));

		var unloadingFired = false;
		manager.PluginUnloading += (_, e) =>
		{
			unloadingFired = true;
			Assert.Equal("vapor.test-plugin", e.Plugin.Info.Id);
		};

		var unloaded = await manager.UnloadAsync("vapor.test-plugin");

		Assert.True(unloaded);
		Assert.True(unloadingFired);
		Assert.Empty(manager.LoadedPlugins);
		Assert.True(File.Exists(Path.Combine(markerDir, "shutdown.marker")));
		Assert.NotNull(plugin.UnloadTracker);
	}

	[Fact]
	public async Task UnloadAsync_UnknownPlugin_ReturnsFalse()
	{
		await using var manager = PluginStaging.CreateManager();

		Assert.False(await manager.UnloadAsync("no-such-plugin"));
	}

	[Fact]
	public async Task UnloadAsync_LoadContextIsEventuallyCollected()
	{
		PluginStaging.StageTestPlugin(_root);

		var tracker = await LoadAndUnloadAsync(_root);

		for (var i = 0; i < 10 && tracker.IsAlive; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			await Task.Delay(50);
		}

		Assert.False(tracker.IsAlive, "plugin load context should be collected after unload");
	}

	[Fact]
	public async Task Plugin_CanBeReloadedAfterUnload()
	{
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		await manager.LoadAsync(descriptor);
		Assert.True(await manager.UnloadAsync("vapor.test-plugin"));

		var reloaded = await manager.LoadAsync(descriptor);
		Assert.Equal("vapor.test-plugin", reloaded.Info.Id);
		Assert.Single(manager.LoadedPlugins);
	}

	[Fact]
	public async Task DisposeAsync_UnloadsAllPlugins()
	{
		var markerDir = Path.Combine(_root, "markers");
		PluginStaging.StageTestPlugin(_root, configuration: new Dictionary<string, string> { ["markerDir"] = markerDir });

		var manager = PluginStaging.CreateManager();
		await manager.LoadAllAsync(_root);
		Assert.Single(manager.LoadedPlugins);

		await manager.DisposeAsync();

		Assert.Empty(manager.LoadedPlugins);
		Assert.True(File.Exists(Path.Combine(markerDir, "shutdown.marker")));
	}

	// Kept in a separate non-inlined method so no strong references to the plugin or its
	// load context survive on the caller's frame.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private static async Task<WeakReference> LoadAndUnloadAsync(string pluginsDir)
	{
		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(pluginsDir));
		var plugin = await manager.LoadAsync(descriptor);

		Assert.True(await manager.UnloadAsync(plugin.Info.Id));

		return plugin.UnloadTracker!;
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
		catch (IOException)
		{
			// Assemblies may still be locked by the load context; best-effort cleanup.
		}
	}
}
