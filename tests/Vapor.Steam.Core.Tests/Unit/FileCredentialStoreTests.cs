using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

[Collection(VaporCryptoHelperTestCollection.Name)]
public sealed class FileCredentialStoreTests : IDisposable
{
	private readonly string _dataDirectory;

	public FileCredentialStoreTests()
	{
		VaporCryptoHelper.ResetForTests();
		VaporCryptoHelper.SetEncryptionKey(new string('K', 32));
		_dataDirectory = Path.Combine(Path.GetTempPath(), "vapor-cred-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dataDirectory);
	}

	public void Dispose()
	{
		VaporCryptoHelper.ResetForTests();

		try
		{
			Directory.Delete(_dataDirectory, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private string StorePath => Path.Combine(_dataDirectory, "credentials.json");

	private string BackupPath => StorePath + ".bak";

	private FileCredentialStore CreateStore() =>
		new(NullLogger<FileCredentialStore>.Instance, _dataDirectory);

	[Fact]
	public async Task SaveAndLoad_RoundTripsTokensEncryptedAtRest()
	{
		string account = "account-a";
		string refreshToken = "refresh-token-secret-value";
		string accessToken = "access-token-secret-value";

		using (var store = CreateStore())
		{
			await store.SaveRefreshTokenAsync(account, refreshToken);
			await store.SaveAccessTokenAsync(account, new StoredAccessToken(accessToken, DateTimeOffset.UtcNow.AddHours(1)));
		}

		string json = await File.ReadAllTextAsync(StorePath);
		Assert.DoesNotContain(refreshToken, json, StringComparison.Ordinal);
		Assert.DoesNotContain(accessToken, json, StringComparison.Ordinal);
		Assert.Contains("\"version\": 2", json, StringComparison.Ordinal);

		using (var store = CreateStore())
		{
			Assert.True(await store.HasCredentialsAsync(account));
			Assert.Equal(refreshToken, await store.GetRefreshTokenAsync(account));
			var storedAccess = await store.GetAccessTokenAsync(account);
			Assert.NotNull(storedAccess);
			Assert.Equal(accessToken, storedAccess.Token);
		}
	}

	[Fact]
	public async Task Load_MigratesLegacyV1FileToEncryptedV2()
	{
		string account = "legacy-account";
		string refreshToken = "legacy-refresh-token";
		string legacyJson = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			[account] = new Dictionary<string, object?>
			{
				["refreshToken"] = refreshToken,
				["refreshTokenUpdatedAt"] = DateTimeOffset.UtcNow
			}
		});

		await File.WriteAllTextAsync(StorePath, legacyJson);

		using (var store = CreateStore())
		{
			Assert.True(await store.HasCredentialsAsync(account));
			Assert.Equal(refreshToken, await store.GetRefreshTokenAsync(account));
		}

		string migratedJson = await File.ReadAllTextAsync(StorePath);
		Assert.Contains("\"version\": 2", migratedJson, StringComparison.Ordinal);
		Assert.DoesNotContain(refreshToken, migratedJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Load_CorruptedFile_FallsBackToBackup()
	{
		string account = "account-a";
		string firstToken = "first-refresh-token";
		string secondToken = "second-refresh-token";

		using (var store = CreateStore())
		{
			await store.SaveRefreshTokenAsync(account, firstToken);
			// Second save backs up the file holding the first token.
			await store.SaveRefreshTokenAsync(account, secondToken);
		}

		Assert.True(File.Exists(BackupPath));
		await File.WriteAllTextAsync(StorePath, "{ this is not valid json");

		using (var store = CreateStore())
		{
			Assert.True(await store.HasCredentialsAsync(account));
			Assert.Equal(firstToken, await store.GetRefreshTokenAsync(account));
		}

		// The corrupted live file should have been restored from the backup.
		string restoredJson = await File.ReadAllTextAsync(StorePath);
		Assert.Contains("\"version\": 2", restoredJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Load_CorruptedFileWithoutBackup_StartsFresh()
	{
		await File.WriteAllTextAsync(StorePath, "corrupted!");

		using var store = CreateStore();

		Assert.False(await store.HasCredentialsAsync("any-account"));
		Assert.Null(await store.GetRefreshTokenAsync("any-account"));
	}

	[Fact]
	public async Task Load_WithMismatchedEncryptionKey_FailsFast()
	{
		string account = "account-a";

		using (var store = CreateStore())
		{
			await store.SaveRefreshTokenAsync(account, "token-encrypted-with-original-key");
		}

		VaporCryptoHelper.ResetForTests();
		VaporCryptoHelper.SetEncryptionKey(new string('X', 32));

		using var reloaded = CreateStore();
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => reloaded.GetRefreshTokenAsync(account));
	}

	[Fact]
	public async Task Save_CreatesBackupAndLeavesNoTempFile()
	{
		string account = "account-a";

		using (var store = CreateStore())
		{
			await store.SaveRefreshTokenAsync(account, "token-v1");
			await store.SaveRefreshTokenAsync(account, "token-v2");
		}

		Assert.True(File.Exists(StorePath));
		Assert.True(File.Exists(BackupPath));

		string backupJson = await File.ReadAllTextAsync(BackupPath);
		Assert.DoesNotContain("token-v2", backupJson, StringComparison.Ordinal);

		string encryptedBackupToken = JsonDocument.Parse(backupJson)
			.RootElement.GetProperty("accounts")
			.GetProperty(account)
			.GetProperty("refreshToken")
			.GetString()!;
		string decryptedBackupToken = (await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, encryptedBackupToken))!;
		Assert.Equal("token-v1", decryptedBackupToken);

		Assert.False(File.Exists(StorePath + ".tmp"));
	}

	[Fact]
	public async Task Save_ModificationsAreVisibleToFreshStoreInstances()
	{
		string account = "account-a";

		using (var first = CreateStore())
		{
			await first.SaveRefreshTokenAsync(account, "shared-token");
		}

		using (var second = CreateStore())
		{
			Assert.Equal("shared-token", await second.GetRefreshTokenAsync(account));
			await second.RevokeCredentialsAsync(account);
		}

		using (var third = CreateStore())
		{
			Assert.False(await third.HasCredentialsAsync(account));
		}
	}

	[Fact]
	public async Task Revoke_RemovesOnlyTargetAccount()
	{
		using var store = CreateStore();

		await store.SaveRefreshTokenAsync("account-a", "token-a");
		await store.SaveRefreshTokenAsync("account-b", "token-b");
		await store.RevokeCredentialsAsync("account-a");

		Assert.False(await store.HasCredentialsAsync("account-a"));
		Assert.True(await store.HasCredentialsAsync("account-b"));
	}

	[Fact]
	public async Task ExpiredAccessToken_IsNotReturned()
	{
		string account = "account-a";

		using var store = CreateStore();

		await store.SaveAccessTokenAsync(account, new StoredAccessToken("expired-token", DateTimeOffset.UtcNow.AddMinutes(-1)));

		Assert.Null(await store.GetAccessTokenAsync(account));
	}

	[Fact]
	public async Task Save_RestrictsFilePermissionsToOwnerOnUnix()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // Unix file modes are not available on Windows.
		}

		using var store = CreateStore();
		await store.SaveRefreshTokenAsync("account-a", "token");

		UnixFileMode mode = File.GetUnixFileMode(StorePath);
		Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
	}

	[Fact]
	public async Task Load_TightensOverlyPermissivePermissionsOnUnix()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // Unix file modes are not available on Windows.
		}

		using (var store = CreateStore())
		{
			await store.SaveRefreshTokenAsync("account-a", "token");
		}

		File.SetUnixFileMode(StorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

		using (var store = CreateStore())
		{
			await store.GetRefreshTokenAsync("account-a");
		}

		UnixFileMode mode = File.GetUnixFileMode(StorePath);
		Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
	}
}
