using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Property-based tests over ProxyOptions.Parse. Well-formed input is built
/// from a controlled generator (scheme × host label × port × optional
/// credential pair over a safe alphabet) and the properties pin the
/// round-trip (every parsed field equals the constructed one, defaulting the
/// port to the scheme convention when omitted), the credential presence
/// contract of ToWebProxy, and password masking: the masked form never
/// contains a non-empty password. Totality is pinned over arbitrary strings:
/// Parse either succeeds or throws an ArgumentException naming the parameter
/// — no other exception ever escapes.
/// </summary>
public sealed class ProxyOptionsPropertyTests
{
	// Host labels over [a-z0-9-]; credentials over [a-zA-Z0-9._-] so no
	// character collides with the URL separators the parser splits on.
	private const string HostChars = "abcdefghijklmnopqrstuvwxyz0123456789-";
	private const string CredentialChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";

	private static string From(string alphabet, int length, int seedIdx)
	{
		var chars = new char[length];
		for (var i = 0; i < length; i++)
		{
			chars[i] = alphabet[(int)((uint)(seedIdx >> (i % 16)) % (uint)alphabet.Length)];
		}

		return new string(chars);
	}

	private static string GenLabel(int seedIdx) => From(HostChars, (int)((uint)seedIdx % 12 + 1), seedIdx >> 3);

	private static string GenCredential(int seedIdx) => From(CredentialChars, (int)((uint)seedIdx % 10 + 1), seedIdx >> 4);

	[Property]
	public void Parse_RoundTripsWellFormedEndpoints(int schemeIdx, PositiveInt hostSeed, PositiveInt portIdx, int credSeed)
	{
		var scheme = (ProxyScheme)((uint)schemeIdx % 3);
		var schemeText = scheme.ToString().ToLowerInvariant();
		var host = GenLabel(hostSeed.Get);
		var useCredentials = (uint)credSeed % 4 != 0; // 3/4 carry credentials
		var user = useCredentials ? GenCredential(credSeed >> 2) : null;
		var password = useCredentials ? GenCredential(credSeed >> 5) : null;
		var includePort = (uint)hostSeed.Get % 3 != 0;
		var port = (ushort)((uint)portIdx.Get % 65535 + 1);

		var url = $"{schemeText}://{(user == null ? string.Empty : $"{user}:{password}@")}{host}{(includePort ? $":{port}" : string.Empty)}";
		var options = ProxyOptions.Parse(url, "p");

		Assert.Equal(scheme, options.Scheme);
		Assert.Equal(host, options.Host);
		Assert.Equal(includePort ? port : ProxyOptions.DefaultPort(scheme), options.Port);
		Assert.Equal(user, options.UserName);
		Assert.Equal(useCredentials ? password : null, options.Password);
	}

	[Property]
	public void ToWebProxy_CarriesCredentialsExactlyWhenConfigured(int schemeIdx, PositiveInt hostSeed, PositiveInt credSeed)
	{
		var scheme = (ProxyScheme)((uint)schemeIdx % 3);
		var host = GenLabel(hostSeed.Get);
		var useCredentials = (uint)credSeed.Get % 2 == 0;
		var user = useCredentials ? GenCredential(credSeed.Get >> 2) : null;
		var password = useCredentials ? GenCredential(credSeed.Get >> 5) : null;

		var url = $"{scheme.ToString().ToLowerInvariant()}://{(user == null ? string.Empty : $"{user}:{password}@")}{host}";
		var proxy = ProxyOptions.Parse(url, "p").ToWebProxy();

		if (useCredentials)
		{
			var credentials = Assert.IsType<System.Net.NetworkCredential>(proxy.Credentials);
			Assert.Equal(user, credentials.UserName);
			Assert.Equal(password, credentials.Password);
		}
		else
		{
			Assert.Null(proxy.Credentials);
		}
	}

	[Property]
	public void ToString_NeverLeaksNonEmptyPassword(PositiveInt hostSeed, PositiveInt credSeed)
	{
		var host = GenLabel(hostSeed.Get);
		var user = GenCredential(credSeed.Get);
		var password = GenCredential(credSeed.Get >> 3);

		// The masked form necessarily shows user and host, so a password that
		// is a substring of either would make the property vacuous — skip.
		if (host.Contains(password, StringComparison.Ordinal) || user.Contains(password, StringComparison.Ordinal))
		{
			return;
		}

		var text = ProxyOptions.Parse($"socks5://{user}:{password}@{host}").ToString();

		Assert.DoesNotContain(password, text);
		Assert.Contains("<redacted>", text);
		Assert.Contains(user, text);
		Assert.Contains(host, text);
	}

	[Property]
	public void Parse_IsTotal_FailsOnlyAsArgumentNamingTheParameter(string? arbitrary)
	{
		try
		{
			_ = ProxyOptions.Parse(arbitrary, "p");
		}
		catch (ArgumentException ex)
		{
			Assert.Equal("p", ex.ParamName);
		}
	}
}
