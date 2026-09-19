using Microsoft.Extensions.Logging;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Utilities;
using Vapor.Steam.Core.Web;

namespace Vapor.Agent;

/// <summary>
/// Executes a single <see cref="JobTask"/>: resolves credentials from the payload
/// (password / token / stored credentials), obtains or restores the bot session and
/// dispatches the action. Never throws — failures are mapped to (success=false, error).
/// </summary>
public static class AgentTaskExecutor
{
	public static async Task<(bool Success, string? Error, IReadOnlyDictionary<string, object?>? Output)> ExecuteAsync(
		JobTask task,
		ISessionManager sessionManager,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		string action = task.Action.Trim().ToLowerInvariant();
		string accountName = task.Target;

		try
		{
			var payload = task.Payload ?? new Dictionary<string, object?>();

			string password =
				PayloadReader.GetString(payload, "password") ??
				PayloadReader.GetString(payload, "pass") ??
				string.Empty;

			string? accessToken = PayloadReader.GetString(payload, "accessToken") ?? PayloadReader.GetString(payload, "access_token");
			string? refreshToken = PayloadReader.GetString(payload, "refreshToken") ?? PayloadReader.GetString(payload, "refresh_token");
			bool qrLogin = PayloadReader.GetBool(payload, "qrLogin") ?? PayloadReader.GetBool(payload, "qr_login") ?? false;

			// Per-account egress proxy (http/https/socks5, credentials inline).
			// Parsed up front so a malformed value fails the task with the
			// account's configuration named, before any session is touched.
			string? proxy = PayloadReader.GetString(payload, "proxy");
			if (proxy != null)
			{
				ProxyOptions.Parse(proxy, "proxy");
			}

			BotSession session;

			if (qrLogin)
			{
				// Explicit QR sign-in: no credentials up front; the account owner
				// approves on their phone and the session continues with minted tokens.
				logger.LogInformation("Starting QR sign-in for {AccountName}", accountName);
				var qrCredentials = new AccountCredentials(
					AccountName: accountName,
					Password: string.Empty,
					QrLogin: true,
					Proxy: proxy
				);

				session = await sessionManager.GetOrCreateSessionAsync(
					accountName,
					qrCredentials,
					cancellationToken
				);
			}
			else if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(refreshToken))
			{
				// Password provided, create new session
				var credentials = new AccountCredentials(
					AccountName: accountName,
					Password: password,
					AuthCode: PayloadReader.GetString(payload, "authCode") ?? PayloadReader.GetString(payload, "auth_code"),
					TwoFactorCode: PayloadReader.GetString(payload, "twoFactorCode") ?? PayloadReader.GetString(payload, "two_factor_code"),
					RefreshToken: refreshToken,
					AccessToken: accessToken,
					Proxy: proxy
				);

				session = await sessionManager.GetOrCreateSessionAsync(
					accountName,
					credentials,
					cancellationToken
				);
			}
			else if (!string.IsNullOrEmpty(password))
			{
				// Password only, create new session
				var credentials = new AccountCredentials(
					AccountName: accountName,
					Password: password,
					AuthCode: PayloadReader.GetString(payload, "authCode") ?? PayloadReader.GetString(payload, "auth_code"),
					TwoFactorCode: PayloadReader.GetString(payload, "twoFactorCode") ?? PayloadReader.GetString(payload, "two_factor_code"),
					Proxy: proxy
				);

				session = await sessionManager.GetOrCreateSessionAsync(
					accountName,
					credentials,
					cancellationToken
				);
			}
			else if (!string.IsNullOrEmpty(refreshToken))
			{
				// Token-only payload: restore session from the provided refresh token
				logger.LogInformation("Attempting to restore session for {AccountName} using tokens", accountName);

				var credentials = new AccountCredentials(
					AccountName: accountName,
					Password: string.Empty,
					AccessToken: accessToken,
					RefreshToken: refreshToken,
					Proxy: proxy
				);

				session = await sessionManager.GetOrCreateSessionAsync(
					accountName,
					credentials,
					cancellationToken
				);
			}
			else
			{
				// No credentials provided, try to restore from stored credentials
				logger.LogInformation("No credentials provided, attempting to restore session for {AccountName}", accountName);
				var restoredSession = await sessionManager.TryRestoreSessionAsync(accountName, cancellationToken);

				if (restoredSession == null)
				{
					return (false, "No credentials provided and no stored session found", null);
				}

				session = restoredSession;
			}

			var result = await session.ExecuteActionAsync(
				action,
				payload,
				cancellationToken
			);

			return (result.Success, result.Error, result.Output);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return (false, "canceled", null);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Execute failed for task {TaskId}", task.Id);
			return (false, ex.Message, null);
		}
	}
}
