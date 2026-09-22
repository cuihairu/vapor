using System.Net;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Example coverage for ProxyOptions.Parse: the accepted forms (each scheme
/// with and without credentials and explicit port, percent-encoded
/// credentials, bracketed IPv6), the default ports (80/443/1080), the masked
/// ToString, the WebProxy address shape, and the failure contract — every
/// malformed input throws ArgumentException naming the parameter.
/// </summary>
public sealed class ProxyOptionsTests
{
	[Theory]
	[InlineData("socks5://proxy.example.com", ProxyScheme.Socks5, "proxy.example.com", 1080, null, null)]
	[InlineData("socks5://user:pass@10.0.0.1:1080", ProxyScheme.Socks5, "10.0.0.1", 1080, "user", "pass")]
	[InlineData("http://proxy.example.com:8080", ProxyScheme.Http, "proxy.example.com", 8080, null, null)]
	[InlineData("HTTP://Proxy:3128", ProxyScheme.Http, "Proxy", 3128, null, null)]
	[InlineData("https://john:s3cret@egress.example.net:8443", ProxyScheme.Https, "egress.example.net", 8443, "john", "s3cret")]
	[InlineData("socks5://us%40er:p%40ss@192.0.2.7", ProxyScheme.Socks5, "192.0.2.7", 1080, "us@er", "p@ss")]
	[InlineData("socks5://justuser@gw.example.com:9050", ProxyScheme.Socks5, "gw.example.com", 9050, "justuser", null)]
	[InlineData("http://[2001:db8::1]:8080", ProxyScheme.Http, "2001:db8::1", 8080, null, null)]
	[InlineData("socks5://[::1]", ProxyScheme.Socks5, "::1", 1080, null, null)]
	public void Parse_AcceptsWellFormedEndpoints(
		string input, ProxyScheme scheme, string host, int port, string? user, string? password)
	{
		var options = ProxyOptions.Parse(input, "cfg");

		Assert.Equal(scheme, options.Scheme);
		Assert.Equal(host, options.Host);
		Assert.Equal(port, options.Port);
		Assert.Equal(user, options.UserName);
		Assert.Equal(password, options.Password);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("proxy.example.com:1080")]
	[InlineData("ftp://proxy.example.com")]
	[InlineData("socks5://")]
	[InlineData("socks5://:1080")]
	[InlineData("socks5://:pass@host")]
	[InlineData("socks5://[::1:1080")]
	[InlineData("socks5://::1:1080")]
	[InlineData("http://[::1]junk")] // junk after a bracketed IPv6 host
	[InlineData("http://host:0")]
	[InlineData("http://host:notaport")]
	[InlineData("http://host:70000")]
	[InlineData("http://host:1080/with/path")]
	public void Parse_RejectsMalformedInput_WithParamName(string? input)
	{
		var ex = Assert.ThrowsAny<ArgumentException>(() => ProxyOptions.Parse(input, "cfg"));

		Assert.Equal("cfg", ex.ParamName);
	}

	[Fact]
	public void Parse_InvalidPercentEncoding_IsKeptVerbatim()
	{
		// Uri.UnescapeDataString leaves malformed sequences untouched on .NET
		// (it does not throw), so garbage in credentials survives verbatim —
		// anchored here so a switch to a stricter decoder is a conscious change.
		var options = ProxyOptions.Parse("socks5://us%zz:pw@10.0.0.9:1080");

		Assert.Equal("us%zz", options.UserName);
		Assert.Equal("pw", options.Password);
	}

	[Fact]
	public void ToString_MasksPassword_ButShowsUserHostPort()
	{
		var options = ProxyOptions.Parse("socks5://john:s3cret@10.0.0.9:1080");

		var text = options.ToString();

		Assert.Equal("socks5://john:<redacted>@10.0.0.9:1080", text);
		Assert.DoesNotContain("s3cret", text);
	}

	[Fact]
	public void ToString_WithoutCredentials_OmitsUserInfo()
	{
		// No explicit port: the scheme's standard port is assumed (80/443).
		Assert.Equal("http://p:80", ProxyOptions.Parse("http://p").ToString());
		Assert.Equal("https://egress:443", ProxyOptions.Parse("https://egress").ToString());
	}

	[Fact]
	public void ToWebProxy_ExposesAddressAndCredentials()
	{
		var proxied = ProxyOptions.Parse("socks5://john:s3cret@10.0.0.9:1080").ToWebProxy();

		var credentials = Assert.IsType<NetworkCredential>(proxied.Credentials);
		Assert.Equal("john", credentials.UserName);
		Assert.Equal("s3cret", credentials.Password);

		var plain = ProxyOptions.Parse("http://p:8080").ToWebProxy();
		Assert.Null(plain.Credentials);
	}

	[Fact]
	public void ToWebProxy_UnbracketsIpv6Host_RebracketsForUri()
	{
		// Parse stores the unbracketed literal; ToWebProxy must re-bracket it for URI form.
		var proxied = Assert.IsType<WebProxy>(ProxyOptions.Parse("http://[::1]:8080").ToWebProxy());

		Assert.NotNull(proxied.Address);
		Assert.Contains("[::1]", proxied.Address!.ToString(), StringComparison.Ordinal);
		Assert.Null(proxied.Credentials);
	}

	[Fact]
	public void ToString_WithoutCredentials_OmitsCredentialSegment()
	{
		Assert.Equal("http://proxy.example.com:80", ProxyOptions.Parse("http://proxy.example.com").ToString());
	}

	[Fact]
	public void DefaultPorts_FollowSchemeConventions()
	{
		Assert.Equal(80, ProxyOptions.DefaultPort(ProxyScheme.Http));
		Assert.Equal(443, ProxyOptions.DefaultPort(ProxyScheme.Https));
		Assert.Equal(1080, ProxyOptions.DefaultPort(ProxyScheme.Socks5));
	}
}
