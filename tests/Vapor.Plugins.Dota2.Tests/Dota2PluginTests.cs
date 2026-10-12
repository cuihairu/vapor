using Microsoft.Extensions.Logging.Abstractions;

using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

using Xunit;

namespace Vapor.Plugins.Dota2.Tests;

public class Dota2PluginTests
{
	[Fact]
	public void Info_DeclaresTheDotaIdentity()
	{
		var plugin = new Dota2Plugin();

		Assert.Equal("vapor.dota2", plugin.Info.Id);
		Assert.Equal("Vapor Dota 2", plugin.Info.Name);
	}

	[Fact]
	public void Constants_MatchTheDotaInventoryScope()
	{
		// The app/context pair is the plugin's whole scoping contract: the
		// inventory read, the trade filter and the send pin all derive from it.
		Assert.Equal(570u, Dota2Plugin.AppId);
		Assert.Equal(2ul, Dota2Plugin.InventoryContextId);
		Assert.Equal("trade.enabled", Dota2Plugin.TradeEnabledConfigKey);
		Assert.Equal("VAPOR_DOTA2_TRADE_ENABLED", Dota2Plugin.TradeEnabledEnvironmentVariable);
	}

	[Fact]
	public async Task GetActions_IsEmpty_BeforeInitialization()
	{
		var plugin = new Dota2Plugin();

		Assert.Empty(plugin.GetActions());
		await Assert.ThrowsAsync<ArgumentNullException>(
			() => plugin.InitializeAsync(null!, CancellationToken.None));
	}

	[Fact]
	public async Task InitializeAsync_DefaultGate_IsOff_AndWiresThreeActions()
	{
		var plugin = new Dota2Plugin();

		await plugin.InitializeAsync(new TestPluginContext(new Dictionary<string, string>()), CancellationToken.None);

		Assert.False(plugin.TradeEnabled);
		Assert.Null(plugin.RateLimiter);
		Assert.Equal(
			["dota2_inventory", "dota2_trades", "dota2_send_trade"],
			plugin.GetActions().Select(action => action.Name).ToArray());
	}

	[Fact]
	public async Task InitializeAsync_ConfigUnlocksTheTradeGate()
	{
		var plugin = new Dota2Plugin();

		await plugin.InitializeAsync(
			new TestPluginContext(new Dictionary<string, string> { ["trade.enabled"] = "true" }),
			CancellationToken.None);

		Assert.True(plugin.TradeEnabled);
	}

	[Fact]
	public async Task InitializeAsync_ResolvesTheSharedRateLimiter_WhenTheHostHasOne()
	{
		var limiter = new TradeRateLimiter();
		var plugin = new Dota2Plugin();

		await plugin.InitializeAsync(
			new TestPluginContext(new Dictionary<string, string>(), new FixedServiceProvider(limiter)),
			CancellationToken.None);

		Assert.Same(limiter, plugin.RateLimiter);
	}

	[Fact]
	public async Task ShutdownAsync_ClearsTheActionList_AndCanBeCalledRepeatedly()
	{
		var plugin = new Dota2Plugin();
		await plugin.InitializeAsync(new TestPluginContext(new Dictionary<string, string>()), CancellationToken.None);

		await plugin.ShutdownAsync(CancellationToken.None);
		await plugin.ShutdownAsync(CancellationToken.None);

		Assert.Empty(plugin.GetActions());
		Assert.Null(plugin.RateLimiter);
	}

	[Fact]
	public async Task ShutdownAsync_WithCancelledToken_Throws()
	{
		var plugin = new Dota2Plugin();

		await Assert.ThrowsAsync<OperationCanceledException>(
			() => plugin.ShutdownAsync(new CancellationToken(canceled: true)));
	}

	[Fact]
	public void TradeClientFactory_FallsBackToTheRealClient_AndHonorsInjection()
	{
		var plugin = new Dota2Plugin();
		Assert.IsType<SteamTradeClient>(plugin.TradeClientFactory(
			new SteamWebHandler(
				new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
				NullLogger<SteamWebHandler>.Instance)));

		var fake = new FakeTradeClient();
		var injected = new Dota2Plugin(_ => fake);
		Assert.Same(fake, injected.TradeClientFactory(null!));
	}

	[Fact]
	public void ActionConstructors_RejectNullPlugin()
	{
		Assert.Throws<ArgumentNullException>(() => new Dota2InventoryAction(null!));
		Assert.Throws<ArgumentNullException>(() => new Dota2TradesAction(null!));
		Assert.Throws<ArgumentNullException>(() => new Dota2SendTradeAction(null!));
	}
}
