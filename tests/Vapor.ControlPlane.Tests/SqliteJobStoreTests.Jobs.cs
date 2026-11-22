using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>CreateJob tests: credential-payload stripping and agent-side provisioning-secret preservation.</summary>
public sealed partial class SqliteJobStoreTests
{
	[Theory]
	[InlineData("password")]
	[InlineData("PASS")]
	[InlineData("pass")]
	[InlineData("refreshToken")]
	[InlineData("refresh_token")]
	[InlineData("accessToken")]
	[InlineData("access_token")]
	[InlineData("authCode")]
	[InlineData("auth_code")]
	[InlineData("twoFactorCode")]
	[InlineData("two_factor_code")]
	public async Task CreateJob_StripsCredentialPayloadKeysBeforeTaskPersistence(string key)
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest(
				"login",
				"local",
				["acct-1"],
				new Dictionary<string, object?> { [key] = "plaintext-secret", ["message"] = "keep" },
				null),
			cts.Token);

		JobTask task = Assert.Single(created.Tasks);
		Assert.False(task.Payload!.ContainsKey(key));
		Assert.Equal("keep", task.Payload["message"]);

		string payloadJson = RawScalar(store, "SELECT payload_json FROM tasks WHERE id = $id", ("$id", task.Id));
		Assert.DoesNotContain("plaintext-secret", payloadJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CreateJob_PreservesAgentSideProvisioningSecrets()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		// set_proxy, save_shared_secret and save_identity_secret are dispatched through
		// the same task queue and read their credential from the payload at execution time;
		// they must NOT be stripped like login-session aliases.
		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest(
				"set_proxy",
				"local",
				["agent:agent-1"],
				new Dictionary<string, object?>
				{
					["account"] = "acct-1",
					["proxy"] = "socks5://alice:s3cret@10.0.0.9:1080",
					["shared_secret"] = "Zm9vYmFy",
					["identitySecret"] = "YmF6cXV4",
				},
				null),
			cts.Token);

		JobTask task = Assert.Single(created.Tasks);
		Assert.Equal("socks5://alice:s3cret@10.0.0.9:1080", task.Payload!["proxy"]?.ToString());
		Assert.Equal("Zm9vYmFy", task.Payload["shared_secret"]?.ToString());
		Assert.Equal("YmF6cXV4", task.Payload["identitySecret"]?.ToString());
	}

}
