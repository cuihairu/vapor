using Microsoft.Extensions.Primitives;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// API-key expiry: the optional "<c>@&lt;ISO-8601&gt;</c>" suffix on configured keys
/// (<see cref="Config.ParseApiKey"/> / <see cref="Config.LoadFromEnvironment"/>) and the
/// time-aware accept/reject arms of <see cref="Auth.TryAdmin(Config, StringValues, DateTimeOffset, out string?)"/> /
/// <see cref="Auth.TryAgent(Config, StringValues, DateTimeOffset, out string?)"/>. Expiry compares
/// against an injected clock; the production overloads delegate with DateTimeOffset.UtcNow.
/// Environment-mutating facts ride the process-global collection (see ConfigEnvironmentTests).
/// </summary>
[Collection(ProcessGlobalTracingCollection.Name)]
public sealed class ApiKeyExpiryTests
{
	private static readonly DateTimeOffset Expiry = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset BeforeExpiry = Expiry.AddSeconds(-1);
	private static readonly DateTimeOffset AfterExpiry = Expiry.AddSeconds(1);

	// --- Config.ParseApiKey: suffix recognition ---

	[Fact]
	public void ParseApiKey_PlainKey_HasNoExpiry()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("agent-token");

		Assert.Equal("agent-token", credential);
		Assert.Null(expiresAt);
	}

	[Fact]
	public void ParseApiKey_IsoSuffix_SplitsCredentialAndExpiry()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("key@2030-01-01T00:00:00Z");

		Assert.Equal("key", credential);
		Assert.Equal(Expiry, expiresAt);
	}

	[Fact]
	public void ParseApiKey_DateOnlySuffix_ParsesAsUtcMidnight()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("key@2030-01-01");

		Assert.Equal("key", credential);
		Assert.Equal(Expiry, expiresAt);
	}

	[Fact]
	public void ParseApiKey_OffsetSuffix_NormalizesToUtc()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("key@2030-06-01T12:00:00+02:00");

		Assert.Equal("key", credential);
		Assert.Equal(new DateTimeOffset(2030, 6, 1, 10, 0, 0, TimeSpan.Zero), expiresAt);
	}

	[Fact]
	public void ParseApiKey_NonDateSuffix_IsLiteralKey()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("a@b");

		Assert.Equal("a@b", credential);
		Assert.Null(expiresAt);
	}

	[Fact]
	public void ParseApiKey_EmptySuffix_IsLiteralKey()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("key@");

		Assert.Equal("key@", credential);
		Assert.Null(expiresAt);
	}

	[Fact]
	public void ParseApiKey_LeadingSeparator_IsLiteralKey()
	{
		// An empty credential must not become a key that matches an empty token.
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("@2030-01-01T00:00:00Z");

		Assert.Equal("@2030-01-01T00:00:00Z", credential);
		Assert.Null(expiresAt);
	}

	[Fact]
	public void ParseApiKey_LastSeparatorWins()
	{
		(string credential, DateTimeOffset? expiresAt) = Config.ParseApiKey("a@b@2030-01-01T00:00:00Z");

		Assert.Equal("a@b", credential);
		Assert.Equal(Expiry, expiresAt);
	}

	// --- Config.LoadFromEnvironment: env shape to Config shape ---

	[Fact]
	public void Load_AdminKeyWithSuffix_ParsesCredentialAndExpiry()
	{
		string? original = Environment.GetEnvironmentVariable("Vapor_ADMIN_API_KEY");
		try
		{
			Environment.SetEnvironmentVariable("Vapor_ADMIN_API_KEY", "admin-key@2030-01-01T00:00:00Z");
			Config config = Config.LoadFromEnvironment();

			Assert.Equal("admin-key", config.AdminApiKey);
			Assert.Equal(Expiry, config.AdminApiKeyExpiresAt);
		}
		finally
		{
			Environment.SetEnvironmentVariable("Vapor_ADMIN_API_KEY", original);
		}
	}

	[Fact]
	public void Load_AgentKeys_BuildsPerKeyExpiryMap()
	{
		string? original = Environment.GetEnvironmentVariable("Vapor_AGENT_API_KEYS");
		try
		{
			Environment.SetEnvironmentVariable("Vapor_AGENT_API_KEYS", "plain,dated@2030-01-01T00:00:00Z,lit@eral");
			Config config = Config.LoadFromEnvironment();

			Assert.Null(config.AgentApiKeys["plain"]);
			Assert.Equal(Expiry, config.AgentApiKeys["dated"]);
			// "@eral" is not a date, so the whole entry stays a literal credential.
			Assert.Null(config.AgentApiKeys["lit@eral"]);
			Assert.Equal(3, config.AgentApiKeys.Count);
		}
		finally
		{
			Environment.SetEnvironmentVariable("Vapor_AGENT_API_KEYS", original);
		}
	}

	[Theory]
	[InlineData("key@2030-01-01T00:00:00Z,key", false)]
	[InlineData("key,key@2030-01-01T00:00:00Z", true)]
	public void Load_DuplicateCredential_LastEntryWins(string raw, bool expectExpiry)
	{
		string? original = Environment.GetEnvironmentVariable("Vapor_AGENT_API_KEYS");
		try
		{
			Environment.SetEnvironmentVariable("Vapor_AGENT_API_KEYS", raw);
			Config config = Config.LoadFromEnvironment();

			Assert.Single(config.AgentApiKeys);
			Assert.Equal(expectExpiry ? Expiry : null, config.AgentApiKeys["key"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable("Vapor_AGENT_API_KEYS", original);
		}
	}

	// --- Auth enforcement (injected clock) ---

	private static Config AdminWithExpiry() =>
		new("admin-key", new Dictionary<string, DateTimeOffset?>(), ":memory:", 300, false, ":memory:", AdminApiKeyExpiresAt: Expiry);

	[Fact]
	public void TryAdmin_KeyWithFutureExpiry_AcceptsBeforeExpiry()
	{
		Assert.True(Auth.TryAdmin(AdminWithExpiry(), new StringValues("Bearer admin-key"), BeforeExpiry, out string? token));
		Assert.Equal("admin-key", token);
	}

	[Fact]
	public void TryAdmin_AtAndAfterExpiryInstant_Rejects()
	{
		// The expiry instant itself is dead: valid strictly before, not at.
		Assert.False(Auth.TryAdmin(AdminWithExpiry(), new StringValues("Bearer admin-key"), Expiry, out _));
		Assert.False(Auth.TryAdmin(AdminWithExpiry(), new StringValues("Bearer admin-key"), AfterExpiry, out _));
	}

	[Fact]
	public void TryAdmin_KeyWithoutExpiry_AcceptsAtAnyTime()
	{
		Config cfg = new("admin-key", new Dictionary<string, DateTimeOffset?>(), ":memory:", 300, false, ":memory:");

		Assert.True(Auth.TryAdmin(cfg, new StringValues("Bearer admin-key"), DateTimeOffset.MinValue, out _));
		Assert.True(Auth.TryAdmin(cfg, new StringValues("Bearer admin-key"), DateTimeOffset.MaxValue, out _));
	}

	[Fact]
	public void TryAgent_PerKeyExpiry_ExpiredKeyRejectedLiveKeyStillAccepted()
	{
		Config cfg = new(
			"admin-key",
			new Dictionary<string, DateTimeOffset?> { ["old-agent"] = Expiry, ["new-agent"] = null },
			":memory:",
			300,
			false,
			":memory:");

		Assert.False(Auth.TryAgent(cfg, new StringValues("Bearer old-agent"), AfterExpiry, out _));
		Assert.True(Auth.TryAgent(cfg, new StringValues("Bearer new-agent"), AfterExpiry, out string? token));
		Assert.Equal("new-agent", token);
	}

	[Fact]
	public void TryAgent_AtExpiryInstant_Rejects()
	{
		Config cfg = new(
			"admin-key",
			new Dictionary<string, DateTimeOffset?> { ["agent-1"] = Expiry },
			":memory:",
			300,
			false,
			":memory:");

		Assert.True(Auth.TryAgent(cfg, new StringValues("Bearer agent-1"), BeforeExpiry, out _));
		Assert.False(Auth.TryAgent(cfg, new StringValues("Bearer agent-1"), Expiry, out _));
	}

	[Fact]
	public void ProductionOverloads_ExpiredKeysRejectAgainstRealClock()
	{
		// The no-clock overloads delegate with DateTimeOffset.UtcNow; an expiry
		// already in the past is deterministically dead regardless of clock skew.
		Config expiredAdmin = new("admin-key", new Dictionary<string, DateTimeOffset?>(), ":memory:", 300, false, ":memory:", AdminApiKeyExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));
		Config expiredAgent = new(
			"admin-key",
			new Dictionary<string, DateTimeOffset?> { ["agent-1"] = DateTimeOffset.UtcNow.AddSeconds(-1) },
			":memory:",
			300,
			false,
			":memory:");

		Assert.False(Auth.TryAdmin(expiredAdmin, new StringValues("Bearer admin-key"), out _));
		Assert.False(Auth.TryAgent(expiredAgent, new StringValues("Bearer agent-1"), out _));
	}
}
