using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

public sealed class MaFileParserTests
{
	private const string SharedSecret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";
	private const string IdentitySecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

	[Fact]
	public void Parse_SdaPlainText_NestedSteamguard()
	{
		string json = """
			{
				"steamid": "76561198000000001",
				"account_name": "alice",
				"phone_number": "+8613800000000",
				"fully_enrolled": true,
				"Steamguard": {
					"shared_secret": "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
					"identity_secret": "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
					"device_id": "android:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
					"revocation_code": "R12345",
					"account_name": "alice"
				},
				"Session": {
					"SteamLogin": "76561198000000001%7C%7Ctoken",
					"OAuthToken": "oauth"
				}
			}
			""";

		MaFileInfo info = MaFileParser.Parse(json);

		Assert.Equal("alice", info.AccountName);
		Assert.Equal("76561198000000001", info.SteamId);
		Assert.Equal(SharedSecret, info.SharedSecret);
		Assert.Equal(IdentitySecret, info.IdentitySecret);
		Assert.Equal("android:xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", info.DeviceId);
		Assert.True(info.HasSession);
	}

	[Fact]
	public void Parse_SteamguardCliFlatLayout()
	{
		string json = $$"""
			{
				"steamid": "76561198000000002",
				"account_name": "bob",
				"serial_number": "1234",
				"revocation_code": "R67890",
				"shared_secret": "{{SharedSecret}}",
				"identity_secret": "{{IdentitySecret}}",
				"uri": "otpauth://totp/Steam:bob",
				"device_id": "android:yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy",
				"fully_enrolled": true
			}
			""";

		MaFileInfo info = MaFileParser.Parse(json);

		Assert.Equal("bob", info.AccountName);
		Assert.Equal("76561198000000002", info.SteamId);
		Assert.Equal(SharedSecret, info.SharedSecret);
		Assert.Equal(IdentitySecret, info.IdentitySecret);
		Assert.False(info.HasSession);
	}

	[Fact]
	public void Parse_EncryptedSda_WithCorrectPassword_Decrypts()
	{
		string password = "hunter2";
		byte[] salt = RandomNumberGenerator.GetBytes(8);
		byte[] iv = RandomNumberGenerator.GetBytes(16);
		string guardJson = JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["shared_secret"] = SharedSecret,
			["identity_secret"] = IdentitySecret,
			["device_id"] = "android:zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz",
			["account_name"] = "carol"
		});

		string json = BuildEncryptedSda(password, salt, iv, guardJson, steamId: "76561198000000003", accountName: "carol");

		MaFileInfo info = MaFileParser.Parse(json, password);

		Assert.Equal("carol", info.AccountName);
		Assert.Equal("76561198000000003", info.SteamId);
		Assert.Equal(SharedSecret, info.SharedSecret);
		Assert.Equal(IdentitySecret, info.IdentitySecret);
	}

	[Fact]
	public void Parse_EncryptedSda_SessionStringInsideGuard_MarksSession()
	{
		// Some SDA builds carry the session token as a plain string inside the
		// encrypted guard blob (not a Session object); the string-kind arm must
		// mark the file as having a session.
		string password = "hunter2";
		byte[] salt = RandomNumberGenerator.GetBytes(8);
		byte[] iv = RandomNumberGenerator.GetBytes(16);
		string guardJson = JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["shared_secret"] = SharedSecret,
			["identity_secret"] = IdentitySecret,
			["device_id"] = "android:zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz",
			["account_name"] = "carol",
			["Session"] = "blob-session-token"
		});

		string json = BuildEncryptedSda(password, salt, iv, guardJson, steamId: "76561198000000003", accountName: "carol");

		MaFileInfo info = MaFileParser.Parse(json, password);

		Assert.True(info.HasSession);
		Assert.Equal("carol", info.AccountName);
	}

	[Fact]
	public void Parse_AccountNameOnly_NoSteamId_SteamIdDefaultsToEmpty()
	{
		// A flat maFile keyed by account name alone: the steamid null-fallback
		// arm yields an empty SteamId rather than throwing.
		string json = $$"""
			{
				"account_name": "solo",
				"shared_secret": "{{SharedSecret}}",
				"identity_secret": "{{IdentitySecret}}"
			}
			""";

		MaFileInfo info = MaFileParser.Parse(json);

		Assert.Equal("solo", info.AccountName);
		Assert.Equal(string.Empty, info.SteamId);
		Assert.False(info.HasSession);
	}

	[Fact]
	public void Parse_EncryptedSda_WithoutPassword_Throws()
	{
		string json = BuildEncryptedSda("hunter2", RandomNumberGenerator.GetBytes(8), RandomNumberGenerator.GetBytes(16),
			JsonSerializer.Serialize(new Dictionary<string, object?> { ["shared_secret"] = SharedSecret }),
			steamId: "76561198000000004", accountName: "dave");

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json));

		Assert.Contains("password-encrypted", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_EncryptedSda_WithWrongPassword_Throws()
	{
		// A wrong password usually trips PKCS7 padding validation (255/256 of the
		// keyspace), but the garbage plaintext also validates as well-padded with
		// probability ~1/256 — the run then surfaces through the JsonException arm
		// instead and the CryptographicException lines go dark (observed once in a
		// coverage round). Pick a salt at run time whose garbage plaintext is
		// provably badly padded, probed with the exact product KDF/cipher shape, so
		// the decrypt-failure arm stays deterministic.
		string json = BuildEncryptedSda(
			"hunter2", SaltWhoseWrongPasswordFailsPadding(), RandomNumberGenerator.GetBytes(16),
			JsonSerializer.Serialize(new Dictionary<string, object?> { ["shared_secret"] = SharedSecret }),
			steamId: "76561198000000005", accountName: "erin");

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json, "wrong"));

		Assert.Contains("decrypt", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Parse_NoSecrets_Throws()
	{
		string json = """{ "steamid": "76561198000000006", "account_name": "frank" }""";

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json));

		Assert.Contains("nothing to import", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_MissingAccountName_FallsBackToSteamId()
	{
		string json = $$"""{ "steamid": "76561198000000007", "shared_secret": "{{SharedSecret}}" }""";

		MaFileInfo info = MaFileParser.Parse(json);

		Assert.Equal("76561198000000007", info.AccountName);
	}

	[Fact]
	public void Parse_NoAccountNameNorSteamId_Throws()
	{
		string json = $$"""{ "shared_secret": "{{SharedSecret}}" }""";

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json));

		Assert.Contains("account key", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_NotJson_Throws()
	{
		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse("this is not json"));

		Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_RootArray_Throws()
	{
		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse("[1,2,3]"));

		Assert.Contains("root must be a JSON object", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_EncryptedSda_MissingSteamguardBlob_Throws()
	{
		// SDA shell carries encryption metadata but the encrypted Steamguard blob is absent.
		string json = """{ "encryption_salt": "AAAA", "encryption_iv": "AAAA" }""";

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json, "hunter2"));

		Assert.Contains("no encrypted Steamguard", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_EncryptedSda_DecryptedPayloadNotAnObject_Throws()
	{
		// Correct password, but the decrypted Steamguard blob is a JSON array.
		string password = "hunter2";
		byte[] salt = RandomNumberGenerator.GetBytes(8);
		byte[] iv = RandomNumberGenerator.GetBytes(16);
		byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 50_000, HashAlgorithmName.SHA1, 32);
		string cipherBase64;
		using (var aes = Aes.Create())
		{
			aes.Key = key;
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using var encryptor = aes.CreateEncryptor();
			byte[] plain = Encoding.UTF8.GetBytes("[1,2,3]");
			cipherBase64 = Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
		}

		string json = JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["encryption_iv"] = Convert.ToBase64String(iv),
			["encryption_salt"] = Convert.ToBase64String(salt),
			["Steamguard"] = cipherBase64
		});

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json, password));

		Assert.Contains("not a JSON object", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_EncryptedSda_DecryptedPayloadNotJson_ThrowsDecryptError()
	{
		// Correct password, but the decrypted Steamguard blob is not JSON at all —
		// the corrupted-file shape a wrong password only sometimes reaches (when the
		// PKCS7 padding of the garbage plaintext happens to validate). Must surface
		// as the decrypt error, never a raw JsonException.
		string password = "hunter2";
		byte[] salt = RandomNumberGenerator.GetBytes(8);
		byte[] iv = RandomNumberGenerator.GetBytes(16);
		byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 50_000, HashAlgorithmName.SHA1, 32);
		string cipherBase64;
		using (var aes = Aes.Create())
		{
			aes.Key = key;
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using var encryptor = aes.CreateEncryptor();
			byte[] plain = [0x1E, 0x1F, 0x00, 0x02, 0x03, 0x04, 0x05, 0x06];
			cipherBase64 = Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
		}

		string json = JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["encryption_iv"] = Convert.ToBase64String(iv),
			["encryption_salt"] = Convert.ToBase64String(salt),
			["Steamguard"] = cipherBase64
		});

		InvalidDataException ex = Assert.Throws<InvalidDataException>(() => MaFileParser.Parse(json, password));

		Assert.Contains("decrypt", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Builds an SDA-style password-encrypted maFile exactly the way SDA writes it.</summary>
	private static string BuildEncryptedSda(string password, byte[] salt, byte[] iv, string guardJson, string steamId, string accountName)
	{
		string cipherBase64 = EncryptSdaGuard(password, salt, iv, guardJson);

		return JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["steamid"] = steamId,
			["account_name"] = accountName,
			["encryption_iv"] = Convert.ToBase64String(iv),
			["encryption_salt"] = Convert.ToBase64String(salt),
			["Steamguard"] = cipherBase64,
			["Session"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
		});
	}

	/// <summary>Encrypts a Steamguard blob exactly the way MaFileParser decrypts it.</summary>
	private static string EncryptSdaGuard(string password, byte[] salt, byte[] iv, string guardJson)
	{
		byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 50_000, HashAlgorithmName.SHA1, 32);
		using var aes = Aes.Create();
		aes.Key = key;
		aes.IV = iv;
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.PKCS7;
		using var encryptor = aes.CreateEncryptor();
		byte[] plain = Encoding.UTF8.GetBytes(guardJson);
		return Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
	}

	/// <summary>
	/// Searches for a salt under which decrypting with the wrong password fails
	/// PKCS7 validation (the CryptographicException arm). Each candidate fails
	/// with probability 255/256, so the first candidate virtually always
	/// qualifies; the probe mirrors MaFileParser's derivation exactly.
	/// </summary>
	private static byte[] SaltWhoseWrongPasswordFailsPadding()
	{
		string guardJson = JsonSerializer.Serialize(new Dictionary<string, object?> { ["shared_secret"] = SharedSecret });
		byte[] iv = RandomNumberGenerator.GetBytes(16);
		for (int attempt = 0; attempt < 512; attempt++)
		{
			byte[] salt = RandomNumberGenerator.GetBytes(8);
			string cipher = EncryptSdaGuard("hunter2", salt, iv, guardJson);
			byte[] wrongKey = Rfc2898DeriveBytes.Pbkdf2("wrong", salt, 50_000, HashAlgorithmName.SHA1, 32);
			using var aes = Aes.Create();
			aes.Key = wrongKey;
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using var decryptor = aes.CreateDecryptor();
			try
			{
				byte[] cipherBytes = Convert.FromBase64String(cipher);
				decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
			}
			catch (CryptographicException)
			{
				return salt;
			}
		}

		throw new InvalidOperationException("no salt produced a badly-padded wrong-password decrypt in 512 tries");
	}
}
