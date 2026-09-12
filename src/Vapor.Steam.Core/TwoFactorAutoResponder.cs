using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core;

/// <summary>
/// Automatically answers Steam mobile-authenticator 2FA challenges using the account's
/// locally stored shared secret (<see cref="ICredentialStore.GetSharedSecretAsync"/>).
/// The secret never leaves the agent. Challenges that cannot be answered locally —
/// accounts without a stored secret, or email auth codes — fall through to the manual
/// SSE channel untouched.
///
/// Repeated challenges within the cooldown window (e.g. Steam rejecting the code) are
/// left to the manual channel instead of looping on the same TOTP window.
/// </summary>
public sealed class TwoFactorAutoResponder
{
	private readonly ISessionManager _sessionManager;
	private readonly ICredentialStore _credentialStore;
	private readonly SteamTimeSynchronizer _timeSynchronizer;
	private readonly ILogger _logger;
	private readonly TimeSpan _reanswerCooldown;
	private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAnswered = new(StringComparer.OrdinalIgnoreCase);
	private long _answeredCount;
	private long _skippedNoSecret;

	/// <param name="reanswerCooldown">
	/// Minimum interval between automatic answers for the same account; should be at
	/// least one TOTP step (30s) so a rejected code is never replayed.
	/// </param>
	public TwoFactorAutoResponder(
		ISessionManager sessionManager,
		ICredentialStore credentialStore,
		SteamTimeSynchronizer timeSynchronizer,
		ILogger<TwoFactorAutoResponder>? logger = null,
		TimeSpan? reanswerCooldown = null)
	{
		_sessionManager = sessionManager;
		_credentialStore = credentialStore;
		_timeSynchronizer = timeSynchronizer;
		_logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TwoFactorAutoResponder>.Instance;
		_reanswerCooldown = reanswerCooldown ?? TimeSpan.FromSeconds(60);
	}

	/// <summary>Challenges answered automatically since startup.</summary>
	public long AnsweredCount => Interlocked.Read(ref _answeredCount);

	/// <summary>Challenges skipped because the account has no stored shared secret.</summary>
	public long SkippedNoSecretCount => Interlocked.Read(ref _skippedNoSecret);

	/// <summary>
	/// Consumes the session manager's event stream and answers 2FA challenges. Runs
	/// until cancelled — wire it to a long-lived token from the host.
	/// </summary>
	public async Task RunAsync(CancellationToken cancellationToken)
	{
		await foreach (SessionEvent evt in _sessionManager.SubscribeAllEvents(cancellationToken).ConfigureAwait(false))
		{
			if (evt.Type != SessionEventType.TwoFactorCodeNeeded)
			{
				continue;
			}

			await TryAnswerAsync(evt.AccountName, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Attempts to answer a pending 2FA challenge for the account; false when it cannot.</summary>
	public async Task<bool> TryAnswerAsync(string accountName, CancellationToken cancellationToken = default)
	{
		string? sharedSecret = await _credentialStore.GetSharedSecretAsync(accountName, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(sharedSecret))
		{
			Interlocked.Increment(ref _skippedNoSecret);
			_logger.LogDebug(
				"No local shared secret for {AccountName}; leaving the 2FA challenge to the manual channel",
				accountName);
			return false;
		}

		// Cooldown gate after the secret check so accounts without a secret never burn one.
		DateTimeOffset now = DateTimeOffset.UtcNow;
		if (_lastAnswered.TryGetValue(accountName, out DateTimeOffset lastAnswered) &&
			now - lastAnswered < _reanswerCooldown)
		{
			_logger.LogInformation(
				"2FA challenge for {AccountName} answered {SecondsAgo}s ago (within cooldown); deferring to the manual channel",
				accountName, (int)(now - lastAnswered).TotalSeconds);
			return false;
		}

		BotSession? session = await _sessionManager.GetSessionAsync(accountName, cancellationToken).ConfigureAwait(false);
		if (session is null)
		{
			_logger.LogWarning("2FA challenge for {AccountName} but no active session exists", accountName);
			return false;
		}

		_lastAnswered[accountName] = now;
		string code = SteamTotp.Generate(sharedSecret, _timeSynchronizer.GetCurrentSteamTime());
		session.Provide2FACode(code);
		Interlocked.Increment(ref _answeredCount);

		_logger.LogInformation(
			"Auto-answered 2FA challenge for {AccountName} from the local shared secret (steam time offset {OffsetSeconds}s)",
			accountName, _timeSynchronizer.OffsetSeconds);
		return true;
	}
}
