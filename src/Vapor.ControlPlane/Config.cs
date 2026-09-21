namespace Vapor.ControlPlane;

public sealed record Config(
	string AdminApiKey,
	IReadOnlySet<string> AgentApiKeys,
	string DbPath,
	int TaskLeaseSeconds,
	bool EnableSwagger,
	string AuditDbPath = "data/audit.db",
	int TaskMaxDispatchAttempts = 10,
	int TaskDispatchRetryDelayMs = 2000,
	int ReconcileIntervalSeconds = 15,
	int ReconcileMaxAccountsPerAgent = 25,
	int ReconcileMaxLoginAttempts = 3,
	int ReconcileLoginCooldownSeconds = 60,
	int ReconcileSessionStalenessSeconds = 120,
	int ReconcileFarmRefreshSeconds = 300,
	int ReconcileBoostRefreshSeconds = 1800,
	int ReconcileTradeRefreshSeconds = 600,
	int ReconcileStandingRefreshSeconds = 21600,
	bool ReconcileDryRun = false,
	string? WebhookNotificationsUrl = null,
	string? WebhookNotificationsSecret = null,
	string? WebhookNotificationsEvents = null,
	int WebhookNotificationsMaxRetries = 3,
	int WebhookNotificationsRetryBaseDelayMs = 500,
	string CrawlDbPath = "data/crawl.db",
	int CrawlWorkerTickSeconds = 5,
	int CrawlKeepRuns = 10,
	int CrawlMaxAppsPerPlan = 500,
	int CrawlMaxAppsPerTask = 200,
	int CrawlRunTimeoutSeconds = 1800,
	int CrawlIntervalMs = 500,
	string? PluginIndexUrl = null
)
{
	/// <summary>Max dispatch attempts per task before it fails permanently; 0 or less means unlimited retries.</summary>
	public bool HasDispatchAttemptLimit => TaskMaxDispatchAttempts > 0;

	public static Config LoadFromEnvironment()
	{
		string adminApiKey = Environment.GetEnvironmentVariable("Vapor_ADMIN_API_KEY") ?? "";
		string agentApiKeysRaw = Environment.GetEnvironmentVariable("Vapor_AGENT_API_KEYS") ?? "";
		string dbPath = Environment.GetEnvironmentVariable("Vapor_DB_PATH") ?? "data/controlplane.db";
		int taskLeaseSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_LEASE_SECONDS"), out int v) && v > 0 ? v : 300;
		bool enableSwagger = string.Equals(Environment.GetEnvironmentVariable("Vapor_ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);
		string auditDbPath = Environment.GetEnvironmentVariable("Vapor_AUDIT_DB_PATH") ?? "data/audit.db";
		int taskMaxDispatchAttempts = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_MAX_DISPATCH_ATTEMPTS"), out int attempts) ? attempts : 10;
		int taskDispatchRetryDelayMs = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_DISPATCH_RETRY_DELAY_MS"), out int delayMs) && delayMs >= 0 ? delayMs : 2000;
		int reconcileIntervalSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_INTERVAL_SECONDS"), out int reconcileInterval) ? reconcileInterval : 15;
		int reconcileMaxAccountsPerAgent = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_MAX_ACCOUNTS_PER_AGENT"), out int maxPerAgent) && maxPerAgent > 0 ? maxPerAgent : 25;
		int reconcileMaxLoginAttempts = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS"), out int maxLoginAttempts) && maxLoginAttempts > 0 ? maxLoginAttempts : 3;
		int reconcileLoginCooldownSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS"), out int loginCooldown) && loginCooldown >= 0 ? loginCooldown : 60;
		int reconcileSessionStalenessSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_SESSION_STALENESS_SECONDS"), out int staleness) && staleness > 0 ? staleness : 120;
		int reconcileFarmRefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_FARM_REFRESH_SECONDS"), out int farmRefresh) && farmRefresh > 0 ? farmRefresh : 300;
		int reconcileBoostRefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_BOOST_REFRESH_SECONDS"), out int boostRefresh) && boostRefresh > 0 ? boostRefresh : 1800;
		int reconcileTradeRefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_TRADE_REFRESH_SECONDS"), out int tradeRefresh) && tradeRefresh > 0 ? tradeRefresh : 600;
		int reconcileStandingRefreshSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_RECONCILE_STANDING_REFRESH_SECONDS"), out int standingRefresh) && standingRefresh > 0 ? standingRefresh : 21600;
		bool reconcileDryRun = string.Equals(Environment.GetEnvironmentVariable("Vapor_RECONCILE_DRY_RUN"), "true", StringComparison.OrdinalIgnoreCase);
		string? webhookUrl = Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_URL");
		string? webhookSecret = Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_SECRET");
		string? webhookEvents = Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_EVENTS");
		int webhookMaxRetries = int.TryParse(Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_MAX_RETRIES"), out int whRetries) && whRetries >= 0 ? whRetries : 3;
		int webhookRetryBaseDelayMs = int.TryParse(Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_RETRY_BASE_DELAY_MS"), out int whDelayMs) && whDelayMs >= 0 ? whDelayMs : 500;
		string crawlDbPath = Environment.GetEnvironmentVariable("Vapor_CRAWL_DB_PATH") ?? "data/crawl.db";
		int crawlWorkerTickSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_CRAWL_WORKER_TICK_SECONDS"), out int crawlTick) ? crawlTick : 5;
		int crawlKeepRuns = int.TryParse(Environment.GetEnvironmentVariable("Vapor_CRAWL_KEEP_RUNS"), out int crawlKeep) && crawlKeep > 0 ? crawlKeep : 10;
		int crawlMaxAppsPerPlan = int.TryParse(Environment.GetEnvironmentVariable("Vapor_CRAWL_MAX_APPS_PER_PLAN"), out int crawlAppsPerPlan) && crawlAppsPerPlan > 0 ? crawlAppsPerPlan : 500;
		int crawlMaxAppsPerTask = int.TryParse(Environment.GetEnvironmentVariable("Vapor_CRAWL_MAX_APPS_PER_TASK"), out int crawlAppsPerTask) && crawlAppsPerTask > 0 ? crawlAppsPerTask : 200;
		int crawlRunTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_CRAWL_RUN_TIMEOUT_SECONDS"), out int crawlRunTimeout) && crawlRunTimeout > 0 ? crawlRunTimeout : 1800;
		int crawlIntervalMs = int.TryParse(Environment.GetEnvironmentVariable("Vapor_CRAWL_INTERVAL_MS"), out int crawlInterval) && crawlInterval >= 0 ? crawlInterval : 500;

		// RemoveEmptyEntries | TrimEntries already drops entries that are empty or
		// whitespace-only (trim runs before removal), so no per-key guard is needed.
		HashSet<string> agentApiKeys = new(
			agentApiKeysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			StringComparer.Ordinal);

		string? pluginIndexUrl = Environment.GetEnvironmentVariable("Vapor_PLUGIN_INDEX_URL");
		if (string.IsNullOrWhiteSpace(pluginIndexUrl))
		{
			pluginIndexUrl = null;
		}

		return new Config(adminApiKey, agentApiKeys, dbPath, taskLeaseSeconds, enableSwagger, auditDbPath, taskMaxDispatchAttempts, taskDispatchRetryDelayMs, reconcileIntervalSeconds, reconcileMaxAccountsPerAgent, reconcileMaxLoginAttempts, reconcileLoginCooldownSeconds, reconcileSessionStalenessSeconds, reconcileFarmRefreshSeconds, reconcileBoostRefreshSeconds, reconcileTradeRefreshSeconds, reconcileStandingRefreshSeconds, reconcileDryRun, webhookUrl, webhookSecret, webhookEvents, webhookMaxRetries, webhookRetryBaseDelayMs, crawlDbPath, crawlWorkerTickSeconds, crawlKeepRuns, crawlMaxAppsPerPlan, crawlMaxAppsPerTask, crawlRunTimeoutSeconds, crawlIntervalMs, pluginIndexUrl);
	}
}

