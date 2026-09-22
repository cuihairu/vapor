using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Vapor.Steam.Core;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Constructor guard clauses and null-logger / fallback arms that the
/// happy-path tests never touch, collected in one place.
/// </summary>
public sealed class GuardClauseTests
{
	[Fact]
	public void DefaultPluginHostServices_NullDependencies_ThrowWithParamName()
	{
		var services = new ServiceCollection().BuildServiceProvider();

		Assert.Equal("loggerFactory", Assert.Throws<ArgumentNullException>(
			() => new DefaultPluginHostServices(null!, services)).ParamName);
		Assert.Equal("services", Assert.Throws<ArgumentNullException>(
			() => new DefaultPluginHostServices(NullLoggerFactory.Instance, null!)).ParamName);
	}

	[Fact]
	public void PluginManager_NullDependencies_ThrowWithParamName()
	{
		var hostServices = new DefaultPluginHostServices(
			NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

		Assert.Equal("hostServices", Assert.Throws<ArgumentNullException>(
			() => new PluginManager(null!, NullLoggerFactory.Instance)).ParamName);
		Assert.Equal("loggerFactory", Assert.Throws<ArgumentNullException>(
			() => new PluginManager(hostServices, null!)).ParamName);
	}

	[Fact]
	public void PluginEventDispatcher_NullLoggerFactory_ThrowsWithParamName()
	{
		Assert.Equal("loggerFactory", Assert.Throws<ArgumentNullException>(
			() => new PluginEventDispatcher(null!)).ParamName);
	}

	[Fact]
	public async Task PluginEventDispatcher_DoubleAdd_SameInstance_IgnoresSecond()
	{
		await using var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);
		var plugin = new StubEventPlugin();

		dispatcher.Add(plugin);
		dispatcher.Add(plugin);

		Assert.Equal(1, dispatcher.SubscriberCount);
		await dispatcher.DisposeAsync();
	}

	[Fact]
	public void Descriptor_UnparseableVersions_FallBackToZero()
	{
		var manifest = new PluginManifest
		{
			Id = "p",
			Name = "P",
			Version = "not-a-version",
			ApiVersion = "also-junk",
			EntryAssembly = "p.dll"
		};

		var descriptor = new PluginDescriptor(manifest, "/plugins/p", "/plugins/p/plugin.json", "/plugins/p/p.dll");

		Assert.Equal(new Version(0, 0), descriptor.Info.Version);
		Assert.Equal(new Version(0, 0), descriptor.Info.ApiVersion);
	}

	[Fact]
	public void Discover_WithLogger_LogsMissingDirectoryAndBrokenPlugin()
	{
		// The ?. arms: the warning paths with a real logger attached. The broken
		// plugin carries a valid manifest whose entry assembly does not exist.
		string root = Path.Combine(Path.GetTempPath(), "vapor-plugin-guards", Guid.NewGuid().ToString("N"));
		var pluginDir = Path.Combine(root, "broken");
		Directory.CreateDirectory(pluginDir);
		File.WriteAllText(Path.Combine(pluginDir, PluginManifest.ManifestFileName), """
			{ "id": "p", "name": "P", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "missing.dll" }
			""");

		var discovery = new PluginDiscovery(NullLogger<PluginDiscovery>.Instance);
		var errors = new List<string>();
		Assert.Empty(discovery.Discover(Path.Combine(root, "no-such-dir")));
		Assert.Empty(discovery.Discover(root, errors));

		Assert.Single(errors);
		Directory.Delete(root, recursive: true);
	}

	private sealed class StubEventPlugin : IEventPlugin
	{
		public PluginInfo Info { get; } = new(
			Id: "stub-event",
			Name: "Stub Event",
			Version: new Version(1, 0),
			ApiVersion: new Version(1, 0),
			Description: null);

		public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}
}
