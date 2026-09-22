using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Monitoring.Tests;

public class MonitoringPluginTests
{

	[Fact]
	public async Task Plugin_Initialize_StartsEndpointAndContributesActionAndRoute()
	{
		var plugin = new MonitoringPlugin();
		var context = new StubPluginContext(plugin.Info, new StubServiceProvider());

		await plugin.InitializeAsync(context, CancellationToken.None);
		try
		{
			Assert.Equal("vapor.monitoring", plugin.Info.Id);

			var action = Assert.Single(plugin.GetActions());
			Assert.Equal("get_metrics", action.Name);
			Assert.False(action.Metadata.RequiresLogin);

			var route = Assert.Single(plugin.GetRoutes());
			Assert.Equal("GET", route.Method);
			Assert.Equal("/metrics", route.Path);
			var response = await route.Handler(new PluginWebRequest("/metrics", Empty, Empty, null), CancellationToken.None);
			Assert.Equal(200, response.StatusCode);

			var exposition = plugin.RenderMetricsSnapshot();
			Assert.Contains("process_uptime_seconds", exposition, StringComparison.Ordinal);
			Assert.Contains("# TYPE dotnet_gc_collections_total counter", exposition, StringComparison.Ordinal);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Plugin_Configuration_EphemeralPortAndCustomPath()
	{
		var plugin = new MonitoringPlugin();
		var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["metrics.port"] = "0",
			["metrics.path"] = "telemetry/metrics"
		};
		var context = new StubPluginContext(plugin.Info, new StubServiceProvider(), config);

		await plugin.InitializeAsync(context, CancellationToken.None);
		try
		{
			// A custom relative path is normalized to a root-relative one; nothing is
			// directly reachable through the plugin object, but exposition must work.
			Assert.Contains("process_working_set_bytes", plugin.RenderMetricsSnapshot(), StringComparison.Ordinal);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Plugin_Shutdown_StopsServerAndActionsThrowBeforeInitialize()
	{
		var plugin = new MonitoringPlugin();
		var context = new StubPluginContext(plugin.Info, new StubServiceProvider());

		await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => plugin.GetActions().ToList()));

		await plugin.InitializeAsync(context, CancellationToken.None);
		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task GetMetricsAction_ReturnsPrometheusAndJsonSummary()
	{
		var plugin = new MonitoringPlugin();
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, new StubServiceProvider()), CancellationToken.None);
		try
		{
			var action = Assert.Single(plugin.GetActions());
			var session = TestSession.Create();
			var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

			Assert.True(result.Success);
			Assert.NotNull(result.Output);
			Assert.Equal("prometheus", result.Output!["format"]);
			Assert.Contains("process_uptime_seconds", (string)result.Output["metrics"]!, StringComparison.Ordinal);

			var summary = JsonDocument.Parse((string)result.Output["summary"]!);
			Assert.True(summary.RootElement.TryGetProperty("uptimeSeconds", out _));
			Assert.True(summary.RootElement.TryGetProperty("executionsTotal", out _));
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Plugin_WithHostServices_CollectsActionCacheAndSessionMetrics()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var cache = new MemoryVaporCache(new MemoryVaporCacheOptions());
		var sessionManager = new StubSessionManager();
		var services = new StubServiceProvider(new Dictionary<Type, object>
		{
			[typeof(IActionRegistry)] = registry,
			[typeof(IVaporCache)] = cache,
			[typeof(ISessionManager)] = sessionManager
		});

		var plugin = new MonitoringPlugin();
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, services), CancellationToken.None);
		try
		{
			registry.Register(new StubAction("echo"));
			registry.RaiseActionExecuted("echo", success: true, durationMs: 12.5);
			registry.RaiseActionExecuted("echo", success: false, durationMs: 3);
			await cache.SetAsync("k", "v");

			var exposition = plugin.RenderMetricsSnapshot();
			Assert.Contains("vapor_action_executions_total{action=\"echo\",status=\"success\"} 1", exposition, StringComparison.Ordinal);
			Assert.Contains("vapor_action_executions_total{action=\"echo\",status=\"failure\"} 1", exposition, StringComparison.Ordinal);
			Assert.Contains("vapor_cache_entries 1", exposition, StringComparison.Ordinal);

			var summary = JsonDocument.Parse(plugin.RenderMetricsJson());
			Assert.Equal(2, summary.RootElement.GetProperty("executionsTotal").GetDouble());
			Assert.Equal(1, summary.RootElement.GetProperty("actionsRegistered").GetInt32());
			Assert.Equal(1, summary.RootElement.GetProperty("cache").GetProperty("entries").GetInt32());

			sessionManager.PublishState("acct", SessionState.Connected);
			sessionManager.PublishState("acct", SessionState.Disconnected);
			await WaitForConditionAsync(() =>
				plugin.RenderMetricsSnapshot().Contains(
					"vapor_session_events_total{type=\"StateChanged\"} 2", StringComparison.Ordinal));
			Assert.True(sessionManager.Observed >= 2);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Initialize_BindFails_PluginRemainsUsableWithoutEndpoint()
	{
		// Occupying an ephemeral port with a standalone server makes the plugin's bind on
		// the same port fail; the plugin must log, disable the endpoint and stay usable
		// for the in-process metrics surface.
		var holder = new MetricsHttpServer("127.0.0.1", 0, "/metrics", () => "held\n");
		holder.Start();
		try
		{
			var plugin = new MonitoringPlugin();
			var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["metrics.port"] = holder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
			};
			var context = new StubPluginContext(plugin.Info, new StubServiceProvider(), config);

			await plugin.InitializeAsync(context, CancellationToken.None);
			try
			{
				// The endpoint field is internal state; reflect it to prove the failure path
				// released the failed server instead of leaving it half-bound.
				var serverField = typeof(MonitoringPlugin).GetField("_server", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				Assert.Null(serverField!.GetValue(plugin));

				var summary = JsonDocument.Parse(plugin.RenderMetricsJson());
				Assert.True(summary.RootElement.TryGetProperty("uptimeSeconds", out _));
			}
			finally
			{
				await plugin.ShutdownAsync(CancellationToken.None);
			}
		}
		finally
		{
			holder.Dispose();
		}
	}

	[Fact]
	public async Task SessionPump_SourceCompletes_SamplesSessionGauges()
	{
		// A session manager whose event stream finishes on its own drives the pump's
		// normal-completion path, including the per-state session gauge sampling.
		var manager = new CompletingSessionManager([TestSession.Create()]);
		var services = new StubServiceProvider(new Dictionary<Type, object>
		{
			[typeof(ISessionManager)] = manager
		});

		var plugin = new MonitoringPlugin();
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, services), CancellationToken.None);
		try
		{
			// Wait on the manager's own counter, then give the pump a moment to finish its
			// post-event gauge sampling, so we never render while the pump is writing —
			// the registry does not support a reader racing a writer.
			await WaitForConditionAsync(() => manager.Observed >= 1);
			await Task.Delay(300);

			var exposition = plugin.RenderMetricsSnapshot();
			Assert.Contains("vapor_session_events_total{type=\"StateChanged\"} 1", exposition, StringComparison.Ordinal);
			Assert.Contains("vapor_sessions_active 1", exposition, StringComparison.Ordinal);
			Assert.Contains("vapor_sessions_by_state{state=\"Disconnected\"} 1", exposition, StringComparison.Ordinal);
		}
		finally
		{
			// The pump already drained by itself; shutdown must not hang or throw.
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task SessionPump_SourceThrows_IsSwallowedAndShutdownStillWorks()
	{
		// A session event source that crashes with a non-cancellation error stops the pump
		// quietly; the plugin keeps rendering metrics and shuts down cleanly.
		var services = new StubServiceProvider(new Dictionary<Type, object>
		{
			[typeof(ISessionManager)] = new ThrowingSourceSessionManager()
		});

		var plugin = new MonitoringPlugin();
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, services), CancellationToken.None);

		await Task.Delay(100);
		await plugin.ShutdownAsync(CancellationToken.None);

		Assert.Contains("process_uptime_seconds", plugin.RenderMetricsSnapshot(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SessionPump_ListSessionsThrows_SamplingFailureIsSwallowed()
	{
		// Sampling session gauges must never take the pump down: a ListSessions that
		// throws is logged per event while the event counters keep advancing.
		var manager = new ThrowingListSessionManager();
		var services = new StubServiceProvider(new Dictionary<Type, object>
		{
			[typeof(ISessionManager)] = manager
		});

		var plugin = new MonitoringPlugin();
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, services), CancellationToken.None);
		try
		{
			await WaitForConditionAsync(() =>
				plugin.RenderMetricsSnapshot().Contains(
					"vapor_session_events_total{type=\"StateChanged\"} 1", StringComparison.Ordinal));

			Assert.DoesNotContain("vapor_sessions_active", plugin.RenderMetricsSnapshot(), StringComparison.Ordinal);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task SessionPump_ListSessionsThrows_WithSuppressedLogger_StaysQuiet()
	{
		// Same sampling failure, but with the plugin logger reflected to null: the
		// diagnostic short-circuit arm must keep the pump alive without formatting.
		// The gated stub holds its first event back until the logger is already
		// suppressed, so the short-circuit (not the logging) arm is deterministic.
		var manager = new GatedThrowingListSessionManager();
		var services = new StubServiceProvider(new Dictionary<Type, object>
		{
			[typeof(ISessionManager)] = manager
		});

		var plugin = new MonitoringPlugin();
		await plugin.InitializeAsync(new StubPluginContext(plugin.Info, services), CancellationToken.None);
		try
		{
			typeof(MonitoringPlugin).GetField("_logger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
				.SetValue(plugin, null);

			manager.Open();
			await WaitForConditionAsync(() => manager.Observed >= 1);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Plugin_LoadsThroughPluginManager()
	{
		var root = Path.Combine(Path.GetTempPath(), "vapor-monitoring-plugin-tests", Guid.NewGuid().ToString("N"));
		var pluginDir = Path.Combine(root, "vapor.monitoring");
		Directory.CreateDirectory(pluginDir);

		foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "Vapor.Plugins.Monitoring.dll"))
		{
			File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)));
		}

		File.Copy(Path.Combine(AppContext.BaseDirectory, "plugin.json"), Path.Combine(pluginDir, "plugin.json"));

		try
		{
			await using var manager = new PluginManager(
				new DefaultPluginHostServices(NullLoggerFactory.Instance, new StubServiceProvider()),
				NullLoggerFactory.Instance);

			var report = await manager.LoadAllAsync(root);

			Assert.Empty(report.Failures);
			var loaded = Assert.Single(report.Loaded);
			Assert.Equal("vapor.monitoring", loaded.Info.Id);
			Assert.Single(loaded.Actions);
			Assert.Single(loaded.Routes);

			Assert.True(await manager.UnloadAsync(loaded.Info.Id));
		}
		finally
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort cleanup; the load context may still hold the assembly
				// (Windows raises UnauthorizedAccessException for directories with open files).
			}
		}
	}

	private static IReadOnlyDictionary<string, string> Empty { get; } = new Dictionary<string, string>();

	private static async Task WaitForConditionAsync(Func<bool> condition, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (Environment.TickCount64 < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(25);
		}

		Assert.Fail("Condition was not met within the timeout");
	}

	private sealed class StubAction(string name) : IAction
	{
		public string Name => name;

		public ActionMetadata Metadata { get; } = new(name, "Stub action for monitoring tests.");

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken) => Task.FromResult(new ActionResult(true));
	}

	private sealed class StubSessionManager : ISessionManager
	{
		private readonly System.Threading.Channels.Channel<SessionEvent> _events =
			System.Threading.Channels.Channel.CreateUnbounded<SessionEvent>();

		public int Observed { get; private set; }

		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public IReadOnlyList<BotSession> ListSessions() => [];

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await foreach (var evt in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				Observed++;
				yield return evt;
			}
		}

		public void PublishState(string accountName, SessionState state)
		{
			_events.Writer.TryWrite(new SessionEvent(SessionEventType.StateChanged, accountName, state));
		}
	}

	/// <summary>Session manager whose event stream yields one event and then completes.</summary>
	private sealed class CompletingSessionManager(IReadOnlyList<BotSession> sessions) : ISessionManager
	{
		public int Observed { get; private set; }

		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public IReadOnlyList<BotSession> ListSessions() => sessions;

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			Observed++;
			yield return new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected);
			// Let the stream end naturally so the pump drains without cancellation.
		}
	}

	/// <summary>Session manager whose event subscription itself crashes.</summary>
	private sealed class ThrowingSourceSessionManager : ISessionManager
	{
		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public IReadOnlyList<BotSession> ListSessions() => [];

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public IAsyncEnumerable<SessionEvent> SubscribeAllEvents(CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("session source exploded");
	}

	/// <summary>
	/// Session manager with a working event stream but a ListSessions that always fails —
	/// the gauge sampler must swallow that per event.
	/// </summary>
	private sealed class ThrowingListSessionManager : ISessionManager
	{
		public int Observed { get; private set; }

		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public IReadOnlyList<BotSession> ListSessions() => throw new InvalidOperationException("list exploded");

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			Observed++;
			yield return new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected);
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}
	}

	/// <summary>
	/// ThrowingListSessionManager whose first event is held back until Open() — lets a
	/// test suppress the plugin logger before the (failing) first gauge sample runs.
	/// </summary>
	private sealed class GatedThrowingListSessionManager : ISessionManager
	{
		private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public int Observed { get; private set; }

		public void Open() => _gate.TrySetResult();

		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default) => Task.CompletedTask;

		public IReadOnlyList<BotSession> ListSessions() => throw new InvalidOperationException("list exploded");

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default) =>
			Task.FromResult<BotSession?>(null);

		public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await _gate.Task.ConfigureAwait(false);
			Observed++;
			yield return new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected);
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}
	}
}
