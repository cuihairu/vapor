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
	/// AES-256-CBC encryption with encrypted IV. Recommended for production.
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

	private static byte[] _encryptionKey = [];
	private static bool _hasDefaultKey = true;

	/// <summary>
	/// Gets whether the default encryption key is being used.
	/// </summary>
	public static bool HasDefaultKey => _hasDefaultKey;

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

		_hasDefaultKey = !encryptionKey.SequenceEqual(GetDefaultKey());
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

	private static string? EncryptAES(string text)
	{
		ArgumentException.ThrowIfNullOrEmpty(text);

		try
		{
			byte[] key = GetKey();
			byte[] textData = Encoding.UTF8.GetBytes(text);

			// Generate random IV
			Span<byte> iv = stackalloc byte[16];
			RandomNumberGenerator.Fill(iv);

			using Aes aes = Aes.Create();
			aes.BlockSize = 128;
			aes.KeySize = 256;
			aes.Key = key;

			// Encrypt the IV itself using ECB (no padding)
			byte[] encryptedIv = aes.EncryptEcb(iv, PaddingMode.None);

			// Encrypt the actual data using CBC with the random IV
			byte[] encryptedText = aes.EncryptCbc(textData, iv);

			// Combine encrypted IV + encrypted text
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
			byte[] key = GetKey();
			byte[] decryptedData = Convert.FromBase64String(text);

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
		catch
		{
			// Log error in production
			return null;
		}
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
