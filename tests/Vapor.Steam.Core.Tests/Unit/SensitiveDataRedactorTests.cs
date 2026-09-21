using System.Text.Json;
using Vapor.Steam.Core.Utilities;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

public sealed class SensitiveDataRedactorTests
{
	[Fact]
	public void Redact_WithJsonPayload_RedactsSensitiveFields()
	{
		const string input = """
			{"accountName":"acct","password":"hunter2","accessToken":"abc123","refresh_token":"xyz789","nested":{"authCode":"123456","twoFactorCode":"654321","key":"AAAAA-BBBBB-CCCCC"}}
			""";

		var redacted = SensitiveDataRedactor.Redact(input);
		using var document = JsonDocument.Parse(redacted);
		var root = document.RootElement;

		Assert.Equal("acct", root.GetProperty("accountName").GetString());
		Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("xyz789", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("123456", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("654321", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("AAAAA-BBBBB-CCCCC", redacted, StringComparison.Ordinal);
		Assert.Equal("<redacted>", root.GetProperty("password").GetString());
		Assert.Equal("<redacted>", root.GetProperty("accessToken").GetString());
		Assert.Equal("<redacted>", root.GetProperty("refresh_token").GetString());
		Assert.Equal("<redacted>", root.GetProperty("nested").GetProperty("authCode").GetString());
		Assert.Equal("<redacted>", root.GetProperty("nested").GetProperty("twoFactorCode").GetString());
		Assert.Equal("<redacted>", root.GetProperty("nested").GetProperty("key").GetString());
	}

	[Fact]
	public void Redact_WithArraysAndNonStringScalars_PreservesStructureRedactsOnlySensitiveKeys()
	{
		const string input = """
			{"ids":[1,2,3],"flags":[true,false,null],"apps":[{"name":"steam","price":9.99},{"password":"hunter2","port":1080}],"count":7}
			""";

		var redacted = SensitiveDataRedactor.Redact(input);
		using var document = JsonDocument.Parse(redacted);
		var root = document.RootElement;

		// Arrays survive with non-string scalars written through untouched.
		Assert.Equal(3, root.GetProperty("ids").GetArrayLength());
		Assert.Equal(1, root.GetProperty("ids")[0].GetInt32());
		Assert.True(root.GetProperty("flags")[0].GetBoolean());
		Assert.Equal(JsonValueKind.Null, root.GetProperty("flags")[2].ValueKind);
		Assert.Equal(7, root.GetProperty("count").GetInt32());

		// Objects inside arrays still redact sensitive keys; numbers pass through.
		Assert.Equal(9.99m, root.GetProperty("apps")[0].GetProperty("price").GetDecimal());
		Assert.Equal("<redacted>", root.GetProperty("apps")[1].GetProperty("password").GetString());
		Assert.Equal(1080, root.GetProperty("apps")[1].GetProperty("port").GetInt32());
		Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
	}

	[Fact]
	public void Redact_WithKeyValueText_RedactsSensitiveFields()
	{
		const string input = "password=hunter2 access_token=abc123 refreshToken=xyz789 authorization=BearerToken code=123456";

		var redacted = SensitiveDataRedactor.Redact(input);

		Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("xyz789", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("BearerToken", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("123456", redacted, StringComparison.Ordinal);
		Assert.Contains("password=<redacted>", redacted, StringComparison.Ordinal);
		Assert.Contains("access_token=<redacted>", redacted, StringComparison.Ordinal);
		Assert.Contains("refreshToken=<redacted>", redacted, StringComparison.Ordinal);
	}

	[Fact]
	public void Redact_WithNonSensitiveText_PreservesValue()
	{
		const string input = "result=ok accountName=acct-1 state=Connected";

		var redacted = SensitiveDataRedactor.Redact(input);

		Assert.Equal(input, redacted);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Redact_WithNullOrEmpty_ReturnsEmpty(string? value)
	{
		Assert.Equal(string.Empty, SensitiveDataRedactor.Redact(value));
	}

	[Fact]
	public void SanitizeLogValue_StripsControlCharacters()
	{
		// A caller-controlled value must not be able to forge log lines via
		// embedded line breaks or other control characters.
		string sanitized = SensitiveDataRedactor.SanitizeLogValue("alice\nERROR injected\r\nfake=entry\ttail");

		Assert.DoesNotContain('\n', sanitized);
		Assert.DoesNotContain('\r', sanitized);
		Assert.DoesNotContain('\t', sanitized);
		Assert.Equal("aliceERROR injectedfake=entrytail", sanitized);
	}

	[Theory]
	[InlineData("alice")]
	[InlineData("job-12345")]
	[InlineData("portal_2")]
	public void SanitizeLogValue_PassesWellFormedIdentifiersThrough(string value)
	{
		Assert.Equal(value, SensitiveDataRedactor.SanitizeLogValue(value));
	}

	[Fact]
	public void SanitizeLogValue_EmptyOrNull_ReturnsEmpty()
	{
		Assert.Equal(string.Empty, SensitiveDataRedactor.SanitizeLogValue(null));
		Assert.Equal(string.Empty, SensitiveDataRedactor.SanitizeLogValue(string.Empty));
	}

	[Theory]
	[InlineData("socks5://john:s3cret@10.0.0.9:1080", "socks5://<redacted>@10.0.0.9:1080")]
	[InlineData("http://proxy.example.com:8080", "http://proxy.example.com:8080")]
	[InlineData("see https://alice:p%40ss@gw.example.net:8443 now", "see https://<redacted>@gw.example.net:8443 now")]
	public void Redact_ProxyUriCredentials_AreMasked_EndpointStaysLegible(string input, string expected)
	{
		Assert.Equal(expected, SensitiveDataRedactor.Redact(input));
	}

	[Fact]
	public void Redact_ProxyKeyValue_IsFullyRedacted()
	{
		const string input = """{"proxy":"socks5://john:s3cret@10.0.0.9:1080"}""";

		var redacted = SensitiveDataRedactor.Redact(input);

		Assert.DoesNotContain("john", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("s3cret", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("10.0.0.9", redacted, StringComparison.Ordinal);
	}

	[Fact]
	public void Redact_ProxyUriNestedInJsonString_MasksCredentialsOnly()
	{
		const string input = """{"note":"routing via socks5://bob:hunter2@egress:9050 tonight"}""";

		var redacted = SensitiveDataRedactor.Redact(input);

		Assert.DoesNotContain("bob", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
		Assert.Contains("egress:9050", redacted, StringComparison.Ordinal);
	}
}
