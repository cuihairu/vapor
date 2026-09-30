using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

public sealed class GameAccessPluginTests
{
	[Fact]
	public async Task InitializeAsync_BuildsAllFourteenActions_WithoutHostLimiter()
	{
		GameAccessPlugin plugin = new();
		await plugin.InitializeAsync(CreateContext(new ServiceProviderStub()), CancellationToken.None);

		Assert.Equal("vapor.game-access", plugin.Info.Id);

		string[] names = plugin.GetActions().Select(static a => a.Name).ToArray();
		Assert.Equal(
		[
			"play_games",
			"redeem_key",
			"get_inventory",
			"get_achievements",
			"unlock_achievements",
			"reset_achievements",
			"get_card_drops",
			"get_playtime",
			"loot_inventory",
			"find_duplicates",
			"swap_duplicates",
			"add_license",
			"get_points_shop_summary",
			"claim_points_shop_items",
		], names);
	}

	[Fact]
	public async Task InitializeAsync_WithHostCacheAndLimiter_WiresSharedInstances()
	{
		Mock<IVaporCache> cache = new(MockBehavior.Loose);
		TradeRateLimiter limiter = new();
		GameAccessPlugin plugin = new();
		await plugin.InitializeAsync(
			CreateContext(new ServiceProviderStub(cache.Object, limiter)), CancellationToken.None);

		Assert.Equal(14, plugin.GetActions().Count());
	}

	[Fact]
	public async Task ShutdownAsync_ReleasesActionReferences()
	{
		GameAccessPlugin plugin = new();
		await plugin.InitializeAsync(CreateContext(new ServiceProviderStub()), CancellationToken.None);

		await plugin.ShutdownAsync(CancellationToken.None);

		Assert.Empty(plugin.GetActions());
	}

	[Fact]
	public async Task Lifecycle_AcceptsCanceledTokens_UpToTheCheck()
	{
		// The lifecycle methods' only guard is the cancellation check; a canceled
		// token fails the call before any wiring happens.
		GameAccessPlugin plugin = new();
		await Assert.ThrowsAsync<OperationCanceledException>(
			() => plugin.InitializeAsync(CreateContext(new ServiceProviderStub()), new CancellationToken(canceled: true)));
		await Assert.ThrowsAsync<OperationCanceledException>(
			() => plugin.ShutdownAsync(new CancellationToken(canceled: true)));
	}

	private static IPluginContext CreateContext(IServiceProvider services) =>
		new TestPluginContext(new DefaultPluginHostServices(NullLoggerFactory.Instance, services));

	private sealed class TestPluginContext(IPluginHostServices host) : IPluginContext
	{
		public PluginInfo Info { get; } = new("vapor.game-access", "Vapor Game Access", new Version(1, 0, 0), PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public IPluginHostServices Host { get; } = host;
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		private readonly object?[] _instances;

		public ServiceProviderStub(params object?[] instances) => _instances = instances;

		public object? GetService(Type serviceType) =>
			_instances.FirstOrDefault(i => i is not null && serviceType.IsInstanceOfType(i));
	}
}
