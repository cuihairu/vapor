using Xunit;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Vapor.Mcp.Tests;

public sealed class McpHostTests
{
	private static ServiceProvider BuildProvider(bool writeEnabled)
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddSingleton(new ControlPlaneClient("http://cp.test", "key"));
		services.AddVaporMcp(writeEnabled);
		return services.BuildServiceProvider();
	}

	private static string[] RegisteredToolNames(IServiceProvider provider)
		=> [.. provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool.Name).OrderBy(name => name, StringComparer.Ordinal)];

	private static readonly string[] ReadOnlyToolNames =
	[
		"agent_status",
		"list_accounts",
		"list_agents",
		"list_plugins",
		"metrics_snapshot",
		"plugin_status",
		"recent_events",
	];

	[Fact]
	public void WriteGateClosed_ToolRegistryHasNoWriteTools()
	{
		using ServiceProvider provider = BuildProvider(writeEnabled: false);

		string[] names = RegisteredToolNames(provider);

		// The gate must remove the tools from the registry entirely — the tools
		// list of a read-only server simply has no enable/disable entries.
		Assert.Equal(ReadOnlyToolNames, names);
		Assert.DoesNotContain("enable_account", names);
		Assert.DoesNotContain("disable_account", names);
	}

	[Fact]
	public void WriteGateOpen_ToolRegistryAddsExactlyTheTwoWriteTools()
	{
		using ServiceProvider provider = BuildProvider(writeEnabled: true);

		string[] names = RegisteredToolNames(provider);

		string[] expected =
		[
			"agent_status",
			"disable_account",
			"enable_account",
			"list_accounts",
			"list_agents",
			"list_plugins",
			"metrics_snapshot",
			"plugin_status",
			"recent_events",
		];
		Assert.Equal(expected, names);
	}

	[Theory]
	[InlineData(null, null, false)]
	[InlineData("", null, false)]
	[InlineData("0", null, false)]
	[InlineData("yes", null, false)]
	[InlineData("false", null, false)]
	[InlineData("1", null, true)]
	[InlineData("true", null, true)]
	[InlineData("TRUE", null, true)]
	[InlineData("True", null, true)]
	[InlineData(null, "1", true)]
	[InlineData(null, "true", true)]
	[InlineData(null, "FALSE", false)]
	[InlineData("0", "1", false)]
	[InlineData("", "1", false)]
	[InlineData(null, "", false)]
	public void ResolveWriteGate_AcceptsExactlyOneAndTrue_WithAliasFallback(string? primary, string? alias, bool expected)
	{
		bool enabled = McpHost.ResolveWriteGate(key =>
			key == McpHost.WriteGateEnv ? primary
			: key == McpHost.WriteGateEnvAlias ? alias
			: null);

		Assert.Equal(expected, enabled);
	}

	[Theory]
	[InlineData(null, "http://127.0.0.1:8080")]
	[InlineData("   ", "http://127.0.0.1:8080")]
	[InlineData("http://cp.internal:9000", "http://cp.internal:9000")]
	[InlineData("http://cp.internal:9000/", "http://cp.internal:9000")]
	[InlineData(" http://cp.internal:9000/ ", "http://cp.internal:9000")]
	public void ResolveBaseUrl_DefaultsAndTrimsTrailingSlash(string? configured, string expected)
		=> Assert.Equal(expected, McpHost.ResolveBaseAddress(name => name == McpHost.BaseUrlEnv ? configured : null));

	[Theory]
	[InlineData("secret", "secret")]
	[InlineData(null, null)]
	public void ResolveAdminApiKey_PassesThrough(string? configured, string? expected)
		=> Assert.Equal(expected, McpHost.ResolveAdminApiKey(name => name == McpHost.ApiKeyEnv ? configured : null));
}
