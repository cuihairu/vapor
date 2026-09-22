using System.IO;
using System.Reflection;
using System.Text.Json;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

public sealed class CredentialStoreRotatorTests : IDisposable
{
	private static readonly byte[] OldKey = new byte[32];
	private static readonly byte[] NewKey = new byte[32];

	static CredentialStoreRotatorTests()
	{
		OldKey.AsSpan().Fill((byte)1);
		NewKey.AsSpan().Fill((byte)2);
	}

	private readonly string _storePath;

	public CredentialStoreRotatorTests()
	{
		_storePath = Path.Combine(Path.GetTempPath(), "vapor-rotate-tests-" + Guid.NewGuid().ToString("N") + ".json");
	}

	public void Dispose()
	{
		try
		{
			File.Delete(_storePath);
		}
		catch (IOException)
		{
		}
	}

	private async Task WriteV2StoreAsync(Dictionary<string, (string? RefreshToken, string? AccessToken)> accounts, byte[] key)
	{
		var fileAccounts = new Dictionary<string, object>();
		foreach (var (account, tokens) in accounts)
		{
			fileAccounts[account] = new Dictionary<string, object?>
			{
				["refreshToken"] = tokens.RefreshToken == null
					? null
					: VaporCryptoHelper.EncryptWithKey(key, tokens.RefreshToken),
				["accessToken"] = tokens.AccessToken == null
					? null
					: VaporCryptoHelper.EncryptWithKey(key, tokens.AccessToken)
			};
		}

		var store = new Dictionary<string, object?>
		{
			["version"] = 2,
			["accounts"] = fileAccounts
		};

		await File.WriteAllTextAsync(_storePath, JsonSerializer.Serialize(store));
	}

	private static async Task<JsonElement> ReadAccountsAsync(string path)
	{
		string json = await File.ReadAllTextAsync(path);
		return JsonDocument.Parse(json).RootElement.GetProperty("accounts");
	}

	[Fact]
	public async Task Rotate_ReEncryptsTokensWithNewKey()
	{
		await WriteV2StoreAsync(new Dictionary<string, (string?, string?)>
		{
			["account-a"] = ("refresh-plain-value", "access-plain-value")
		}, OldKey);

		var result = CredentialStoreRotator.Rotate(_storePath, OldKey, NewKey);

		Assert.True(result.Success);
		Assert.Equal(1, result.TotalAccounts);
		Assert.Equal(1, result.RotatedAccounts);

		JsonElement accounts = await ReadAccountsAsync(_storePath);
		string encryptedRefresh = accounts.GetProperty("account-a").GetProperty("refreshToken").GetString()!;

		Assert.StartsWith("gcm:", encryptedRefresh, StringComparison.Ordinal);

		string decryptedWithNewKey = VaporCryptoHelper.DecryptWithKey(NewKey, encryptedRefresh)!;
		Assert.Equal("refresh-plain-value", decryptedWithNewKey);

		string? decryptedWithOldKey = VaporCryptoHelper.DecryptWithKey(OldKey, encryptedRefresh);
		Assert.Null(decryptedWithOldKey);
	}

	[Fact]
	public async Task Rotate_DryRun_DoesNotModifyFile()
	{
		await WriteV2StoreAsync(new Dictionary<string, (string?, string?)>
		{
			["account-a"] = ("refresh-plain-value", null)
		}, OldKey);

		string before = await File.ReadAllTextAsync(_storePath);

		var result = CredentialStoreRotator.Rotate(_storePath, OldKey, NewKey, dryRun: true);

		Assert.True(result.Success);
		Assert.True(result.DryRun);

		string after = await File.ReadAllTextAsync(_storePath);
		Assert.Equal(before, after);
		Assert.False(File.Exists(_storePath + ".bak.pre-rotate"));
	}

	[Fact]
	public async Task Rotate_WithWrongOldKey_AbortsAndLeavesFileUnchanged()
	{
		await WriteV2StoreAsync(new Dictionary<string, (string?, string?)>
		{
			["account-a"] = ("refresh-plain-value", null)
		}, OldKey);

		string before = await File.ReadAllTextAsync(_storePath);
		byte[] wrongKey = new byte[32];
		wrongKey.AsSpan().Fill((byte)9);

		var result = CredentialStoreRotator.Rotate(_storePath, wrongKey, NewKey);

		Assert.False(result.Success);
		Assert.Contains("account-a", result.FailedAccounts);
		Assert.Equal(0, result.RotatedAccounts);

		string after = await File.ReadAllTextAsync(_storePath);
		Assert.Equal(before, after);
	}

	[Fact]
	public async Task Rotate_LegacyV1PlainText_UpgradesToEncryptedV2()
	{
		var legacy = new Dictionary<string, object>
		{
			["legacy-account"] = new Dictionary<string, object?>
			{
				["refreshToken"] = "plain-legacy-token"
			}
		};
		await File.WriteAllTextAsync(_storePath, JsonSerializer.Serialize(legacy));

		var result = CredentialStoreRotator.Rotate(_storePath, OldKey, NewKey);

		Assert.True(result.Success);

		string json = await File.ReadAllTextAsync(_storePath);
		using var document = JsonDocument.Parse(json);
		Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
		Assert.DoesNotContain("plain-legacy-token", json, StringComparison.Ordinal);

		string encrypted = document.RootElement.GetProperty("accounts").GetProperty("legacy-account").GetProperty("refreshToken").GetString()!;
		string decrypted = VaporCryptoHelper.DecryptWithKey(NewKey, encrypted)!;
		Assert.Equal("plain-legacy-token", decrypted);
	}

	[Fact]
	public void Rotate_MissingStoreFile_Throws()
	{
		Assert.Throws<FileNotFoundException>(
			() => CredentialStoreRotator.Rotate(_storePath + ".missing", OldKey, NewKey));
	}

	[Fact]
	public void CredentialRotationException_ConstructorChain_CarriesMessageAndInner()
	{
		// Full ctor surface of the private rotation exception: reflection keeps the
		// type private while pinning the contract every future throw site relies on.
		Type exType = typeof(CredentialStoreRotator).GetNestedType(
			"CredentialRotationException", BindingFlags.NonPublic)!;
		var parameterless = (Exception)Activator.CreateInstance(exType, nonPublic: true)!;
		Assert.StartsWith("Exception of type", parameterless.Message, StringComparison.Ordinal);

		var inner = new IOException("disk gone");
		var full = (Exception)exType.GetConstructor(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
				[typeof(string), typeof(Exception)], null)!
			.Invoke(["rotation failed", inner]);
		Assert.Equal("rotation failed", full.Message);
		Assert.Same(inner, full.InnerException);
	}

	[Fact]
	public async Task Rotate_NullAccountsRoot_RotatesNothingThroughSuccessPath()
	{
		// A JSON "null" document deserializes to no accounts; the rotation then
		// walks the full success path (backup + atomic replace) with an empty
		// map, and the null-logger arms of every diagnostic site stay quiet.
		await File.WriteAllTextAsync(_storePath, "null");

		var result = CredentialStoreRotator.Rotate(_storePath, OldKey, NewKey, logger: null);

		Assert.True(result.Success);
		Assert.Equal(0, result.TotalAccounts);
		Assert.Equal(0, result.RotatedAccounts);
		Assert.Empty(result.FailedAccounts);
		Assert.True(File.Exists(_storePath + ".bak.pre-rotate"));
	}

	[Fact]
	public async Task Rotate_UndecryptableAccountsWithNullLogger_AbortsQuietly()
	{
		// An account that fails to decrypt with the old key aborts the rotation;
		// with a null logger both the per-account error and the abort warning
		// are skipped instead of thrown.
		await WriteV2StoreAsync(new Dictionary<string, (string?, string?)>
		{
			["ghost"] = ("refresh-plain-value", null)
		}, OldKey);
		byte[] wrongKey = new byte[32];
		wrongKey.AsSpan().Fill((byte)3);

		var result = CredentialStoreRotator.Rotate(_storePath, wrongKey, NewKey, logger: null);

		Assert.False(result.Success);
		Assert.Equal(new[] { "ghost" }, result.FailedAccounts);
	}
}
