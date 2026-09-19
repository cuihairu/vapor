using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Steam;

/// <summary>
/// The logon challenge exceptions are part of the session's public failure
/// contract (BotSession catches them to route QR/password challenges); these
/// tests pin the full CA1032 constructor surface callers may rely on.
/// </summary>
public class ExceptionContractTests
{
	[Fact]
	public void SteamAuthCodeRequiredException_Ctors_PreserveMessageAndInner()
	{
		var bare = new SteamAuthCodeRequiredException();
		Assert.NotNull(bare.Message);
		Assert.Null(bare.InnerException);

		var withMessage = new SteamAuthCodeRequiredException("auth code required");
		Assert.Equal("auth code required", withMessage.Message);
		Assert.Null(withMessage.InnerException);

		var inner = new InvalidOperationException("cancelled upstream");
		var wrapped = new SteamAuthCodeRequiredException("auth code required", inner);
		Assert.Equal("auth code required", wrapped.Message);
		Assert.Same(inner, wrapped.InnerException);
	}

	[Fact]
	public void SteamTwoFactorCodeRequiredException_Ctors_PreserveMessageAndInner()
	{
		var bare = new SteamTwoFactorCodeRequiredException();
		Assert.NotNull(bare.Message);
		Assert.Null(bare.InnerException);

		var withMessage = new SteamTwoFactorCodeRequiredException("two factor required");
		Assert.Equal("two factor required", withMessage.Message);
		Assert.Null(withMessage.InnerException);

		var inner = new InvalidOperationException("cancelled upstream");
		var wrapped = new SteamTwoFactorCodeRequiredException("two factor required", inner);
		Assert.Equal("two factor required", wrapped.Message);
		Assert.Same(inner, wrapped.InnerException);
	}
}
