using SteamKit2;
using Microsoft.Extensions.Logging;
using SteamKit2.GC;
using CMsgClientHello = SteamKit2.GC.CSGO.Internal.CMsgClientHello;
using SteamKit2.Internal;
using CMsgGCItemCustomizationNotification = SteamKit2.GC.CSGO.Internal.CMsgGCItemCustomizationNotification;
using CMsgOpenCrate = SteamKit2.GC.CSGO.Internal.CMsgOpenCrate;
using CMsgClientGamesPlayed = SteamKit2.Internal.CMsgClientGamesPlayed;

namespace Vapor.Steam.Core.Steam;

/// <summary>Games surface: play-games tracking, CS2 GC session and open-crate.</summary>
public sealed partial class SteamClientManager
{
	private readonly object _playingGamesLock = new();

	public void PlayGames(HashSet<uint> appIds)
	{
		ThrowIfDisposed();

		lock (_playingGamesLock)
		{
			// Check if the games being played are the same (idempotent)
			if (_playingGames.SetEquals(appIds))
			{
				_logger.LogDebug("PlayGames: already playing requested games, skipping");
				return;
			}

			_playingGames.Clear();
			_playingGames.UnionWith(appIds);
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("PlayGames: Steam client not connected, games will be played on next connection");
			return;
		}

		var gamesPlayed = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

		if (appIds.Count > 0)
		{
			foreach (var appId in appIds)
			{
				gamesPlayed.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
				{
					game_id = new GameID { AppID = (ushort)appId }.ToUInt64(),
					game_extra_info = string.Empty
				});
			}

			_logger.LogInformation("Playing {Count} games: {Games}", appIds.Count, string.Join(", ", appIds.OrderBy(id => id)));
		}
		else
		{
			_logger.LogInformation("Stopping all games");
		}

		_steamClient.Send(gamesPlayed);
	}

	public IReadOnlySet<uint> GetPlayingGames()
	{
		lock (_playingGamesLock)
		{
			return _playingGames.ToHashSet();
		}
	}

	/// <inheritdoc/>
	public async Task<Cs2GcSessionResult> EnsureCs2SessionAsync(CancellationToken cancellationToken = default)
	{
		if (!await IsConnectedAsync().ConfigureAwait(false))
		{
			return new Cs2GcSessionResult(false, "steam client is not connected");
		}

		if (_cs2GcSessionLive)
		{
			return new Cs2GcSessionResult(true, "cs2 game coordinator session already live");
		}

		var gameCoordinator = _steamClient.GetHandler<SteamGameCoordinator>();
		if (gameCoordinator == null)
		{
			return new Cs2GcSessionResult(false, "game coordinator handler not available");
		}

		PlayGames([Cs2GcProtocol.AppId]);

		// Steam needs a beat to spin the GC route up after the play state
		// changes; hello resends cover the rest (the GC drops first hellos).
		await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

		(Task<object?> welcome, IDisposable registration) = _gcWaits.Register(Cs2GcProtocol.ClientWelcomeMsg);
		using (registration)
		{
			var hello = new ClientGCMsgProtobuf<CMsgClientHello>(Cs2GcProtocol.ClientHelloMsg)
			{
				Body = { version = Cs2GcProtocol.ClientHelloVersion }
			};

			for (int attempt = 0; attempt < 5 && !welcome.IsCompleted; attempt++)
			{
				gameCoordinator.Send(hello, Cs2GcProtocol.AppId);
				try
				{
					_ = await welcome.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
				}
				catch (TimeoutException)
				{
					// Resend below unless this was the last attempt.
				}
			}

			if (!welcome.IsCompleted)
			{
				return new Cs2GcSessionResult(false, "game coordinator did not welcome the client hello");
			}

			_cs2GcSessionLive = true;
			return new Cs2GcSessionResult(true, "cs2 game coordinator session established");
		}
	}

	/// <inheritdoc/>
	public async Task<Cs2OpenCrateResult?> OpenCrateAsync(ulong crateAssetId, ulong keyAssetId, CancellationToken cancellationToken = default)
	{
		if (!_cs2GcSessionLive)
		{
			return null;
		}

		var gameCoordinator = _steamClient.GetHandler<SteamGameCoordinator>();
		if (gameCoordinator == null)
		{
			return null;
		}

		(Task<object?> reply, IDisposable registration) = _gcWaits.Register(
			Cs2GcProtocol.ItemCustomizationNotificationMsg,
			body => body is CMsgGCItemCustomizationNotification notification && notification.request == Cs2GcProtocol.UnlockCrateRequest);
		using (registration)
		{
			var openCrate = new ClientGCMsgProtobuf<CMsgOpenCrate>(Cs2GcProtocol.OpenCrateMsg)
			{
				Body = { tool_item_id = keyAssetId, subject_item_id = crateAssetId }
			};

			gameCoordinator.Send(openCrate, Cs2GcProtocol.AppId);

			try
			{
				object? body = await reply.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
				var notification = (CMsgGCItemCustomizationNotification)body!;
				return new Cs2OpenCrateResult(notification.item_id);
			}
			catch (TimeoutException)
			{
				return null;
			}
		}
	}

	internal static RedeemReceiptParseResult? ParseRedeemReceipt(CStore_RegisterCDKey_Response? responseBody)
	{
		var receipt = responseBody?.purchase_receipt_info;
		if (receipt == null)
		{
			return null;
		}

		var grantedAppIds = receipt.line_items
			.Select(lineItem => lineItem.appid)
			.Where(appId => appId > 0)
			.Distinct()
			.ToArray();

		var grantedPackageIds = receipt.line_items
			.Select(lineItem => lineItem.packageid)
			.Append(receipt.packageid)
			.Where(packageId => packageId > 0)
			.Distinct()
			.ToArray();

		var detailParts = new List<string>();
		if (receipt.transactionid > 0)
		{
			detailParts.Add($"TransactionId={receipt.transactionid}");
		}

		if (receipt.packageid > 0)
		{
			detailParts.Add($"PackageId={receipt.packageid}");
		}

		if (!string.IsNullOrWhiteSpace(receipt.country_code))
		{
			detailParts.Add($"Country={receipt.country_code}");
		}

		if (receipt.line_items.Count > 0)
		{
			var lineItemSummary = string.Join(
				", ",
				receipt.line_items.Select(lineItem =>
				{
					var description = string.IsNullOrWhiteSpace(lineItem.line_item_description)
						? "N/A"
						: lineItem.line_item_description;
					return $"AppId={lineItem.appid}, PackageId={lineItem.packageid}, Description={description}";
				}));
			detailParts.Add($"LineItems=[{lineItemSummary}]");
		}

		if (!string.IsNullOrWhiteSpace(receipt.error_headline))
		{
			detailParts.Add($"ErrorHeadline={receipt.error_headline}");
		}

		if (!string.IsNullOrWhiteSpace(receipt.error_string))
		{
			detailParts.Add($"Error={receipt.error_string}");
		}

		return new RedeemReceiptParseResult(
			grantedAppIds,
			grantedPackageIds,
			detailParts.Count > 0 ? string.Join("; ", detailParts) : null
		);
	}

}
