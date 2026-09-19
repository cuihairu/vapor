using System.Globalization;
using System.Net;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// The proxy transport a per-account proxy speaks. Socks5 resolves the target
/// hostname at the proxy (remote DNS), so the local resolver never observes the
/// Steam endpoints the account touches.
/// </summary>
public enum ProxyScheme
{
	Http,
	Https,
	Socks5
}

/// <summary>
/// A parsed per-account proxy configuration. <see cref="Parse"/> accepts
/// <c>scheme://[user:pass@]host[:port]</c> — scheme is one of http/https/socks5
/// (case-insensitive), credentials are optional and percent-decoded, and IPv6
/// hosts must be bracketed. A missing port falls back to the scheme's standard
/// port (80/443/1080). <see cref="ToString"/> always masks the password;
/// the original string is never recoverable from the parsed form.
/// </summary>
public sealed record ProxyOptions
{
	private static readonly Dictionary<string, ProxyScheme> Schemes = new(StringComparer.OrdinalIgnoreCase)
	{
		["http"] = ProxyScheme.Http,
		["https"] = ProxyScheme.Https,
		["socks5"] = ProxyScheme.Socks5,
	};

	private ProxyOptions(ProxyScheme scheme, string host, ushort port, string? userName, string? password)
	{
		Scheme = scheme;
		Host = host;
		Port = port;
		UserName = userName;
		Password = password;
	}

	public ProxyScheme Scheme { get; }

	/// <summary>The proxy host: brackets stripped for IPv6 literals.</summary>
	public string Host { get; }

	public ushort Port { get; }

	public string? UserName { get; }

	public string? Password { get; }

	/// <summary>The port assumed when the configuration omits one.</summary>
	public static ushort DefaultPort(ProxyScheme scheme) => scheme switch
	{
		ProxyScheme.Http => 80,
		ProxyScheme.Https => 443,
		_ => 1080,
	};

	/// <summary>
	/// Parses a proxy endpoint string. Every failure — null, blank, missing or
	/// unknown scheme, bad credentials encoding, empty host, unbracketed IPv6,
	/// zero port — throws with <paramref name="paramName"/> so the caller's
	/// configuration field is named in the error.
	/// </summary>
	public static ProxyOptions Parse(string? value, string paramName = "proxy")
	{
		if (value == null)
		{
			throw new ArgumentNullException(paramName, "Proxy configuration must not be null.");
		}

		var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
		if (schemeEnd < 0)
		{
			throw Invalid(value, paramName, "expected scheme://host:port (http, https or socks5)");
		}

		if (!Schemes.TryGetValue(value[..schemeEnd], out var scheme))
		{
			throw Invalid(value, paramName, "scheme must be http, https or socks5");
		}

		var rest = value[(schemeEnd + 3)..];
		if (rest.Length == 0)
		{
			throw Invalid(value, paramName, "host is missing");
		}

		string? userName = null;
		string? password = null;

		// Credentials, if present, are everything before the last '@' — the
		// host part can never contain an unencoded '@'. A missing ':' means a
		// user name without a password.
		var at = rest.LastIndexOf('@');
		if (at >= 0)
		{
			var userInfo = rest[..at];
			rest = rest[(at + 1)..];

			var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
			userName = colon < 0 ? userInfo : userInfo[..colon];
			password = colon < 0 ? null : userInfo[(colon + 1)..];

			if (userName.Length == 0)
			{
				throw Invalid(value, paramName, "user name is missing before ':' in credentials");
			}

			userName = Decode(userInfo, userName, value, paramName);
			if (password != null)
			{
				password = Decode(userInfo, password, value, paramName);
			}
		}

		var (host, port, hasPort) = SplitHostPort(rest, value, paramName);

		if (host.Length == 0)
		{
			throw Invalid(value, paramName, "host is missing");
		}

		return new ProxyOptions(scheme, host, (ushort)(hasPort ? port : DefaultPort(scheme)), userName, password);
	}

	/// <summary>
	/// Builds the <see cref="IWebProxy"/> for HttpClient stacks (Steam web APIs
	/// and the SteamKit CM WebSocket invoker alike). socks5 targets are resolved
	/// by the proxy itself.
	/// </summary>
	public IWebProxy ToWebProxy()
	{
		// IPv6 literals must be re-bracketed for URI form; host names and IPv4
		// are used as-is.
		var hostPart = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
		var address = new Uri($"{Scheme switch { ProxyScheme.Https => "https", ProxyScheme.Socks5 => "socks5", _ => "http" }}://{hostPart}:{Port}");

		var proxy = new WebProxy(address)
		{
			BypassProxyOnLocal = false,
		};

		if (UserName != null)
		{
			proxy.Credentials = new NetworkCredential(UserName, Password);
		}

		return proxy;
	}

	/// <summary>The endpoint with the password always masked.</summary>
	public override string ToString()
	{
		var hostPart = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
		var credentials = UserName == null ? string.Empty : $"{UserName}:<redacted>@";
		return $"{Scheme.ToString().ToLowerInvariant()}://{credentials}{hostPart}:{Port}";
	}

	private static (string Host, int Port, bool HasPort) SplitHostPort(string input, string value, string paramName)
	{
		string host;
		string? portText;

		if (input.StartsWith('['))
		{
			// Bracketed IPv6 literal: [::1]:1080 — the closing bracket is
			// mandatory and must be followed by nothing or ":port".
			var close = input.IndexOf(']', StringComparison.Ordinal);
			if (close < 0)
			{
				throw Invalid(value, paramName, "IPv6 host must be closed with ']'");
			}

			host = input[1..close];
			var remainder = input[(close + 1)..];
			if (remainder.Length == 0)
			{
				return (host, 0, false);
			}

			if (!remainder.StartsWith(':'))
			{
				throw Invalid(value, paramName, "expected ':port' after bracketed IPv6 host");
			}

			portText = remainder[1..];
		}
		else
		{
			// A bare ':' before the port split means an unbracketed IPv6
			// literal, which is ambiguous — brackets are required.
			var colon = input.LastIndexOf(':');
			if (colon < 0)
			{
				return (input, 0, false);
			}

			if (input.IndexOf(':', StringComparison.Ordinal) != colon)
			{
				throw Invalid(value, paramName, "IPv6 host must be bracketed, e.g. [::1]:1080");
			}

			host = input[..colon];
			portText = input[(colon + 1)..];
		}

		if (!ushort.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port == 0)
		{
			throw Invalid(value, paramName, $"port '{portText}' is not a number between 1 and 65535");
		}

		return (host, port, true);
	}

	private static string Decode(string userInfo, string component, string value, string paramName)
	{
		if (!component.Contains('%', StringComparison.Ordinal))
		{
			return component;
		}

		try
		{
			return Uri.UnescapeDataString(component);
		}
		catch (UriFormatException)
		{
			throw Invalid(value, paramName, $"invalid percent-encoding in credentials '{userInfo}'");
		}
	}

	private static ArgumentException Invalid(string value, string paramName, string reason) =>
		new($"Invalid proxy configuration '{value}': {reason}.", paramName);
}
