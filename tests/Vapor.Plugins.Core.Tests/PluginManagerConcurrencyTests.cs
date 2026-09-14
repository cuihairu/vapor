using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Concurrency behaviour of PluginManager: two loads of the same plugin id racing through
/// the duplicate-insertion guard. The slower load must lose cleanly — its instance is shut
/// down, its load context released and the caller sees a PluginException.
/// </summary>
public class PluginManagerConcurrencyTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "vapor-plugin-tests", Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task LoadAsync_ConcurrentSameId_LoserIsShutDownAndThrows()
	{
		var signalA = Path.Combine(_root, "signals-a");
		var signalB = Path.Combine(_root, "signals-b");
		Directory.CreateDirectory(signalB);
		// The second copy initializes immediately because its release marker already exists.
		File.WriteAllText(Path.Combine(signalB, "release.marker"), "pre-released");

		PluginStaging.StageFixturePlugin(
			_root,
			entryType: "Vapor.Plugins.TestFixtures.ParkingInitPlugin",
			pluginId: "vapor.race-dup",
			pluginDirName: "parking",
			configuration: new Dictionary<string, string> { ["signalDir"] = signalA });
		PluginStaging.StageFixturePlugin(
			_root,
			entryType: "Vapor.Plugins.TestFixtures.ParkingInitPlugin",
			pluginId: "vapor.race-dup",
			pluginDirName: "quick",
			configuration: new Dictionary<string, string> { ["signalDir"] = signalB });

		await using var manager = PluginStaging.CreateManager();
		var descriptors = manager.Discover(_root);
		Assert.Equal(2, descriptors.Count);

		// Launch the parking load first; it gets as far as InitializeAsync and parks.
		var parkedMarker = Path.Combine(signalA, "parked.marker");
		var parkingLoad = manager.LoadAsync(descriptors.Single(d => d.Directory.EndsWith("parking", StringComparison.Ordinal)));
		try
		{
			await WaitForFileAsync(parkedMarker);

			// While the first load is parked before the insertion point, the second load
			// passes the duplicate check, initializes and wins the dictionary slot.
			var winner = await manager.LoadAsync(descriptors.Single(d => d.Directory.EndsWith("quick", StringComparison.Ordinal)));
			Assert.Equal("vapor.race-dup", winner.Descriptor.Manifest.Id);
			Assert.Single(manager.LoadedPlugins);
			Assert.Null(winner.UnloadTracker);

			// Release the parked load: its TryAdd loses, so the manager must shut it down
			// (safety shutdown), release its load context and surface "already loaded".
			File.WriteAllText(Path.Combine(signalA, "release.marker"), "go");
			var ex = await Assert.ThrowsAsync<PluginException>(() => parkingLoad);
			Assert.Contains("already loaded", ex.Message);

			Assert.True(File.Exists(Path.Combine(signalA, "shutdown.marker")));
			Assert.Single(manager.LoadedPlugins);
		}
		finally
		{
			// Never leave a parked load hanging if an assertion above failed early.
			File.WriteAllText(Path.Combine(signalA, "release.marker"), "go");
			try
			{
				await parkingLoad.WaitAsync(TimeSpan.FromSeconds(5));
			}
			catch (Exception)
			{
				// Expected outcome (PluginException) or the timeout of an already-failed test.
			}
		}
	}

	private static async Task WaitForFileAsync(string path, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (Environment.TickCount64 < deadline)
		{
			if (File.Exists(path))
			{
				return;
			}

			await Task.Delay(25);
		}

		Assert.Fail($"Timed out waiting for {path}");
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
