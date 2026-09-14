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
	public async Task Load_FileWithNewerFormatVersion_FailsFast()
	{
		// A store written by a future version must not be silently reinterpreted.
		await File.WriteAllTextAsync(StorePath, """{ "version": 99, "accounts": {} }""");

		using var store = CreateStore();

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => store.HasCredentialsAsync("any-account"));
		Assert.Contains("newer than supported", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Load_V2FileWithoutAccountsObject_Throws()
	{
		await File.WriteAllTextAsync(StorePath, """{ "version": 2 }""");

		using var store = CreateStore();

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => store.HasCredentialsAsync("any-account"));
		Assert.Contains("missing the accounts object", ex.Message, StringComparison.Ordinal);
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
	public async Task SharedSecret_RoundTripsAcrossStoreInstances()
	{
		string account = "account-a";
		const string secret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";

		using (var store = CreateStore())
		{
			await store.SaveSharedSecretAsync(account, secret);
		}

		using var reloaded = CreateStore();

		Assert.Equal(secret, await reloaded.GetSharedSecretAsync(account));
	}

	[Fact]
	public async Task SharedSecret_IsEncryptedAtRest()
	{
		const string secret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";

		using var store = CreateStore();
		await store.SaveSharedSecretAsync("account-a", secret);

		string file = await File.ReadAllTextAsync(StorePath);

		// The secret must never appear in plain text on disk.
		Assert.DoesNotContain(secret, file, StringComparison.Ordinal);
		Assert.Contains("sharedSecret", file, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SharedSecret_MissingAccount_ReturnsNull()
	{
		using var store = CreateStore();

		Assert.Null(await store.GetSharedSecretAsync("ghost"));
	}

	[Fact]
	public async Task SharedSecret_OverwriteReplacesPreviousValue()
	{
		string account = "account-a";

		using var store = CreateStore();
		await store.SaveSharedSecretAsync(account, "first");
		await store.SaveSharedSecretAsync(account, "second");

		Assert.Equal("second", await store.GetSharedSecretAsync(account));
	}

	[Fact]
	public async Task IdentitySecret_RoundTripsAcrossStoreInstances()
	{
		string account = "account-a";
		const string secret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

		using (var store = CreateStore())
		{
			await store.SaveIdentitySecretAsync(account, secret);
		}

		using var reloaded = CreateStore();

		Assert.Equal(secret, await reloaded.GetIdentitySecretAsync(account));
	}

	[Fact]
	public async Task IdentitySecret_IsEncryptedAtRest()
	{
		const string secret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

		using var store = CreateStore();
		await store.SaveIdentitySecretAsync("account-a", secret);

		string file = await File.ReadAllTextAsync(StorePath);

		// The identity secret must never appear in plain text on disk.
		Assert.DoesNotContain(secret, file, StringComparison.Ordinal);
		Assert.Contains("identitySecret", file, StringComparison.Ordinal);
	}

	[Fact]
	public async Task IdentitySecret_MissingAccount_ReturnsNull()
	{
		using var store = CreateStore();

		Assert.Null(await store.GetIdentitySecretAsync("ghost"));
	}

	[Fact]
	public async Task IdentitySecret_OverwriteReplacesPreviousValue()
	{
		string account = "account-a";

		using var store = CreateStore();
		await store.SaveIdentitySecretAsync(account, "first");
		await store.SaveIdentitySecretAsync(account, "second");

		Assert.Equal("second", await store.GetIdentitySecretAsync(account));
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

	[Fact]
	public void Dispose_Twice_IsIdempotent()
	{
		var store = CreateStore();
		store.Dispose();

		store.Dispose(); // Second dispose must be a no-op, not an ObjectDisposedException.
	}

	[Fact]
	public async Task SecondLoad_HitsDoubleCheckLockEarlyReturn()
	{
		// Park one reader inside the semaphore so a second reader queues behind the
		// outer _loaded check; after the first reader populates the store, the second
		// must exit through the in-lock early return.
		var store = CreateStore();
		var gate = (System.Threading.SemaphoreSlim)typeof(FileCredentialStore)
			.GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(store)!;

		await gate.WaitAsync();
		var first = store.GetRefreshTokenAsync("account-a");
		var second = store.GetRefreshTokenAsync("account-b");
		// Give both tasks time to reach the semaphore before releasing it.
		await Task.Delay(50);
		gate.Release();

		await Task.WhenAll(first, second);
		store.Dispose();
	}

	[Fact]
	public async Task Load_BothMainAndBackupCorrupted_StartsFresh()
	{
		await File.WriteAllTextAsync(StorePath, "{ this is not json");
		await File.WriteAllTextAsync(BackupPath, "][ also not json");

		using var store = CreateStore();

		Assert.False(await store.HasCredentialsAsync("account-a"));
		Assert.Null(await store.GetRefreshTokenAsync("account-a"));

		// The fresh store remains usable and rewrites a valid file.
		await store.SaveRefreshTokenAsync("account-a", "token");
		Assert.Equal("token", await store.GetRefreshTokenAsync("account-a"));
	}

	[Fact]
	public async Task Save_WhenDirectoryNotWritable_SwallowsWriteFailure()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // Unix directory modes are not available on Windows.
		}

		using var store = CreateStore();
		File.SetUnixFileMode(_dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		try
		{
			// The write into the read-only directory fails; the store logs and swallows.
			await store.SaveRefreshTokenAsync("account-a", "token");
			Assert.False(File.Exists(StorePath));
		}
		finally
		{
			// Restore so the fixture's directory cleanup can delete the tree.
			File.SetUnixFileMode(_dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}
}
