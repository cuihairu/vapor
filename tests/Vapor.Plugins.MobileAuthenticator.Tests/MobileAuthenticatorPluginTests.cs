using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Plugins.Core;
using Vapor.Plugins.MobileAuthenticator;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class MobileAuthenticatorPluginTests
{
	[Fact]
	public async Task Plugin_InitializeAndContributesFiveActions()
	{
		var plugin = new MobileAuthenticatorPlugin();

		Assert.Equal("vapor.mobile-authenticator", plugin.Info.Id);
		Assert.Equal(new Version(1, 0, 0), plugin.Info.Version);

		await plugin.InitializeAsync(new StubPluginContext(plugin.Info), CancellationToken.None);

		var actions = plugin.GetActions().ToList();

		Assert.Equal(5, actions.Count);
		Assert.Equal(
			new[] { "generate_totp", "generate_confirmation_hash", "sync_steam_time", "get_trade_confirmations", "respond_trade_confirmation" },
			actions.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase));

		var loggedInOnly = actions.Where(a => a.Metadata.RequiresLogin).Select(a => a.Name).ToList();
		Assert.Equal(new[] { "get_trade_confirmations", "respond_trade_confirmation" }, loggedInOnly.Order(StringComparer.OrdinalIgnoreCase));

		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public void Plugin_GetActionsBeforeInitialize_Throws()
	{
		var plugin = new MobileAuthenticatorPlugin();

		Assert.Throws<InvalidOperationException>(() => plugin.GetActions().ToList());
	}

	[Fact]
	public async Task Plugin_LoadsThroughPluginManager()
	{
		// End-to-end: stage the built plugin DLL and load it through Vapor.Plugins.Core.
		var root = Path.Combine(Path.GetTempPath(), "vapor-ma-plugin-tests", Guid.NewGuid().ToString("N"));
		var pluginDir = Path.Combine(root, "vapor.mobile-authenticator");
		Directory.CreateDirectory(pluginDir);

		foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Vapor.Plugins.MobileAuthenticator.dll"))
		{
			File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)));
		}

		File.Copy(
			Path.Combine(AppContext.BaseDirectory, "plugin.json"),
			Path.Combine(pluginDir, "plugin.json"));

		try
		{
			await using var manager = new PluginManager(
				new DefaultPluginHostServices(NullLoggerFactory.Instance, new StubServiceProvider()),
				NullLoggerFactory.Instance);

			var report = await manager.LoadAllAsync(root);

			Assert.Empty(report.Failures);
			var loaded = Assert.Single(report.Loaded);
			Assert.Equal("vapor.mobile-authenticator", loaded.Info.Id);
			Assert.Equal(5, loaded.Actions.Count);

			Assert.True(await manager.UnloadAsync(loaded.Info.Id));
		}
		finally
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
				// Best-effort cleanup; the load context may still hold the assembly.
			}
		}
	}

	private sealed class StubPluginContext(PluginInfo info) : IPluginContext
	{
		public PluginInfo Info { get; } = info;
		public IReadOnlyDictionary<string, string> Configuration { get; } = new Dictionary<string, string>();
		public IPluginHostServices Host { get; } = new DefaultPluginHostServices(NullLoggerFactory.Instance, new StubServiceProvider());
	}

	private sealed class StubServiceProvider : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
