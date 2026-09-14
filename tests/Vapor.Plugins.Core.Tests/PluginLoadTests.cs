using Xunit;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginLoadTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "vapor-plugin-tests", Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task LoadAllAsync_LoadsStagedPluginWithContributions()
	{
		var markerDir = Path.Combine(_root, "markers");
		PluginStaging.StageTestPlugin(_root, configuration: new Dictionary<string, string> { ["markerDir"] = markerDir });

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Failures);
		var plugin = Assert.Single(report.Loaded);
		Assert.Equal("vapor.test-plugin", plugin.Info.Id);
		Assert.Equal("Vapor Test Plugin", plugin.Info.Name);

		// Configuration reached the plugin (InitializeAsync wrote the marker file).
		Assert.True(File.Exists(Path.Combine(markerDir, "initialized.marker")));

		// Actions are contributed and share the host's IAction type identity.
		var action = Assert.Single(plugin.Actions);
		Assert.Equal("plugin_echo", action.Name);
		Assert.IsAssignableFrom<IAction>(action);

		var command = Assert.Single(plugin.Commands);
		Assert.Equal("plugin-ping", command.Name);

		var route = Assert.Single(plugin.Routes);
		Assert.Equal("GET", route.Method);
		Assert.Equal("/hello", route.Path);

		Assert.Single(manager.LoadedPlugins);
	}

	[Fact]
	public async Task ContributedAction_Executes()
	{
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);
		var action = Assert.Single(Assert.Single(report.Loaded).Actions);

		var result = await action.ExecuteAsync(
			session: null!,
			new Dictionary<string, object?> { ["k"] = "v" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal(1, result.Output["echoed"]);
		Assert.Equal("vapor.test-plugin", result.Output["plugin"]);
	}

	[Fact]
	public async Task ContributedCommand_Executes()
	{
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);
		var command = Assert.Single(Assert.Single(report.Loaded).Commands);

		Assert.False(string.IsNullOrWhiteSpace(command.Description));

		var result = await command.ExecuteAsync([], CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("pong", result.Message);
	}

	[Fact]
	public async Task ContributedRoute_Executes()
	{
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);
		var route = Assert.Single(Assert.Single(report.Loaded).Routes);

		var response = await route.Handler(
			new PluginWebRequest("/hello", new Dictionary<string, string>(), new Dictionary<string, string>(), null),
			CancellationToken.None);

		Assert.Equal(200, response.StatusCode);
		Assert.Contains("plugin", response.Body);
	}

	[Fact]
	public async Task LoadAsync_IncompatibleApiVersion_Throws()
	{
		PluginStaging.StageTestPlugin(_root, apiVersion: "99.0");

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));
		Assert.Contains("not compatible", ex.Message);
	}

	[Fact]
	public async Task LoadAllAsync_IncompatibleApiVersion_ReportedAsFailure()
	{
		PluginStaging.StageTestPlugin(_root, apiVersion: "99.0");

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Loaded);
		Assert.Single(report.Failures);
	}

	[Fact]
	public async Task LoadAsync_DuplicatePluginId_Throws()
	{
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		await manager.LoadAsync(descriptor);
		var ex = await Assert.ThrowsAsync<PluginException>(() => manager.LoadAsync(descriptor));
		Assert.Contains("already loaded", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_ExplicitEntryType_Loads()
	{
		PluginStaging.StageTestPlugin(_root, entryType: "Vapor.Plugins.TestPlugin.TestPlugin");

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);

		Assert.Single(report.Loaded);
		Assert.Empty(report.Failures);
	}

	[Fact]
	public async Task LoadAsync_UnknownEntryType_Fails()
	{
		PluginStaging.StageTestPlugin(_root, entryType: "Vapor.Plugins.TestPlugin.DoesNotExist");

		await using var manager = PluginStaging.CreateManager();
		var report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Loaded);
		Assert.Single(report.Failures);
		Assert.Contains("DoesNotExist", report.Failures[0]);
	}

	[Fact]
	public async Task PluginLoadedEvent_Fires()
	{
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		var fired = new List<string>();
		manager.PluginLoaded += (_, e) => fired.Add(e.Plugin.Info.Id);

		await manager.LoadAllAsync(_root);

		Assert.Equal(["vapor.test-plugin"], fired);
	}

	[Fact]
	public async Task PluginLoadedHandler_Throws_LoadStillSucceeds()
	{
		// Host notification handlers run inside the manager; a buggy handler must be logged
		// and swallowed so it cannot fail the plugin load itself.
		PluginStaging.StageTestPlugin(_root);

		await using var manager = PluginStaging.CreateManager();
		manager.PluginLoaded += (_, _) => throw new InvalidOperationException("handler boom");

		var report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Failures);
		var plugin = Assert.Single(report.Loaded);
		Assert.Single(manager.LoadedPlugins);
		Assert.Equal("vapor.test-plugin", plugin.Info.Id);
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
