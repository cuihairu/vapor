using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Agent.Tests;

/// <summary>
/// The import CLI touches the global encryption key through FileCredentialStore, so the
/// whole class runs as one non-parallel collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MaFileImportCliCollection
{
	public const string Name = "MaFileImportCli";
}

[Collection(MaFileImportCliCollection.Name)]
public sealed class MaFileImportCliTests : IDisposable
{
	private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "vapor-mafile-cli-tests-" + Guid.NewGuid().ToString("N"));
	private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "vapor-mafile-cli-work-" + Guid.NewGuid().ToString("N"));
	private const string SharedSecret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";
	private const string IdentitySecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

	public MaFileImportCliTests()
	{
		VaporCryptoHelper.ResetForTests();
		VaporCryptoHelper.SetEncryptionKey(new string('K', 32));
		Directory.CreateDirectory(_dataDirectory);
		Directory.CreateDirectory(_workDirectory);
	}

	public void Dispose()
	{
		VaporCryptoHelper.ResetForTests();

		try
		{
			Directory.Delete(_dataDirectory, recursive: true);
			Directory.Delete(_workDirectory, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private FileCredentialStore CreateStore() =>
		new(NullLogger<FileCredentialStore>.Instance, _dataDirectory);

	[Fact]
	public async Task RunAsync_ImportsSecretsIntoStore()
	{
		string file = Path.Combine(_workDirectory, "alice.maFile");
		await File.WriteAllTextAsync(file, $$"""
			{
				"steamid": "76561198000000001",
				"account_name": "alice",
				"Steamguard": {
					"shared_secret": "{{SharedSecret}}",
					"identity_secret": "{{IdentitySecret}}",
					"device_id": "android:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
				}
			}
			""");

		int exitCode;
		using (var store = CreateStore())
		{
			exitCode = await MaFileImportCli.RunAsync([file], store, NullLogger.Instance);
		}

		Assert.Equal(0, exitCode);

		using var reloaded = CreateStore();
		Assert.Equal(SharedSecret, await reloaded.GetSharedSecretAsync("alice"));
		Assert.Equal(IdentitySecret, await reloaded.GetIdentitySecretAsync("alice"));
	}

	[Fact]
	public async Task RunAsync_Directory_ScansMaFilesOnly()
	{
		await File.WriteAllTextAsync(Path.Combine(_workDirectory, "bob.maFile"), $$"""{ "steamid": "76561198000000002", "account_name": "bob", "shared_secret": "{{SharedSecret}}" }""");
		await File.WriteAllTextAsync(Path.Combine(_workDirectory, "carol.maFile"), $$"""{ "steamid": "76561198000000003", "account_name": "carol", "identity_secret": "{{IdentitySecret}}" }""");
		await File.WriteAllTextAsync(Path.Combine(_workDirectory, "notes.txt"), "not a maFile");

		int exitCode;
		using (var store = CreateStore())
		{
			exitCode = await MaFileImportCli.RunAsync([_workDirectory], store, NullLogger.Instance);
		}

		Assert.Equal(0, exitCode);

		using var reloaded = CreateStore();
		Assert.Equal(SharedSecret, await reloaded.GetSharedSecretAsync("bob"));
		Assert.Equal(IdentitySecret, await reloaded.GetIdentitySecretAsync("carol"));
		Assert.False(await reloaded.HasCredentialsAsync("notes"));
	}

	[Fact]
	public async Task RunAsync_PartialFailure_ReturnsNonZeroButImportsTheRest()
	{
		await File.WriteAllTextAsync(Path.Combine(_workDirectory, "good.maFile"), $$"""{ "steamid": "76561198000000004", "account_name": "good", "shared_secret": "{{SharedSecret}}" }""");
		await File.WriteAllTextAsync(Path.Combine(_workDirectory, "bad.maFile"), "{ not json");

		int exitCode;
		using (var store = CreateStore())
		{
			exitCode = await MaFileImportCli.RunAsync(
			[
				Path.Combine(_workDirectory, "good.maFile"),
				Path.Combine(_workDirectory, "bad.maFile")
			], store, NullLogger.Instance);
		}

		Assert.Equal(1, exitCode);

		using var reloaded = CreateStore();
		Assert.Equal(SharedSecret, await reloaded.GetSharedSecretAsync("good"));
	}

	[Fact]
	public async Task RunAsync_EncryptedFile_WithPasswordOption()
	{
		string file = Path.Combine(_workDirectory, "encrypted.maFile");
		await File.WriteAllTextAsync(file, SdaEncryptedSharedSecretOnly("erin", "76561198000000005", "pw"));

		int exitCode;
		using (var store = CreateStore())
		{
			exitCode = await MaFileImportCli.RunAsync([file, "--password", "pw"], store, NullLogger.Instance);
		}

		Assert.Equal(0, exitCode);

		using var reloaded = CreateStore();
		Assert.Equal(SharedSecret, await reloaded.GetSharedSecretAsync("erin"));
	}

	[Fact]
	public async Task RunAsync_NoArguments_ReturnsUsageExitCode()
	{
		using var store = CreateStore();

		int exitCode = await MaFileImportCli.RunAsync([], store, NullLogger.Instance);

		Assert.Equal(2, exitCode);
	}

	[Fact]
	public async Task RunAsync_EmptyDirectory_NothingToImport()
	{
		// A directory that exists but holds no .maFile yields an empty expansion:
		// the CLI reports that nothing was found instead of silently succeeding.
		string emptyDir = Path.Combine(_workDirectory, "empty");
		Directory.CreateDirectory(emptyDir);

		using var store = CreateStore();

		int exitCode = await MaFileImportCli.RunAsync([emptyDir], store, NullLogger.Instance);

		Assert.Equal(2, exitCode);
	}

	[Fact]
	public async Task RunAsync_ExistingNonMaFilePath_ImportAttemptsTheFileDirectly()
	{
		// A path pointing at an existing file is kept as-is (directory scans filter to
		// .maFile, explicit paths do not), so a non-maFile name fails its own parse.
		string notes = Path.Combine(_workDirectory, "notes.txt");
		await File.WriteAllTextAsync(notes, "not a maFile");

		using var store = CreateStore();

		int exitCode = await MaFileImportCli.RunAsync([notes], store, NullLogger.Instance);

		Assert.Equal(1, exitCode);
		Assert.False(await store.HasCredentialsAsync("notes"));
	}

	[Fact]
	public async Task RunAsync_PasswordWithoutValue_ReturnsUsageExitCode()
	{
		using var store = CreateStore();
		string file = Path.Combine(_workDirectory, "x.maFile");
		await File.WriteAllTextAsync(file, "{}");

		int exitCode = await MaFileImportCli.RunAsync([file, "--password"], store, NullLogger.Instance);

		Assert.Equal(2, exitCode);
	}

	/// <summary>Builds a minimal password-encrypted SDA maFile (shared secret only).</summary>
	private static string SdaEncryptedSharedSecretOnly(string accountName, string steamId, string password)
	{
		byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
		byte[] iv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
		byte[] key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 50_000, System.Security.Cryptography.HashAlgorithmName.SHA1, 32);

		string cipher;
		using (var aes = System.Security.Cryptography.Aes.Create())
		{
			aes.Key = key;
			aes.IV = iv;
			aes.Mode = System.Security.Cryptography.CipherMode.CBC;
			aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
			using var encryptor = aes.CreateEncryptor();
			byte[] plain = System.Text.Encoding.UTF8.GetBytes($$"""{ "shared_secret": "{{SharedSecret}}" }""");
			cipher = Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
		}

		return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["steamid"] = steamId,
			["account_name"] = accountName,
			["encryption_iv"] = Convert.ToBase64String(iv),
			["encryption_salt"] = Convert.ToBase64String(salt),
			["Steamguard"] = cipher
		});
	}
}
