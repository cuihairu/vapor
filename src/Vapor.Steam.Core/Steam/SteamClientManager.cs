using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;
using Vapor.Steam.Core.Security;
using System.Diagnostics.CodeAnalysis;
using Vapor.Steam.Core.Utilities;

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
		var ctor = typeof(SteamAuthentication).GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: [typeof(SteamClient)],
			modifiers: null)
			?? throw new InvalidOperationException("SteamAuthentication constructor was not found.");

		_authentication = ctor.Invoke([steamClient]);
		_generateAccessTokenMethod = typeof(SteamAuthentication).GetMethod(
			name: nameof(GenerateAccessTokenForAppAsync),
			bindingAttr: BindingFlags.Instance | BindingFlags.Public)
			?? throw new InvalidOperationException("GenerateAccessTokenForAppAsync method was not found.");
	}

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

/// <summary>
/// SteamKit2 logon/callback state machine. The callback pump and logon
/// transitions only run inside a live SteamKit2 network session, which unit
/// tests cannot drive (consumers mock <see cref="ISteamClientManager"/>);
/// excluded from coverage.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class SteamClientManager : ISteamClientManager, IDisposable
{
	private sealed record LoginState(string AccountName, string Password)
	{
		public string? AccessToken { get; init; }
		public string? RefreshToken { get; init; }
		public SteamID? SteamId { get; init; }
		public string? AuthCode { get; init; }
		public string? TwoFactorCode { get; init; }
		public TaskCompletionSource<SteamUser.LoggedOnCallback>? LoginTcs { get; init; }
	}

	private readonly SteamClient _steamClient;
	private readonly CallbackManager _callbackManager;
	private readonly ILogger<SteamClientManager> _logger;
	private readonly ICredentialStore? _credentialStore;
	private readonly ISteamAuthTokenProvider _steamAuthTokenProvider;
	private readonly SteamUserStatsProtocolHandler _userStatsProtocol = new();
	private readonly ConcurrentDictionary<string, LoginState> _loginStates = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _connectLock = new();
	private TaskCompletionSource<bool> _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private string? _activeLoginAccountName;
	private bool _disposed;

	public SteamClientManager(ILogger<SteamClientManager> logger, ICredentialStore? credentialStore = null)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_steamClient = new SteamClient();
		_callbackManager = new CallbackManager(_steamClient);
		_steamAuthTokenProvider = new SteamAuthTokenProvider(_steamClient);
		_steamClient.AddHandler(_userStatsProtocol);

		SubscribeCallbacks();
	}

	internal SteamClientManager(
		ILogger<SteamClientManager> logger,
		ICredentialStore? credentialStore,
		ISteamAuthTokenProvider steamAuthTokenProvider)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_steamClient = new SteamClient();
		_callbackManager = new CallbackManager(_steamClient);
		_steamAuthTokenProvider = steamAuthTokenProvider;
		_steamClient.AddHandler(_userStatsProtocol);

		SubscribeCallbacks();
	}

	public Task<TransportLogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		if (!_loginStates.TryGetValue(accountName, out var state))
		{
			return Task.FromResult<TransportLogOnDetails?>(null);
		}

		var token = state.AccessToken ?? state.RefreshToken;

		return Task.FromResult<TransportLogOnDetails?>(new TransportLogOnDetails(
			state.AccountName,
			state.Password,
			state.AuthCode,
			state.TwoFactorCode,
			token,
			!string.IsNullOrWhiteSpace(token)));
	}

	public Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { AccessToken = accessToken, RefreshToken = refreshToken },
			(_, existing) => existing with { AccessToken = accessToken, RefreshToken = refreshToken }
		);

		return Task.CompletedTask;
	}

	internal Task UpdateSteamIdAsync(string accountName, SteamID steamId)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { SteamId = steamId },
			(_, existing) => existing with { SteamId = steamId });

		return Task.CompletedTask;
	}

	public Task<bool> IsConnectedAsync()
	{
		return Task.FromResult(_steamClient.IsConnected);
	}

	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ThrowIfDisposed();

		if (_steamClient.IsConnected)
		{
			return;
		}

		TaskCompletionSource<bool> tcs;
		lock (_connectLock)
		{
			if (_steamClient.IsConnected)
			{
				return;
			}

			_connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			tcs = _connectedTcs;
			_steamClient.Connect();
		}

		bool connected = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		if (!connected)
		{
			throw new InvalidOperationException("Steam client failed to connect");
		}
	}

	public async Task DisconnectAsync()
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			_steamClient.Disconnect();
		}
		catch
		{
		}

		await Task.Delay(100).ConfigureAwait(false);
	}

	public async Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		var tcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
		_activeLoginAccountName = accountName;

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

	public void RunCallbacks()
	{
		if (_disposed)
		{
			return;
		}

		_callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
	}

	public async Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot redeem key: Steam client not connected");
			return null;
		}

		var requestId = Guid.NewGuid().ToString("N")[..12];
		var stopwatch = ValueStopwatch.StartNew();

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var request = new CStore_RegisterCDKey_Request
			{
				activation_code = key,
				is_request_from_client = true
			};

			_logger.LogInformation("Redeeming key: {Key} (RequestId: {RequestId})", MaskKey(key), requestId);

			var asyncJob = unifiedMessages.SendMessage<CStore_RegisterCDKey_Request, CStore_RegisterCDKey_Response>(
				"Store#RegisterCDKey",
				request
			);

			// Set timeout
			asyncJob.Timeout = TimeSpan.FromSeconds(60);

			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Key redemption timed out (RequestId: {RequestId})", requestId);
				return new RedeemKeyResult(MapResult(EResult.Timeout), requestId, stopwatch.ElapsedMilliseconds);
			}

			_logger.LogInformation(
				"Key redemption result: {Result} (RequestId: {RequestId}, Duration: {Duration}ms)",
				response.Result,
				requestId,
				stopwatch.ElapsedMilliseconds
			);

			var parsedReceipt = ParseRedeemReceipt(response.Body);

			return new RedeemKeyResult(
				MapResult(response.Result),
				requestId,
				stopwatch.ElapsedMilliseconds,
				parsedReceipt?.GrantedAppIds,
				parsedReceipt?.GrantedPackageIds,
				parsedReceipt?.ReceiptDetails
			);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to redeem key (RequestId: {RequestId})", requestId);
			return new RedeemKeyResult(MapResult(EResult.Fail), requestId, stopwatch.ElapsedMilliseconds);
		}
	}

	public async Task<FreeLicenseResult?> RequestFreeLicenseAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(appIds);

		if (appIds.Count == 0)
		{
			return new FreeLicenseResult(SteamResult.OK, [], []);
		}

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot request free license: Steam client not connected");
			return null;
		}

		try
		{
			var steamApps = _steamClient.GetHandler<SteamApps>()
				?? throw new InvalidOperationException("SteamApps handler not available");

			var asyncJob = steamApps.RequestFreeLicense(appIds);
			asyncJob.Timeout = TimeSpan.FromSeconds(60);
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Free license request timed out for {Count} apps", appIds.Count);
				return new FreeLicenseResult(SteamResult.Timeout, [], []);
			}

			_logger.LogInformation(
				"Free license request result: {Result} (granted {GrantedApps} apps / {GrantedPackages} packages for {Count} requested apps)",
				response.Result,
				response.GrantedApps.Count,
				response.GrantedPackages.Count,
				appIds.Count);

			return new FreeLicenseResult(
				MapResult(response.Result),
				response.GrantedApps.ToList(),
				response.GrantedPackages.ToList());
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to request free license for {Count} apps", appIds.Count);
			return new FreeLicenseResult(SteamResult.Fail, [], []);
		}
	}

	public async Task<PointsShopSummary?> GetPointsShopSummaryAsync(CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot fetch points shop summary: Steam client not connected");
			return null;
		}

		if (_steamClient.SteamID is not { } steamId)
		{
			_logger.LogWarning("Cannot fetch points shop summary: not logged on");
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var asyncJob = unifiedMessages.SendMessage<CLoyaltyRewards_GetSummary_Request, CLoyaltyRewards_GetSummary_Response>(
				"LoyaltyRewards#GetSummary",
				new CLoyaltyRewards_GetSummary_Request { steamid = steamId });
			asyncJob.Timeout = TimeSpan.FromSeconds(60);
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Points shop summary request timed out");
				return null;
			}

			if (response.Result != EResult.OK)
			{
				_logger.LogWarning("Points shop summary request failed: {Result}", response.Result);
				return null;
			}

			return response.Body.summary is { } summary
				? new PointsShopSummary(summary.points, summary.points_earned, summary.points_spent)
				: null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to fetch points shop summary");
			return null;
		}
	}

	public async Task<IReadOnlyList<PointsShopItemInfo>?> QueryPointsShopItemsAsync(IReadOnlyCollection<uint> definitionIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definitionIds);

		if (definitionIds.Count == 0)
		{
			return [];
		}

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot query points shop items: Steam client not connected");
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var request = new CLoyaltyRewards_QueryRewardItems_Request();
			request.definitionids.AddRange(definitionIds.Distinct());

			var result = new List<PointsShopItemInfo>();
			while (true)
			{
				var asyncJob = unifiedMessages.SendMessage<CLoyaltyRewards_QueryRewardItems_Request, CLoyaltyRewards_QueryRewardItems_Response>(
					"LoyaltyRewards#QueryRewardItems",
					request);
				asyncJob.Timeout = TimeSpan.FromSeconds(60);
				var response = await asyncJob.ToTask().ConfigureAwait(false);

				if (response == null || response.Result != EResult.OK)
				{
					_logger.LogWarning("Points shop item query failed: {Result}", response?.Result.ToString() ?? "no response");
					return null;
				}

				result.AddRange(response.Body.definitions.Select(ToItemInfo));

				// Normally comparing counts suffices, but guard against Steam looping
				// the same cursor forever all the same (ASF's bulletproofing).
				if (result.Count >= response.Body.total_count ||
					string.IsNullOrEmpty(response.Body.next_cursor) ||
					request.cursor == response.Body.next_cursor)
				{
					return result;
				}

				request.cursor = response.Body.next_cursor;
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to query points shop items for {Count} definitions", definitionIds.Count);
			return null;
		}
	}

	public async Task<RedeemPointsResult?> RedeemPointsShopItemAsync(uint definitionId, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfZero(definitionId);

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot redeem points shop item: Steam client not connected");
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			// expected_points_cost stays 0 (unchecked), mirroring the ASF RP command:
			// the caller validates the price up front, so no racing-price failure mode.
			var asyncJob = unifiedMessages.SendMessage<CLoyaltyRewards_RedeemPoints_Request, CLoyaltyRewards_RedeemPoints_Response>(
				"LoyaltyRewards#RedeemPoints",
				new CLoyaltyRewards_RedeemPoints_Request { defid = definitionId });
			asyncJob.Timeout = TimeSpan.FromSeconds(60);
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Points shop redemption timed out for definition {DefId}", definitionId);
				return new RedeemPointsResult(SteamResult.Timeout, 0);
			}

			_logger.LogInformation(
				"Points shop redemption for definition {DefId}: {Result} (community item {CommunityItemId})",
				definitionId,
				response.Result,
				response.Body.communityitemid);

			return new RedeemPointsResult(MapResult(response.Result), response.Body.communityitemid);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to redeem points shop definition {DefId}", definitionId);
			return new RedeemPointsResult(SteamResult.Fail, 0);
		}
	}

	private static PointsShopItemInfo ToItemInfo(LoyaltyRewardDefinition definition) => new(
		definition.defid,
		definition.appid,
		definition.type,
		string.IsNullOrWhiteSpace(definition.internal_description) ? null : definition.internal_description,
		definition.point_cost,
		definition.active,
		definition.timestamp_free_until);

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

	private void SubscribeCallbacks()
	{
		_callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
		_callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
		_callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
		_callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
	}

	private void OnConnected(SteamClient.ConnectedCallback callback)
	{
		_logger.LogInformation("Steam client connected");
		_connectedTcs.TrySetResult(true);
	}

	private void OnDisconnected(SteamClient.DisconnectedCallback callback)
	{
		_logger.LogInformation("Steam client disconnected: {UserInitiated}", callback.UserInitiated);
		_connectedTcs.TrySetResult(false);
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

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			_steamClient.Disconnect();
		}
		catch
		{
		}

		_loginStates.Clear();
	}
}
