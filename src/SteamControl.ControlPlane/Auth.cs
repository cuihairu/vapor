using Microsoft.Extensions.Primitives;

namespace SteamControl.ControlPlane;

public static class Auth {
	public static bool TryAdmin(Config cfg, StringValues authorizationHeader, out string? token) {
		return TryBearerToken(authorizationHeader, out token) && !string.IsNullOrEmpty(cfg.AdminApiKey) && string.Equals(cfg.AdminApiKey, token, StringComparison.Ordinal);
	}

	public static bool TryAgent(Config cfg, StringValues authorizationHeader, out string? token) {
		return TryBearerToken(authorizationHeader, out token) && cfg.AgentApiKeys.Count > 0 && token != null && cfg.AgentApiKeys.Contains(token);
	}

	private static bool TryBearerToken(StringValues header, out string? token) {
		token = null;
		string? raw = header.ToString();

		if (string.IsNullOrWhiteSpace(raw)) {
			return false;
		}

		string[] parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2) {
			return false;
		}

		if (!parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase)) {
			return false;
		}

		token = parts[1];
		return !string.IsNullOrWhiteSpace(token);
	}
}

