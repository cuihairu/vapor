using Vapor.Steam.Core.Utilities;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

public class SensitiveDataRedactorTests
{
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
}
