using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vapor.Mcp;

/// <summary>
/// Entry shell: resolves the environment, wires the client and the tool faces
/// and runs the stdio MCP server. The stdio transport owns stdout for protocol
/// frames, so the host's entire log pipeline is routed to stderr before any
/// other provider can write; startup failures also go to stderr.
/// </summary>
[ExcludeFromCodeCoverage]
public static partial class Program
{
	public static async Task<int> Main(string[] args)
	{
		HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
		builder.Logging.ClearProviders();
		builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

		string? adminApiKey = McpHost.ResolveAdminApiKey(Environment.GetEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(adminApiKey))
		{
			Console.Error.WriteLine(
				$"vapor mcp: no admin API key (set {McpHost.ApiKeyEnv}); refusing to start");
			return 2;
		}

		bool writeEnabled = McpHost.ResolveWriteGate(Environment.GetEnvironmentVariable);
		// CA2000 suppressed: ownership of the client transfers to the host's DI container.
#pragma warning disable CA2000
		var client = new ControlPlaneClient(McpHost.ResolveBaseAddress(Environment.GetEnvironmentVariable), adminApiKey);
#pragma warning restore CA2000
		builder.Services
			.AddSingleton(client)
			.AddVaporMcp(writeEnabled);

		using IHost host = builder.Build();
		await host.RunAsync().ConfigureAwait(false);
		return 0;
	}
}
