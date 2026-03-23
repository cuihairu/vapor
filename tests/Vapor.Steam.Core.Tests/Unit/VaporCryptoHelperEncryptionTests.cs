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
