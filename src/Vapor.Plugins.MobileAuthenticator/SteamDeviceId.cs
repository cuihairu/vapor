using System.Security.Cryptography;
using System.Text;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>
/// Derives the android-style device id that Steam associates with a mobile authenticator,
/// deterministically from the account's 64-bit SteamID (same algorithm as SDA/ASF).
/// </summary>
public static class SteamDeviceId
{
	public static string FromSteamId(ulong steamId64)
	{
		var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(steamId64.ToString(System.Globalization.CultureInfo.InvariantCulture))))
			.ToLowerInvariant();

		return string.Create(
			System.Globalization.CultureInfo.InvariantCulture,
			$"android:{hash[0..8]}-{hash[8..12]}-{hash[12..16]}-{hash[16..20]}-{hash[20..32]}");
	}
}
