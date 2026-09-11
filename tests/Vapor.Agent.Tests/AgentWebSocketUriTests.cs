using Xunit;

namespace Vapor.Agent.Tests;

public class AgentWebSocketUriTests
{
	[Fact]
	public void Build_PlainBaseUrl_AppendsIdentityQuery()
	{
		var uri = AgentWebSocketUri.Build("ws://127.0.0.1:5000/v1/agent/ws", "agent-1", "cn-east");

		Assert.Equal("ws://127.0.0.1:5000/v1/agent/ws", uri.GetLeftPart(UriPartial.Path));
		Assert.Contains("agentId=agent-1", uri.Query);
		Assert.Contains("region=cn-east", uri.Query);
	}

	[Fact]
	public void Build_ExistingQuery_IsMerged()
	{
		var uri = AgentWebSocketUri.Build("ws://127.0.0.1:5000/v1/agent/ws?tenant=acme", "agent-1", "cn-east");

		Assert.Contains("tenant=acme", uri.Query);
		Assert.Contains("agentId=agent-1", uri.Query);
		Assert.Contains("region=cn-east", uri.Query);
	}

	[Fact]
	public void Build_SpecialCharacters_AreEscaped()
	{
		var uri = AgentWebSocketUri.Build("ws://127.0.0.1:5000/v1/agent/ws", "agent id/1", "eu west&2");

		Assert.DoesNotContain("agent id/1", uri.Query, StringComparison.Ordinal);
		Assert.DoesNotContain("eu west&2", uri.Query, StringComparison.Ordinal);
		Assert.Equal("agent id/1", Uri.UnescapeDataString(GetQueryValue(uri.Query, "agentId")!));
		Assert.Equal("eu west&2", Uri.UnescapeDataString(GetQueryValue(uri.Query, "region")!));
	}

	[Fact]
	public void Build_WssScheme_IsPreserved()
	{
		var uri = AgentWebSocketUri.Build("wss://control.example.com/v1/agent/ws", "agent-1", "us-west");

		Assert.Equal(Uri.UriSchemeWss, uri.Scheme);
		Assert.Equal("control.example.com", uri.Host);
	}

	private static string? GetQueryValue(string query, string key)
	{
		foreach (var pair in query.TrimStart('?').Split('&'))
		{
			var parts = pair.Split('=', 2);
			if (parts.Length == 2 && parts[0] == key)
			{
				return parts[1];
			}
		}

		return null;
	}
}
