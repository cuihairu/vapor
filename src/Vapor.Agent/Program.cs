using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Utilities;
using Vapor.Steam.Core.Logging;
using Vapor.Agent;

static string RequireEnv(string key) => Environment.GetEnvironmentVariable(key) switch
{
	{ Length: > 0 } v => v,
	_ => throw new InvalidOperationException($"{key} is required")
};

// The agent's encrypted credential store normally lives under the OS user
// profile (~/.vapor). A test/container host may not resolve that path from a
// relocated $HOME, so the directory is overridable via VAPOR_CREDENTIALS_DIR
// (mirrors VAPOR_PLUGINS_DIR).
static string CredentialDirectory() =>
	Environment.GetEnvironmentVariable("VAPOR_CREDENTIALS_DIR") ?? "";

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
	// CA2000 suppressed: ownership of importStore transfers to MaFileImportCli.RunAsync,
	// which disposes it before returning.
#pragma warning disable CA2000
	string credentialDir = CredentialDirectory();
	var importStore = new FileCredentialStore(
		importLoggerFactory.CreateLogger<FileCredentialStore>(),
		string.IsNullOrEmpty(credentialDir) ? null : credentialDir);
	int exitCode = await MaFileImportCli.RunAsync(
		args.Skip(1).ToArray(),
		importStore,
		importLoggerFactory.CreateLogger("Vapor.Agent.ImportMaFile"));
	return exitCode;
#pragma warning restore CA2000
}

string agentId = RequireEnv("AGENT_ID");
string region = RequireEnv("AGENT_REGION");
string wsUrlBase = RequireEnv("AGENT_CONTROLPLANE_WS_URL");
string agentApiKey = RequireEnv("AGENT_API_KEY");
var reconnectPolicy = AgentReconnectPolicy.FromEnvironment(Environment.GetEnvironmentVariable);
var taskTimeoutPolicy = TaskTimeoutPolicy.FromEnvironment(Environment.GetEnvironmentVariable);

var serviceCollection = new ServiceCollection()
	.AddLogging(configure => configure.AddRedactingConsole())
	.AddSingleton<IActionRegistry, ActionRegistry>()
	.AddSingleton<ICredentialStore>(p => new FileCredentialStore(
		p.GetRequiredService<ILogger<FileCredentialStore>>(),
		string.IsNullOrEmpty(CredentialDirectory()) ? null : CredentialDirectory()))
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
	.AddSingleton<CheckProxyAction>()
	.AddSingleton<CheckAccountStandingAction>()
	.AddSingleton<LoginAction>()
	.AddSingleton<SendTradeOfferAction>(p => new SendTradeOfferAction(
		p.GetRequiredService<ILogger<SendTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<GetTradeOffersAction>(p => new GetTradeOffersAction(
		p.GetRequiredService<ILogger<GetTradeOffersAction>>()))
	.AddSingleton<GetMyMarketListingsAction>(p => new GetMyMarketListingsAction(
		p.GetRequiredService<ILogger<GetMyMarketListingsAction>>()))
	.AddSingleton<CancelMarketListingsAction>(p => new CancelMarketListingsAction(
		p.GetRequiredService<ILogger<CancelMarketListingsAction>>()))
	// Real market listing creation is opt-in per agent (default off, aligned
	// with AGENT_2FA_AUTO_SUBMIT); the account-level switch is enforced by the
	// control plane. Dry runs (send absent) are unrestricted either way.
	.AddSingleton<CreateMarketListingAction>(p => new CreateMarketListingAction(
		p.GetRequiredService<ILogger<CreateMarketListingAction>>(),
		marketListingsEnabled: string.Equals(Environment.GetEnvironmentVariable("AGENT_MARKET_LISTINGS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase)))
	.AddSingleton<AcceptTradeOfferAction>(p => new AcceptTradeOfferAction(
		p.GetRequiredService<ILogger<AcceptTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<DeclineTradeOfferAction>(p => new DeclineTradeOfferAction(
		p.GetRequiredService<ILogger<DeclineTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()))
	.AddSingleton<CancelTradeOfferAction>(p => new CancelTradeOfferAction(
		p.GetRequiredService<ILogger<CancelTradeOfferAction>>(),
		p.GetRequiredService<Vapor.Steam.Core.Trading.TradeRateLimiter>()));

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
	.AddSingleton<GetGameInfoBatchAction>(p => new GetGameInfoBatchAction(
		p.GetRequiredService<ILogger<GetGameInfoBatchAction>>(),
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
logger.LogInformation(
	"Task watchdog: {TaskWatchdog}",
	taskTimeoutPolicy.IsEnabled ? $"{(int)taskTimeoutPolicy.Timeout.TotalSeconds}s" : "disabled");

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
actionRegistry.Register(serviceProvider.GetRequiredService<CheckProxyAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<CheckAccountStandingAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<LoginAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<SendTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetTradeOffersAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetMyMarketListingsAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<CancelMarketListingsAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<CreateMarketListingAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<AcceptTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<DeclineTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<CancelTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetGameInfoAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetGameInfoBatchAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<SearchGamesAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetPriceAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetMarketListingsAction>());

// Load plugins (discovery + isolated load + contribution registration).
var (pluginManager, pluginEvents, pluginsDirectory) = await LoadPluginsAsync(serviceProvider, actionRegistry, logger);

// Host-scoped actions run on the machine itself (no bot session): plugin lifecycle.
// They ride the regular task pipeline so retries/audits/jobs-panel apply, and are
// advertised in hello capabilities like any other action.
var hostLoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
// Operator-pinned publisher keys (base64 SPKI, comma-separated) anchor declared
// package signatures; an empty pin list makes signed installs fail closed.
IReadOnlyList<string> trustedPublisherKeys = (Environment.GetEnvironmentVariable("VAPOR_PLUGIN_TRUST_KEYS") ?? string.Empty)
	.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var installer = new PluginPackageInstaller(pluginsDirectory, null, hostLoggerFactory.CreateLogger<PluginPackageInstaller>(), trustedPublisherKeys);
IReadOnlyDictionary<string, IHostAction> hostActions = new Dictionary<string, IHostAction>(StringComparer.OrdinalIgnoreCase)
{
	["plugin_install"] = new PluginInstallAction(installer, pluginManager, hostLoggerFactory.CreateLogger<PluginInstallAction>()),
	["plugin_uninstall"] = new PluginUninstallAction(pluginsDirectory, pluginManager, hostLoggerFactory.CreateLogger<PluginUninstallAction>()),
	["plugin_list"] = new PluginListAction(pluginsDirectory, pluginManager),
	["plugin_update_check"] = new PluginUpdateCheckAction(pluginManager),
	["script_exec"] = new ScriptExecAction(),
	["set_proxy"] = new SetProxyAction(sessionManager, hostLoggerFactory.CreateLogger<SetProxyAction>())
};

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
		await RunOnce(wsUrlBase, agentId, region, agentApiKey, taskTimeoutPolicy, actionRegistry, sessionManager, hostActions, logger, cts.Token);
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

await pluginManager.DisposeAsync();
await pluginEvents.DisposeAsync();

return 0;
