using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Plugins.TestFixtures;
using Xunit;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Direct exercises for the fixture members that no loader/manager path reaches on its
/// own: unused lifecycle methods, the reflection-only constructor, and the early-return
/// branch of the parking init. Keeps Vapor.Plugins.TestFixtures fully covered without
/// contorting the loader tests that consume the fixtures for their real purpose.
/// </summary>
public class TestFixturesTests
{
	[Fact]
	public async Task FixturePluginTwo_ImplementsTheFullLifecycle()
	{
		var plugin = new FixturePluginTwo();

		Assert.Equal("vapor.fixture-two", plugin.Info.Id);
		Assert.Equal("Fixture Plugin Two", plugin.Info.Name);
		await plugin.InitializeAsync(EmptyContext(), CancellationToken.None);
		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public void NotAPlugin_ToString_SelfDescribes()
	{
		Assert.Equal("not a plugin", new NotAPlugin().ToString());
	}

	[Fact]
	public async Task NoPublicConstructorPlugin_IsConstructibleThroughNonPublicActivator()
	{
		// Mirrors how a loader would have to reach it — the constructor is deliberately internal.
		var plugin = (NoPublicConstructorPlugin)Activator.CreateInstance(
			typeof(NoPublicConstructorPlugin), nonPublic: true)!;

		Assert.Equal("vapor.fixture-no-ctor", plugin.Info.Id);
		await plugin.InitializeAsync(EmptyContext(), CancellationToken.None);
		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task ThrowingInitPlugin_ShutdownCompletesQuietly()
	{
		// InitializeAsync throws (the loader tests assert that failure path); the
		// shutdown counterpart is intentionally harmless.
		await new ThrowingInitPlugin().ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task EventFixturePlugin_AcceptsSessionEvents()
	{
		await new EventFixturePlugin().OnSessionEventAsync(
			new Vapor.Steam.Core.SessionEvent(Vapor.Steam.Core.SessionEventType.Connected, "alice"),
			CancellationToken.None);
	}

	[Fact]
	public async Task ParkingInitPlugin_WithoutSignalDir_InitializesImmediately()
	{
		// No "signalDir" in the configuration: InitializeAsync returns at once instead
		// of parking for a release marker.
		await new ParkingInitPlugin().InitializeAsync(EmptyContext(), CancellationToken.None);
	}

	private static IPluginContext EmptyContext() => new StubPluginContext();

	private sealed class StubPluginContext : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			Id: "stub-context",
			Name: "Stub Context",
			Version: new Version(1, 0, 0),
			ApiVersion: PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } =
			new Dictionary<string, string>();

		public IPluginHostServices Host { get; } = new StubHostServices();
	}

	private sealed class StubHostServices : IPluginHostServices
	{
		public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
		public IServiceProvider Services { get; } = new ServiceProviderStub();
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
