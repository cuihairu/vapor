using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Caching;

namespace Vapor.Plugins.Monitoring;

/// <summary>
/// Monitoring plugin: collects action, cache, session and runtime metrics and exposes
/// them in Prometheus text format via a self-hosted HTTP endpoint (the Agent has no web
/// server of its own). Also contributes a <c>get_metrics</c> action and a
/// <c>/metrics</c> plugin web route for pull-based access through hosts.
/// </summary>
public sealed class MonitoringPlugin : IPlugin, IActionPlugin, IWebApiPlugin
{
	/// <summary>Default TCP port for the self-hosted metrics endpoint.</summary>
	public const int DefaultPort = 9700;

	private readonly MetricsRegistry _metrics = new();
	private ILoggerFactory? _loggerFactory;
	private ILogger<MonitoringPlugin>? _logger;
	private MetricsHttpServer? _server;
	private ActionRegistry? _actionRegistry;
	private IVaporCache? _cache;
	private ISessionManager? _sessionManager;
	private CancellationTokenSource? _sessionPumpCts;
	private Task? _sessionPump;
	private ActionMetricsObserver? _actionObserver;
	private long _startTimestamp;

	public PluginInfo Info { get; } = new(
		Id: "vapor.monitoring",
		Name: "Vapor Monitoring",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Prometheus metrics endpoint: actions, cache, sessions and runtime");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_loggerFactory = context.Host.LoggerFactory;
		_logger = _loggerFactory.CreateLogger<MonitoringPlugin>();
		_startTimestamp = Environment.TickCount64;

		var config = context.Configuration;
		var host = GetString(config, "metrics.host", "127.0.0.1", "VAPOR_METRICS_HOST");
		var port = GetInt(config, "metrics.port", DefaultPort, "VAPOR_METRICS_PORT");
		var path = GetString(config, "metrics.path", "/metrics", "VAPOR_METRICS_PATH");

		_actionRegistry = context.Host.Services.GetService(typeof(IActionRegistry)) as ActionRegistry;
		_cache = context.Host.Services.GetService(typeof(IVaporCache)) as IVaporCache;
		_sessionManager = context.Host.Services.GetService(typeof(ISessionManager)) as ISessionManager;

		if (_actionRegistry is null)
		{
			_logger.LogDebug("Host does not expose a concrete ActionRegistry; action metrics disabled");
		}
		else
		{
			_actionObserver = new ActionMetricsObserver(_metrics);
			_actionRegistry.AddExecutionObserver(_actionObserver);
		}

		if (_cache is null)
		{
			_logger.LogDebug("Host does not expose IVaporCache; cache metrics disabled");
		}

		_server = new MetricsHttpServer(
			host,
			port,
			path,
			RenderMetricsSnapshot,
			_loggerFactory.CreateLogger<MetricsHttpServer>());
		try
		{
			_server.Start();
			_logger.LogInformation(
				"Monitoring metrics endpoint listening on http://{Host}:{Port}{Path}", host, _server.Port, path);
		}
		catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ArgumentException)
		{
			_logger.LogError(ex, "Failed to bind monitoring metrics endpoint on {Host}:{Port}; endpoint disabled", host, port);
			_server.Dispose();
			_server = null;
		}

		StartSessionPump();

		return Task.CompletedTask;
	}

	public async Task ShutdownAsync(CancellationToken cancellationToken)
	{
		if (_actionRegistry is not null && _actionObserver is not null)
		{
			_actionRegistry.RemoveExecutionObserver(_actionObserver);
			_actionObserver = null;
		}

		_sessionPumpCts?.Cancel();
		if (_sessionPump is not null)
		{
			try
			{
				await _sessionPump.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		_sessionPumpCts?.Dispose();
		_sessionPumpCts = null;
		_sessionPump = null;

		_server?.Stop();
		_server?.Dispose();
		_server = null;
	}

	public IEnumerable<IAction> GetActions()
	{
		if (_loggerFactory is null)
		{
			throw new InvalidOperationException("Plugin has not been initialized");
		}

		yield return new GetMetricsAction(RenderMetricsSnapshot, RenderMetricsJson);
	}

	public IEnumerable<PluginWebRoute> GetRoutes()
	{
		yield return new PluginWebRoute(
			"GET",
			"/metrics",
			(request, cancellationToken) =>
				Task.FromResult(PluginWebResponse.Json(RenderMetricsJson())));
	}

	/// <summary>Prometheus text exposition of the current snapshot.</summary>
	internal string RenderMetricsSnapshot()
	{
		CollectRuntimeMetrics();
		return _metrics.RenderPrometheus();
	}

	/// <summary>JSON summary for the plugin web route and the get_metrics action.</summary>
	internal string RenderMetricsJson()
	{
		CollectRuntimeMetrics();

		var payload = new
		{
			uptimeSeconds = (Environment.TickCount64 - _startTimestamp) / 1000.0,
			actionsRegistered = _actionRegistry?.ListNames().Count ?? 0,
			cache = _cache is null
				? null
				: new { entries = _cache.Count, hits = _cache.Hits, misses = _cache.Misses },
			executionsTotal = _metrics.SumSeries("vapor_action_executions_total")
		};

		return JsonSerializer.Serialize(payload);
	}

	private void CollectRuntimeMetrics()
	{
		_metrics.GaugeSet("process_uptime_seconds", "Process uptime in seconds.", (Environment.TickCount64 - _startTimestamp) / 1000.0);
		_metrics.GaugeSet("process_working_set_bytes", "Process working set in bytes.", Environment.WorkingSet);
		_metrics.GaugeSet("dotnet_gc_heap_size_bytes", "Total managed heap size (GC.GetTotalMemory).", GC.GetTotalMemory(forceFullCollection: false));
		_metrics.GaugeSet("dotnet_threadpool_threads", "Thread pool worker threads currently in use.", ThreadPool.ThreadCount);
		_metrics.GaugeSet("dotnet_threadpool_pending_work_items", "Thread pool work items queued.", ThreadPool.PendingWorkItemCount);

		for (var generation = 0; generation <= GC.MaxGeneration; generation++)
		{
			_metrics.CounterSet(
				"dotnet_gc_collections_total",
				"GC collections since process start, per generation.",
				GC.CollectionCount(generation),
				("generation", generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		if (_actionRegistry is not null)
		{
			_metrics.GaugeSet("vapor_actions_registered", "Actions currently registered in the host registry.", _actionRegistry.ListNames().Count);
		}

		if (_cache is not null)
		{
			_metrics.CounterSet("vapor_cache_hits_total", "Cache hits since process start.", _cache.Hits);
			_metrics.CounterSet("vapor_cache_misses_total", "Cache misses since process start.", _cache.Misses);
			_metrics.GaugeSet("vapor_cache_entries", "Live entries in the Vapor cache.", _cache.Count);
		}
	}

	private void StartSessionPump()
	{
		if (_sessionManager is null)
		{
			_logger?.LogDebug("Host does not expose ISessionManager; session metrics disabled");
			return;
		}

		_sessionPumpCts = new CancellationTokenSource();
		var token = _sessionPumpCts.Token;
		var sessionManager = _sessionManager;
		_sessionPump = Task.Run(async () =>
		{
			try
			{
				await foreach (var evt in sessionManager.SubscribeAllEvents(token).ConfigureAwait(false))
				{
					_metrics.CounterInc(
						"vapor_session_events_total",
						"Total session events observed by the monitoring plugin.",
						1,
						("type", evt.Type.ToString()));
					GaugeSessions(sessionManager);
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "Session metrics pump stopped unexpectedly");
			}
		}, token);
	}

	private void GaugeSessions(ISessionManager sessionManager)
	{
		try
		{
			var sessions = sessionManager.ListSessions();
			_metrics.GaugeSet("vapor_sessions_active", "Currently tracked bot sessions.", sessions.Count);
			foreach (var group in sessions.GroupBy(static s => s.State.ToString()).OrderBy(static g => g.Key, StringComparer.Ordinal))
			{
				_metrics.GaugeSet(
					"vapor_sessions_by_state",
					"Bot sessions grouped by session state.",
					group.Count(),
					("state", group.Key));
			}
		}
		catch (Exception ex)
		{
			_logger?.LogDebug(ex, "Failed to sample session metrics");
		}
	}

	private static string GetString(IReadOnlyDictionary<string, string> config, string key, string fallback, string? envVar = null)
	{
		var fromEnv = envVar is null ? null : Environment.GetEnvironmentVariable(envVar);
		if (!string.IsNullOrWhiteSpace(fromEnv))
		{
			return fromEnv;
		}

		return config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
	}

	private static int GetInt(IReadOnlyDictionary<string, string> config, string key, int fallback, string? envVar = null)
	{
		var fromEnv = envVar is null ? null : Environment.GetEnvironmentVariable(envVar);
		if (!string.IsNullOrWhiteSpace(fromEnv) && int.TryParse(fromEnv, out var envParsed) && envParsed is >= 0 and <= 65535)
		{
			return envParsed;
		}

		return config.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) && parsed is >= 0 and <= 65535 ? parsed : fallback;
	}

	/// <summary>Turns registry execution notifications into action metrics.</summary>
	private sealed class ActionMetricsObserver(MetricsRegistry metrics) : IActionExecutionObserver
	{
		public void OnActionExecuted(string actionName, bool success, double durationMs)
		{
			var status = success ? "success" : "failure";
			metrics.CounterInc(
				"vapor_action_executions_total",
				"Total action executions, by action and outcome.",
				1,
				("action", actionName),
				("status", status));
			metrics.CounterInc(
				"vapor_action_duration_seconds_sum",
				"Cumulative action execution time in seconds, by action and outcome.",
				durationMs / 1000.0,
				("action", actionName),
				("status", status));
		}
	}
}
