using SteamKit2;
using Microsoft.Extensions.Logging;
using SteamKit2.Internal;

namespace Vapor.Steam.Core.Steam;

/// <summary>User-stats surface: load/store stats and achievement read/write.</summary>
public sealed partial class SteamClientManager
{
	/// <summary>How long a stats-protocol exchange waits for Steam's answer (mirrors the 60s AsyncJob convention).</summary>
	private static readonly TimeSpan StatsProtocolTimeout = TimeSpan.FromSeconds(60);

	/// <inheritdoc />
	public async Task<UserStatsLoadResult?> LoadUserStatsAsync(uint appId, CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot load user stats for app {AppId}: Steam client not connected", appId);
			return null;
		}

		if (_steamClient.SteamID is not { } steamId)
		{
			_logger.LogWarning("Cannot load user stats for app {AppId}: not logged on", appId);
			return null;
		}

		try
		{
			var msg = new ClientMsgProtobuf<CMsgClientGetUserStats>(EMsg.ClientGetUserStats);
			var jobId = _steamClient.GetNextJobID();
			msg.SourceJobID = jobId;
			msg.ProtoHeader.routing_appid = appId;
			// crc 0 forces a full-blob answer; without a local schema there is no
			// incremental delta to negotiate.
			msg.Body.crc_stats = 0;
			msg.Body.game_id = new GameID((int)appId).ToUInt64();
			msg.Body.steam_id_for_user = steamId;

			var (wait, registration) = _userStatsProtocol.RegisterWait(jobId.Value);
			using (registration)
			{
				_steamClient.Send(msg);
				var response = await wait.WaitAsync(StatsProtocolTimeout, cancellationToken).ConfigureAwait(false);
				if (response is null)
				{
					_logger.LogWarning("User stats load for app {AppId} timed out", appId);
					return null;
				}

				if (response.Result != EResult.OK || response.GetResponse is null)
				{
					_logger.LogWarning("User stats load for app {AppId} failed: {Result}", appId, response.Result);
					return new UserStatsLoadResult(MapResult(response.Result), null);
				}

				return new UserStatsLoadResult(SteamResult.OK, new UserStatsLoad(
					response.GetResponse.crc_stats,
					response.GetResponse.stats
						.Select(s => new UserStatsEntry(s.stat_id, s.stat_value))
						.ToList(),
					response.GetResponse.achievement_blocks
						.Select(b => new AchievementUnlockBlock(
							b.achievement_id,
							b.unlock_time.ToList()))
						.ToList()));
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to load user stats for app {AppId}", appId);
			return new UserStatsLoadResult(SteamResult.Fail, null);
		}
	}

	/// <inheritdoc />
	public async Task<UserStatsStoreResult?> StoreUserStatsAsync(
		uint appId,
		uint crcStats,
		IReadOnlyList<UserStatsEntry> stats,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stats);

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot store user stats for app {AppId}: Steam client not connected", appId);
			return null;
		}

		if (_steamClient.SteamID is not { } steamId)
		{
			_logger.LogWarning("Cannot store user stats for app {AppId}: not logged on", appId);
			return null;
		}

		try
		{
			var msg = new ClientMsgProtobuf<CMsgClientStoreUserStats2>(EMsg.ClientStoreUserStats2);
			var jobId = _steamClient.GetNextJobID();
			msg.SourceJobID = jobId;
			msg.ProtoHeader.routing_appid = appId;
			msg.Body.game_id = new GameID((int)appId).ToUInt64();
			msg.Body.settor_steam_id = steamId;
			msg.Body.settee_steam_id = steamId;
			msg.Body.crc_stats = crcStats;
			// explicit_reset stays unset: its semantics are unverified and the
			// write path does not use fields it cannot reason about.
			msg.Body.stats.AddRange(stats.Select(e => new CMsgClientStoreUserStats2.Stats
			{
				stat_id = e.StatId,
				stat_value = e.StatValue
			}));

			var (wait, registration) = _userStatsProtocol.RegisterWait(jobId.Value);
			using (registration)
			{
				_steamClient.Send(msg);
				var response = await wait.WaitAsync(StatsProtocolTimeout, cancellationToken).ConfigureAwait(false);
				if (response is null)
				{
					_logger.LogWarning("User stats store for app {AppId} timed out", appId);
					return null;
				}

				var storeBody = response.StoreResponse;
				_logger.LogInformation(
					"User stats store for app {AppId}: {Result} (outOfDate={OutOfDate}, failedValidation={FailedCount})",
					appId, response.Result, storeBody?.stats_out_of_date ?? false, storeBody?.stats_failed_validation.Count ?? 0);

				return new UserStatsStoreResult(
					MapResult(response.Result),
					storeBody?.stats_out_of_date ?? false,
					storeBody?.stats_failed_validation.Select(s => s.stat_id).ToList() ?? []);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to store user stats for app {AppId}", appId);
			return null;
		}
	}

	/// <inheritdoc />
	public async Task<AchievementNamesResult?> GetGameAchievementNamesAsync(uint appId, CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot list achievement names for app {AppId}: Steam client not connected", appId);
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var asyncJob = unifiedMessages.SendMessage<CPlayer_GetGameAchievements_Request, CPlayer_GetGameAchievements_Response>(
				"Player#GetGameAchievements",
				new CPlayer_GetGameAchievements_Request { appid = appId, language = "english" });
			asyncJob.Timeout = StatsProtocolTimeout;
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Achievement schema query for app {AppId} timed out", appId);
				return null;
			}

			if (response.Result != EResult.OK)
			{
				_logger.LogWarning("Achievement schema query for app {AppId} failed: {Result}", appId, response.Result);
				return new AchievementNamesResult(MapResult(response.Result), []);
			}

			return new AchievementNamesResult(
				SteamResult.OK,
				response.Body.achievements
					.Select(a => a.internal_name)
					.Where(n => !string.IsNullOrEmpty(n))
					.ToList());
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to list achievement names for app {AppId}", appId);
			return null;
		}
	}

	/// <inheritdoc />
	public async Task<AchievementWriteResult?> SetAchievementStatesAsync(
		uint appId,
		IReadOnlyList<string> names,
		bool unlock,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(names);

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot write achievements for app {AppId}: Steam client not connected", appId);
			return null;
		}

		if (_steamClient.SteamID is null)
		{
			_logger.LogWarning("Cannot write achievements for app {AppId}: not logged on", appId);
			return null;
		}

		List<string> requested = names
			.Where(n => !string.IsNullOrWhiteSpace(n))
			.Select(n => n.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		// Write precondition (protocol constraint is the safety constraint):
		// a failed stats load aborts the whole batch — never write blind.
		var load = await LoadUserStatsAsync(appId, cancellationToken).ConfigureAwait(false);
		if (load is null)
		{
			return null;
		}

		if (load.Result != SteamResult.OK || load.Load is null)
		{
			_logger.LogWarning("Achievement write for app {AppId} aborted: stats load failed ({Result})", appId, load.Result);
			return new AchievementWriteResult(
				false,
				load.Result,
				requested.Select(n => new AchievementWriteEntry(n, false, "stats load failed")).ToList(),
				Verified: false);
		}

		var schema = await GetGameAchievementNamesAsync(appId, cancellationToken).ConfigureAwait(false);
		if (schema is null)
		{
			return null;
		}

		if (schema.Result != SteamResult.OK)
		{
			_logger.LogWarning("Achievement write for app {AppId} aborted: schema query failed ({Result})", appId, schema.Result);
			return new AchievementWriteResult(
				false,
				schema.Result,
				requested.Select(n => new AchievementWriteEntry(n, false, "achievement schema unavailable")).ToList(),
				Verified: false);
		}

		// Name → id mapping. The list index is the achievement id the bitmap
		// addresses (schema order); Steam's unlock blocks — an independent view —
		// must fit inside that range or the ordering assumption is broken and the
		// batch is refused rather than written to guessed positions.
		var indexByName = new Dictionary<string, uint>(schema.InternalNames.Count, StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < schema.InternalNames.Count; i++)
		{
			if (!indexByName.ContainsKey(schema.InternalNames[i]))
			{
				indexByName[schema.InternalNames[i]] = (uint)i;
			}
		}

		bool mismatch = load.Load.AchievementBlocks.Any(b => b.AchievementId >= (uint)schema.InternalNames.Count);
		if (mismatch)
		{
			_logger.LogWarning(
				"Achievement write for app {AppId} refused: unlock blocks reference ids outside the {Count}-entry schema (ordering assumption broken)",
				appId, schema.InternalNames.Count);
			return new AchievementWriteResult(
				false,
				SteamResult.Fail,
				requested.Select(n => new AchievementWriteEntry(n, false, "schema mismatch: unlock ids outside schema range")).ToList(),
				Verified: false);
		}

		var entries = new List<AchievementWriteEntry>();
		var mapped = new List<(string Name, uint Id)>();
		foreach (string name in requested)
		{
			if (indexByName.TryGetValue(name, out uint id))
			{
				mapped.Add((name, id));
			}
			else
			{
				entries.Add(new AchievementWriteEntry(name, false, "unknown achievement name"));
			}
		}

		if (mapped.Count > 0)
		{
			var patched = AchievementStatsBitmap.Apply(load.Load.Stats, mapped.Select(m => m.Id).ToList(), unlock);

			var store = await StoreUserStatsAsync(appId, load.Load.CrcStats, patched, cancellationToken).ConfigureAwait(false);
			if (store is null)
			{
				return null;
			}

			if (store.Result != SteamResult.OK)
			{
				_logger.LogWarning("Achievement write for app {AppId} failed at store: {Result}", appId, store.Result);
				entries.AddRange(mapped.Select(m => new AchievementWriteEntry(m.Name, false, $"store failed: {store.Result}")));
				return new AchievementWriteResult(false, store.Result, entries, Verified: false);
			}

			var failedStatIds = store.FailedValidationStatIds.ToHashSet();
			var stored = new List<(string Name, uint Id)>();
			foreach (var (name, id) in mapped)
			{
				if (failedStatIds.Contains(AchievementStatsBitmap.StatIdFor(id)))
				{
					entries.Add(new AchievementWriteEntry(name, false, "stat validation failed"));
				}
				else
				{
					stored.Add((name, id));
				}
			}

			// Read-back verification: a store ack alone is not proof the bit
			// stuck, so every claim is checked against a fresh load.
			if (stored.Count > 0)
			{
				var verify = await LoadUserStatsAsync(appId, cancellationToken).ConfigureAwait(false);
				if (verify is not null && verify.Result == SteamResult.OK && verify.Load is not null)
				{
					foreach (var (name, id) in stored)
					{
						bool isSet = AchievementStatsBitmap.IsSet(verify.Load.Stats, id);
						bool stuck = unlock ? isSet : !isSet;
						entries.Add(stuck
							? new AchievementWriteEntry(name, true, null)
							: new AchievementWriteEntry(name, false, "not reflected after store"));
					}
				}
				else
				{
					entries.AddRange(stored.Select(m => new AchievementWriteEntry(m.Name, true, "stored; read-back verification unavailable")));
				}
			}
		}

		return new AchievementWriteResult(entries.All(e => e.Success), SteamResult.OK, entries, Verified: true);
	}

	/// <summary>Maps a SteamKit2 result code onto the protocol-agnostic <see cref="SteamResult"/>.</summary>
	private static SteamResult MapResult(EResult result)
	{
		return result switch
		{
			EResult.OK => SteamResult.OK,
			EResult.Fail => SteamResult.Fail,
			EResult.InvalidParam => SteamResult.InvalidParam,
			EResult.Busy => SteamResult.Busy,
			EResult.Timeout => SteamResult.Timeout,
			EResult.ServiceUnavailable => SteamResult.ServiceUnavailable,
			EResult.DuplicateRequest => SteamResult.DuplicateRequest,
			EResult.AlreadyOwned => SteamResult.AlreadyOwned,
			EResult.TryAnotherCM => SteamResult.TryAnotherCM,
			EResult.AccountLogonDenied => SteamResult.AccountLogonDenied,
			EResult.AccountLoginDeniedNeedTwoFactor => SteamResult.AccountLoginDeniedNeedTwoFactor,
			EResult.RateLimitExceeded => SteamResult.RateLimitExceeded,
			_ => SteamResult.Other
		};
	}

}
