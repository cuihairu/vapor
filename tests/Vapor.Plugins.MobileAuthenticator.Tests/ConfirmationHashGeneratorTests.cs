using Xunit;
using Vapor.Plugins.MobileAuthenticator;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class ConfirmationHashGeneratorTests
{
	private const string IdentitySecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
	private const long Time = 1610000000L;

	[Theory]
	[InlineData("conf", "piNVxlRLQas/jj4EcBgxk/yegec=")]
	[InlineData("details", "d62taK2+LtqWngoKEdTWoCXHLeQ=")]
	[InlineData("allow", "zJsKoS9LykHHgQG+K6g0aHCd45o=")]
	[InlineData("cancel", "HYzaSy0vEmGOX/Wr/mHmhyH1msk=")]
	public void Generate_MatchesKnownVectors(string tag, string expected)
	{
		Assert.Equal(expected, ConfirmationHashGenerator.Generate(IdentitySecret, Time, tag));
	}

	[Fact]
	public void Generate_ChangesWithTime()
	{
		Assert.NotEqual(
			ConfirmationHashGenerator.Generate(IdentitySecret, Time, "conf"),
			ConfirmationHashGenerator.Generate(IdentitySecret, Time + 1, "conf"));
	}

	[Fact]
	public void Generate_EmptyTag_Throws()
	{
		Assert.Throws<ArgumentException>(() => ConfirmationHashGenerator.Generate(IdentitySecret, Time, ""));
	}

	[Fact]
	public void Generate_InvalidSecret_Throws()
	{
		Assert.Throws<ArgumentException>(() => ConfirmationHashGenerator.Generate("!!bad!!", Time, "conf"));
	}

	[Fact]
	public void KnownTags_ContainsExpectedTags()
	{
		Assert.True(ConfirmationHashGenerator.KnownTags.SetEquals(new[] { "conf", "details", "allow", "cancel" }));
	}
}
