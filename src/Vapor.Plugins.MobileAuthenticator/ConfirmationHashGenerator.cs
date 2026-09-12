using System.Security.Cryptography;
using Vapor.Steam.Core.Steam;
using System.Text;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>
/// Computes the HMAC-SHA1 confirmation hashes used by the Steam mobile confirmation
/// endpoints (/mobileconf/*). Hash input is the 8-byte big-endian time followed by the
/// UTF-8 tag bytes, keyed by the identity secret.
/// </summary>
public static class ConfirmationHashGenerator
{
	/// <summary>Tags understood by the mobileconf endpoints.</summary>
	public static readonly IReadOnlySet<string> KnownTags = new HashSet<string>(StringComparer.Ordinal)
	{
		"conf", "details", "allow", "cancel"
	};

	/// <summary>
	/// Generates the base64 confirmation hash for the given identity secret, Unix time and
	/// tag ("conf", "details", "allow" or "cancel").
	/// </summary>
	public static string Generate(string identitySecretBase64, long unixTimeSeconds, string tag)
	{
		var secret = SteamTotp.DecodeSecret(identitySecretBase64, nameof(identitySecretBase64));

		if (string.IsNullOrWhiteSpace(tag))
		{
			throw new ArgumentException("tag is required", nameof(tag));
		}

		var tagBytes = Encoding.UTF8.GetBytes(tag);
		var buffer = new byte[8 + tagBytes.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer, unixTimeSeconds);
		tagBytes.CopyTo(buffer, 8);

		return Convert.ToBase64String(HMACSHA1.HashData(secret, buffer));
	}
}
