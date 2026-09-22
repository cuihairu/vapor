using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
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
	public void ParameterlessConstructor_ExposesPluginInfo()
	{
		// The host activates plugins through the parameterless constructor; a bare
		// instance holds no resources (nothing is created until InitializeAsync).
		var plugin = new MarketWatchPlugin();

		Assert.Equal("vapor.market-watch", plugin.Info.Id);
		Assert.Equal(PluginApi.Current, plugin.Info.ApiVersion);
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
	public async Task PollOnce_CanceledDuringParkedFreeFetch_Rethrows()
	{
		// The free-watch source has its own cancellation filter: a cancellation surfacing
		// from the appdetails fetch while the cycle token is already cancelled must
		// rethrow, not be swallowed as a failed fetch. The token is only cancelled once
		// the cycle is parked inside the fetch — cancelling any earlier trips the loop
		// guard before the fetch is ever reached.
		var client = new ScriptedStoreClient();
		var parked = new TaskCompletionSource<GameInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var plugin = await CreateInitializedAsync(client);
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570", ["kind"] = "free" });

		client.PendingGameInfo = parked.Task;
		client.GameInfoFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var cts = new CancellationTokenSource();
		var poll = plugin.PollOnceAsync(cts.Token);
		await client.GameInfoFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();
		parked.SetException(new OperationCanceledException(cts.Token));

		// A TCS faulted with an OCE surfaces as TaskCanceledException on await.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
	}

	[Fact]
	public async Task PollOnce_WebhookCanceledDuringSend_Rethrows()
	{
		// A webhook transport that cancels mid-send while the cycle token is cancelled
		// must propagate the cancellation instead of treating it as a delivery failure.
		// The cycle starts with a live token and is cancelled while parked in the send.
		var client = new ScriptedStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts"
		};
		var handler = new ParkedWebhookHandler();
		await using var plugin = new MarketWatchPlugin(client, new HttpClient(handler));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		await plugin.PollOnceAsync(CancellationToken.None); // baseline, no alert yet

		client.NextPrice = new PriceOverview { Currency = "USD", Final = 10m, Initial = 100m };
		using var cts = new CancellationTokenSource();
		var poll = plugin.PollOnceAsync(cts.Token);
		await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cts.Cancel();
		handler.Release.SetException(new OperationCanceledException());

		// HttpClient surfaces the cancelled send as TaskCanceledException (an OCE subclass).
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
	}

	[Fact]
	public async Task Shutdown_WhileLoopParkedInFetch_UnwindsThroughCancellationFilters()
	{
		// Park the background loop inside its price fetch, then cancel: the parked fetch
		// unwinds with OperationCanceledException while the token is already cancelled, so
		// the per-fetch filter rethrows and the loop-level filter stops the cycle quietly.
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

		// Script the parked fetch before any cycle can reach it, then restart the loop
		// with a fast interval: the original loop's first delay arm captured the
		// configured 10s before any test-side change could land. Cycle 1 sees no
		// watches; the next cycle enters GetPriceAsync, signals and parks.
		client.PriceFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		client.PendingPrice = parked.Task;
		await plugin.RestartLoopForTestsAsync(TimeSpan.FromMilliseconds(30));
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		await client.PriceFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var shutdown = plugin.ShutdownAsync(CancellationToken.None);
		await Task.Delay(100);
		parked.SetException(new OperationCanceledException());
		await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task PollLoop_ShortInterval_RunsRepeatedCyclesUntilShutdown()
	{
		// Collapse the polling interval so several full poll/delay cycles run in test time;
		// the loop keeps cycling through its delay arm until shutdown cancels it.
		var client = new ScriptedStoreClient(new PriceOverview { Currency = "USD", Final = 20m, Initial = 20m });
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

		// Restart the loop with a fast interval: the original loop's first delay arm
		// captured the configured 10s before any test-side change could land.
		await plugin.RestartLoopForTestsAsync(TimeSpan.FromMilliseconds(30));

		try
		{
			var deadline = Environment.TickCount64 + 5000;
			while (plugin.Store.Snapshot()[0].LastCheckedAt is null && Environment.TickCount64 < deadline)
			{
				await Task.Delay(25);
			}

			Assert.NotNull(plugin.Store.Snapshot()[0].LastCheckedAt);

			// A few more cycles run through the delay arm before the shutdown below.
			await Task.Delay(150);
		}
		finally
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await plugin.ShutdownAsync(timeout.Token);
		}
	}

	[Fact]
	public async Task AddAction_ExplicitPriceKind_IsStored()
	{
		// An explicit (case-insensitive) kind=price takes the same normalization path as
		// the default and stores a price watch.
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["kind"] = " Price "
		});

		Assert.True(result.Success, result.Error);
		Assert.Equal("price", result.Output!["kind"]);
		WatchEntry entry = Assert.Single(plugin.Store.Snapshot());
		Assert.Equal(WatchKind.Price, entry.Kind);
	}

	[Fact]
	public async Task AddAction_DecimalThresholdShape_Accepted()
	{
		// A boxed decimal payload value is accepted as-is (host adapters pass typed values).
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = 5.5m
		});

		Assert.True(result.Success, result.Error);
		Assert.Equal(5.5m, plugin.Store.Snapshot()[0].ThresholdPercent);
	}

	[Fact]
	public async Task AddAction_LongThresholdShape_Accepted()
	{
		// A boxed long is distinct from int at runtime; both must be accepted.
		await using var plugin = await CreateInitializedAsync(new ScriptedStoreClient());

		var result = await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?>
		{
			["app_id"] = "570",
			["threshold_percent"] = 7L
		});

		Assert.True(result.Success, result.Error);
		Assert.Equal(7m, plugin.Store.Snapshot()[0].ThresholdPercent);
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
	public async Task PollOnce_LoggerSuppressed_AllDiagnosticShortCircuitsStayQuiet()
	{
		var client = new ScriptedStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts"
		};
		await using var plugin = new MarketWatchPlugin(client, new HttpClient(new ThrowingHandler()));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);
		await plugin.StopLoopForTestsAsync();

		// Suppress the logger: every `_logger?.` diagnostic site must short-circuit instead
		// of formatting, on every path (baseline, alert, fetch failure, webhook failure).
		typeof(MarketWatchPlugin).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(plugin, null);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		await plugin.PollOnceAsync(CancellationToken.None); // price baseline

		client.PriceFailures[570] = new InvalidOperationException("source down");
		await plugin.PollOnceAsync(CancellationToken.None); // price fetch failure

		client.PriceFailures.Remove(570);
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 10m, Initial = 100m };
		await plugin.PollOnceAsync(CancellationToken.None); // alert + crashing webhook

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "730", ["kind"] = "free" });
		client.GameInfoFailures[730] = new InvalidOperationException("source down");
		await plugin.PollOnceAsync(CancellationToken.None); // free fetch failure

		client.GameInfoFailures.Remove(730);
		client.NextGameInfo = new GameInfo { AppId = 730, Name = "Paid Game", IsFree = false };
		await plugin.PollOnceAsync(CancellationToken.None); // free baseline (paid)

		client.NextGameInfo = new GameInfo { AppId = 730, Name = "Paid Game", IsFree = true };
		await plugin.PollOnceAsync(CancellationToken.None); // free edge + crashing webhook

		Assert.Equal(1, plugin.Store.Snapshot().Single(w => w.AppId == 570).AlertCount);
		Assert.Equal(1, plugin.Store.Snapshot().Single(w => w.AppId == 730).AlertCount);
	}

	[Fact]
	public async Task PollOnce_WebhookNonSuccessStatus_LoggedAndSwallowed()
	{
		var client = new ScriptedStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts"
		};
		await using var plugin = new MarketWatchPlugin(
			client, new HttpClient(new FixedStatusHandler(HttpStatusCode.InternalServerError)));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);
		await plugin.StopLoopForTestsAsync();

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		await plugin.PollOnceAsync(CancellationToken.None); // baseline, no webhook
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 10m, Initial = 100m };
		await plugin.PollOnceAsync(CancellationToken.None); // alert + webhook answers 500

		// The 500 is logged as a warning, never surfaced, and the alert still counted.
		Assert.Equal(1, plugin.Store.Snapshot()[0].AlertCount);
	}

	[Fact]
	public async Task StopLoopForTests_AfterStart_AwaitsRunningLoop()
	{
		// The started-loop arm: _loopCts and _loop are both live, so stop must cancel,
		// await the swallow-all loop body and clear the fields. Manual PollOnceAsync
		// keeps driving the store afterwards (the loop is not restarted here).
		var client = new ScriptedStoreClient();
		await using var plugin = await CreateInitializedAsync(client);

		await plugin.StopLoopForTestsAsync();

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 20m, Initial = 20m };
		await plugin.PollOnceAsync(CancellationToken.None);
		Assert.Equal(20m, plugin.Store.Snapshot()[0].LastPrice);
	}

	[Fact]
	public async Task StopLoopForTests_OnBareInstance_SkipsNullLoopAndCts()
	{
		// A plugin that was never initialized has neither loop nor CTS: every
		// nullable step of the stop hook must be skipped (the null arms that the
		// initialized-instance tests can never reach).
		var client = new ScriptedStoreClient();
		await using var plugin = new MarketWatchPlugin(client, new HttpClient());

		await plugin.StopLoopForTestsAsync();
	}

	[Fact]
	public async Task PollOnce_LoggerSuppressed_NonSuccessWebhook_StaysQuiet()
	{
		// The remaining webhook-diagnosis combination: a non-2xx response while
		// the logger is suppressed — the warning must short-circuit on the null
		// logger and the alert must still count.
		var client = new ScriptedStoreClient(new PriceOverview { Currency = "USD", Final = 100m, Initial = 100m });
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["market.webhook_url"] = "http://webhook.test/alerts"
		};
		await using var plugin = new MarketWatchPlugin(
			client, new HttpClient(new FixedStatusHandler(HttpStatusCode.InternalServerError)));
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider(), config), CancellationToken.None);
		await plugin.StopLoopForTestsAsync();
		typeof(MarketWatchPlugin).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(plugin, null);

		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });
		await plugin.PollOnceAsync(CancellationToken.None); // baseline, no webhook
		client.NextPrice = new PriceOverview { Currency = "USD", Final = 10m, Initial = 100m };
		await plugin.PollOnceAsync(CancellationToken.None); // alert + 500 webhook, quiet

		Assert.Equal(1, plugin.Store.Snapshot()[0].AlertCount);
	}

	/// <summary>Webhook transport that always answers with a fixed status code.</summary>
	private sealed class FixedStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(statusCode));
	}

	[Fact]
	public async Task Shutdown_DuringParkedFetch_CancelsCleanly()
	{
		var client = new ScriptedStoreClient();
		var parked = new TaskCompletionSource<PriceOverview?>(TaskCreationOptions.RunContinuationsAsynchronously);
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

		// Script the parked fetch and register the watch only after the original
		// loop is gone (the restart awaits its exit): a pre-restart cycle whose
		// first snapshot lands after the add would park on a task that ignores the
		// cancellation token and deadlock the restart's await — the CI-grade
		// scheduling delay that intermittently stalled this assembly (2026-09-17).
		client.PriceFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		client.PendingPrice = parked.Task;
		await plugin.RestartLoopForTestsAsync(TimeSpan.FromMilliseconds(30));
		await ExecuteAsync(plugin, "market_watch_add", new Dictionary<string, object?> { ["app_id"] = "570" });

		// The fast loop parks deterministically inside the price fetch; begin
		// shutdown while parked, then unwind the fetch.
		await client.PriceFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		// Begin shutdown while the loop is parked inside the fetch, then unwind the fetch.
		await client.PriceFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

		public Task<GameInfo?>? PendingGameInfo { get; set; }

		/// <summary>Completed when a price fetch is entered; lets tests park the poll loop deterministically.</summary>
		public TaskCompletionSource? PriceFetchStarted { get; set; }

		/// <summary>Completed when a free-state fetch is entered.</summary>
		public TaskCompletionSource? GameInfoFetchStarted { get; set; }

		public Task<PriceOverview?> GetPriceAsync(uint appId, string country = "us", CancellationToken cancellationToken = default)
		{
			PriceFetchStarted?.TrySetResult();
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
			GameInfoFetchStarted?.TrySetResult();
			if (PendingGameInfo is not null)
			{
				return PendingGameInfo;
			}

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

	/// <summary>Webhook transport that parks each send until the test releases it.</summary>
	private sealed class ParkedWebhookHandler : HttpMessageHandler
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Started.TrySetResult();
			await Release.Task;
			return new HttpResponseMessage(HttpStatusCode.OK);
		}
	}
}
