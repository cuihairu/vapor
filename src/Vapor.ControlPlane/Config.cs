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
	bool ReconcileDryRun = false,
	string? WebhookNotificationsUrl = null,
	string? WebhookNotificationsSecret = null,
	string? WebhookNotificationsEvents = null,
	int WebhookNotificationsMaxRetries = 3,
	int WebhookNotificationsRetryBaseDelayMs = 500
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
		bool reconcileDryRun = string.Equals(Environment.GetEnvironmentVariable("Vapor_RECONCILE_DRY_RUN"), "true", StringComparison.OrdinalIgnoreCase);
		string? webhookUrl = Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_URL");
		string? webhookSecret = Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_SECRET");
		string? webhookEvents = Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_EVENTS");
		int webhookMaxRetries = int.TryParse(Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_MAX_RETRIES"), out int whRetries) && whRetries >= 0 ? whRetries : 3;
		int webhookRetryBaseDelayMs = int.TryParse(Environment.GetEnvironmentVariable("Vapor_WEBHOOK_NOTIFICATIONS_RETRY_BASE_DELAY_MS"), out int whDelayMs) && whDelayMs >= 0 ? whDelayMs : 500;

		HashSet<string> agentApiKeys = new(StringComparer.Ordinal);
		foreach (string key in agentApiKeysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (key.Length == 0)
			{
				continue;
			}
			agentApiKeys.Add(key);
		}

		return new Config(adminApiKey, agentApiKeys, dbPath, taskLeaseSeconds, enableSwagger, auditDbPath, taskMaxDispatchAttempts, taskDispatchRetryDelayMs, reconcileIntervalSeconds, reconcileMaxAccountsPerAgent, reconcileMaxLoginAttempts, reconcileLoginCooldownSeconds, reconcileSessionStalenessSeconds, reconcileDryRun, webhookUrl, webhookSecret, webhookEvents, webhookMaxRetries, webhookRetryBaseDelayMs);
	}
}

