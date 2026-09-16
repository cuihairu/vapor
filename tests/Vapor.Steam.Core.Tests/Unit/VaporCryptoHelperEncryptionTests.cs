using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

[Collection(VaporCryptoHelperTestCollection.Name)]
public sealed class VaporCryptoHelperEncryptionTests : IDisposable
{
	public VaporCryptoHelperEncryptionTests()
	{
		VaporCryptoHelper.ResetForTests();
		VaporCryptoHelper.SetEncryptionKey(new string('K', 32));
	}

	[Fact]
	public async Task EncryptAndDecryptAES_UsesAesGcmRoundTrip()
	{
		const string plaintext = "secret-value";

		var encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, plaintext);
		var decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, encrypted!);

		Assert.NotNull(encrypted);
		Assert.StartsWith("gcm:", encrypted, StringComparison.Ordinal);
		Assert.Equal(plaintext, decrypted);
	}

	[Fact]
	public async Task DecryptAES_WithTamperedCiphertext_ReturnsNull()
	{
		const string plaintext = "secret-value";
		var encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, plaintext)!;
		var payload = encrypted["gcm:".Length..].ToCharArray();
		payload[^1] = payload[^1] == 'A' ? 'B' : 'A';

		var decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, "gcm:" + new string(payload));

		Assert.Null(decrypted);
	}

	[Fact]
	public async Task DecryptAES_WithLegacyCbcCiphertext_RemainsCompatible()
	{
		const string plaintext = "legacy-secret";
		var legacy = EncryptLegacyCbc(plaintext, new string('K', 32));

		var decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, legacy);

		Assert.Equal(plaintext, decrypted);
	}

	[Fact]
	public void EncryptWithKey_DecryptWithKey_RoundTripsWithoutGlobalKey()
	{
		byte[] keyMaterial = Encoding.UTF8.GetBytes(new string('Z', 40));
		const string plaintext = "rotation-secret";

		string? encrypted = VaporCryptoHelper.EncryptWithKey(keyMaterial, plaintext);
		string? decrypted = VaporCryptoHelper.DecryptWithKey(keyMaterial, encrypted!);

		Assert.NotNull(encrypted);
		Assert.StartsWith("gcm:", encrypted, StringComparison.Ordinal);
		Assert.Equal(plaintext, decrypted);
	}

	[Fact]
	public void EncryptWithKey_WithShortKey_Throws()
	{
		Assert.Throws<ArgumentException>(
			() => VaporCryptoHelper.EncryptWithKey(new byte[16], "value"));
	}

	[Fact]
	public void DecryptWithKey_WithNullKey_Throws()
	{
		Assert.Throws<ArgumentNullException>(
			() => VaporCryptoHelper.DecryptWithKey(null!, "value"));
	}

	[Fact]
	public void ConfigureFromEnvironment_WithBase64Key_AppliesRawKeyBytes()
	{
		VaporCryptoHelper.ResetForTests();
		byte[] rawKey = RandomNumberGenerator.GetBytes(32);
		string base64Key = Convert.ToBase64String(rawKey);
		var environment = new Dictionary<string, string?>
		{
			["VAPOR_ENCRYPTION_KEY_BASE64"] = base64Key
		};

		VaporCryptoHelper.ConfigureFromEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

		Assert.False(VaporCryptoHelper.HasDefaultKey);

		const string plaintext = "kms-provisioned-secret";
		string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, plaintext);
		string? decryptedWithRawKey = VaporCryptoHelper.DecryptWithKey(rawKey, encrypted!);

		Assert.Equal(plaintext, decryptedWithRawKey);
	}

	[Fact]
	public async Task ConfigureFromEnvironment_WithKeyFile_AppliesFileContent()
	{
		VaporCryptoHelper.ResetForTests();
		// Content that is not valid base64 falls back to raw UTF-8 interpretation.
		string keyText = "vapor-master-key-file-content!0123456789";
		string keyFile = Path.Combine(Path.GetTempPath(), "vapor-key-" + Guid.NewGuid().ToString("N"));
		await File.WriteAllTextAsync(keyFile, keyText);

		try
		{
			var environment = new Dictionary<string, string?>
			{
				["VAPOR_ENCRYPTION_KEY_FILE"] = keyFile
			};

			VaporCryptoHelper.ConfigureFromEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

			Assert.False(VaporCryptoHelper.HasDefaultKey);

			const string plaintext = "file-provisioned-secret";
			string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, plaintext);
			string? decryptedWithRawKey = VaporCryptoHelper.DecryptWithKey(Encoding.UTF8.GetBytes(keyText), encrypted!);

			Assert.Equal(plaintext, decryptedWithRawKey);
		}
		finally
		{
			File.Delete(keyFile);
		}
	}

	[Fact]
	public async Task ConfigureFromEnvironment_WithBase64KeyFile_DecodesKeyBytes()
	{
		VaporCryptoHelper.ResetForTests();
		byte[] rawKey = RandomNumberGenerator.GetBytes(32);
		string keyFile = Path.Combine(Path.GetTempPath(), "vapor-key-" + Guid.NewGuid().ToString("N"));
		await File.WriteAllTextAsync(keyFile, Convert.ToBase64String(rawKey));

		try
		{
			var environment = new Dictionary<string, string?>
			{
				["VAPOR_ENCRYPTION_KEY_FILE"] = keyFile
			};

			VaporCryptoHelper.ConfigureFromEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

			Assert.False(VaporCryptoHelper.HasDefaultKey);

			const string plaintext = "file-b64-secret";
			string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, plaintext);
			string? decryptedWithRawKey = VaporCryptoHelper.DecryptWithKey(rawKey, encrypted!);

			Assert.Equal(plaintext, decryptedWithRawKey);
		}
		finally
		{
			File.Delete(keyFile);
		}
	}

	[Fact]
	public void ConfigureFromEnvironment_Base64TakesPrecedenceOverPlainKey()
	{
		VaporCryptoHelper.ResetForTests();
		byte[] rawKey = RandomNumberGenerator.GetBytes(32);
		var environment = new Dictionary<string, string?>
		{
			["VAPOR_ENCRYPTION_KEY"] = new string('K', 32),
			["VAPOR_ENCRYPTION_KEY_BASE64"] = Convert.ToBase64String(rawKey)
		};

		VaporCryptoHelper.ConfigureFromEnvironment(key => environment.TryGetValue(key, out var value) ? value : null);

		const string plaintext = "precedence-secret";
		string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, plaintext);
		string? decryptedWithBase64Key = VaporCryptoHelper.DecryptWithKey(rawKey, encrypted!);

		Assert.Equal(plaintext, decryptedWithBase64Key);
	}

	[Fact]
	public void SetEncryptionKeyFromBase64_WithInvalidBase64_Throws()
	{
		VaporCryptoHelper.ResetForTests();
		Assert.Throws<ArgumentException>(
			() => VaporCryptoHelper.SetEncryptionKeyFromBase64("not-valid-base64!!!"));
	}

	[Fact]
	public void SetEncryptionKeyFromFile_WithMissingFile_Throws()
	{
		VaporCryptoHelper.ResetForTests();
		Assert.Throws<FileNotFoundException>(
			() => VaporCryptoHelper.SetEncryptionKeyFromFile(Path.Combine(Path.GetTempPath(), "vapor-missing-" + Guid.NewGuid().ToString("N"))));
	}

	private static string EncryptLegacyCbc(string text, string keyMaterial)
	{
		byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));
		byte[] textData = Encoding.UTF8.GetBytes(text);

		Span<byte> iv = stackalloc byte[16];
		RandomNumberGenerator.Fill(iv);

		using Aes aes = Aes.Create();
		aes.BlockSize = 128;
		aes.KeySize = 256;
		aes.Key = key;

		byte[] encryptedIv = aes.EncryptEcb(iv, PaddingMode.None);
		byte[] encryptedText = aes.EncryptCbc(textData, iv);

		int encryptedCount = encryptedIv.Length + encryptedText.Length;
		byte[] result = ArrayPool<byte>.Shared.Rent(encryptedCount);
		try
		{
			Array.Copy(encryptedIv, result, encryptedIv.Length);
			Array.Copy(encryptedText, 0, result, encryptedIv.Length, encryptedText.Length);
			return Convert.ToBase64String(result, 0, encryptedCount);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(result);
		}
	}

	public void Dispose()
	{
		VaporCryptoHelper.ResetForTests();
	}
}
