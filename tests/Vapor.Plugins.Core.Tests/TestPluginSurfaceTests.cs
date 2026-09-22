using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Xunit;
using TestPluginClass = Vapor.Plugins.TestPlugin.TestPlugin;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Direct exercises for the TestPlugin surface members that the loader/manager
/// tests only reach through PluginStaging. Staged copies ship without PDBs, so
/// coverlet cannot credit those executions against the source lines — direct
/// instantiation from the referenced assembly keeps the echo/command/marker
/// behavior both asserted and visible to coverage collection.
/// </summary>
public class TestPluginSurfaceTests
{
	[Fact]
	public void Plugin_DeclaresIdentity()
	{
		var plugin = new TestPluginClass();

		Assert.Equal("vapor.test-plugin", plugin.Info.Id);
		Assert.Equal("Vapor Test Plugin", plugin.Info.Name);
	}

	[Fact]
	public async Task EchoAction_ReflectsPayloadSizeAndPluginId()
	{
		IAction action = new TestPluginClass().GetActions().Single();

		Assert.Equal("plugin_echo", action.Name);

		// The action only reads the payload; no session is required.
		var result = await action.ExecuteAsync(
			null!,
			new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["echoed"]);
		Assert.Equal("vapor.test-plugin", result.Output!["plugin"]);
	}

	[Fact]
	public async Task PingCommand_RespondsWithPong()
	{
		IPluginCommand command = new TestPluginClass().GetCommands().Single();

		Assert.Equal("plugin-ping", command.Name);
		Assert.Contains("pong", command.Description);

		var result = await command.ExecuteAsync(["ping"], CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("pong", result.Message);
	}

	[Fact]
	public async Task Lifecycle_WithMarkerDir_WritesMarkerFiles()
	{
		var plugin = new TestPluginClass();
		var markerDir = Path.Combine(Path.GetTempPath(), "vapor-testplugin-markers", Guid.NewGuid().ToString("N"));
		var context = new ConfiguredContext(new Dictionary<string, string> { ["markerDir"] = markerDir });

		await plugin.InitializeAsync(context, CancellationToken.None);
		Assert.Equal("vapor.test-plugin", await File.ReadAllTextAsync(Path.Combine(markerDir, "initialized.marker")));

		await plugin.ShutdownAsync(CancellationToken.None);
		Assert.Equal("vapor.test-plugin", await File.ReadAllTextAsync(Path.Combine(markerDir, "shutdown.marker")));
	}

	[Fact]
	public async Task Lifecycle_WithoutMarkerDir_IsANoOp()
	{
		// Both callbacks take the early return when no marker directory is configured.
		var plugin = new TestPluginClass();
		await plugin.InitializeAsync(new ConfiguredContext(new Dictionary<string, string>()), CancellationToken.None);
		await plugin.ShutdownAsync(CancellationToken.None);
	}

	private sealed class ConfiguredContext(IReadOnlyDictionary<string, string> configuration) : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			Id: "testplugin-surface-context",
			Name: "TestPlugin Surface Context",
			Version: new Version(1, 0, 0),
			ApiVersion: PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } = new HostServicesStub();
	}

	private sealed class HostServicesStub : IPluginHostServices
	{
		public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory =>
			Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

		public IServiceProvider Services { get; } = new ServicesStub();
	}

	private sealed class ServicesStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
