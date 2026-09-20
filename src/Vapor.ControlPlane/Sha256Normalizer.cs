namespace Vapor.ControlPlane;

/// <summary>
/// Normalizes the checksum of a plugin install instruction: trimmed, exactly 64
/// ASCII hex digits, lowercased. Anything else is rejected as <c>null</c> — the
/// installer treats a null checksum as a missing one and refuses to install.
/// </summary>
internal static class Sha256Normalizer
{
	public static string? Normalize(string? sha256)
	{
		if (sha256 is null)
		{
			return null;
		}

		string trimmed = sha256.Trim();
		if (trimmed.Length != 64 || !trimmed.All(char.IsAsciiHexDigit))
		{
			return null;
		}

		return trimmed.ToLowerInvariant();
	}
}
