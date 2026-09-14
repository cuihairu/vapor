using System.Security.Cryptography;
using System.Text.Json;

namespace Vapor.Steam.Core.Security;

/// <summary>
/// The 2FA material extracted from a SteamDesktopAuthenticator (SDA) or steamguard-cli
/// .maFile. Only the authenticator secrets are kept — session tokens are intentionally
/// not imported (the account logs in through the normal credential flow).
/// </summary>
public sealed record MaFileInfo(
	string AccountName,
	string SteamId,
	string? SharedSecret,
	string? IdentitySecret,
	string? DeviceId,
	bool HasSession
);

/// <summary>
/// Parses .maFile authenticator exports in both flavors that exist in the wild:
/// SDA (secrets nested under "Steamguard", optionally password-encrypted with
/// PBKDF2 + AES-CBC) and steamguard-cli (secrets at the top level). The parsed
/// secrets are meant to be stored with <see cref="ICredentialStore"/> so they
/// never leave the agent.
/// </summary>
public static class MaFileParser
{
	// SDA's chosen KDF parameters: PBKDF2-SHA1, 50k iterations, 32-byte key.
	// Weak by modern standards, but fixed by the SDA format we must stay
	// compatible with.
	private const int SdaIterations = 50_000;
	private const int SdaKeySize = 32;

	public static MaFileInfo Parse(string json, string? password = null)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(json);
		}
		catch (JsonException ex)
		{
			throw new InvalidDataException($"maFile is not valid JSON: {ex.Message}");
		}

		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException("maFile root must be a JSON object");
			}

			JsonElement guard = root;
			if (root.TryGetProperty("Steamguard", out var nested) && nested.ValueKind == JsonValueKind.Object)
			{
				guard = nested;
			}

			// SDA password encryption: encryption_iv/encryption_salt sit next to encrypted
			// "Steamguard"/"Session" values. Decrypt the guarded blob before parsing it.
			if (root.TryGetProperty("encryption_salt", out var saltElem) && root.TryGetProperty("encryption_iv", out var ivElem))
			{
				return ParseEncryptedSda(root, saltElem, ivElem, password);
			}

			return FromJson(root, guard);
		}
	}

	private static MaFileInfo ParseEncryptedSda(JsonElement root, JsonElement saltElem, JsonElement ivElem, string? password)
	{
		if (string.IsNullOrWhiteSpace(password))
		{
			throw new InvalidDataException("this maFile is password-encrypted (SDA format); supply its password");
		}

		string? encryptedGuard = GetString(root, "Steamguard");

		// When the file is encrypted the "Steamguard" property of the root holds the
		// ciphertext (the shell may carry non-secret metadata only).
		if (encryptedGuard is null)
		{
			throw new InvalidDataException("encrypted maFile has no encrypted Steamguard blob");
		}

		byte[] salt = Convert.FromBase64String(saltElem.GetString()!);
		byte[] iv = Convert.FromBase64String(ivElem.GetString()!);
		byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, SdaIterations, HashAlgorithmName.SHA1, SdaKeySize);

		string guardJson;
		try
		{
			using var aes = Aes.Create();
			aes.Key = key;
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using var decryptor = aes.CreateDecryptor();
			byte[] cipher = Convert.FromBase64String(encryptedGuard);
			byte[] plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
			guardJson = System.Text.Encoding.UTF8.GetString(plain);
		}
		catch (CryptographicException)
		{
			throw new InvalidDataException("failed to decrypt the maFile (wrong password or corrupted file)");
		}

		// A wrong password only sometimes trips PKCS7 padding validation; when it
		// doesn't, the plaintext is garbage that usually isn't even JSON. Route
		// that through the same wrong-password error instead of a raw JsonException.
		JsonDocument guardDoc;
		try
		{
			guardDoc = JsonDocument.Parse(guardJson);
		}
		catch (JsonException ex)
		{
			throw new InvalidDataException($"failed to decrypt the maFile (wrong password or corrupted file): {ex.Message}");
		}

		using (guardDoc)
		{
			if (guardDoc.RootElement.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidDataException("decrypted maFile payload is not a JSON object");
			}

			bool hasSession = root.TryGetProperty("Session", out var sessionElem) &&
							  sessionElem.ValueKind == JsonValueKind.String;

			// The outer shell may carry the steam id / account name; the decrypted guard
			// blob carries the secrets.
			var info = FromJson(root, guardDoc.RootElement);
			return info with { HasSession = info.HasSession || hasSession };
		}
	}

	private static MaFileInfo FromJson(JsonElement root, JsonElement guard)
	{
		string? steamId = GetString(root, "steamid") ?? GetString(guard, "steamid");
		string? accountName = GetString(root, "account_name") ?? GetString(guard, "account_name");
		string? sharedSecret = GetString(guard, "shared_secret");
		string? identitySecret = GetString(guard, "identity_secret");
		string? deviceId = GetString(guard, "device_id");
		bool hasSession = root.TryGetProperty("Session", out var sessionElem) &&
						  (sessionElem.ValueKind == JsonValueKind.Object || sessionElem.ValueKind == JsonValueKind.String);

		if (string.IsNullOrWhiteSpace(sharedSecret) && string.IsNullOrWhiteSpace(identitySecret))
		{
			throw new InvalidDataException("maFile contains no shared_secret or identity_secret — nothing to import");
		}

		if (string.IsNullOrWhiteSpace(accountName))
		{
			// Fall back to the steam id so the credential entry still has a stable key.
			accountName = steamId;
		}

		if (string.IsNullOrWhiteSpace(accountName))
		{
			throw new InvalidDataException("maFile has neither account_name nor steamid — cannot determine the account key");
		}

		return new MaFileInfo(
			AccountName: accountName.Trim(),
			SteamId: steamId?.Trim() ?? string.Empty,
			SharedSecret: sharedSecret,
			IdentitySecret: identitySecret,
			DeviceId: deviceId,
			HasSession: hasSession);
	}

	private static string? GetString(JsonElement element, string propertyName)
	{
		return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
			? prop.GetString()
			: null;
	}
}
