using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Xunit;

namespace Vapor.Plugins.MarketWatch.Tests;

/// <summary>
/// Loads the real compiled MarketWatch plugin through the plugin host (discovery,
/// isolated load context, permission gating, action registration) — an end-to-end
/// exercise of the plugin API surface.
/// </summary>
public sealed class PluginHostLoadTests : IDisposable
{
	private readonly string _root;

	public PluginHostLoadTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "vapor-marketwatch-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "market-watch"));

		var binDirectory = AppContext.BaseDirectory;
		File.Copy(
			Path.Combine(binDirectory, "Vapor.Plugins.MarketWatch.dll"),
			Path.Combine(_root, "market-watch", "Vapor.Plugins.MarketWatch.dll"));
		File.Copy(
			Path.Combine(binDirectory, "plugin.json"),
			Path.Combine(_root, "market-watch", "plugin.json"));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
		}
	}

	[Fact]
	public async Task HostLoadsPlugin_WithDeclaredPermissionsAndActions()
	{
		var manager = new PluginManager(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance);

		await using (manager)
		{
			var report = await manager.LoadAllAsync(_root);

			Assert.Empty(report.Failures);
			var plugin = Assert.Single(report.Loaded);

			Assert.Equal("vapor.market-watch", plugin.Descriptor.Manifest.Id);
			Assert.Equal(PluginTrust.Official, plugin.Descriptor.Trust);
			Assert.Equal([PluginPermissions.Actions], plugin.GrantedPermissions);

			var actionNames = plugin.Actions.Select(static a => a.Name).Order().ToArray();
			Assert.Equal(["market_watch_add", "market_watch_list", "market_watch_remove"], actionNames);
		}
	}

	[Fact]
	public async Task HostRejectsPlugin_WhenTrustBelowPolicy()
	{
		var manager = new PluginManager(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance,
			new PluginManagerOptions { MinimumTrust = PluginTrust.Official, ThrowOnLoadFailure = true });

		// The staged manifest declares official trust, so it loads.
		var report = await manager.LoadAllAsync(_root);
		Assert.Single(report.Loaded);

		// A manifest with lower trust is refused before any plugin code runs.
		var restrictedRoot = Path.Combine(_root, "..", Path.GetFileName(_root) + "-restricted");
		Directory.CreateDirectory(Path.Combine(restrictedRoot, "market-watch"));
		File.Copy(
			Path.Combine(_root, "market-watch", "Vapor.Plugins.MarketWatch.dll"),
			Path.Combine(restrictedRoot, "market-watch", "Vapor.Plugins.MarketWatch.dll"));
		var manifest = File.ReadAllText(Path.Combine(_root, "market-watch", "plugin.json"))
			.Replace("\"trust\": \"official\"", "\"trust\": \"community\"");
		File.WriteAllText(Path.Combine(restrictedRoot, "market-watch", "plugin.json"), manifest);

		await using (var strictManager = new PluginManager(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance,
			new PluginManagerOptions { MinimumTrust = PluginTrust.Official }))
		{
			var strictReport = await strictManager.LoadAllAsync(restrictedRoot);

			Assert.Empty(strictReport.Loaded);
			var failure = Assert.Single(strictReport.Failures);
			Assert.Contains("below the host minimum", failure);
		}

		Directory.Delete(restrictedRoot, recursive: true);
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
