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
}
