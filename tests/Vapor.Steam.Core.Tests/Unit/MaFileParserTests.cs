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
		string json = BuildEncryptedSda("hunter2", RandomNumberGenerator.GetBytes(8), RandomNumberGenerator.GetBytes(16),
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

	/// <summary>Builds an SDA-style password-encrypted maFile exactly the way SDA writes it.</summary>
	private static string BuildEncryptedSda(string password, byte[] salt, byte[] iv, string guardJson, string steamId, string accountName)
	{
		byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 50_000, HashAlgorithmName.SHA1, 32);
		string cipherBase64;
		using (var aes = Aes.Create())
		{
			aes.Key = key;
			aes.IV = iv;
			aes.Mode = CipherMode.CBC;
			aes.Padding = PaddingMode.PKCS7;
			using var encryptor = aes.CreateEncryptor();
			byte[] plain = Encoding.UTF8.GetBytes(guardJson);
			cipherBase64 = Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
		}

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
}
