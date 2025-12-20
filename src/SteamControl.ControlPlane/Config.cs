namespace SteamControl.ControlPlane;

public sealed record Config(
	string AdminApiKey,
	IReadOnlySet<string> AgentApiKeys,
	string DbPath,
	int TaskLeaseSeconds,
	bool EnableSwagger
) {
	public static Config LoadFromEnvironment() {
		string adminApiKey = Environment.GetEnvironmentVariable("STEAMCONTROL_ADMIN_API_KEY") ?? "";
		string agentApiKeysRaw = Environment.GetEnvironmentVariable("STEAMCONTROL_AGENT_API_KEYS") ?? "";
		string dbPath = Environment.GetEnvironmentVariable("STEAMCONTROL_DB_PATH") ?? "data/controlplane.db";
		int taskLeaseSeconds = int.TryParse(Environment.GetEnvironmentVariable("STEAMCONTROL_TASK_LEASE_SECONDS"), out int v) && v > 0 ? v : 300;
		bool enableSwagger = string.Equals(Environment.GetEnvironmentVariable("STEAMCONTROL_ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);

		HashSet<string> agentApiKeys = new(StringComparer.Ordinal);
		foreach (string v in agentApiKeysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (v.Length == 0) {
				continue;
			}
			agentApiKeys.Add(v);
		}

		return new Config(adminApiKey, agentApiKeys, dbPath, taskLeaseSeconds, enableSwagger);
	}
}
