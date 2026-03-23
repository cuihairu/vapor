using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace Vapor.Steam.Core.Security;

/// <summary>
/// Encryption method for password storage.
/// </summary>
public enum ECryptoMethod : byte
{
	/// <summary>
	/// No encryption - plain text storage. Not recommended for production.
	/// </summary>
	PlainText,

	/// <summary>
	/// AES encryption for credential storage. New writes use AES-GCM; legacy AES-CBC data remains readable.
	/// </summary>
	AES,

	/// <summary>
	/// Password is read from environment variable.
	/// Format: "env:VARIABLE_NAME"
	/// </summary>
	EnvironmentVariable,

	/// <summary>
	/// Password is read from an external file.
	/// Format: "file:/path/to/file"
	/// </summary>
	File
}

/// <summary>
/// Cryptographic helper for password encryption/decryption.
/// Based on ArchiSteamFarm's ArchiCryptoHelper.
/// </summary>
public static partial class VaporCryptoHelper
{
	private const byte MinimumCryptKeyBytes = 32;
	private const byte DefaultKeyLength = 32;
	private const string AesGcmPrefix = "gcm:";
	private const int AesGcmNonceSize = 12;
	private const int AesGcmTagSize = 16;
	internal const string EncryptionKeyEnvironmentVariable = "VAPOR_ENCRYPTION_KEY";
	internal const string AllowInsecureDefaultKeyEnvironmentVariable = "VAPOR_ALLOW_INSECURE_DEFAULT_KEY";

	private static byte[] _encryptionKey = [];
	private static bool _hasDefaultKey = true;

	/// <summary>
	/// Gets whether the default encryption key is being used.
	/// </summary>
	public static bool HasDefaultKey => _hasDefaultKey;

	public static void ConfigureFromEnvironment(Func<string, string?> getEnvironmentVariable)
	{
		ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

		var encryptionKey = getEnvironmentVariable(EncryptionKeyEnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(encryptionKey) && _hasDefaultKey)
		{
			SetEncryptionKey(encryptionKey);
		}
	}

	public static void EnsureSafeForEnvironment(Func<string, string?> getEnvironmentVariable)
	{
		ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

		if (!IsProductionEnvironment(getEnvironmentVariable))
		{
			return;
		}

		if (!_hasDefaultKey)
		{
			return;
		}

		var allowInsecure = getEnvironmentVariable(AllowInsecureDefaultKeyEnvironmentVariable);
		if (string.Equals(allowInsecure, "true", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		throw new InvalidOperationException(
			$"Default encryption key is not allowed in production. Set {EncryptionKeyEnvironmentVariable} to a custom key.");
	}

	/// <summary>
	/// Sets a custom encryption key for AES encryption.
	/// </summary>
	/// <param name="key">The encryption key.</param>
	public static void SetEncryptionKey(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);

		if (!_hasDefaultKey)
		{
			throw new InvalidOperationException("Encryption key can only be set once");
		}

		byte[] encryptionKey = Encoding.UTF8.GetBytes(key);

		if (encryptionKey.Length < MinimumCryptKeyBytes)
		{
			throw new ArgumentException(
				$"Encryption key is too short. Minimum recommended: {MinimumCryptKeyBytes} bytes",
				nameof(key)
			);
		}

		_hasDefaultKey = encryptionKey.SequenceEqual(GetDefaultKey());
		_encryptionKey = encryptionKey;
	}

	/// <summary>
	/// Encrypts a string using the specified method.
	/// </summary>
	/// <param name="cryptoMethod">The encryption method.</param>
	/// <param name="text">The text to encrypt.</param>
	/// <returns>The encrypted text, or null if encryption failed.</returns>
	public static string? Encrypt(ECryptoMethod cryptoMethod, string text)
	{
		if (!Enum.IsDefined(cryptoMethod))
		{
			throw new InvalidEnumArgumentException(nameof(cryptoMethod), (int)cryptoMethod, typeof(ECryptoMethod));
		}

		ArgumentException.ThrowIfNullOrEmpty(text);

		return cryptoMethod switch
		{
			ECryptoMethod.PlainText => text,
			ECryptoMethod.AES => EncryptAES(text),
			ECryptoMethod.EnvironmentVariable => text, // Stored as-is reference
			ECryptoMethod.File => text, // Stored as-is reference
			_ => throw new InvalidOperationException($"Unsupported crypto method: {cryptoMethod}")
		};
	}

	/// <summary>
	/// Decrypts a string using the specified method.
	/// </summary>
	/// <param name="cryptoMethod">The decryption method.</param>
	/// <param name="text">The encrypted text.</param>
	/// <returns>The decrypted text, or null if decryption failed.</returns>
	public static async Task<string?> Decrypt(ECryptoMethod cryptoMethod, string text)
	{
		if (!Enum.IsDefined(cryptoMethod))
		{
			throw new InvalidEnumArgumentException(nameof(cryptoMethod), (int)cryptoMethod, typeof(ECryptoMethod));
		}

		ArgumentException.ThrowIfNullOrEmpty(text);

		return cryptoMethod switch
		{
			ECryptoMethod.PlainText => text,
			ECryptoMethod.AES => DecryptAES(text),
			ECryptoMethod.EnvironmentVariable => await DecryptFromEnvironmentVariable(text).ConfigureAwait(false),
			ECryptoMethod.File => await DecryptFromFile(text).ConfigureAwait(false),
			_ => throw new InvalidOperationException($"Unsupported crypto method: {cryptoMethod}")
		};
	}

	/// <summary>
	/// Checks if the crypto method involves transformation (encryption/decryption).
	/// </summary>
	public static bool HasTransformation(ECryptoMethod cryptoMethod) =>
		cryptoMethod == ECryptoMethod.AES;

	private static byte[] GetKey()
	{
		if (_encryptionKey.Length == 0)
		{
			_encryptionKey = GetDefaultKey();
		}

		byte[] key = SHA256.HashData(_encryptionKey);
		return key;
	}

	private static byte[] GetDefaultKey()
	{
		return Encoding.UTF8.GetBytes("Vapor"); // Default key - should be changed in production
	}

	private static bool IsProductionEnvironment(Func<string, string?> getEnvironmentVariable)
	{
		var environment =
			getEnvironmentVariable("DOTNET_ENVIRONMENT") ??
			getEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
			getEnvironmentVariable("VAPOR_ENVIRONMENT");

		return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
	}

	internal static void ResetForTests()
	{
		_encryptionKey = [];
		_hasDefaultKey = true;
	}

	private static string? EncryptAES(string text)
	{
		ArgumentException.ThrowIfNullOrEmpty(text);

		try
		{
			byte[] key = GetKey();
			byte[] textData = Encoding.UTF8.GetBytes(text);
			byte[] nonce = RandomNumberGenerator.GetBytes(AesGcmNonceSize);
			byte[] ciphertext = new byte[textData.Length];
			byte[] tag = new byte[AesGcmTagSize];

			using var aesGcm = new AesGcm(key, AesGcmTagSize);
			aesGcm.Encrypt(nonce, textData, ciphertext, tag);

			byte[] result = ArrayPool<byte>.Shared.Rent(nonce.Length + tag.Length + ciphertext.Length);
			try
			{
				Array.Copy(nonce, result, nonce.Length);
				Array.Copy(tag, 0, result, nonce.Length, tag.Length);
				Array.Copy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

				return AesGcmPrefix + Convert.ToBase64String(result, 0, nonce.Length + tag.Length + ciphertext.Length);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(result);
			}
		}
		catch
		{
			// Log error in production
			return null;
		}
	}

	private static string? DecryptAES(string text)
	{
		ArgumentException.ThrowIfNullOrEmpty(text);

		try
		{
			if (text.StartsWith(AesGcmPrefix, StringComparison.Ordinal))
			{
				return DecryptAesGcm(text[AesGcmPrefix.Length..]);
			}

			return DecryptAesCbc(text);
		}
		catch
		{
			// Log error in production
			return null;
		}
	}

	private static string? DecryptAesGcm(string base64Text)
	{
		byte[] key = GetKey();
		byte[] encryptedData = Convert.FromBase64String(base64Text);

		if (encryptedData.Length < AesGcmNonceSize + AesGcmTagSize)
		{
			return null;
		}

		Span<byte> nonce = encryptedData.AsSpan(0, AesGcmNonceSize);
		Span<byte> tag = encryptedData.AsSpan(AesGcmNonceSize, AesGcmTagSize);
		Span<byte> ciphertext = encryptedData.AsSpan(AesGcmNonceSize + AesGcmTagSize);
		byte[] plaintext = new byte[ciphertext.Length];

		using var aesGcm = new AesGcm(key, AesGcmTagSize);
		aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

		return Encoding.UTF8.GetString(plaintext);
	}

	private static string? DecryptAesCbc(string base64Text)
	{
		byte[] key = GetKey();
		byte[] decryptedData = Convert.FromBase64String(base64Text);

		if (decryptedData.Length < 16)
		{
			return null; // Invalid data
		}

		using Aes aes = Aes.Create();
		aes.BlockSize = 128;
		aes.KeySize = 256;
		aes.Key = key;

		// First 16 bytes are the encrypted IV
		Span<byte> encryptedIv = decryptedData.AsSpan(0, 16);
		Span<byte> encryptedText = decryptedData.AsSpan(16);

		// Decrypt the IV
		Span<byte> iv = stackalloc byte[16];
		aes.DecryptEcb(encryptedIv, iv, PaddingMode.None);

		// Decrypt the actual data using the decrypted IV
		byte[] decryptedText = aes.DecryptCbc(encryptedText, iv);

		return Encoding.UTF8.GetString(decryptedText);
	}

	private static Task<string?> DecryptFromEnvironmentVariable(string text)
	{
		// Format: "env:VARIABLE_NAME" or just "VARIABLE_NAME"
		string varName = text.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
			? text[4..]
			: text;

		string? value = Environment.GetEnvironmentVariable(varName);
		return Task.FromResult(value?.Trim());
	}

	private static async Task<string?> DecryptFromFile(string text)
	{
		// Format: "file:/path/to/file" or just "/path/to/file"
		string filePath = text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
			? text[5..]
			: text;

		if (!File.Exists(filePath))
		{
			return null;
		}

		try
		{
			string content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
			return content.Trim();
		}
		catch
		{
			return null;
		}
	}
}
