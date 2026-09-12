using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.MarketWatch.Tests;

/// <summary>
/// Tests for the MarketWatch plugin surface: the three watch actions, configuration
/// handling, poll/alert/webhook flow and clean shutdown.
/// </summary>
public sealed class MarketWatchPluginTests
{
	[Fact]
	public async Task AddAction_MissingOrInvalidAppId_Fails()
	{
		var plugin = await CreateInitializedAsync();

		var missing = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>());
		var invalid = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "0" });
		var notNumber = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "abc" });

		Assert.False(missing.Success);
		Assert.False(invalid.Success);
		Assert.False(notNumber.Success);
		Assert.All(new[] { missing, invalid, notNumber }, r => Assert.Contains("app_id", r.Error));
	}

	[Fact]
	public async Task AddRemoveList_RoundTrip()
	{
		var plugin = await CreateInitializedAsync();

		var add = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		Assert.True(add.Success);
		Assert.Equal(1, add.Output!["watched"]);

		var duplicate = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		Assert.False(duplicate.Success);

		var list = await ExecuteAsync(plugin, "market_watch_list", new Dictionary<string, object?>());
		Assert.True(list.Success);
		Assert.Equal(1, list.Output!["count"]);
		var watches = Assert.IsType<Dictionary<string, object?>[]>(list.Output["watches"]);
		var watch = Assert.Single(watches);
		Assert.Equal(570u, watch["app_id"]);
		Assert.Equal(10m, watch["threshold_percent"]);
		Assert.Equal("us", watch["cc"]);
		Assert.Null(watch["baseline"]);

		var remove = await ExecuteAsync(plugin, "market_watch_remove", new Dictionary<string, object?> { ["app_id"] = "570" });
		Assert.True(remove.Success);
		Assert.Equal(0, remove.Output!["watched"]);

		var removeAgain = await ExecuteAsync(plugin, "market_watch_remove", new Dictionary<string, object?> { ["app_id"] = "570" });
		Assert.False(removeAgain.Success);
	}

	[Fact]
	public async Task AddAction_PerWatchOverrides()
	{
		var plugin = await CreateInitializedAsync();

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "440",
			["threshold_percent"] = "25.5",
			["cc"] = "DE"
		});

		var watch = Assert.Single(plugin.Store.Snapshot());
		Assert.Equal(25.5m, watch.ThresholdPercent);
		Assert.Equal("de", watch.Country);
	}

	[Fact]
	public async Task AddAction_InvalidThreshold_Fails()
	{
		var plugin = await CreateInitializedAsync();

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "440",
			["threshold_percent"] = "-5"
		});

		Assert.False(result.Success);
		Assert.Contains("threshold_percent", result.Error);
	}

	[Fact]
	public async Task Configuration_DefaultsComeFromPluginConfig()
	{
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.threshold_percent"] = "33.3",
			["market.country"] = " FR ",
			["market.webhook_url"] = ""
		};
		var plugin = await CreateInitializedAsync(config);

		Assert.Equal(TimeSpan.FromSeconds(300), plugin.Interval);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "440" });
		var watch = Assert.Single(plugin.Store.Snapshot());
		Assert.Equal(33.3m, watch.ThresholdPercent);
		Assert.Equal("fr", watch.Country);
	}

	[Fact]
	public async Task PollOnce_BuildsBaselineThenAlertsWithWebhook()
	{
		var handler = new StubHttpHandler();
		var client = new FakeStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts",
			["market.check_interval_seconds"] = "10"
		};
		await using var plugin = new MarketWatchPlugin(client, new HttpClient(handler));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		// First cycle: baseline, no webhook.
		await plugin.PollOnceAsync(CancellationToken.None);
		Assert.Empty(handler.Bodies);

		// Price drops 50%: alert fires and the webhook receives the JSON payload.
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 50m, Initial = 100m, DiscountPercent = 50 };
		await plugin.PollOnceAsync(CancellationToken.None);

		var body = Assert.Single(handler.Bodies);
		using var document = JsonDocument.Parse(body);
		var root = document.RootElement;
		Assert.Equal("price_alert", root.GetProperty("type").GetString());
		Assert.Equal(570u, root.GetProperty("appId").GetUInt32());
		Assert.Equal(100m, root.GetProperty("baselinePrice").GetDecimal());
		Assert.Equal(50m, root.GetProperty("newPrice").GetDecimal());
		Assert.Equal(-50m, root.GetProperty("changePercent").GetDecimal());

		// Steady price afterwards: no further alerts.
		await plugin.PollOnceAsync(CancellationToken.None);
		Assert.Single(handler.Bodies);

		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task PollOnce_WebhookFailure_DoesNotBreakLoop()
	{
		var handler = new StubHttpHandler(returnError: true);
		var client = new FakeStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts"
		};
		await using var plugin = new MarketWatchPlugin(client, new HttpClient(handler));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		await plugin.PollOnceAsync(CancellationToken.None);
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 10m, Initial = 100m };
		await plugin.PollOnceAsync(CancellationToken.None);

		Assert.Equal(1, plugin.Store.Snapshot()[0].AlertCount);
		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task Shutdown_StopsQuicklyWithoutWork()
	{
		var plugin = await CreateInitializedAsync();
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await plugin.ShutdownAsync(timeout.Token);

		// A second shutdown is a no-op, not a crash.
		await plugin.ShutdownAsync(timeout.Token);
	}

	private static async Task<MarketWatchPlugin> CreateInitializedAsync(
		IReadOnlyDictionary<string, string>? configuration = null)
	{
		var plugin = new MarketWatchPlugin(new FakeStoreClient(null), new HttpClient(new StubHttpHandler()));
		await plugin.InitializeAsync(
			new StubPluginContext(plugin.Info, new StubServiceProvider(), configuration), CancellationToken.None);
		return plugin;
	}

	private static Task<ActionResult> ExecuteAsync(
		MarketWatchPlugin plugin, string actionName, IReadOnlyDictionary<string, object?> payload)
	{
		var action = plugin.GetActions().Single(a => a.Name == actionName);
		return action.ExecuteAsync(TestSession.Create(), payload, CancellationToken.None);
	}
}

/// <summary>Store client stub resolving prices through a settable field.</summary>
internal sealed class FakeStoreClient(PriceOverview? nextPrice) : ISteamStoreApiClient
{
	public PriceOverview? NextPrice { get; set; } = nextPrice;

	public Task<PriceOverview?> GetPriceAsync(uint appId, string country = "us", CancellationToken cancellationToken = default) =>
		Task.FromResult(NextPrice);

	public Task<GameInfo?> GetGameInfoAsync(uint appId, string country = "us", CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<IReadOnlyList<GameSearchResult>> SearchGamesAsync(string term, int limit = 20, string country = "us", CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<MarketListingsPage?> GetMarketListingsAsync(uint appId, int start = 0, int count = 20, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();
}

/// <summary>HTTP handler stub capturing webhook bodies; optionally always failing.</summary>
internal sealed class StubHttpHandler(bool returnError = false) : HttpMessageHandler
{
	public List<string> Bodies { get; } = [];

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.Content is not null)
		{
			Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
		}

		return new HttpResponseMessage(returnError ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
	}
}
