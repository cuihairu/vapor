using SteamKit2;
using SteamKit2.GC;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Security;
using CMsgClientWelcome = SteamKit2.GC.CSGO.Internal.CMsgClientWelcome;
using CMsgGCItemCustomizationNotification = SteamKit2.GC.CSGO.Internal.CMsgGCItemCustomizationNotification;

namespace Vapor.Steam.Core.Steam;

/// <summary>SteamKit2 callback pump and the connected/disconnected/logon/logoff/GC handlers.</summary>
public sealed partial class SteamClientManager
{
	public void RunCallbacks()
	{
		if (_disposed)
		{
			return;
		}

		_callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
	}

	private void SubscribeCallbacks()
	{
		_callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
		_callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
		_callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
		_callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
		_callbackManager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage);
	}

	private void OnConnected(SteamClient.ConnectedCallback callback)
	{
		_logger.LogInformation("Steam client connected");
		_connectedTcs.TrySetResult(true);
	}

	private void OnDisconnected(SteamClient.DisconnectedCallback callback)
	{
		_logger.LogInformation("Steam client disconnected: {UserInitiated}", callback.UserInitiated);
		_cs2GcSessionLive = false;
		// The playing set is connection-scoped: nothing is being played on the new
		// connection yet. Clearing it lets a post-reconnect PlayGames(desired)
		// re-send instead of being skipped by the SetEquals idempotence check —
		// otherwise the desired idle/farm state silently never reaches Steam.
		lock (_playingGamesLock)
		{
			_playingGames.Clear();
		}
		lock (_connectLock)
		{
			foreach (var pending in _pendingDisconnects)
			{
				pending.TrySetResult(true);
			}
			_pendingDisconnects.Clear();
		}
		_connectedTcs.TrySetResult(false);
	}

	/// <summary>
	/// Routes decoded CS2 GC replies into the wait registry. Everything here
	/// rides the excluded SteamKit2 wiring; wrong-shape payloads are dropped
	/// with a debug line rather than failing the callback pump.
	/// </summary>
	private void OnGcMessage(SteamGameCoordinator.MessageCallback callback)
	{
		if (callback.AppID != Cs2GcProtocol.AppId)
		{
			return;
		}

		try
		{
			switch (callback.EMsg)
			{
				case Cs2GcProtocol.ClientWelcomeMsg:
					_gcWaits.Complete(callback.EMsg, new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message).Body);
					break;
				case Cs2GcProtocol.ItemCustomizationNotificationMsg:
					_gcWaits.Complete(callback.EMsg, new ClientGCMsgProtobuf<CMsgGCItemCustomizationNotification>(callback.Message).Body);
					break;
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Failed to decode CS2 GC message {EMsg}", callback.EMsg);
		}
	}

	private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
	{
		var accountName = _activeLoginAccountName;
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return;
		}

		if (_loginStates.TryGetValue(accountName, out var state))
		{
			state.LoginTcs?.TrySetResult(callback);
		}

		if (callback.ClientSteamID != null)
		{
			_ = UpdateSteamIdAsync(accountName, callback.ClientSteamID);
		}

		// Extract and store tokens from successful login
		if (callback.Result == EResult.OK && _credentialStore != null)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					if (!_loginStates.TryGetValue(accountName, out var currentState))
					{
						return;
					}

					if (!string.IsNullOrWhiteSpace(currentState.RefreshToken))
					{
						await _credentialStore.SaveRefreshTokenAsync(accountName, currentState.RefreshToken).ConfigureAwait(false);
						_logger.LogInformation("Refresh token saved for {AccountName}", accountName);
					}

					if (!string.IsNullOrWhiteSpace(currentState.AccessToken))
					{
						var accessToken = new StoredAccessToken(
							currentState.AccessToken,
							DateTimeOffset.UtcNow.AddHours(8)
						);
						await _credentialStore.SaveAccessTokenAsync(accountName, accessToken).ConfigureAwait(false);
						_logger.LogInformation("Access token saved for {AccountName}", accountName);
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to store tokens for {AccountName}", accountName);
				}
			});
		}
	}

	private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
	{
		_logger.LogInformation("Logged off: {Result}", callback.Result);
	}

}
