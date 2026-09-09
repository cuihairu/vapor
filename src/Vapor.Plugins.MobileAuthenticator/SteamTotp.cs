using System.Security.Cryptography;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>
/// Generates Steam-style mobile authenticator TOTP codes: 5 characters from the Steam
/// alphabet, HMAC-SHA1, 30-second time step.
/// </summary>
public static class SteamTotp
{
	public const int CodeLength = 5;
	public const int TimeStepSeconds = 30;

	private const string SteamAlphabet = "23456789BCDFGHJKMNPQRTVWXY";

	/// <summary>
	/// Generates the Steam TOTP code for a base64-encoded shared secret at the given Unix
	/// time (in seconds).
	/// </summary>
	public static string Generate(string sharedSecretBase64, long unixTimeSeconds)
	{
		var secret = DecodeSecret(sharedSecretBase64, nameof(sharedSecretBase64));

		Span<byte> timeBytes = stackalloc byte[8];
		System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(timeBytes, unixTimeSeconds / TimeStepSeconds);

		Span<byte> hash = stackalloc byte[20];
		HMACSHA1.HashData(secret, timeBytes, hash);

		var offset = hash[19] & 0x0F;
		var codepoint =
			((hash[offset] & 0x7F) << 24) |
			(hash[offset + 1] << 16) |
			(hash[offset + 2] << 8) |
			hash[offset + 3];

		var chars = new char[CodeLength];
		for (var i = 0; i < CodeLength; i++)
		{
			chars[i] = SteamAlphabet[codepoint % SteamAlphabet.Length];
			codepoint /= SteamAlphabet.Length;
		}

		return new string(chars);
	}

	/// <summary>Seconds remaining until the current code rotates.</summary>
	public static int SecondsRemaining(long unixTimeSeconds)
	{
		var remainder = (int)(unixTimeSeconds % TimeStepSeconds);
		if (remainder < 0)
		{
			remainder += TimeStepSeconds;
		}

		return TimeStepSeconds - remainder;
	}

	internal static byte[] DecodeSecret(string? secretBase64, string paramName)
	{
		if (string.IsNullOrWhiteSpace(secretBase64))
		{
			throw new ArgumentException($"{paramName} is required", paramName);
		}

		try
		{
			var bytes = Convert.FromBase64String(secretBase64.Trim());
			if (bytes.Length == 0)
			{
				throw new ArgumentException($"{paramName} must not decode to an empty secret", paramName);
			}

			return bytes;
		}
		catch (FormatException ex)
		{
			throw new ArgumentException($"{paramName} must be valid base64", paramName, ex);
		}
	}
}
