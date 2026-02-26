using System.Security.Cryptography;

namespace Vapor.ControlPlane;

public static class Id {
	public static string New() {
		Span<byte> bytes = stackalloc byte[16];
		RandomNumberGenerator.Fill(bytes);
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}
}


