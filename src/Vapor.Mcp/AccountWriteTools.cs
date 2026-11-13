using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Vapor.Mcp;

/// <summary>
/// The gated write face. These tools are registered only when the write gate
/// is enabled (see <see cref="McpHost.ResolveWriteGate"/>), so a read-only
/// server's tools list never contains them. They back onto the control plane's
/// existing account enable/disable endpoints — the same audited desired-state
/// surface the dashboard uses — and no further: real Steam actions (case
/// opening, consumption, trades) are not exposed here and stay behind their
/// own plugin gates.
/// </summary>
[McpServerToolType]
public sealed class AccountWriteTools
{
	[McpServerTool(Name = "enable_account")]
	[Description("Enable a declared farm account: resumes orchestration toward its desired state; desired state and config are kept. Audited by the control plane. Gated write tool, present only when MCP_WRITE_ENABLED=1.")]
	public static Task<string> EnableAccount(
		ControlPlaneClient client,
		[Description("Declared account name.")] string accountName,
		CancellationToken cancellationToken)
		=> SetEnabledAsync(client, accountName, enabled: true, cancellationToken);

	[McpServerTool(Name = "disable_account")]
	[Description("Disable a declared farm account: stops orchestration for it; desired state and config are kept for later re-enable. Audited by the control plane. Gated write tool, present only when MCP_WRITE_ENABLED=1.")]
	public static Task<string> DisableAccount(
		ControlPlaneClient client,
		[Description("Declared account name.")] string accountName,
		CancellationToken cancellationToken)
		=> SetEnabledAsync(client, accountName, enabled: false, cancellationToken);

	private static async Task<string> SetEnabledAsync(
		ControlPlaneClient client,
		string accountName,
		bool enabled,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			throw new McpException("accountName is required");
		}

		string action = enabled ? "enable" : "disable";
		string path = $"/v1/accounts/{Uri.EscapeDataString(accountName.Trim())}/{action}";
		string body = await client.PostAsync(path, cancellationToken).ConfigureAwait(false);
		return AccountSanitizer.SanitizeSpecEnvelope(body);
	}
}
