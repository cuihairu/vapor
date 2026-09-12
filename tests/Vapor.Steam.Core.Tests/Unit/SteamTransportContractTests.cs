using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Tests.Mocks;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Contract tests for the protocol-agnostic transport layer. <see cref="SteamResult"/>
/// mirrors the wire encoding on purpose — persisted task outputs store its numeric
/// value — so any accidental renumbering must fail loudly here.
/// </summary>
public sealed class SteamTransportContractTests
{
	public static TheoryData<string, string> MirroredResultNames => new()
	{
		{ "OK", nameof(SteamResult.OK) },
		{ "Fail", nameof(SteamResult.Fail) },
		{ "InvalidParam", nameof(SteamResult.InvalidParam) },
		{ "Busy", nameof(SteamResult.Busy) },
		{ "Timeout", nameof(SteamResult.Timeout) },
		{ "ServiceUnavailable", nameof(SteamResult.ServiceUnavailable) },
		{ "DuplicateRequest", nameof(SteamResult.DuplicateRequest) },
		{ "AlreadyOwned", nameof(SteamResult.AlreadyOwned) },
		{ "TryAnotherCM", nameof(SteamResult.TryAnotherCM) },
		{ "AccountLogonDenied", nameof(SteamResult.AccountLogonDenied) },
		{ "AccountLoginDeniedNeedTwoFactor", nameof(SteamResult.AccountLoginDeniedNeedTwoFactor) },
		{ "RateLimitExceeded", nameof(SteamResult.RateLimitExceeded) }
	};

	[Theory]
	[MemberData(nameof(MirroredResultNames))]
	public void SteamResult_NumericValues_MirrorWireEncoding(string wireName, string steamResultName)
	{
		int wireValue = (int)Enum.Parse<SteamKit2.EResult>(wireName);
		int mappedValue = (int)Enum.Parse<SteamResult>(steamResultName);

		Assert.Equal(wireValue, mappedValue);
	}

	[Fact]
	public void SteamClientManager_IsAssignableToTransportInterface()
	{
		Assert.True(typeof(ISteamTransport).IsAssignableFrom(typeof(SteamClientManager)));
		Assert.True(typeof(ISteamTransport).IsAssignableFrom(typeof(MockSteamClientManager)));
	}
}
