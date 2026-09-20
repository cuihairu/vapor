using System.Collections.Concurrent;
using System.Text.Json;

namespace Vapor.ControlPlane;

/// <summary>
/// In-memory mirror of what is installed on each agent, rebuilt wholesale from the
/// "plugins" array that every plugin_* host action returns in its output. The
/// mirror is best-effort state (agent restarts, manual directory edits and offline
/// agents all drift) — the refresh endpoint re-runs plugin_list to re-sync.
/// </summary>
public sealed class PluginInventory
{
	private readonly ConcurrentDictionary<string, AgentPlugins> _agents = new(StringComparer.Ordinal);

	/// <summary>
	/// Rebuilds one agent's mirror from a plugin_* task output: every host action
	/// returns the full installed list under "plugins", success or not, so the
	/// mirror stays current even for failed installs (which still report state).
	/// </summary>
	public void Update(string agentId, DateTimeOffset reportedAt, IReadOnlyDictionary<string, object?>? output)
	{
		if (output is null || !output.TryGetValue("plugins", out var raw))
		{
			return;
		}

		IReadOnlyList<JsonElement>? plugins = raw switch
		{
			JsonElement { ValueKind: JsonValueKind.Array } e => [.. e.EnumerateArray()],
			_ => null
		};
		if (plugins is null)
		{
			return;
		}

		var entries = new List<PluginInventoryEntry>();
		foreach (var item in plugins)
		{
			if (item.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			string? GetString(string name) =>
				item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

			var permissions = new List<string>();
			if (item.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
			{
				permissions.AddRange(perms.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
			}

			var actions = new List<string>();
			if (item.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
			{
				actions.AddRange(acts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
			}

			entries.Add(new PluginInventoryEntry(
				GetString("id") ?? string.Empty,
				GetString("name") ?? string.Empty,
				GetString("version") ?? string.Empty,
				GetString("apiVersion") ?? string.Empty,
				GetString("trust"),
				permissions,
				actions));
		}

		_agents[agentId] = new AgentPlugins(reportedAt, entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList());
	}

	/// <summary>Drops one agent's mirror entry (e.g. on disconnect).</summary>
	public void Remove(string agentId) => _agents.TryRemove(agentId, out _);

	public IReadOnlyDictionary<string, AgentPlugins> Snapshot() =>
		_agents.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
}

/// <summary>One agent's last-reported plugin inventory.</summary>
public sealed record AgentPlugins(DateTimeOffset ReportedAt, IReadOnlyList<PluginInventoryEntry> Plugins);

/// <summary>A plugin installed on an agent, as reported by the agent itself.</summary>
public sealed record PluginInventoryEntry(
	string Id,
	string Name,
	string Version,
	string ApiVersion,
	string? Trust,
	IReadOnlyList<string> Permissions,
	IReadOnlyList<string> Actions);
