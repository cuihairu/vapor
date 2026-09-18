using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.MarketWatch;

/// <summary>
/// Market Watch plugin: keeps a list of watched Steam AppIDs, polls their price overview
/// on a background timer and fires an alert (log + optional webhook POST) when a price
/// moves beyond the configured threshold percent from its baseline. Watch entries are
/// managed through the market_watch_add / market_watch_remove / market_watch_list actions.
/// </summary>
public sealed class MarketWatchPlugin : IPlugin, IActionPlugin, IAsyncDisposable
{
	/// <summary>Default polling interval in seconds.</summary>
	public const int DefaultIntervalSeconds = 300;

	/// <summary>Lower bound for the polling interval (seconds) — be polite to the store API.</summary>
	public const int MinIntervalSeconds = 10;

	/// <summary>Default price-change threshold in percent.</summary>
	public const decimal DefaultThresholdPercent = 10m;

	private readonly ISteamStoreApiClient? _storeClientOverride;
	private readonly HttpClient? _httpClientOverride;

	private ILoggerFactory? _loggerFactory;
	private ILogger<MarketWatchPlugin>? _logger;
	private MarketWatchStore _store = new();
	private ISteamStoreApiClient? _storeClient;
	private HttpClient? _httpClient;
	private SteamWebHandler? _ownedWebHandler;
	private CancellationTokenSource? _loopCts;
	private Task? _loop;
	private TimeSpan _interval;
	private decimal _defaultThresholdPercent = DefaultThresholdPercent;
	private string _defaultCountry = "us";
	private string? _webhookUrl;

	public PluginInfo Info { get; } = new(
		Id: "vapor.market-watch",
		Name: "Vapor Market Watch",
		Version: new Version(1, 1, 0),
		ApiVersion: PluginApi.Current,
		Description: "Price threshold and free-game watches: background polling with log/webhook alerts");

	public MarketWatchPlugin()
	{
	}

	/// <summary>Test hook: inject a store client and webhook transport instead of real ones.</summary>
	internal MarketWatchPlugin(ISteamStoreApiClient storeClient, HttpClient? httpClient = null)
	{
		_storeClientOverride = storeClient;
		_httpClientOverride = httpClient;
	}

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_loggerFactory = context.Host.LoggerFactory;
		_logger = _loggerFactory.CreateLogger<MarketWatchPlugin>();
		_store = new MarketWatchStore();

		var config = context.Configuration;
		var intervalSeconds = config.GetInt32(
			"market.check_interval_seconds", DefaultIntervalSeconds, "VAPOR_MARKETWATCH_INTERVAL_SECONDS",
			min: MinIntervalSeconds, max: 86_400);
		_interval = TimeSpan.FromSeconds(intervalSeconds);
		_defaultThresholdPercent = config.GetDecimal(
			"market.threshold_percent", DefaultThresholdPercent, "VAPOR_MARKETWATCH_THRESHOLD_PERCENT",
			min: 0.01m, max: 10_000m);
		_defaultCountry = config.GetString("market.country", "us", "VAPOR_MARKETWATCH_COUNTRY").Trim().ToLowerInvariant();
		_webhookUrl = config.GetString("market.webhook_url", string.Empty, "VAPOR_MARKETWATCH_WEBHOOK_URL");
		if (string.IsNullOrWhiteSpace(_webhookUrl))
		{
			_webhookUrl = null;
		}

		_storeClient = _storeClientOverride ?? CreateDefaultStoreClient();
		_httpClient = _httpClientOverride ?? new HttpClient();

		_loopCts = new CancellationTokenSource();
		_loop = Task.Run(() => PollLoopAsync(_loopCts.Token), CancellationToken.None);

		_logger.LogInformation(
			"Market watch started: interval {Interval}s, default threshold {Threshold}%, country {Country}, webhook {Webhook}",
			intervalSeconds, _defaultThresholdPercent, _defaultCountry, _webhookUrl is null ? "disabled" : "enabled");

		return Task.CompletedTask;
	}

	public async Task ShutdownAsync(CancellationToken cancellationToken)
	{
		_loopCts?.Cancel();
		if (_loop is not null)
		{
			// The loop swallows its own errors, so this await never throws.
			await _loop.ConfigureAwait(false);
		}

		_loopCts?.Dispose();
		_loopCts = null;
		_loop = null;

		_ownedWebHandler?.Dispose();
		_ownedWebHandler = null;
		_storeClient = null;
	}

	public IEnumerable<IAction> GetActions()
	{
		yield return new MarketWatchAddAction(this);
		yield return new MarketWatchRemoveAction(this);
		yield return new MarketWatchListAction(this);
	}

	/// <summary>Registered watches (for actions and tests).</summary>
	internal MarketWatchStore Store => _store;

	/// <summary>Current polling interval (for tests).</summary>
	internal TimeSpan Interval => _interval;

	/// <summary>
	/// Test hook: restarts the poll loop with a custom interval. The loop started by
	/// InitializeAsync captures the configured interval (minimum 10s) in its first
	/// delay arm before a test can shorten it, so loop-level tests restart with a
	/// fast interval to stay deterministic in test time.
	/// </summary>
	internal async Task RestartLoopForTestsAsync(TimeSpan interval)
	{
		_loopCts?.Cancel();
		if (_loop is not null)
		{
			// The loop swallows its own errors, so this await never throws.
			await _loop.ConfigureAwait(false);
		}

		_loopCts?.Dispose();

		ISteamStoreApiClient? client = _storeClient;
		_interval = interval;
		_loopCts = new CancellationTokenSource();
		_storeClient = client;
		_loop = Task.Run(() => PollLoopAsync(_loopCts.Token), CancellationToken.None);
	}

	/// <summary>
	/// Test hook: drains and stops the poll loop started by InitializeAsync without a
	/// replacement. The loop's first tick runs immediately on the thread pool, so a
	/// manually driven test would race it for the store; stopping it while the store
	/// is still empty makes the manual PollOnceAsync calls fully deterministic.
	/// </summary>
	internal async Task StopLoopForTestsAsync()
	{
		_loopCts?.Cancel();
		if (_loop is not null)
		{
			// The loop swallows its own errors, so this await never throws.
			await _loop.ConfigureAwait(false);
		}

		_loopCts?.Dispose();
		_loopCts = null;
		_loop = null;
	}

	private ISteamStoreApiClient CreateDefaultStoreClient()
	{
		// The price overview endpoint is public; an anonymous web handler suffices and no
		// logged-in session is required. Owned and disposed with the plugin.
		_ownedWebHandler = new SteamWebHandler(
			new SteamWebHandlerConfig(), _loggerFactory!.CreateLogger<SteamWebHandler>());
		return new SteamStoreApiClient(
			_ownedWebHandler, _loggerFactory!.CreateLogger<SteamStoreApiClient>());
	}

	private async Task PollLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await PollOnceAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "Market watch poll cycle failed");
			}

			try
			{
				await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	/// <summary>One poll cycle over all watches (internal for tests).</summary>
	internal async Task PollOnceAsync(CancellationToken cancellationToken)
	{
		var entries = _store.Snapshot();
		var client = _storeClient;
		if (client is null || entries.Count == 0)
		{
			return;
		}

		foreach (var entry in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (entry.Kind == WatchKind.Free)
			{
				await PollFreeWatchAsync(client, entry, cancellationToken).ConfigureAwait(false);
				continue;
			}

			PriceOverview? price = null;
			try
			{
				price = await client.GetPriceAsync(entry.AppId, entry.Country, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logger?.LogDebug(ex, "Price fetch failed for app {AppId}", entry.AppId);
			}

			var (outcome, alert) = _store.RecordPrice(entry.AppId, price, DateTime.UtcNow);
			switch (outcome)
			{
				case PriceRecordOutcome.BaselineRecorded:
					_logger?.LogInformation(
						"Market watch baseline for {AppId}: {Price} {Currency}",
						entry.AppId, price!.Final, price.Currency);
					break;
				case PriceRecordOutcome.AlertFired:
					PriceAlert fired = alert!;
					_logger?.LogInformation(
						"Price alert for {AppId}: {Baseline} -> {New} ({Change}% >= {Threshold}%)",
						fired.AppId, fired.BaselinePrice, fired.NewPrice, fired.ChangePercent, fired.ThresholdPercent);
					await PostWebhookAsync(JsonSerializer.Serialize(new
					{
						type = "price_alert",
						appId = fired.AppId,
						country = fired.Country,
						currency = fired.Currency,
						baselinePrice = fired.BaselinePrice,
						newPrice = fired.NewPrice,
						changePercent = fired.ChangePercent,
						thresholdPercent = fired.ThresholdPercent,
						checkedAt = fired.CheckedAt.ToString("O")
					}), fired.AppId, cancellationToken).ConfigureAwait(false);
					break;
			}
		}
	}

	/// <summary>Polls one free watch: is-free state comes from the store's appdetails endpoint.</summary>
	private async Task PollFreeWatchAsync(ISteamStoreApiClient client, WatchEntry entry, CancellationToken cancellationToken)
	{
		GameInfo? game = null;
		try
		{
			game = await client.GetGameInfoAsync(entry.AppId, entry.Country, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger?.LogDebug(ex, "Free-state fetch failed for app {AppId}", entry.AppId);
		}

		var (outcome, alert) = _store.RecordFreeObservation(entry.AppId, game?.IsFree, DateTime.UtcNow);
		switch (outcome)
		{
			case FreeRecordOutcome.BaselineRecorded:
				_logger?.LogInformation(
					"Free watch baseline for {AppId}: is_free={IsFree}",
					entry.AppId, game!.IsFree);
				break;
			case FreeRecordOutcome.AlertFired:
				FreeGameAlert fired = alert!;
				_logger?.LogInformation("Free game alert for {AppId}: the game is now free", fired.AppId);
				await PostWebhookAsync(JsonSerializer.Serialize(new
				{
					type = "free_game_alert",
					appId = fired.AppId,
					country = fired.Country,
					checkedAt = fired.CheckedAt.ToString("O")
				}), fired.AppId, cancellationToken).ConfigureAwait(false);
				break;
		}
	}

	private async Task PostWebhookAsync(string jsonPayload, uint appId, CancellationToken cancellationToken)
	{
		if (_webhookUrl is null || _httpClient is null)
		{
			return;
		}

		try
		{
			using var request = new HttpRequestMessage(
				HttpMethod.Post, _webhookUrl)
			{
				Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
			};
			using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				_logger?.LogWarning(
					"Alert webhook for {AppId} returned {StatusCode}", appId, (int)response.StatusCode);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Webhook delivery must never take down the poll loop.
			_logger?.LogWarning(ex, "Alert webhook delivery failed for {AppId}", appId);
		}
	}

	/// <summary>Disposes by running <see cref="ShutdownAsync"/> (cancels the poll loop).</summary>
	public async ValueTask DisposeAsync()
	{
		await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
	}

	// --- Actions ---

	private sealed class MarketWatchAddAction(MarketWatchPlugin plugin) : IAction
	{
		public string Name => "market_watch_add";

		public ActionMetadata Metadata { get; } = new(
			Name: "market_watch_add",
			Description: "Start watching a game: kind=price alerts on threshold moves (default), kind=free alerts when the game turns free",
			RequiresLogin: false,
			TimeoutSeconds: 10);

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken)
		{
			if (!TryReadAppId(payload, out uint appId, out string? error))
			{
				return Task.FromResult(new ActionResult(false, error, null));
			}

			var kind = WatchKind.Price;
			var kindParam = PayloadReader.GetString(payload, "kind");
			if (!string.IsNullOrWhiteSpace(kindParam))
			{
				if (!TryReadKind(kindParam, out kind))
				{
					return Task.FromResult(new ActionResult(false, "kind must be 'price' or 'free'", null));
				}
			}

			var threshold = plugin._defaultThresholdPercent;
			var country = plugin._defaultCountry;

			if (PayloadReader.TryGetValue(payload, "threshold_percent", out var thresholdValue))
			{
				if (!TryReadDecimal(thresholdValue, out threshold) || threshold <= 0)
				{
					return Task.FromResult(new ActionResult(false, "threshold_percent must be a positive number", null));
				}
			}

			var countryParam = PayloadReader.GetString(payload, "cc");
			if (!string.IsNullOrWhiteSpace(countryParam))
			{
				country = countryParam.Trim().ToLowerInvariant();
			}

			if (!plugin._store.Add(appId, threshold, country, currency: string.Empty, kind))
			{
				return Task.FromResult(new ActionResult(false, $"App {appId} is already being watched", null));
			}

			return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
			{
				["app_id"] = appId,
				["kind"] = KindName(kind),
				["threshold_percent"] = threshold,
				["cc"] = country,
				["watched"] = plugin._store.Count
			}));
		}
	}

	private sealed class MarketWatchRemoveAction(MarketWatchPlugin plugin) : IAction
	{
		public string Name => "market_watch_remove";

		public ActionMetadata Metadata { get; } = new(
			Name: "market_watch_remove",
			Description: "Stop watching a game's price",
			RequiresLogin: false,
			TimeoutSeconds: 10);

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken)
		{
			if (!TryReadAppId(payload, out uint appId, out string? error))
			{
				return Task.FromResult(new ActionResult(false, error, null));
			}

			if (!plugin._store.Remove(appId))
			{
				return Task.FromResult(new ActionResult(false, $"App {appId} is not being watched", null));
			}

			return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
			{
				["app_id"] = appId,
				["watched"] = plugin._store.Count
			}));
		}
	}

	private sealed class MarketWatchListAction(MarketWatchPlugin plugin) : IAction
	{
		public string Name => "market_watch_list";

		public ActionMetadata Metadata { get; } = new(
			Name: "market_watch_list",
			Description: "List watched games with their baselines and last observed prices",
			RequiresLogin: false,
			TimeoutSeconds: 10);

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken)
		{
			var watches = plugin._store.Snapshot()
				.Select(static w => new Dictionary<string, object?>
				{
					["app_id"] = w.AppId,
					["kind"] = KindName(w.Kind),
					["threshold_percent"] = w.ThresholdPercent,
					["cc"] = w.Country,
					["currency"] = w.Currency,
					["baseline"] = w.BaselinePrice,
					["last_price"] = w.LastPrice,
					["last_known_free"] = w.LastKnownFree,
					["last_checked_at"] = w.LastCheckedAt,
					["alerts"] = w.AlertCount
				})
				.ToArray();

			return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
			{
				["watches"] = watches,
				["count"] = watches.Length,
				["interval_seconds"] = (int)plugin.Interval.TotalSeconds
			}));
		}
	}

	private static bool TryReadAppId(IReadOnlyDictionary<string, object?> payload, out uint appId, out string? error)
	{
		appId = 0;
		error = null;
		var raw = PayloadReader.GetString(payload, "app_id");
		if (string.IsNullOrWhiteSpace(raw) || !uint.TryParse(raw, out appId) || appId == 0)
		{
			error = "Valid app_id is required";
			return false;
		}

		return true;
	}

	private static bool TryReadKind(string raw, out WatchKind kind)
	{
		switch (raw.Trim().ToLowerInvariant())
		{
			case "price":
				kind = WatchKind.Price;
				return true;
			case "free":
				kind = WatchKind.Free;
				return true;
			default:
				kind = WatchKind.Price;
				return false;
		}
	}

	private static string KindName(WatchKind kind) =>
		kind == WatchKind.Free ? "free" : "price";

	/// <summary>Reads a decimal payload value (mirrors PayloadReader's type handling).</summary>
	private static bool TryReadDecimal(object? value, out decimal parsed)
	{
		switch (value)
		{
			case decimal d:
				parsed = d;
				return true;
			case int i:
				parsed = i;
				return true;
			case long l:
				parsed = l;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDecimal(out parsed):
				return true;
			case JsonElement { ValueKind: JsonValueKind.String } je:
				return decimal.TryParse(je.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
			case string s:
				return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}
}
