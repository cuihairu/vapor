namespace Vapor.ControlPlane;

public sealed record Config(
	string AdminApiKey,
	IReadOnlySet<string> AgentApiKeys,
	string DbPath,
	int TaskLeaseSeconds,
	bool EnableSwagger,
	string AuditDbPath = "data/audit.db",
	int TaskMaxDispatchAttempts = 10,
	int TaskDispatchRetryDelayMs = 2000
) {
	/// <summary>Max dispatch attempts per task before it fails permanently; 0 or less means unlimited retries.</summary>
	public bool HasDispatchAttemptLimit => TaskMaxDispatchAttempts > 0;

	public static Config LoadFromEnvironment() {
		string adminApiKey = Environment.GetEnvironmentVariable("Vapor_ADMIN_API_KEY") ?? "";
		string agentApiKeysRaw = Environment.GetEnvironmentVariable("Vapor_AGENT_API_KEYS") ?? "";
		string dbPath = Environment.GetEnvironmentVariable("Vapor_DB_PATH") ?? "data/controlplane.db";
		int taskLeaseSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_LEASE_SECONDS"), out int v) && v > 0 ? v : 300;
		bool enableSwagger = string.Equals(Environment.GetEnvironmentVariable("Vapor_ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);
		string auditDbPath = Environment.GetEnvironmentVariable("Vapor_AUDIT_DB_PATH") ?? "data/audit.db";
		int taskMaxDispatchAttempts = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_MAX_DISPATCH_ATTEMPTS"), out int attempts) ? attempts : 10;
		int taskDispatchRetryDelayMs = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_DISPATCH_RETRY_DELAY_MS"), out int delayMs) && delayMs >= 0 ? delayMs : 2000;

		HashSet<string> agentApiKeys = new(StringComparer.Ordinal);
		foreach (string key in agentApiKeysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (key.Length == 0) {
				continue;
			}
			agentApiKeys.Add(key);
		}

		return new Config(adminApiKey, agentApiKeys, dbPath, taskLeaseSeconds, enableSwagger, auditDbPath, taskMaxDispatchAttempts, taskDispatchRetryDelayMs);
	}
}

