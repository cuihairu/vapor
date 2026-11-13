using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace Vapor.Mcp;

/// <summary>
/// Projects control-plane account payloads onto an explicit whitelist of
/// <see cref="Vapor.Protocol.AccountSpec"/> fields before they leave the MCP
/// server. Belt-and-suspenders on top of the architecture: the control plane
/// never stores credentials, but the whitelist guarantees that even if a
/// credential-shaped field ever appears upstream, it cannot reach tool output
/// — unknown fields are dropped, not passed through.
/// </summary>
public static class AccountSanitizer
{
	/// <summary>
	/// The declared-safe projection of <c>AccountSpec</c> (camelCase wire
	/// names). Anything an account payload carries beyond this list —
	/// passwords, maFile paths, shared/identity secrets, refresh tokens — is
	/// structurally excluded from MCP output.
	/// </summary>
	public static readonly string[] SafeFields =
	[
		"accountName",
		"enabled",
		"desiredState",
		"idleApps",
		"region",
		"agentId",
		"note",
		"version",
		"marketListingsEnabled",
		"boostTargets",
		"tradePolicy",
		"farmPolicy",
		"role",
		"steamId",
	];

	/// <summary>Sanitize a <c>/v1/accounts</c> response: <c>{"accounts":[ … ]}</c> in, projected list out.</summary>
	public static string SanitizeAccounts(string body)
	{
		using JsonDocument doc = JsonDocument.Parse(body);
		if (!doc.RootElement.TryGetProperty("accounts", out JsonElement accounts) || accounts.ValueKind != JsonValueKind.Array)
		{
			throw new McpException("unexpected control plane response shape: 'accounts' array is missing");
		}

		var projected = new JsonArray();
		foreach (JsonElement account in accounts.EnumerateArray())
		{
			projected.Add(Project(account));
		}

		return new JsonObject { ["accounts"] = projected }.ToJsonString();
	}

	/// <summary>Sanitize an enable/disable response: <c>{"spec":{ … }}</c> in, projected spec out.</summary>
	public static string SanitizeSpecEnvelope(string body)
	{
		using JsonDocument doc = JsonDocument.Parse(body);
		if (!doc.RootElement.TryGetProperty("spec", out JsonElement spec))
		{
			throw new McpException("unexpected control plane response shape: 'spec' is missing");
		}

		return new JsonObject { ["spec"] = Project(spec) }.ToJsonString();
	}

	private static JsonObject Project(JsonElement account)
	{
		var projected = new JsonObject();
		foreach (string field in SafeFields)
		{
			if (account.TryGetProperty(field, out JsonElement value))
			{
				projected[field] = JsonNode.Parse(value.GetRawText());
			}
		}
		return projected;
	}
}
