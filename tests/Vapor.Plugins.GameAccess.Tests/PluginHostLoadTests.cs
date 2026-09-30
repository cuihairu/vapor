using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

/// <summary>
/// Loads the real compiled GameAccess plugin through the plugin host (discovery,
/// isolated load context, permission gating, action registration) — the split's
/// wire-compat proof: the fourteen formerly-host actions come back through a real
/// PluginManager load with unchanged names. Mirrors the MarketWatch precedent.
/// </summary>
public sealed class PluginHostLoadTests : IDisposable
{
	private readonly string _root;

	public PluginHostLoadTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "vapor-gameaccess-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "game-access"));

		var binDirectory = AppContext.BaseDirectory;
		File.Copy(
			Path.Combine(binDirectory, "Vapor.Plugins.GameAccess.dll"),
			Path.Combine(_root, "game-access", "Vapor.Plugins.GameAccess.dll"));
		File.Copy(
			Path.Combine(binDirectory, "plugin.json"),
			Path.Combine(_root, "game-access", "plugin.json"));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
		{
			// Best-effort cleanup; the load context may still hold the assembly.
		}
	}

	[Fact]
	public async Task HostLoadsPlugin_WithDeclaredPermissionsAndUnchangedActionNames()
	{
		var manager = new PluginManager(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance);

		await using (manager)
		{
			var report = await manager.LoadAllAsync(_root);

			Assert.Empty(report.Failures);
			var plugin = Assert.Single(report.Loaded);

			Assert.Equal("vapor.game-access", plugin.Descriptor.Manifest.Id);
			Assert.Equal(PluginTrust.Official, plugin.Descriptor.Trust);
			Assert.Equal([PluginPermissions.Actions], plugin.GrantedPermissions);

			string[] actionNames = plugin.Actions.Select(static a => a.Name).Order().ToArray();
			Assert.Equal(
			[
				"add_license",
				"claim_points_shop_items",
				"find_duplicates",
				"get_achievements",
				"get_card_drops",
				"get_inventory",
				"get_playtime",
				"get_points_shop_summary",
				"loot_inventory",
				"play_games",
				"redeem_key",
				"reset_achievements",
				"swap_duplicates",
				"unlock_achievements",
			], actionNames);
		}
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
