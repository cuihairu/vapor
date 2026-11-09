using System.Reflection;
using SteamKit2;
using SteamKit2.Authentication;

namespace Vapor.Steam.Core.Steam;

internal interface ISteamAuthTokenProvider
{
	Task<SteamTokenRenewalResult> GenerateAccessTokenForAppAsync(
		SteamID steamId,
		string refreshToken,
		bool allowRenewal,
		CancellationToken cancellationToken = default);

	/// <summary>The underlying SteamKit2 authentication service for direct public-surface calls (QR sign-in).</summary>
	SteamAuthentication Authentication { get; }
}

internal sealed record SteamTokenRenewalResult(
	string AccessToken,
	string? RefreshToken
);

internal sealed class SteamAuthTokenProvider : ISteamAuthTokenProvider
{
	private readonly object _authentication;
	private readonly MethodInfo _generateAccessTokenMethod;

	public SteamAuthTokenProvider(SteamClient steamClient)
	{
		_authentication = ResolveAuthenticationCtor(typeof(SteamAuthentication)).Invoke([steamClient]);
		_generateAccessTokenMethod = ResolveGenerateAccessTokenMethod(typeof(SteamAuthentication));
	}

	// Reflection resolution against SteamKit2's package shape, extracted as a
	// Type-parameterized seam so the version-drift fail-fast arms are
	// deterministically testable (call with any type lacking the member).
	internal static ConstructorInfo ResolveAuthenticationCtor(Type type) =>
		type.GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: [typeof(SteamClient)],
			modifiers: null)
		?? throw new InvalidOperationException("SteamAuthentication constructor was not found.");

	internal static MethodInfo ResolveGenerateAccessTokenMethod(Type type) =>
		type.GetMethod(
			name: nameof(GenerateAccessTokenForAppAsync),
			bindingAttr: BindingFlags.Instance | BindingFlags.Public)
		?? throw new InvalidOperationException("GenerateAccessTokenForAppAsync method was not found.");

	/// <summary>
	/// The underlying SteamKit2 authentication service (created via its internal
	/// constructor). Public surface such as BeginAuthSessionViaQRAsync is usable
	/// directly on this instance.
	/// </summary>
	public SteamAuthentication Authentication => (SteamAuthentication)_authentication;

	public async Task<SteamTokenRenewalResult> GenerateAccessTokenForAppAsync(
		SteamID steamId,
		string refreshToken,
		bool allowRenewal,
		CancellationToken cancellationToken = default)
	{
		var result = (Task<AccessTokenGenerateResult>)_generateAccessTokenMethod.Invoke(
			_authentication,
			[steamId, refreshToken, allowRenewal])!;

		var generated = await result.ConfigureAwait(false);
		return new SteamTokenRenewalResult(generated.AccessToken, generated.RefreshToken);
	}
}

internal sealed record RedeemReceiptParseResult(
	IReadOnlyList<uint> GrantedAppIds,
	IReadOnlyList<uint> GrantedPackageIds,
	string? ReceiptDetails
);

public sealed class SteamAuthCodeRequiredException : Exception
{
	public SteamAuthCodeRequiredException() { }

	public SteamAuthCodeRequiredException(string message) : base(message) { }

	public SteamAuthCodeRequiredException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class SteamTwoFactorCodeRequiredException : Exception
{
	public SteamTwoFactorCodeRequiredException() { }

	public SteamTwoFactorCodeRequiredException(string message) : base(message) { }

	public SteamTwoFactorCodeRequiredException(string message, Exception innerException) : base(message, innerException) { }
}

