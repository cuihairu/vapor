using Xunit;
using System.Text.Json;
using ModelContextProtocol;

namespace Vapor.Mcp.Tests;

public sealed class AccountSanitizerTests
{
	[Theory]
	[InlineData("password")]
	[InlineData("maFile")]
	[InlineData("shared_secret")]
	[InlineData("identity_secret")]
	[InlineData("refreshToken")]
	[InlineData("accessToken")]
	[InlineData("apiKey")]
	[InlineData("secretNote")]
	public async Task SanitizeAccounts_DropsCredentialShapedAndUnknownFields(string leakyField)
	{
		string body = $$"""
			{"accounts":[{
				"accountName":"alice",
				"enabled":true,
				"desiredState":"online",
				"region":"eu",
				"agentId":"agent-1",
				"steamId":76561197960265728,
				"{{leakyField}}":"top-secret-value"
			}]}
			""";

		string output = AccountSanitizer.SanitizeAccounts(body);

		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement account = Assert.Single(doc.RootElement.GetProperty("accounts").EnumerateArray());
		Assert.False(account.TryGetProperty(leakyField, out _));
		Assert.Equal("alice", account.GetProperty("accountName").GetString());
		Assert.True(account.GetProperty("enabled").GetBoolean());
		Assert.Equal("online", account.GetProperty("desiredState").GetString());
		Assert.Equal("eu", account.GetProperty("region").GetString());
		Assert.Equal("agent-1", account.GetProperty("agentId").GetString());
		Assert.Equal(76561197960265728, account.GetProperty("steamId").GetInt64());
	}

	[Fact]
	public async Task SanitizeAccounts_PreservesWhitelistedNestedFields()
	{
		string body = """
			{"accounts":[{
				"accountName":"bob",
				"enabled":false,
				"desiredState":"idle",
				"idleApps":[730,570],
				"note":"farm",
				"version":{"version":3,"updatedAt":"2026-11-14T00:00:00Z"},
				"marketListingsEnabled":true,
				"boostTargets":[{"appId":730,"targetHours":100.5}],
				"tradePolicy":{"autoAcceptGifts":true,"partnerWhitelist":[76561197960265728]},
				"farmPolicy":{"queue":[{"appId":730}],"dailyBudgetHours":2.5},
				"role":"Farm"
			}]}
			""";

		string output = AccountSanitizer.SanitizeAccounts(body);

		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement account = Assert.Single(doc.RootElement.GetProperty("accounts").EnumerateArray());
		Assert.Equal([730, 570], account.GetProperty("idleApps").EnumerateArray().Select(e => e.GetInt32()).ToArray());
		Assert.Equal("farm", account.GetProperty("note").GetString());
		Assert.Equal(3, account.GetProperty("version").GetProperty("version").GetInt32());
		Assert.True(account.GetProperty("marketListingsEnabled").GetBoolean());
		Assert.Equal(100.5, account.GetProperty("boostTargets")[0].GetProperty("targetHours").GetDouble());
		Assert.True(account.GetProperty("tradePolicy").GetProperty("autoAcceptGifts").GetBoolean());
		Assert.Equal(76561197960265728, account.GetProperty("tradePolicy").GetProperty("partnerWhitelist")[0].GetInt64());
		Assert.Equal(2.5, account.GetProperty("farmPolicy").GetProperty("dailyBudgetHours").GetDouble());
		Assert.Equal("Farm", account.GetProperty("role").GetString());
	}

	[Fact]
	public async Task SanitizeAccounts_MissingWhitelistedFields_OmitsThem()
	{
		string output = AccountSanitizer.SanitizeAccounts("""{"accounts":[{"accountName":"carol"}]}""");

		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement account = Assert.Single(doc.RootElement.GetProperty("accounts").EnumerateArray());
		Assert.Equal("carol", account.GetProperty("accountName").GetString());
		Assert.False(account.TryGetProperty("region", out _));
		Assert.False(account.TryGetProperty("steamId", out _));
	}

	[Fact]
	public async Task SanitizeAccounts_ProjectsEveryListEntry()
	{
		string output = AccountSanitizer.SanitizeAccounts("""
			{"accounts":[{"accountName":"alice"},{"accountName":"bob","password":"x"}]}
			""");

		using JsonDocument doc = JsonDocument.Parse(output);
		Assert.Equal(2, doc.RootElement.GetProperty("accounts").GetArrayLength());
		Assert.Equal("bob", doc.RootElement.GetProperty("accounts")[1].GetProperty("accountName").GetString());
	}

	[Theory]
	[InlineData("""{"accounts":{"accountName":"alice"}}""")]
	[InlineData("""{"agents":[]}""")]
	public void SanitizeAccounts_RejectsUnexpectedEnvelopeShape(string body)
		=> Assert.Throws<McpException>(() => AccountSanitizer.SanitizeAccounts(body));

	[Fact]
	public async Task SanitizeSpecEnvelope_ProjectsSpecAndDropsCredentials()
	{
		string output = AccountSanitizer.SanitizeSpecEnvelope("""
			{"spec":{"accountName":"alice","enabled":true,"desiredState":"offline","password":"x","maFile":"a.maFile"}}
			""");

		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement spec = doc.RootElement.GetProperty("spec");
		Assert.Equal("alice", spec.GetProperty("accountName").GetString());
		Assert.True(spec.GetProperty("enabled").GetBoolean());
		Assert.Equal("offline", spec.GetProperty("desiredState").GetString());
		Assert.False(spec.TryGetProperty("password", out _));
		Assert.False(spec.TryGetProperty("maFile", out _));
	}

	[Fact]
	public void SanitizeSpecEnvelope_RejectsMissingSpec()
		=> Assert.Throws<McpException>(
			() => AccountSanitizer.SanitizeSpecEnvelope("""{"accounts":[]}"""));

	[Fact]
	public void SafeFields_AreASubsetOfTheDeclaredAccountSpecWireShape()
	{
		// The whitelist is the safety contract: every entry must remain a real
		// AccountSpec wire field, and no credential-looking name may ever join it.
		string[] expected =
		[
			"accountName", "enabled", "desiredState", "idleApps", "region", "agentId",
			"note", "version", "marketListingsEnabled", "boostTargets", "tradePolicy",
			"farmPolicy", "role", "steamId",
		];
		Assert.Equal(expected, AccountSanitizer.SafeFields);
		Assert.All(AccountSanitizer.SafeFields, field =>
			Assert.DoesNotContain(field, ["password", "maFile", "sharedSecret", "identitySecret", "refreshToken", "accessToken"], StringComparer.OrdinalIgnoreCase));
	}
}
