using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.MarketWatch.Tests;

/// <summary>
/// Edge-path coverage for the MarketWatch plugin: fetch failures per source,
/// cancellation during a cycle, webhook transport crashes, threshold value shapes
/// and clean shutdown while a fetch is parked mid-flight.
/// </summary>
public sealed class MarketWatchPluginEdgeTests
{
	[Fact]
	public void Constants_MatchDefaults()
	{
		Assert.Equal(300, MarketWatchPlugin.DefaultIntervalSeconds);
		Assert.Equal(10, MarketWatchPlugin.MinIntervalSeconds);
		Assert.Equal(10m, MarketWatchPlugin.DefaultThresholdPercent);
	}

	[Fact]
	public async Task PollOnce_PriceFetchThrows_SkipsEntryButProcessesOthers()
	{
		var client = new ScriptedStoreClient();
		client.PriceFailures[440] = new InvalidOperationException("source down");
		await using var plugin = await CreateInitializedAsync(client);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "440" });
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		client.NextPrice = new PriceOverview { Currency = "USD", Final = 20m, Initial = 20m };
		await plugin.PollOnceAsync(CancellationToken.None);

		var watches = plugin.Store.Snapshot().ToDictionary(w => w.AppId, w => w);
		Assert.Null(watches[440].LastPrice);  // failed fetch left no observation
		Assert.Equal(20m, watches[570].LastPrice);
	}

	[Fact]
	public async Task PollOnce_FreeFetchThrows_ContinuesWithoutObservation()
	{
		var client = new ScriptedStoreClient();
		client.GameInfoFailures[570] = new InvalidOperationException("source down");
		await using var plugin = await CreateInitializedAsync(client);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570", ["kind"] = "free" });

		await plugin.PollOnceAsync(CancellationToken.None);

		var watch = Assert.Single(plugin.Store.Snapshot());
		Assert.Null(watch.LastKnownFree);
		Assert.Equal(0, watch.AlertCount);
	}

	[Fact]
	public async Task PollOnce_CanceledDuringFetch_Rethrows()
	{
		var client = new ScriptedStoreClient();
		await using var plugin = await CreateInitializedAsync(client);
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		using var cts = new CancellationTokenSource();
		cts.Cancel();
		client.PriceFailures[570] = new OperationCanceledException(cts.Token);

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			plugin.PollOnceAsync(cts.Token));
	}

	[Fact]
	public async Task PollOnce_WebhookTransportCrash_IsSwallowed()
	{
		var client = new ScriptedStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts"
		};
		await using var plugin = new MarketWatchPlugin(client, new HttpClient(new ThrowingHandler()));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		await plugin.PollOnceAsync(CancellationToken.None); // baseline
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 10m, Initial = 100m };
		await plugin.PollOnceAsync(CancellationToken.None); // alert + crashing webhook

		// The alert itself still counted; the transport crash never surfaced.
		Assert.Equal(1, plugin.Store.Snapshot()[0].AlertCount);
	}

	[Fact]
	public async Task Shutdown_DuringParkedFetch_CancelsCleanly()
	{
		var client = new ScriptedStoreClient();
		var parked = new TaskCompletionSource<PriceOverview?>(TaskCreationOptions.RunContinuationsAsynchronously);
		client.PendingPrice = parked.Task;
		var plugin = new MarketWatchPlugin(client, new HttpClient(new ThrowingHandler()));
		await plugin.InitializeAsync(
			new StubPluginContext(
				plugin.Info,
				new StubServiceProvider(),
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["market.check_interval_seconds"] = "10"
				}),
			CancellationToken.None);
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		// Begin shutdown while the loop is parked inside the fetch, then unwind the fetch.
		var shutdown = plugin.ShutdownAsync(CancellationToken.None);
		await Task.Delay(100);
		parked.SetException(new OperationCanceledException());
		await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

		// A second shutdown is a no-op, not a crash.
		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task RemoveAction_MissingAppId_Fails()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_remove", new Dictionary<string, object?>());

		Assert.False(result.Success);
		Assert.Contains("app_id", result.Error);
	}

	[Fact]
	public async Task AddAction_WhitespaceKind_FallsBackToPrice()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["kind"] = "   "
		});

		Assert.True(result.Success);
		Assert.Equal("price", result.Output!["kind"]);
	}

	[Theory]
	[InlineData("int", 5, 5.0)]
	[InlineData("long", 7, 7.0)]
	public async Task AddAction_NumericThresholdShapes_Accepted(string label, object raw, double expected)
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = raw
		});

		Assert.True(result.Success, $"{label}: {result.Error}");
		Assert.Equal((decimal)expected, plugin.Store.Snapshot()[0].ThresholdPercent);
	}

	[Fact]
	public async Task AddAction_JsonNumberThreshold_Accepted()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = JsonSerializer.Deserialize<JsonElement>("123.45")
		});

		Assert.True(result.Success);
		Assert.Equal(123.45m, plugin.Store.Snapshot()[0].ThresholdPercent);
	}

	[Fact]
	public async Task AddAction_JsonStringThreshold_Accepted()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = JsonSerializer.Deserialize<JsonElement>("\"8.5\"")
		});

		Assert.True(result.Success);
		Assert.Equal(8.5m, plugin.Store.Snapshot()[0].ThresholdPercent);
	}

	[Fact]
	public async Task AddAction_StringThreshold_Accepted()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = "9.5"
		});

		Assert.True(result.Success);
		Assert.Equal(9.5m, plugin.Store.Snapshot()[0].ThresholdPercent);
	}

	[Fact]
	public async Task AddAction_JsonNumberOutOfRangeThreshold_Rejected()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = JsonSerializer.Deserialize<JsonElement>("1e30")
		});

		Assert.False(result.Success);
		Assert.Contains("threshold_percent", result.Error);
	}

	[Fact]
	public async Task AddAction_BoolThreshold_Rejected()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = true
		});

		Assert.False(result.Success);
		Assert.Contains("threshold_percent", result.Error);
	}

	[Fact]
	public async Task AddAction_NonPositiveThreshold_Rejected()
	{
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = 0
		});

		Assert.False(result.Success);
	}

	private static async Task<MarketWatchPlugin> CreateInitializedAsync(ScriptedStoreClient client)
	{
		var plugin = new MarketWatchPlugin(client, new HttpClient(new ThrowingHandler()));
		await plugin.InitializeAsync(
			new StubPluginContext(
				plugin.Info,
				new StubServiceProvider(),
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["market.check_interval_seconds"] = "10"
				}),
			CancellationToken.None);
		return plugin;
	}

	private static Task<ActionResult> ExecuteAsync(
		MarketWatchPlugin plugin, string actionName, IReadOnlyDictionary<string, object?> payload)
	{
		var action = plugin.GetActions().Single(a => a.Name == actionName);
		return action.ExecuteAsync(TestSession.Create(), payload, CancellationToken.None);
	}

	/// <summary>Store client whose per-app failures, responses and parked fetches are scripted.</summary>
	private sealed class ScriptedStoreClient(PriceOverview? nextPrice = null) : ISteamStoreApiClient
	{
		public PriceOverview? NextPrice { get; set; } = nextPrice;

		public GameInfo? NextGameInfo { get; set; }

		public Dictionary<uint, Exception> PriceFailures { get; } = [];

		public Dictionary<uint, Exception> GameInfoFailures { get; } = [];

		public Task<PriceOverview?>? PendingPrice { get; set; }

		public Task<PriceOverview?> GetPriceAsync(uint appId, string country = "us", CancellationToken cancellationToken = default)
		{
			if (PendingPrice is not null)
			{
				return PendingPrice;
			}

			return PriceFailures.TryGetValue(appId, out var failure)
				? Task.FromException<PriceOverview?>(failure)
				: Task.FromResult(NextPrice);
		}

		public Task<GameInfo?> GetGameInfoAsync(uint appId, string country = "us", CancellationToken cancellationToken = default)
		{
			return GameInfoFailures.TryGetValue(appId, out var failure)
				? Task.FromException<GameInfo?>(failure)
				: Task.FromResult(NextGameInfo);
		}

		public Task<IReadOnlyList<GameSearchResult>> SearchGamesAsync(string term, int limit = 20, string country = "us", CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<MarketListingsPage?> GetMarketListingsAsync(uint appId, int start = 0, int count = 20, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<StorePurchaseResult?> AddFreeLicenseAsync(uint subId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	/// <summary>Webhook transport that always throws, simulating a dead network.</summary>
	private sealed class ThrowingHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			throw new HttpRequestException("webhook transport down");
	}
}
