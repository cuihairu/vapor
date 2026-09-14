using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Plugins.TestFixtures;

/// <summary>
/// Deliberately broken/multiple plugin fixtures used by Vapor.Plugins.Core.Tests to drive
/// PluginLoader and PluginManager failure paths. The assembly intentionally contains more
/// than one public IPlugin implementation, so discovery-by-scan always fails for it;
/// tests that need a specific fixture must pin it via the manifest's entryType.
/// </summary>

// Two public IPlugin implementations: scanning this assembly without an entryType hits the
// "multiple IPlugin implementations found" loader failure.
public sealed class FixturePluginOne : IPlugin
{
	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-one",
		Name: "Fixture Plugin One",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FixturePluginTwo : IPlugin
{
	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-two",
		Name: "Fixture Plugin Two",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Public type that does not implement IPlugin: an entryType pointing here exercises the
// "does not implement IPlugin" guard in the loader.
public sealed class NotAPlugin
{
	public override string ToString() => "not a plugin";
}

// IPlugin implementation without a public parameterless constructor: exercises the
// MissingMethodException guard in the loader.
public sealed class NoPublicConstructorPlugin : IPlugin
{
	internal NoPublicConstructorPlugin()
	{
	}

	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-no-ctor",
		Name: "Fixture Plugin Without Public Constructor",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// InitializeAsync always throws: exercises the loader's initialize-failure unload path.
public sealed class ThrowingInitPlugin : IPlugin
{
	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-throwing-init",
		Name: "Fixture Plugin That Throws During Init",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) =>
		throw new InvalidOperationException("init exploded");

	public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// ShutdownAsync always throws: exercises the manager's swallow-during-shutdown path.
public sealed class ThrowingShutdownPlugin : IPlugin
{
	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-throwing-shutdown",
		Name: "Fixture Plugin That Throws During Shutdown",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ShutdownAsync(CancellationToken cancellationToken) =>
		throw new InvalidOperationException("shutdown exploded");
}

// IEventPlugin implementation: lets tests verify the events permission is granted.
public sealed class EventFixturePlugin : IEventPlugin
{
	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-events",
		Name: "Fixture Event Plugin",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

// InitializeAsync parks until a "release.marker" file appears in the configured signalDir
// (or the token fires). Tests use this to hold one load in-flight while another load of
// the same plugin id races it through the manager's duplicate-insertion guard.
public sealed class ParkingInitPlugin : IPlugin
{
	private string? _signalDir;

	public PluginInfo Info { get; } = new(
		Id: "vapor.fixture-parking-init",
		Name: "Fixture Plugin That Parks During Init",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current);

	public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		if (!context.Configuration.TryGetValue("signalDir", out var dir))
		{
			return;
		}

		_signalDir = dir;
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "parked.marker"), Info.Id);

		// Park until the test drops a release marker; bail out on cancellation so a stuck
		// test cannot hang the whole run.
		while (!File.Exists(Path.Combine(dir, "release.marker")))
		{
			cancellationToken.ThrowIfCancellationRequested();
			await Task.Delay(25, cancellationToken);
		}
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		if (_signalDir is not null)
		{
			File.WriteAllText(Path.Combine(_signalDir, "shutdown.marker"), Info.Id);
		}

		return Task.CompletedTask;
	}
}
