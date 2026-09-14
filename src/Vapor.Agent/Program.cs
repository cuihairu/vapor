using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Threading.Channels;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Utilities;
using Vapor.Steam.Core.Logging;
using Vapor.Agent;
using Vapor.Plugins.Core;

static string RequireEnv(string key) => Environment.GetEnvironmentVariable(key) switch
{
	{ Length: > 0 } v => v,
	_ => throw new InvalidOperationException($"{key} is required")
};

// The encryption key must be resolved before anything touches the credential store —
// including the offline maFile import below.
VaporCryptoHelper.ConfigureFromEnvironment(Environment.GetEnvironmentVariable);
VaporCryptoHelper.EnsureSafeForEnvironment(Environment.GetEnvironmentVariable);

// Offline helper: `import-mafile <file-or-dir...> [--password <pw>]` imports SDA /
// steamguard-cli authenticator exports into this agent's encrypted credential store
// and exits. The import runs agent-side on purpose — those secrets must never travel
// through the control plane or land in task records.
if (args.Length > 0 && args[0].Equals("import-mafile", StringComparison.OrdinalIgnoreCase))
{
	using var importLoggerFactory = LoggerFactory.Create(builder => builder.AddRedactingConsole().SetMinimumLevel(LogLevel.Information));
	var importStore = new FileCredentialStore(importLoggerFactory.CreateLogger<FileCredentialStore>());
	int exitCode = await MaFileImportCli.RunAsync(
		args.Skip(1).ToArray(),
		importStore,
		importLoggerFactory.CreateLogger("Vapor.Agent.ImportMaFile"));
	return exitCode;
}

string agentId = RequireEnv("AGENT_ID");
string region = RequireEnv("AGENT_REGION");
string wsUrlBase = RequireEnv("AGENT_CONTROLPLANE_WS_URL");
string agentApiKey = RequireEnv("AGENT_API_KEY");
var reconnectPolicy = AgentReconnectPolicy.FromEnvironment(Environment.GetEnvironmentVariable);

var serviceCollection = new ServiceCollection()
	.AddLogging(configure => configure.AddRedactingConsole())
	.AddSingleton<IActionRegistry, ActionRegistry>()
	.AddSingleton<ICredentialStore, FileCredentialStore>()
	.AddSingleton<Vapor.Steam.Core.Trading.TradeRateLimiter>()
	.AddSingleton<ISessionManager>(p => new SessionManager(
		p.GetRequiredService<IActionRegistry>(),
		p.GetRequiredService<ILogger<SessionManager>>(),
		p.GetRequiredService<ISteamClientManager>(),
		p.GetRequiredService<ICredentialStore>(),
		p.GetRequiredService<ILoggerFactory>()
	))
	.AddSingleton<SteamClientManager>()
	.AddSingleton<ISteamClientManager>(p => p.GetRequiredService<SteamClientManager>())
	.AddSingleton<PingAction>()
	.AddSingleton<IdleAction>()
	.AddSingleton<EchoAction>()
	.AddSingleton<LoginAction>()
	.AddSingleton<PlayGamesAction>()
	.AddSingleton<RedeemKeyAction>()
	.AddSingleton<GetInventoryAction>()
	.AddSingleton<GetCardDropsAction>(p => new GetCardDropsAction(
		p.GetRequiredService<ILogger<GetCardDropsAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Caching.IVaporCache>()))
	.AddSingleton<SendTradeOfferAction>(p => new SendTradeOfferAction(
		p.GetRequiredService<ILogger<SendTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<GetTradeOffersAction>(p => new GetTradeOffersAction(
		p.GetRequiredService<ILogger<GetTradeOffersAction>>()))
	.AddSingleton<GetMyMarketListingsAction>(p => new GetMyMarketListingsAction(
		p.GetRequiredService<ILogger<GetMyMarketListingsAction>>()))
	.AddSingleton<AcceptTradeOfferAction>(p => new AcceptTradeOfferAction(
		p.GetRequiredService<ILogger<AcceptTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<DeclineTradeOfferAction>(p => new DeclineTradeOfferAction(
		p.GetRequiredService<ILogger<DeclineTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<CancelTradeOfferAction>(p => new CancelTradeOfferAction(
		p.GetRequiredService<ILogger<CancelTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<LootInventoryAction>(p => new LootInventoryAction(
		p.GetRequiredService<ILogger<LootInventoryAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<SwapDuplicatesAction>(p => new SwapDuplicatesAction(
		p.GetRequiredService<ILogger<SwapDuplicatesAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<FindDuplicatesAction>(p => new FindDuplicatesAction(
		p.GetRequiredService<ILogger<FindDuplicatesAction>>()))
	.AddSingleton<AddLicenseAction>(p => new AddLicenseAction(
		p.GetRequiredService<ILogger<AddLicenseAction>>()));

// Cache backend: Redis when VAPOR_REDIS points at a server, in-memory otherwise.
string redisConfiguration = Environment.GetEnvironmentVariable("VAPOR_REDIS") ?? string.Empty;
if (!string.IsNullOrWhiteSpace(redisConfiguration))
{
	serviceCollection.AddSingleton<Vapor.Steam.Core.Caching.IVaporCache>(_ =>
		Vapor.Steam.Core.Caching.RedisVaporCache.CreateFromConnectionString(redisConfiguration));
}
else
{
	serviceCollection.AddSingleton<Vapor.Steam.Core.Caching.IVaporCache>(p => new Vapor.Steam.Core.Caching.MemoryVaporCache(
		new Vapor.Steam.Core.Caching.MemoryVaporCacheOptions { Capacity = 4096, DefaultTtl = TimeSpan.FromMinutes(10) }));
}

serviceCollection
	.AddSingleton<GetGameInfoAction>(p => new GetGameInfoAction(
		p.GetRequiredService<ILogger<GetGameInfoAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Caching.IVaporCache>()))
	.AddSingleton<SearchGamesAction>(p => new SearchGamesAction(
		p.GetRequiredService<ILogger<SearchGamesAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Caching.IVaporCache>()))
	.AddSingleton<GetPriceAction>(p => new GetPriceAction(
		p.GetRequiredService<ILogger<GetPriceAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Caching.IVaporCache>()))
	.AddSingleton<GetMarketListingsAction>(p => new GetMarketListingsAction(
		p.GetRequiredService<ILogger<GetMarketListingsAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Caching.IVaporCache>()))
	.AddSingleton<InvalidateCacheAction>(p => new InvalidateCacheAction(
		p.GetRequiredService<ILogger<InvalidateCacheAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Caching.IVaporCache>()));

// Distributed tracing: enabled when the standard OTLP endpoint variable is set.
// Without it no OpenTelemetry SDK is registered and the ActivitySource stays inert.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
{
	serviceCollection.AddOpenTelemetry()
		.ConfigureResource(resource => resource.AddService("vapor-agent"))
		.WithTracing(tracing => tracing
			.AddSource(VaporAgentTracing.SourceName)
			.AddOtlpExporter());
}

var serviceProvider = serviceCollection.BuildServiceProvider();

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var actionRegistry = serviceProvider.GetRequiredService<IActionRegistry>();
var sessionManager = serviceProvider.GetRequiredService<ISessionManager>();
logger.LogInformation(
	"Cache backend: {CacheBackend}",
	string.IsNullOrWhiteSpace(redisConfiguration) ? "memory" : "redis");
logger.LogInformation(
	"Agent reconnect policy: initialDelayMs={InitialDelayMs}, maxDelayMs={MaxDelayMs}, backoffFactor={BackoffFactor}, maxRetries={MaxRetries}",
	reconnectPolicy.InitialDelay.TotalMilliseconds,
	reconnectPolicy.MaxDelay.TotalMilliseconds,
	reconnectPolicy.BackoffFactor,
	reconnectPolicy.IsUnlimitedRetries ? "unlimited" : reconnectPolicy.MaxRetries);

// Set up session event callback to publish to Control Plane
sessionManager.SetEventCallback(async (accountName, eventType, state, message) =>
{
	// Will be called when session state changes or auth challenges occur
	logger.LogInformation("Session event: {AccountName} - {EventType} - {State}", accountName, eventType, state);

	// Send session event to Control Plane via HTTP
	await PublishSessionEventAsync(wsUrlBase, agentApiKey, accountName, eventType, state, message, logger);
});

actionRegistry.Register(serviceProvider.GetRequiredService<PingAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<IdleAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<EchoAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<LoginAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<PlayGamesAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<RedeemKeyAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetInventoryAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetCardDropsAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<SendTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetTradeOffersAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetMyMarketListingsAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<AcceptTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<DeclineTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<CancelTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<LootInventoryAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<FindDuplicatesAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<SwapDuplicatesAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<AddLicenseAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetGameInfoAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<SearchGamesAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetPriceAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetMarketListingsAction>());

// Load plugins (discovery + isolated load + contribution registration).
var (pluginManager, pluginEvents) = await LoadPluginsAsync(serviceProvider, actionRegistry, logger);

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Start one background task to listen for auth challenge events via SSE.
_ = Task.Run(() => PollAuthChallengesAsync(agentId, region, wsUrlBase, agentApiKey, sessionManager, logger, cts.Token), cts.Token);

// Automatic 2FA answering: opt-in via AGENT_2FA_AUTO_SUBMIT=true. Answers challenges
// from the locally stored shared secret (it never leaves this agent); accounts without
// one fall through to the manual SSE channel above.
if (string.Equals(Environment.GetEnvironmentVariable("AGENT_2FA_AUTO_SUBMIT"), "true", StringComparison.OrdinalIgnoreCase))
{
	logger.LogInformation("Automatic 2FA answering is enabled (AGENT_2FA_AUTO_SUBMIT=true)");
	var steamTime = new SteamTimeSynchronizer(
		SteamTimeSynchronizer.QuerySteamServerTimeAsync,
		logger: logger);
	var responder = new TwoFactorAutoResponder(
		sessionManager,
		serviceProvider.GetRequiredService<ICredentialStore>(),
		steamTime,
		logger: serviceProvider.GetRequiredService<ILogger<TwoFactorAutoResponder>>());
	_ = Task.Run(() => RunSteamTimeSyncAsync(steamTime, logger, cts.Token), cts.Token);
	_ = Task.Run(() => responder.RunAsync(cts.Token), cts.Token);
}
else
{
	logger.LogInformation("Automatic 2FA answering is disabled (set AGENT_2FA_AUTO_SUBMIT=true to enable)");
}

var consecutiveFailures = 0;
while (!cts.IsCancellationRequested)
{
	try
	{
		await RunOnce(cts.Token);
		consecutiveFailures = 0;
	}
	catch (OperationCanceledException) when (cts.IsCancellationRequested)
	{
		break;
	}
	catch (Exception ex)
	{
		consecutiveFailures++;
		Console.Error.WriteLine($"agent disconnected: {SensitiveDataRedactor.Redact(ex.Message)}");

		if (reconnectPolicy.HasReachedRetryLimit(consecutiveFailures))
		{
			logger.LogError(
				ex,
				"Agent reconnect retry limit reached after {ConsecutiveFailures} failures. Shutting down.",
				consecutiveFailures);
			break;
		}

		var backoff = reconnectPolicy.GetDelayForAttempt(consecutiveFailures);
		logger.LogWarning(
			"Agent reconnect attempt {Attempt} failed. Waiting {DelayMs}ms before retry.",
			consecutiveFailures,
			backoff.TotalMilliseconds);
		await Task.Delay(backoff, cts.Token);
	}
}

if (pluginManager is not null)
{
	await pluginManager.DisposeAsync();
}

if (pluginEvents is not null)
{
	await pluginEvents.DisposeAsync();
}

return 0;

static async Task<(PluginManager? Manager, PluginEventDispatcher? Events)> LoadPluginsAsync(IServiceProvider services, IActionRegistry actionRegistry, ILogger logger)
{
	var pluginsDir = Environment.GetEnvironmentVariable("VAPOR_PLUGINS_DIR");
	if (string.IsNullOrWhiteSpace(pluginsDir))
	{
		pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
	}

	if (!Directory.Exists(pluginsDir))
	{
		logger.LogDebug("No plugins directory found at {PluginsDirectory}; skipping plugin load", pluginsDir);
		return (null, null);
	}

	var loggerFactory = services.GetRequiredService<ILoggerFactory>();
	var eventDispatcher = new PluginEventDispatcher(loggerFactory);
	eventDispatcher.Start(services.GetRequiredService<ISessionManager>());

	var manager = new PluginManager(
		new DefaultPluginHostServices(loggerFactory, services),
		loggerFactory);

	manager.PluginLoaded += (_, e) =>
	{
		foreach (var action in e.Plugin.Actions)
		{
			actionRegistry.Register(action);
		}

		if (e.Plugin.Instance is IEventPlugin eventPlugin && e.Plugin.GrantedPermissions.Contains(PluginPermissions.Events))
		{
			eventDispatcher.Add(eventPlugin);
		}
	};
	manager.PluginUnloading += (_, e) =>
	{
		foreach (var action in e.Plugin.Actions)
		{
			actionRegistry.Unregister(action.Name);
		}

		if (e.Plugin.Instance is IEventPlugin eventPlugin && e.Plugin.GrantedPermissions.Contains(PluginPermissions.Events))
		{
			eventDispatcher.Remove(eventPlugin);
		}
	};

	var report = await manager.LoadAllAsync(pluginsDir);
	foreach (var failure in report.Failures)
	{
		logger.LogWarning("Plugin load failure: {Failure}", SensitiveDataRedactor.Redact(failure));
	}

	logger.LogInformation(
		"Plugins loaded: {LoadedCount}, failures: {FailureCount}, event subscribers: {EventSubscriberCount}",
		report.Loaded.Count,
		report.Failures.Count,
		eventDispatcher.SubscriberCount);
	return (manager, eventDispatcher);
}

async Task RunOnce(CancellationToken cancellationToken)
{
	Uri uri = AgentWebSocketUri.Build(wsUrlBase, agentId, region);

	using ClientWebSocket ws = new();
	ws.Options.SetRequestHeader("Authorization", $"Bearer {agentApiKey}");

	Console.WriteLine($"connecting: {SensitiveDataRedactor.Redact(uri.ToString())}");
	await ws.ConnectAsync(uri, cancellationToken);

	using SemaphoreSlim sendGate = new(1, 1);
	var tasks = Channel.CreateUnbounded<WSMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

	var executionGate = new object();
	CancellationTokenSource? currentTaskCts = null;
	string? currentTaskId = null;
	int currentAttempt = 0;

	var capabilities = actionRegistry.ListNames().ToDictionary(name => name, _ => true, StringComparer.OrdinalIgnoreCase);
	var hello = new AgentHello(agentId, region, capabilities, null);
	await SendLocked(ws, sendGate, new WSMessage("hello", hello, null, null), cancellationToken);

	var receiver = Task.Run(async () =>
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
			{
				WSMessage msg = await Receive<WSMessage>(ws, cancellationToken);
				if (string.Equals(msg.Type, "task", StringComparison.Ordinal) && msg.Task != null)
				{
					// Keep the whole message: TraceHeaders carries the dispatch span context.
					await tasks.Writer.WriteAsync(msg, cancellationToken);
					continue;
				}

				if (string.Equals(msg.Type, "task_cancel", StringComparison.Ordinal) && msg.TaskCancel != null)
				{
					bool matches;
					lock (executionGate)
					{
						matches =
						  currentTaskCts != null &&
						  string.Equals(currentTaskId, msg.TaskCancel.TaskId, StringComparison.Ordinal) &&
						  currentAttempt == msg.TaskCancel.Attempt;
					}

					if (matches)
					{
						try
						{
							currentTaskCts!.Cancel();
						}
						catch
						{
						}
					}
				}
			}
		}
		catch
		{
			// Receiver loop stops; outer loop will reconnect.
		}
		finally
		{
			tasks.Writer.TryComplete();
		}
	}, cancellationToken);

	try
	{
		while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
		{
			WSMessage dispatch = await tasks.Reader.ReadAsync(cancellationToken);
			JobTask task = dispatch.Task!;

			Console.WriteLine($"task received: id={task.Id} action={task.Action} target={task.Target}");

			using var executeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, executeCts.Token);

			lock (executionGate)
			{
				currentTaskCts = executeCts;
				currentTaskId = task.Id;
				currentAttempt = task.Attempt;
			}

			var heartbeatTask = Task.Run(
				() => HeartbeatLoop(ws, sendGate, task, heartbeatCts.Token),
				heartbeatCts.Token
			);

			bool success;
			string? error;
			IReadOnlyDictionary<string, object?>? output;
			string? replyTraceparent;
			using (Activity? execute = VaporAgentTracing.StartExecuteSpan(task, dispatch.TraceHeaders))
			{
				try
				{
					(success, error, output) = await AgentTaskExecutor.ExecuteAsync(
						task,
						sessionManager,
						logger,
						executeCts.Token
					);

					execute?.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error);
					replyTraceparent = execute?.Id;
				}
				finally
				{
					lock (executionGate)
					{
						currentTaskCts = null;
						currentTaskId = null;
						currentAttempt = 0;
					}
				}
			}

			TaskResult result = new(
				TaskId: task.Id,
				Success: success,
				Error: error,
				Output: output,
				FinishedAt: DateTimeOffset.UtcNow,
				Attempt: task.Attempt
			);

			heartbeatCts.Cancel();
			try
			{
				await heartbeatTask;
			}
			catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
			{
			}

			if (!executeCts.IsCancellationRequested)
			{
				await SendLocked(ws, sendGate, new WSMessage(
					"task_result", null, null, result,
					TraceHeaders: replyTraceparent != null
						? new Dictionary<string, string> { ["traceparent"] = replyTraceparent }
						: null), cancellationToken);
			}
		}
	}
	finally
	{
		try
		{
			await receiver;
		}
		catch
		{
		}
	}
}

static async Task<T> Receive<T>(ClientWebSocket ws, CancellationToken cancellationToken)
{
	ArraySegment<byte> chunk = new(new byte[16 * 1024]);
	using var ms = new MemoryStream();

	while (true)
	{
		WebSocketReceiveResult r = await ws.ReceiveAsync(chunk, cancellationToken);
		if (r.MessageType == WebSocketMessageType.Close)
		{
			throw new IOException("websocket closed");
		}

		ms.Write(chunk.Array!, chunk.Offset, r.Count);
		if (r.EndOfMessage)
		{
			break;
		}
	}

	return JsonSerializer.Deserialize<T>(ms.ToArray(), JsonDefaults.Options) ?? throw new InvalidOperationException("invalid json");
}

static async Task Send<T>(ClientWebSocket ws, T value, CancellationToken cancellationToken)
{
	byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
	await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task SendLocked<T>(ClientWebSocket ws, SemaphoreSlim sendGate, T value, CancellationToken cancellationToken)
{
	await sendGate.WaitAsync(cancellationToken);
	try
	{
		await Send(ws, value, cancellationToken);
	}
	finally
	{
		sendGate.Release();
	}
}

static async Task HeartbeatLoop(ClientWebSocket ws, SemaphoreSlim sendGate, JobTask task, CancellationToken cancellationToken)
{
	static async Task SendHeartbeat(ClientWebSocket ws, SemaphoreSlim sendGate, JobTask task, CancellationToken cancellationToken)
	{
		if (ws.State != WebSocketState.Open)
		{
			return;
		}

		var hb = new TaskHeartbeat(TaskId: task.Id, Attempt: task.Attempt, Ts: DateTimeOffset.UtcNow);
		var msg = new WSMessage(Type: "task_heartbeat", Hello: null, Task: null, TaskResult: null, TaskHeartbeat: hb);
		await SendLocked(ws, sendGate, msg, cancellationToken);
	}

	try
	{
		await SendHeartbeat(ws, sendGate, task, cancellationToken);

		using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
		while (await timer.WaitForNextTickAsync(cancellationToken))
		{
			await SendHeartbeat(ws, sendGate, task, cancellationToken);
		}
	}
	catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
	{
	}
	catch
	{
		// Best-effort: if the websocket is disconnected or errors, don't fail the task itself.
	}
}

/// <summary>
/// Keeps the Steam time offset fresh for TOTP generation: syncs once at startup and
/// then hourly (clock drift is slow, and a stale offset of a few seconds is harmless
/// within the 30s TOTP window).
/// </summary>
static async Task RunSteamTimeSyncAsync(SteamTimeSynchronizer timeSynchronizer, ILogger logger, CancellationToken cancellationToken)
{
	while (!cancellationToken.IsCancellationRequested)
	{
		try
		{
			await timeSynchronizer.SyncAsync(cancellationToken);
			logger.LogDebug("Steam time sync: offset {OffsetSeconds}s", timeSynchronizer.OffsetSeconds);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Steam time sync failed; keeping the previous offset");
		}

		try
		{
			await Task.Delay(TimeSpan.FromHours(1), cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}
}

static async Task PollAuthChallengesAsync(
	string agentId,
	string region,
	string wsUrlBase,
	string agentApiKey,
	ISessionManager sessionManager,
	ILogger logger,
	CancellationToken cancellationToken)
{
	try
	{
		// Build HTTP base URL from WebSocket URL
		var wsUri = new Uri(wsUrlBase);
		var httpScheme = wsUri.Scheme == "wss" ? "https" : "http";
		var httpBaseUrl = $"{httpScheme}://{wsUri.Host}:{wsUri.Port}";

		using var httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentApiKey}");
		httpClient.Timeout = TimeSpan.FromMinutes(5);

		logger.LogInformation("Starting auth challenge polling for agent {AgentId}", agentId);

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				// Connect to auth challenge events stream
				var url = $"{httpBaseUrl}/v1/auth/challenges/events";
				using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
				using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

				response.EnsureSuccessStatusCode();

				using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
				using var reader = new System.IO.StreamReader(stream);

				while (!cancellationToken.IsCancellationRequested)
				{
					var line = await reader.ReadLineAsync(cancellationToken);
					if (line is null) break;
					if (string.IsNullOrWhiteSpace(line)) continue;

					// Parse SSE format: "event: <type>" then "data: <json>"
					if (line.StartsWith("event: ", StringComparison.Ordinal))
					{
						var eventType = line["event: ".Length..].Trim();
						var dataLine = await reader.ReadLineAsync(cancellationToken);
						if (dataLine?.StartsWith("data: ", StringComparison.Ordinal) == true)
						{
							var jsonData = dataLine["data: ".Length..];
							try
							{
								if (eventType is not ("auth.code_provided_email" or "auth.code_provided_totp" or "auth.code_provided_2fa"))
								{
									continue;
								}

								using var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonData);
								var root = jsonDoc.RootElement;

								if (!root.TryGetProperty("accountName", out var accountNameProp))
								{
									continue;
								}

								var accountName = accountNameProp.GetString();
								if (string.IsNullOrWhiteSpace(accountName))
								{
									continue;
								}

								if (!root.TryGetProperty("code", out var codeProp))
								{
									logger.LogWarning("Auth code event missing code for {AccountName}", accountName);
									continue;
								}

								var code = codeProp.GetString();
								if (string.IsNullOrWhiteSpace(code))
								{
									logger.LogWarning("Auth code event has empty code for {AccountName}", accountName);
									continue;
								}

								var session = await sessionManager.GetSessionAsync(accountName, cancellationToken);
								if (session == null)
								{
									logger.LogWarning("Auth code received but no active session for {AccountName}", accountName);
									continue;
								}

								if (eventType == "auth.code_provided_email")
								{
									logger.LogInformation("Applying email auth code for {AccountName}", accountName);
									session.ProvideAuthCode(code);
								}
								else
								{
									logger.LogInformation("Applying 2FA code for {AccountName}", accountName);
									session.Provide2FACode(code);
								}
							}
							catch (System.Text.Json.JsonException ex)
							{
								logger.LogWarning(ex, "Failed to parse auth challenge event: {Data}", SensitiveDataRedactor.Redact(jsonData));
							}
							catch (Exception ex)
							{
								logger.LogWarning(ex, "Failed to handle auth challenge event: {Data}", SensitiveDataRedactor.Redact(jsonData));
							}
						}
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Auth challenge polling error, will retry");
				await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
			}
		}
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Auth challenge polling task failed");
	}
}

static async Task PublishSessionEventAsync(string wsUrlBase, string agentApiKey, string accountName, string eventType, string state, string? message, ILogger logger)
{
	try
	{
		// Build HTTP base URL from WebSocket URL
		var wsUri = new Uri(wsUrlBase);
		var httpScheme = wsUri.Scheme == "wss" ? "https" : "http";
		var httpBaseUrl = $"{httpScheme}://{wsUri.Host}:{wsUri.Port}";

		using var httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentApiKey}");
		httpClient.Timeout = TimeSpan.FromSeconds(10);

		var payload = new
		{
			accountName,
			eventType,
			state,
			message,
			timestamp = DateTimeOffset.UtcNow
		};

		var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
		var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

		// POST to a new endpoint that will publish the event
		var response = await httpClient.PostAsync($"{httpBaseUrl}/v1/sessions/events", content);

		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("Failed to publish session event: {StatusCode}", response.StatusCode);
		}
	}
	catch (Exception ex)
	{
		logger.LogWarning(ex, "Failed to publish session event for {AccountName}", accountName);
	}
}
