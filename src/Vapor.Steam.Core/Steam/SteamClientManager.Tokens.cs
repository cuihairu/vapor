using SteamKit2;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Security;
using SteamKit2.Authentication;

namespace Vapor.Steam.Core.Steam;

/// <summary>Access-token supply: refresh and key masking.</summary>
public sealed partial class SteamClientManager
{
	public async Task<bool> RefreshAccessTokenAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		if (_credentialStore == null)
		{
			_logger.LogWarning("Cannot refresh token: no credential store configured");
			return false;
		}

		ThrowIfDisposed();

		try
		{
			var refreshToken = await _credentialStore.GetRefreshTokenAsync(accountName, cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(refreshToken))
			{
				_logger.LogDebug("No refresh token found for {AccountName}", accountName);
				return false;
			}

			if (!_loginStates.TryGetValue(accountName, out var state) || state.SteamId == null)
			{
				_logger.LogWarning("Cannot rotate refresh token for {AccountName}: SteamID is unavailable", accountName);
				return false;
			}

			var generated = await _steamAuthTokenProvider
				.GenerateAccessTokenForAppAsync(state.SteamId, refreshToken, allowRenewal: true, cancellationToken)
				.ConfigureAwait(false);

			var newRefreshToken = string.IsNullOrWhiteSpace(generated.RefreshToken) ? refreshToken : generated.RefreshToken;
			var newAccessToken = generated.AccessToken;

			await UpdateLogOnDetailsAsync(accountName, newAccessToken, newRefreshToken).ConfigureAwait(false);
			await _credentialStore.SaveRefreshTokenAsync(accountName, newRefreshToken, cancellationToken).ConfigureAwait(false);
			await _credentialStore.SaveAccessTokenAsync(
				accountName,
				new StoredAccessToken(newAccessToken, DateTimeOffset.UtcNow.AddHours(8)),
				cancellationToken).ConfigureAwait(false);

			_logger.LogInformation("Token refresh and renewal succeeded for {AccountName}", accountName);
			return true;
		}
		catch (AuthenticationException ex)
		{
			_logger.LogError(ex, "Steam authentication token renewal failed for {AccountName}: {Result}", accountName, ex.Result);
			return false;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to refresh access token for {AccountName}", accountName);
			return false;
		}
	}

	private static string MaskKey(string key)
	{
		if (key.Length == 0)
		{
			return string.Empty;
		}

		if (key.Contains('-', StringComparison.Ordinal))
		{
			var parts = key.Split('-', StringSplitOptions.None);
			if (parts.Length >= 3)
			{
				return string.Join(
					"-",
					parts.Select((part, i) => (i == 0 || i == parts.Length - 1) ? part : new string('*', part.Length))
				);
			}
		}

		// Short keys: mask everything.
		if (key.Length <= 8)
		{
			return new string('*', key.Length);
		}

		// Medium keys: preserve first/last char.
		if (key.Length <= 20)
		{
			return $"{key[0]}{new string('*', key.Length - 2)}{key[^1]}";
		}

		// Long keys: preserve first/last 10 chars.
		const int edgeLen = 10;
		return key[..edgeLen] + new string('*', key.Length - (edgeLen * 2)) + key[^edgeLen..];
	}

	private readonly HashSet<uint> _playingGames = [];
}
