using SteamKit2;
using Microsoft.Extensions.Logging;
using SteamKit2.Internal;
using SteamKit2.Authentication;

namespace Vapor.Steam.Core.Steam;

/// <summary>Single-slot login state machine: logon, auth/2FA codes, QR sign-in and logon-details build.</summary>
public sealed partial class SteamClientManager
{
	public async Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		// One login in flight at a time: LoggedOnCallback carries no account
		// identity, so it is routed through the single _activeLoginAccountName
		// slot. Two overlapping LoginAsync calls would overwrite that slot and
		// complete the wrong account's TCS with the other's callback. The gate
		// spans send-to-callback so the slot has exactly one owner while a
		// logon response can arrive.
		await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		// The generation identifies this attempt to the exit path, which runs
		// outside the block that declares it.
		long generation = ++_loginGeneration;
		try
		{
			var tcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
			_activeLoginAccountName = accountName;
			_activeLoginGeneration = generation;

			_loginStates.AddOrUpdate(
				accountName,
				_ => new LoginState(accountName, password) { LoginTcs = tcs },
				(_, existing) => existing with { Password = password, LoginTcs = tcs }
			);

			if (!_steamClient.IsConnected)
			{
				throw new InvalidOperationException("Steam client is not connected");
			}

			var steamUser = _steamClient.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler not available");
			steamUser.LogOn(BuildLogOnDetails(accountName, password));

			var callback = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

			if (callback.Result == EResult.OK)
			{
				return;
			}

			if (callback.Result == EResult.AccountLogonDenied)
			{
				throw new SteamAuthCodeRequiredException("Steam auth code required (email Steam Guard)");
			}

			if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
			{
				throw new SteamTwoFactorCodeRequiredException("Steam 2FA code required (authenticator)");
			}

			throw new InvalidOperationException($"Steam login failed: {callback.Result}");
		}
		finally
		{
			// This login is leaving (success, failure or cancellation). Clear the
			// single routing slot only if this attempt still owns it — compared by
			// generation, not by account name, so a same-name re-login that grabs
			// the slot is never clobbered by an earlier attempt's exit. A response
			// arriving after the slot is dropped (or handed to a different
			// account's login) is fenced in OnLoggedOn by the slotted account's
			// known SteamId; callbacks without a usable identity (failure
			// results, first-ever logins) remain the SteamKit2 JobID-routing
			// limitation.
			if (_activeLoginGeneration == generation)
			{
				_activeLoginAccountName = null;
				_activeLoginGeneration = 0;
			}
			_loginGate.Release();
		}
	}

	public void SetAuthCode(string accountName, string code)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { AuthCode = code },
			(_, existing) => existing with { AuthCode = code }
		);
	}

	public void SetTwoFactorCode(string accountName, string code)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { TwoFactorCode = code },
			(_, existing) => existing with { TwoFactorCode = code }
		);
	}

	public async Task<QrLoginResult> BeginQrLoginAsync(string accountName, Action<string> onChallengeUrl, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		if (!_steamClient.IsConnected)
		{
			return new QrLoginResult(false, "Steam client is not connected");
		}

		try
		{
			var details = new AuthSessionDetails
			{
				DeviceFriendlyName = "Vapor",
				PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
				WebsiteID = "Client"
			};

			var session = await _steamAuthTokenProvider.Authentication
				.BeginAuthSessionViaQRAsync(details)
				.ConfigureAwait(false);

			// Steam periodically rotates the challenge URL; forward every value so
			// upstream consumers can keep the rendered QR current. The request key
			// (session.RequestID) deliberately stays inside the transport.
			session.ChallengeURLChanged += () =>
			{
				try
				{
					onChallengeUrl(session.ChallengeURL);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "QR challenge URL callback failed for {AccountName}", accountName);
				}
			};

			onChallengeUrl(session.ChallengeURL);

			var poll = await session.PollingWaitForResultAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogInformation("QR sign-in approved for {AccountName} ({PollAccountName})", accountName, poll.AccountName);
			return new QrLoginResult(true, null, poll.RefreshToken);
		}
		catch (AuthenticationException ex)
		{
			_logger.LogWarning("QR sign-in challenge ended for {AccountName}: {Result}", accountName, ex.Result);
			return new QrLoginResult(false, $"QR sign-in challenge failed: {ex.Result}");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation("QR sign-in for {AccountName} was canceled or timed out", accountName);
			return new QrLoginResult(false, "QR sign-in was not approved in time (challenge expired or timed out)");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "QR sign-in failed for {AccountName}", accountName);
			return new QrLoginResult(false, ex.Message);
		}
	}

	private SteamUser.LogOnDetails BuildLogOnDetails(string accountName, string password)
	{
		_loginStates.TryGetValue(accountName, out var state);
		var token = state?.AccessToken ?? state?.RefreshToken;

		return new SteamUser.LogOnDetails
		{
			Username = accountName,
			Password = password,
			AuthCode = state?.AuthCode,
			TwoFactorCode = state?.TwoFactorCode,
			AccessToken = token,
			ShouldRememberPassword = !string.IsNullOrWhiteSpace(token)
		};
	}

}
