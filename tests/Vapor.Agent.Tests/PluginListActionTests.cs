using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

public sealed class PluginListActionTests
{
	[Fact]
	public void Metadata_DescribesList()
	{
		var action = new PluginListAction("/tmp/plugins", null);

		Assert.Equal("plugin_list", action.Name);
		Assert.Equal("plugin_list", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_ReportsEmptyInventoryAndDirectory()
	{
		string root = PluginTestPackages.NewPluginsRoot("list-empty");
		try
		{
			var action = new PluginListAction(root, null);

			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(root, result.Output!["directory"]);
			Assert.Equal(0, result.Output["count"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_ListsLoadedPluginsWithMetadata()
	{
		string root = PluginTestPackages.NewPluginsRoot("list-loaded");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			string manifestWithResources = JsonSerializer.Serialize(new Dictionary<string, object?>
			{
				["id"] = PluginTestPackages.PluginId,
				["name"] = "Vapor Test Plugin",
				["version"] = PluginTestPackages.PluginVersion,
				["apiVersion"] = "1.0",
				["entryAssembly"] = "Vapor.Plugins.TestPlugin.dll",
				["entryType"] = "Vapor.Plugins.TestPlugin.TestPlugin",
				["permissions"] = PluginPermissions.All,
				["resources"] = new[] { "Network", "network", "FileSystem" }
			});
			(string url, string sha) = PluginTestPackages.StageAsFile(root, manifestWithResources);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			var action = new PluginListAction(root, manager);
			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(1, result.Output!["count"]);
			var plugins = Assert.IsType<List<object>>(result.Output["plugins"]);
			var entry = Assert.IsType<Dictionary<string, object?>>(plugins.Single());
			Assert.Equal(PluginTestPackages.PluginId, entry["id"]);
			Assert.Equal(PluginTestPackages.PluginVersion, entry["version"]);
			Assert.Equal("unknown", entry["trust"]);
			Assert.Equal(new[] { "network", "filesystem" }, entry["resources"]);
			Assert.NotNull(entry["actions"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}
