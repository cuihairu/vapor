using Microsoft.Extensions.DependencyInjection;

namespace Vapor.Mcp;

/// <summary>
/// Shared composition root for the MCP server: environment resolution and tool
/// registration. Kept out of <see cref="Program"/> so the write-gate contract —
/// the write tools must be absent from the tool registry unless the gate is
/// enabled — is testable against a plain <see cref="IServiceCollection"/>.
/// </summary>
public static class McpHost
{
	public const string WriteGateEnv = "MCP_WRITE_ENABLED";
	public const string WriteGateEnvAlias = "Vapor_MCP_WRITE_ENABLED";
	public const string BaseUrlEnv = "Vapor_MCP_BASE_URL";
	public const string ApiKeyEnv = "Vapor_MCP_ADMIN_API_KEY";

	/// <summary>Default control plane address; matches docs/running.md's <c>ASPNETCORE_URLS</c> example.</summary>
	public const string DefaultBaseAddress = "http://127.0.0.1:8080";

	/// <summary>
	/// The write gate accepts exactly "1" or "true" (case-insensitive); every
	/// other value — including the variable being unset — keeps the server
	/// read-only. The Vapor_-prefixed alias is read as a fallback so operators
	/// can keep one naming scheme across their deployment.
	/// </summary>
	public static bool ResolveWriteGate(Func<string, string?> env)
	{
		string? raw = env(WriteGateEnv) ?? env(WriteGateEnvAlias);
		return raw is not null
			&& (raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase));
	}

	public static string ResolveBaseAddress(Func<string, string?> env)
	{
		string? raw = env(BaseUrlEnv);
		return string.IsNullOrWhiteSpace(raw) ? DefaultBaseAddress : raw.Trim().TrimEnd('/');
	}

	/// <summary>Admin API key that reaches every tool call as the Bearer credential; empty means fail-closed at startup.</summary>
	public static string? ResolveAdminApiKey(Func<string, string?> env) => env(ApiKeyEnv);

	/// <summary>
	/// Registers the MCP server with the stdio transport and the read-only tool
	/// set. The write tools are added only when <paramref name="writeEnabled"/>
	/// is set: the registration is skipped entirely, so a gated server's tools
	/// list never contains them rather than containing tools that refuse.
	/// </summary>
	public static IServiceCollection AddVaporMcp(this IServiceCollection services, bool writeEnabled)
	{
		IMcpServerBuilder mcp = services
			.AddMcpServer()
			.WithStdioServerTransport()
			.WithTools<VaporReadTools>();

		if (writeEnabled)
		{
			mcp.WithTools<AccountWriteTools>();
		}

		return services;
	}
}
