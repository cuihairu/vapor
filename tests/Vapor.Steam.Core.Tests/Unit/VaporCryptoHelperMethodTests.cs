using System.Text;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Branch coverage for the per-method Encrypt/Decrypt dispatch surface
/// (plain text / env var / file references), key-reuse guards, and the
/// corrupted-ciphertext paths that must degrade to null rather than throw.
/// </summary>
[Collection(VaporCryptoHelperTestCollection.Name)]
public sealed class VaporCryptoHelperMethodTests : IDisposable
{
	private const string TestEnvVar = "VAPOR_TEST_CRYPTO_SECRET";

	public VaporCryptoHelperMethodTests()
	{
		VaporCryptoHelper.ResetForTests();
		VaporCryptoHelper.SetEncryptionKey(new string('K', 32));
	}

	// --- key can only be set once ---

	[Fact]
	public void SetEncryptionKey_Twice_Throws()
	{
		Assert.Throws<InvalidOperationException>(() => VaporCryptoHelper.SetEncryptionKey(new string('X', 32)));
	}

	[Fact]
	public void SetEncryptionKeyFromBase64_Twice_Throws()
	{
		byte[] rawKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

		Assert.Throws<InvalidOperationException>(
			() => VaporCryptoHelper.SetEncryptionKeyFromBase64(Convert.ToBase64String(rawKey)));
	}

	[Fact]
	public void SetEncryptionKeyFromFile_Twice_Throws()
	{
		// The key-once guard fires before the path is touched — no file needed.
		Assert.Throws<InvalidOperationException>(
			() => VaporCryptoHelper.SetEncryptionKeyFromFile("/nonexistent/key"));
	}

	[Fact]
	public void ConfigureFromEnvironment_WhenKeyAlreadySet_DoesNotOverride()
	{
		VaporCryptoHelper.ConfigureFromEnvironment(key => new string('Y', 32));

		// The configured value is ignored; a bad key string would have thrown.
		Assert.False(VaporCryptoHelper.HasDefaultKey);
	}

	[Fact]
	public async Task SetEncryptionKeyFromFile_NonBase64Text_FallsBackToRawUtf8Key()
	{
		VaporCryptoHelper.ResetForTests();
		string path = Path.Combine(Path.GetTempPath(), "vapor-key-" + Guid.NewGuid().ToString("N"));
		await File.WriteAllTextAsync(path, "raw vapor key file content with spaces !! not base64");
		try
		{
			VaporCryptoHelper.SetEncryptionKeyFromFile(path);

			// The raw UTF-8 bytes became the active key material and round-trip cleanly.
			string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, "roundtrip");
			Assert.NotNull(encrypted);
			Assert.Equal("roundtrip", await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, encrypted!));
		}
		finally
		{
			File.Delete(path);
		}
	}

	// --- per-method dispatch (Encrypt) ---

	[Fact]
	public void Encrypt_PlainText_PassesThrough()
	{
		Assert.Equal("raw-secret", VaporCryptoHelper.Encrypt(ECryptoMethod.PlainText, "raw-secret"));
	}

	[Fact]
	public void Encrypt_EnvironmentVariable_StoresReferenceAsIs()
	{
		const string reference = "env:MY_SECRET_VAR";

		Assert.Equal(reference, VaporCryptoHelper.Encrypt(ECryptoMethod.EnvironmentVariable, reference));
	}

	[Fact]
	public void Encrypt_File_StoresReferenceAsIs()
	{
		const string reference = "file:/etc/vapor/secret";

		Assert.Equal(reference, VaporCryptoHelper.Encrypt(ECryptoMethod.File, reference));
	}

	[Fact]
	public void Encrypt_UndefinedMethod_Throws()
	{
		Assert.Throws<System.ComponentModel.InvalidEnumArgumentException>(
			() => VaporCryptoHelper.Encrypt((ECryptoMethod)0xFF, "value"));
	}

	// --- per-method dispatch (Decrypt) ---

	[Fact]
	public async Task Decrypt_PlainText_PassesThrough()
	{
		string? decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.PlainText, "raw-secret");
		Assert.Equal("raw-secret", decrypted);
	}

	[Fact]
	public async Task Decrypt_EnvironmentVariable_WithPrefix_ReadsValue()
	{
		Environment.SetEnvironmentVariable(TestEnvVar, "  from-env  ");
		try
		{
			var decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.EnvironmentVariable, $"env:{TestEnvVar}");
			Assert.Equal("from-env", decrypted);
		}
		finally
		{
			Environment.SetEnvironmentVariable(TestEnvVar, null);
		}
	}

	[Fact]
	public async Task Decrypt_EnvironmentVariable_BareName_ReadsValue()
	{
		Environment.SetEnvironmentVariable(TestEnvVar, "bare-value");
		try
		{
			var decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.EnvironmentVariable, TestEnvVar);
			Assert.Equal("bare-value", decrypted);
		}
		finally
		{
			Environment.SetEnvironmentVariable(TestEnvVar, null);
		}
	}

	[Fact]
	public async Task Decrypt_EnvironmentVariable_Missing_ReturnsNull()
	{
		Environment.SetEnvironmentVariable(TestEnvVar, null);

		string? decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.EnvironmentVariable, $"env:{TestEnvVar}");
		Assert.Null(decrypted);
	}

	[Fact]
	public async Task Decrypt_File_WithPrefix_ReadsAndTrims()
	{
		string path = Path.Combine(Path.GetTempPath(), "vapor-secret-" + Guid.NewGuid().ToString("N"));
		await File.WriteAllTextAsync(path, "  file-secret  \n");

		try
		{
			var decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.File, $"file:{path}");
			Assert.Equal("file-secret", decrypted);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Decrypt_File_Missing_ReturnsNull()
	{
		string? decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.File, "file:/nonexistent/secret");
		Assert.Null(decrypted);
	}

	[Fact]
	public async Task Decrypt_File_UnreadableFile_ReturnsNull()
	{
		string path = Path.Combine(Path.GetTempPath(), "vapor-secret-" + Guid.NewGuid().ToString("N"));
		await File.WriteAllTextAsync(path, "locked");
		try
		{
			// An exclusive handle makes the file unreadable to ReadAllTextAsync;
			// the failure degrades to null like a miss.
			using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
			{
				Assert.Null(await VaporCryptoHelper.Decrypt(ECryptoMethod.File, path));
			}
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Decrypt_UndefinedMethod_Throws()
	{
		await Assert.ThrowsAsync<System.ComponentModel.InvalidEnumArgumentException>(
			() => VaporCryptoHelper.Decrypt((ECryptoMethod)0xFF, "value"));
	}

	// --- HasTransformation ---

	[Fact]
	public void HasTransformation_IsOnlyTrueForAes()
	{
		Assert.False(VaporCryptoHelper.HasTransformation(ECryptoMethod.PlainText));
		Assert.True(VaporCryptoHelper.HasTransformation(ECryptoMethod.AES));
		Assert.False(VaporCryptoHelper.HasTransformation(ECryptoMethod.EnvironmentVariable));
		Assert.False(VaporCryptoHelper.HasTransformation(ECryptoMethod.File));
	}

	// --- corrupted ciphertext degrades to null ---

	[Fact]
	public async Task Decrypt_GcmCiphertextNotBase64_ReturnsNull()
	{
		Assert.Null(await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, "gcm:not-valid-base64!!!"));
	}

	[Fact]
	public async Task Decrypt_GcmPayloadTooShort_ReturnsNull()
	{
		string tooShort = Convert.ToBase64String(new byte[8]); // < nonce + tag sizes
		Assert.Null(await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, "gcm:" + tooShort));
	}

	[Fact]
	public async Task Decrypt_CbcPayloadTooShort_ReturnsNull()
	{
		string tooShort = Convert.ToBase64String(new byte[8]); // < one AES block
		Assert.Null(await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, tooShort));
	}

	// --- default key path ---

	[Fact]
	public async Task Encrypt_WithoutConfiguredKey_UsesDefaultKey()
	{
		VaporCryptoHelper.ResetForTests(); // back to the default key
		Assert.True(VaporCryptoHelper.HasDefaultKey);

		string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, "default-key-secret");
		string? decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, encrypted!);

		Assert.Equal("default-key-secret", decrypted);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(TestEnvVar, null);
		VaporCryptoHelper.ResetForTests();
	}
}
