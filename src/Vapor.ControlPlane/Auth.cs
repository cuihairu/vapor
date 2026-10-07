using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Vapor.ControlPlane;

public static class Auth
{
	public static bool TryAdmin(Config cfg, StringValues authorizationHeader, out string? token)
		=> TryAdmin(cfg, authorizationHeader, DateTimeOffset.UtcNow, out token);

	/// <summary>The admin key is valid from load until <see cref="Config.AdminApiKeyExpiresAt"/>;
	/// from the expiry instant on, the bearer token is rejected like an unknown key.</summary>
	public static bool TryAdmin(Config cfg, StringValues authorizationHeader, DateTimeOffset now, out string? token)
	{
		return TryBearerToken(authorizationHeader, out token)
			&& !string.IsNullOrEmpty(cfg.AdminApiKey)
			&& string.Equals(cfg.AdminApiKey, token, StringComparison.Ordinal)
			&& (cfg.AdminApiKeyExpiresAt is null || cfg.AdminApiKeyExpiresAt.Value > now);
	}

	public static bool TryAgent(Config cfg, StringValues authorizationHeader, out string? token)
		=> TryAgent(cfg, authorizationHeader, DateTimeOffset.UtcNow, out token);

	/// <summary>Agent keys carry their own optional expiry (see <see cref="Config.ParseApiKey"/>);
	/// an expired key fails exactly like an unconfigured one.</summary>
	public static bool TryAgent(Config cfg, StringValues authorizationHeader, DateTimeOffset now, out string? token)
	{
		return TryBearerToken(authorizationHeader, out token)
			&& cfg.AgentApiKeys.Count > 0
			&& token != null
			&& cfg.AgentApiKeys.TryGetValue(token, out DateTimeOffset? expiresAt)
			&& (expiresAt is null || expiresAt.Value > now);
	}

	/// <summary>
	/// The viewer key is the read-only console credential: it authenticates
	/// only safe (read-only) HTTP methods, and only when it differs from the
	/// admin key — a demo key misconfigured equal to the admin key must fail
	/// closed instead of widening into full admin rights.
	/// </summary>
	public static bool TryViewer(Config cfg, StringValues authorizationHeader, string method)
	{
		return cfg.ViewerApiKey is { Length: > 0 }
			&& !string.Equals(cfg.ViewerApiKey, cfg.AdminApiKey, StringComparison.Ordinal)
			&& (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
			&& TryBearerToken(authorizationHeader, out string? token)
			&& string.Equals(cfg.ViewerApiKey, token, StringComparison.Ordinal);
	}

	private static bool TryBearerToken(StringValues header, out string? token)
	{
		token = null;
		string? raw = header.ToString();

		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}

		string[] parts = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			return false;
		}

		if (!parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		token = parts[1];
		return !string.IsNullOrWhiteSpace(token);
	}
}


