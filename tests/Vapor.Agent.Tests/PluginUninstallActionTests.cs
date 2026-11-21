using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

public sealed class PluginUninstallActionTests
{
	[Fact]
	public void Metadata_DescribesUninstall()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		Assert.Equal("plugin_uninstall", action.Name);
		Assert.Equal("plugin_uninstall", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresPluginId()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("pluginId", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_FailsWhenPluginHostIsNotInitialized()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["pluginId"] = "vapor.absent" }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not initialized", result.Error);
	}

	[Fact]
	public void RetirePluginDirectory_IgnoresMissingDirectory()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		// Must not throw for a plugin that was never on disk.
		action.RetirePluginDirectory("vapor.never-installed");
	}

	[Fact]
	public async Task ExecuteAsync_UninstallSurvivesReadOnlyPluginsRoot()
	{
		if (!OperatingSystem.IsLinux())
		{
			return; // The read-only-root trick uses Unix file modes.
		}

		string root = PluginTestPackages.NewPluginsRoot("uninstall-readonly");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			string pluginDir = Path.Combine(root, PluginTestPackages.PluginId);
			// Make the plugins root read-only: the directory rename-aside fails and
			// the uninstall must still succeed (the unload happened; deletion of the
			// orphaned directory is deferred to the next restart/discovery pass).
			File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				var action = new PluginUninstallAction(root, manager, NullLogger.Instance);
				ActionResult result = await action.ExecuteAsync(
					new Dictionary<string, object?> { ["pluginId"] = PluginTestPackages.PluginId }, CancellationToken.None);

				Assert.True(result.Success, result.Error);
				Assert.Equal(true, result.Output!["removed"]);
				Assert.Empty(manager.LoadedPlugins);
				Assert.True(Directory.Exists(pluginDir), "read-only root keeps the directory in place");
			}
			finally
			{
				File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_UninstallingUnknownPluginIsIdempotentSuccess()
	{
		string root = PluginTestPackages.NewPluginsRoot("uninstall-idempotent");
		try
		{
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginUninstallAction(root, manager, NullLogger.Instance);

			ActionResult result = await action.ExecuteAsync(
				new Dictionary<string, object?> { ["pluginId"] = "vapor.absent" }, CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(false, result.Output!["removed"]);
			Assert.NotNull(result.Output["plugins"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_UnloadsAndRetiresPluginDirectory()
	{
		string root = PluginTestPackages.NewPluginsRoot("uninstall");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			var action = new PluginUninstallAction(root, manager, NullLogger.Instance);
			ActionResult result = await action.ExecuteAsync(
				new Dictionary<string, object?> { ["pluginId"] = PluginTestPackages.PluginId }, CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(true, result.Output!["removed"]);
			Assert.Empty(manager.LoadedPlugins);
			Assert.False(Directory.Exists(Path.Combine(root, PluginTestPackages.PluginId)));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_DeleteFailureKeepsTheRetiredDirectory()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // The deferred-deletion trick uses Unix file modes.
		}

		string root = PluginTestPackages.NewPluginsRoot("uninstall-defer");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			string pluginDir = Path.Combine(root, PluginTestPackages.PluginId);
			// A read-only plugin directory renames aside but cannot be deleted
			// recursively; the unload must still succeed and the leftover must
			// stay out of discovery's sight.
			File.SetUnixFileMode(pluginDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				var action = new PluginUninstallAction(root, manager, NullLogger.Instance);
				ActionResult result = await action.ExecuteAsync(
					new Dictionary<string, object?> { ["pluginId"] = PluginTestPackages.PluginId }, CancellationToken.None);

				Assert.True(result.Success, result.Error);
				Assert.Equal(true, result.Output!["removed"]);
				Assert.Empty(manager.LoadedPlugins);
				Assert.False(Directory.Exists(pluginDir), "the directory itself must move aside");
				Assert.Single(Directory.GetDirectories(root, "*.old-*"));
			}
			finally
			{
				foreach (string leftover in Directory.GetDirectories(root, "*.old-*"))
				{
					File.SetUnixFileMode(leftover, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}
