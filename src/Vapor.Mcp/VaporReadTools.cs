using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Vapor.Mcp;

/// <summary>
/// Read-only tools over the control plane's existing REST read faces. Every
/// tool proxies the control plane — there is deliberately no second query
/// path, so MCP callers see exactly what the dashboard and REST clients see,
/// behind the same admin authentication.
/// </summary>
[McpServerToolType]
public sealed class VaporReadTools
{
	private const int RecentEventsDefaultLimit = 20;

	[McpServerTool(Name = "list_agents")]
	[Description("List the declared agents of the vapor control plane, including offline ones.")]
	public static Task<string> ListAgents(
		ControlPlaneClient client,
		CancellationToken cancellationToken)
		=> client.GetAsync("/v1/agents", cancellationToken);

	[McpServerTool(Name = "agent_status")]
	[Description("Get the connection status of one agent (region, capabilities, connected since). The agent must be connected to the control plane to have a status row.")]
	public static async Task<string> AgentStatus(
		ControlPlaneClient client,
		[Description("Agent id as declared in the control plane, e.g. \"agent-1\".")] string agentId,
		CancellationToken cancellationToken)
	{
		string body = await client.GetAsync("/v1/agents/status", cancellationToken).ConfigureAwait(false);
		using JsonDocument doc = JsonDocument.Parse(body);
		if (!doc.RootElement.TryGetProperty("agents", out JsonElement agents) || agents.ValueKind != JsonValueKind.Array)
		{
			throw new McpException("unexpected control plane response shape: 'agents' array is missing");
		}

		foreach (JsonElement agent in agents.EnumerateArray())
		{
			if (agent.TryGetProperty("id", out JsonElement id)
				&& id.ValueKind == JsonValueKind.String
				&& string.Equals(id.GetString(), agentId, StringComparison.OrdinalIgnoreCase))
			{
				return agent.GetRawText();
			}
		}

		throw new McpException($"agent '{agentId}' has no status row (not connected, or not a declared agent)");
	}

	[McpServerTool(Name = "list_accounts")]
	[Description("List declared farm accounts with their desired state and assignment hints. Output is sanitized to status and labeling fields only: credentials (passwords, maFile, shared/identity secrets, tokens) are never included, by whitelist.")]
	public static async Task<string> ListAccounts(
		ControlPlaneClient client,
		[Description("Optional filter by desired state: offline, online or idle.")] string? state,
		[Description("Optional filter by region.")] string? region,
		[Description("Optional filter by assigned agent id.")] string? agent,
		CancellationToken cancellationToken)
	{
		string path = ControlPlaneClient.WithQuery("/v1/accounts", ("state", state), ("region", region), ("agent", agent));
		string body = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
		return AccountSanitizer.SanitizeAccounts(body);
	}

	[McpServerTool(Name = "list_plugins")]
	[Description("List the plugin inventory as last reported by each agent (plugin ids, versions, updates).")]
	public static Task<string> ListPlugins(
		ControlPlaneClient client,
		CancellationToken cancellationToken)
		=> client.GetAsync("/v1/plugins/installed", cancellationToken);

	[McpServerTool(Name = "plugin_status")]
	[Description("Get the per-agent rows for one plugin id from the agents' last inventory reports. Each row carries the reporting agent, its report time, the plugin entry, and that agent's update verdict for the plugin.")]
	public static async Task<string> PluginStatus(
		ControlPlaneClient client,
		[Description("Plugin id as reported in the inventory, e.g. \"monitoring\".")] string pluginId,
		CancellationToken cancellationToken)
	{
		string body = await client.GetAsync("/v1/plugins/installed", cancellationToken).ConfigureAwait(false);
		using JsonDocument doc = JsonDocument.Parse(body);
		if (!doc.RootElement.TryGetProperty("agents", out JsonElement agents) || agents.ValueKind != JsonValueKind.Array)
		{
			throw new McpException("unexpected control plane response shape: 'agents' array is missing");
		}

		// One row per reporting agent: the plugin entry alone loses the enclosing
		// agentId/reportedAt that give the version numbers their meaning, and the
		// agent's update verdict for this plugin is what the row is usually asked for.
		var rows = new List<Dictionary<string, object?>>();
		foreach (JsonElement agent in agents.EnumerateArray())
		{
			if (!agent.TryGetProperty("agentId", out JsonElement agentId)
				|| agentId.ValueKind != JsonValueKind.String
				|| !agent.TryGetProperty("plugins", out JsonElement plugins)
				|| plugins.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			JsonElement? match = null;
			foreach (JsonElement plugin in plugins.EnumerateArray())
			{
				if (plugin.TryGetProperty("id", out JsonElement id)
					&& id.ValueKind == JsonValueKind.String
					&& string.Equals(id.GetString(), pluginId, StringComparison.OrdinalIgnoreCase))
				{
					match = plugin;
					break;
				}
			}

			if (match is null)
			{
				continue;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["agentId"] = agentId.GetString(),
				["reportedAt"] = GetStringOrNull(agent, "reportedAt"),
				["plugin"] = match.Value,
				["update"] = FindUpdate(agent, pluginId),
			});
		}

		if (rows.Count == 0)
		{
			throw new McpException($"plugin '{pluginId}' is not present in any agent's reported inventory");
		}

		return JsonSerializer.Serialize(rows);
	}

	private static string? GetStringOrNull(JsonElement element, string name)
		=> element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static JsonElement? FindUpdate(JsonElement agent, string pluginId)
	{
		if (!agent.TryGetProperty("updates", out JsonElement updates) || updates.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		foreach (JsonElement update in updates.EnumerateArray())
		{
			if (update.TryGetProperty("id", out JsonElement id)
				&& id.ValueKind == JsonValueKind.String
				&& string.Equals(id.GetString(), pluginId, StringComparison.OrdinalIgnoreCase))
			{
				return update;
			}
		}

		return null;
	}

	[McpServerTool(Name = "recent_events")]
	[Description("Query the control plane's audit/event log, newest entries first (filters: action name, account, agent id, job id). This is the control-plane event stream; agent-side plugin metrics have their own endpoint.")]
	public static async Task<string> RecentEvents(
		ControlPlaneClient client,
		[Description("Maximum entries to return, 1-500 (default 20).")] int? limit,
		[Description("Optional action-name filter, e.g. \"account.enabled\".")] string? action,
		[Description("Optional account-name filter.")] string? account,
		[Description("Optional agent-id filter.")] string? agentId,
		CancellationToken cancellationToken)
	{
		int clamped = Math.Clamp(limit ?? RecentEventsDefaultLimit, 1, 500);
		string path = ControlPlaneClient.WithQuery(
			"/v1/audit/logs",
			("limit", clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)),
			("action", action),
			("account", account),
			("agentId", agentId));
		return await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
	}

	[McpServerTool(Name = "metrics_snapshot")]
	[Description("Take a snapshot of the control plane's Prometheus metrics (tasks by status, connected agents, reconcile actions, dispatch failures). Returns the raw Prometheus text format.")]
	public static Task<string> MetricsSnapshot(
		ControlPlaneClient client,
		CancellationToken cancellationToken)
		=> client.GetAsync("/metrics", cancellationToken);
}
