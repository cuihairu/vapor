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
	[Description("Get the per-agent reporting rows for one plugin id from the agents' last plugin inventory reports.")]
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

		var matches = new List<JsonElement>();
		foreach (JsonElement agent in agents.EnumerateArray())
		{
			if (!agent.TryGetProperty("plugins", out JsonElement plugins) || plugins.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			foreach (JsonElement plugin in plugins.EnumerateArray())
			{
				if (plugin.TryGetProperty("id", out JsonElement id)
					&& id.ValueKind == JsonValueKind.String
					&& string.Equals(id.GetString(), pluginId, StringComparison.OrdinalIgnoreCase))
				{
					matches.Add(plugin);
				}
			}
		}

		if (matches.Count == 0)
		{
			throw new McpException($"plugin '{pluginId}' is not present in any agent's reported inventory");
		}

		return JsonSerializer.Serialize(matches);
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
