using Xunit;
using Vapor.Plugins.MobileAuthenticator;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class SteamTotpTests
{
	// shared_secret = base64("12345678901234567890"), the classic RFC 4226 test secret.
	private const string SharedSecret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";

	[Theory]
	[InlineData(59L, "PV9M4")]
	[InlineData(1111111109L, "PY4YB")]
	[InlineData(1234567890L, "VHHQY")]
	public void Generate_MatchesKnownVectors(long time, string expected)
	{
		Assert.Equal(expected, SteamTotp.Generate(SharedSecret, time));
	}

	[Fact]
	public void Generate_SameTimeStepProducesSameCode()
	{
		// Times within the same 30-second window must produce the same code.
		Assert.Equal(
			SteamTotp.Generate(SharedSecret, 60),
			SteamTotp.Generate(SharedSecret, 89));
	}

	[Fact]
	public void Generate_DifferentTimeStepsProduceDifferentCodes()
	{
		Assert.NotEqual(
			SteamTotp.Generate(SharedSecret, 60),
			SteamTotp.Generate(SharedSecret, 90));
	}

	[Theory]
	[InlineData(0L, 30)]
	[InlineData(29L, 1)]
	[InlineData(30L, 30)]
	[InlineData(45L, 15)]
	public void SecondsRemaining_ComputesCorrectly(long time, int expected)
	{
		Assert.Equal(expected, SteamTotp.SecondsRemaining(time));
	}

	[Fact]
	public void Generate_InvalidBase64_Throws()
	{
		Assert.Throws<ArgumentException>(() => SteamTotp.Generate("!!not-base64!!", 59));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Generate_MissingSecret_Throws(string? secret)
	{
		Assert.Throws<ArgumentException>(() => SteamTotp.Generate(secret!, 59));
	}
}
