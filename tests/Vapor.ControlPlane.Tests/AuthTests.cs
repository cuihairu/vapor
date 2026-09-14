using Microsoft.Extensions.Primitives;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Bearer-header parsing branches of the static auth helpers: malformed
/// schemes must fail closed without consulting the configured keys.
/// </summary>
public sealed class AuthTests
{
	private static Config CreateConfig() =>
		new("admin-key", new HashSet<string>(StringComparer.Ordinal) { "agent-1" }, ":memory:", 300, false, ":memory:");

	[Fact]
	public void TryAdmin_WithValidBearerToken_Succeeds()
	{
		var cfg = CreateConfig();

		Assert.True(Auth.TryAdmin(cfg, new StringValues("Bearer admin-key"), out string? token));
		Assert.Equal("admin-key", token);
	}

	[Fact]
	public void TryAdmin_WithTokenOnly_MissingScheme_ReturnsFalse()
	{
		// A raw token with no "Bearer <value>" shape fails before any key comparison.
		Assert.False(Auth.TryAdmin(CreateConfig(), new StringValues("admin-key"), out string? token));
		Assert.Null(token);
	}

	[Fact]
	public void TryAdmin_WithNonBearerScheme_ReturnsFalse()
	{
		Assert.False(Auth.TryAdmin(CreateConfig(), new StringValues("Basic YWRtaW4="), out string? token));
		Assert.Null(token);
	}

	[Fact]
	public void TryAdmin_WithEmptyHeader_ReturnsFalse()
	{
		Assert.False(Auth.TryAdmin(CreateConfig(), StringValues.Empty, out string? token));
		Assert.Null(token);
	}

	[Fact]
	public void TryAgent_WithKnownAgentToken_Succeeds()
	{
		var cfg = CreateConfig();

		Assert.True(Auth.TryAgent(cfg, new StringValues("bearer agent-1"), out string? token));
		Assert.Equal("agent-1", token);
	}

	[Fact]
	public void TryAgent_WithUnknownAgentToken_ReturnsFalse()
	{
		// The header parsed fine, so the out param carries the token even though
		// the key does not match any configured agent key.
		Assert.False(Auth.TryAgent(CreateConfig(), new StringValues("Bearer agent-2"), out string? token));
		Assert.Equal("agent-2", token);
	}

	[Fact]
	public void TryAgent_WithAdminToken_IsNotAnAgentToken()
	{
		Assert.False(Auth.TryAgent(CreateConfig(), new StringValues("Bearer admin-key"), out _));
	}

	[Fact]
	public void TryAdmin_WithEmptyConfiguredKey_NeverMatches()
	{
		var cfg = new Config("", new HashSet<string>(StringComparer.Ordinal), ":memory:", 300, false, ":memory:");

		// An empty configured key disqualifies every token (string.Equals against "" fails).
		Assert.False(Auth.TryAdmin(cfg, new StringValues("Bearer anything"), out string? token));
		Assert.Equal("anything", token);
	}
}
