namespace Vapor.ControlPlane;

public sealed record Config(
	string AdminApiKey,
	IReadOnlySet<string> AgentApiKeys,
	string DbPath,
	int TaskLeaseSeconds,
	bool EnableSwagger
) {
	public static Config LoadFromEnvironment() {
		string adminApiKey = Environment.GetEnvironmentVariable("Vapor_ADMIN_API_KEY") ?? "";
		string agentApiKeysRaw = Environment.GetEnvironmentVariable("Vapor_AGENT_API_KEYS") ?? "";
		string dbPath = Environment.GetEnvironmentVariable("Vapor_DB_PATH") ?? "data/controlplane.db";
		int taskLeaseSeconds = int.TryParse(Environment.GetEnvironmentVariable("Vapor_TASK_LEASE_SECONDS"), out int v) && v > 0 ? v : 300;
		bool enableSwagger = string.Equals(Environment.GetEnvironmentVariable("Vapor_ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);

		HashSet<string> agentApiKeys = new(StringComparer.Ordinal);
		foreach (string key in agentApiKeysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (key.Length == 0) {
				continue;
			}
			agentApiKeys.Add(key);
		}

		return new Config(adminApiKey, agentApiKeys, dbPath, taskLeaseSeconds, enableSwagger);
	}
}

