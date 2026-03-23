using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Utilities;

namespace Vapor.Steam.Core.Steam;

public interface ISteamClientManager
{
	Task<SteamUser.LogOnDetails?> GetLogOnDetailsAsync(string accountName);
	Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken);
	SteamClient GetClient();
	Task ConnectAsync(CancellationToken cancellationToken = default);
	Task DisconnectAsync();
	Task<bool> IsConnectedAsync();
	Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default);
	void SetAuthCode(string accountName, string code);
	void SetTwoFactorCode(string accountName, string code);
	void RunCallbacks();
	Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default);
	/// <summary>
	/// Plays the specified games on Steam. Pass empty set to stop playing all games.
	/// </summary>
	void PlayGames(HashSet<uint> appIds);
	/// <summary>
	/// Gets the currently playing game AppIDs.
	/// </summary>
	IReadOnlySet<uint> GetPlayingGames();
	/// <summary>
	/// Refreshes the access token for the given account using stored credentials.
	/// </summary>
	Task<bool> RefreshAccessTokenAsync(string accountName, CancellationToken cancellationToken = default);
}

internal interface ISteamAuthTokenProvider
{
	Task<SteamTokenRenewalResult> GenerateAccessTokenForAppAsync(
		SteamID steamId,
		string refreshToken,
		bool allowRenewal,
		CancellationToken cancellationToken = default);
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

/// <summary>
/// Result of a key redemption attempt.
/// </summary>
public sealed record RedeemKeyResult(
	EResult Result,
	string? RequestId = null,
	long DurationMs = 0,
	IReadOnlyList<uint>? GrantedAppIDs = null,
	IReadOnlyList<uint>? GrantedPackageIDs = null,
	string? ReceiptDetails = null
);

internal sealed record RedeemReceiptParseResult(
	IReadOnlyList<uint> GrantedAppIds,
	IReadOnlyList<uint> GrantedPackageIds,
	string? ReceiptDetails
);

public sealed class SteamAuthCodeRequiredException : Exception
{
	public SteamAuthCodeRequiredException(string message) : base(message) { }
}

public sealed class SteamTwoFactorCodeRequiredException : Exception
{
	public SteamTwoFactorCodeRequiredException(string message) : base(message) { }
}

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

		SubscribeCallbacks();
	}

	public SteamClient GetClient() => _steamClient;

	public Task<SteamUser.LogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		if (!_loginStates.TryGetValue(accountName, out var state))
		{
			return Task.FromResult<SteamUser.LogOnDetails?>(null);
		}

		var token = state.AccessToken ?? state.RefreshToken;

		return Task.FromResult<SteamUser.LogOnDetails?>(new SteamUser.LogOnDetails
		{
			Username = state.AccountName,
			Password = state.Password,
			AuthCode = state.AuthCode,
			TwoFactorCode = state.TwoFactorCode,
			AccessToken = token,
			ShouldRememberPassword = !string.IsNullOrWhiteSpace(token)
		});
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
				return new RedeemKeyResult(EResult.Timeout, requestId, stopwatch.ElapsedMilliseconds);
			}

			_logger.LogInformation(
				"Key redemption result: {Result} (RequestId: {RequestId}, Duration: {Duration}ms)",
				response.Result,
				requestId,
				stopwatch.ElapsedMilliseconds
			);

			var parsedReceipt = ParseRedeemReceipt(response.Body);

			return new RedeemKeyResult(
				response.Result,
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
			return new RedeemKeyResult(EResult.Fail, requestId, stopwatch.ElapsedMilliseconds);
		}
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
