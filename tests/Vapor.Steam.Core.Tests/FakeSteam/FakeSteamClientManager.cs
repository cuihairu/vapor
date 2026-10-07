using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.FakeSteam;

/// <summary>Scripted outcome of the next transport log-on attempt.</summary>
public enum FakeLoginOutcome
{
	/// <summary>Log-on succeeds (EResult.OK).</summary>
	Success,
	/// <summary>Email Steam Guard challenge (EResult.AccountLogonDenied).</summary>
	AuthCodeRequired,
	/// <summary>Authenticator challenge (EResult.AccountLoginDeniedNeedTwoFactor).</summary>
	TwoFactorRequired,
	/// <summary>
	/// Any other non-OK result: fails with the next queued failure message in the
	/// exact <c>InvalidOperationException("Steam login failed: …")</c> shape the
	/// real transport throws (invalid password, RateLimitExceeded, …).
	/// </summary>
	Failure
}

/// <summary>One recorded log-on attempt as the transport saw it, staged codes included.</summary>
public sealed record FakeLoginAttempt(
	string AccountName,
	string Password,
	string? AuthCode,
	string? TwoFactorCode,
	bool TokenLogOn);

/// <summary>
/// Deterministic FakeSteam backend on the ISteamClientManager transport seam
/// (40.7-1 audit: the seam expresses connect/logon/challenge/QR/proxy fully, so
/// the six session state-machine scenarios need no simulator extension and no
/// real network). Every outcome is scripted in the arrange phase — nothing is
/// inferred from timing — and the journal records what actually crossed the
/// seam for the assert phase.
/// </summary>
public sealed class FakeSteamClientManager : ISteamClientManager
{
	/// <summary>Mirrors the real transport's email Steam Guard challenge message.</summary>
	public const string AuthCodeRequiredMessage = "Steam auth code required (email Steam Guard)";

	/// <summary>Mirrors the real transport's authenticator challenge message.</summary>
	public const string TwoFactorRequiredMessage = "Steam 2FA code required (authenticator)";

	private readonly object _lock = new();
	private readonly Queue<FakeLoginOutcome> _loginScript = new();
	private readonly Queue<string> _failureMessages = new();
	private readonly Queue<FakeQrSignIn> _qrScript = new();
	private readonly List<FakeLoginAttempt> _loginAttempts = [];
	private readonly List<(string AccountName, string? AccessToken, string? RefreshToken)> _tokenStages = [];
	private readonly List<(string AccountName, string? Proxy)> _proxiesStaged = [];
	private readonly Dictionary<string, TransportLogOnDetails> _stagedLogOn = new(StringComparer.Ordinal);
	private readonly HashSet<uint> _playingGames = [];
	private Exception? _connectFailure;
	private TaskCompletionSource? _connectGate;
	private TaskCompletionSource? _connectStarted;
	private bool _connected;
	private int _connectCount;
	private int _disconnectCount;

	// --- scripting surface (arrange; nothing reads the clock) ------------------

	/// <summary>Queues log-on outcomes, consumed one per LoginAsync call; unscripted attempts succeed.</summary>
	public void QueueLoginOutcomes(params FakeLoginOutcome[] outcomes)
	{
		lock (_lock)
		{
			foreach (var outcome in outcomes)
			{
				_loginScript.Enqueue(outcome);
			}
		}
	}

	/// <summary>Queues the failure message for the next Failure outcome (mirrors "Steam login failed: {EResult}").</summary>
	public void QueueFailure(string message)
	{
		lock (_lock)
		{
			_failureMessages.Enqueue(message);
		}
	}

	/// <summary>Schedules the next connect attempt (while disconnected) to fail with this exception.</summary>
	public void SetConnectFailure(Exception failure)
	{
		lock (_lock)
		{
			_connectFailure = failure;
		}
	}

	/// <summary>Parks the next connect attempt until <see cref="ReleaseConnect"/> or the caller's token fires.</summary>
	public void ParkNextConnect()
	{
		lock (_lock)
		{
			_connectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}
	}

	/// <summary>Releases a connect parked by <see cref="ParkNextConnect"/>.</summary>
	public void ReleaseConnect()
	{
		TaskCompletionSource? gate;
		lock (_lock)
		{
			gate = _connectGate;
		}

		gate?.TrySetResult();
	}

	/// <summary>Resolves when a parked connect attempt has been entered (deterministic park proof).</summary>
	public Task WhenConnectStarted
	{
		get
		{
			lock (_lock)
			{
				return _connectStarted?.Task ?? Task.CompletedTask;
			}
		}
	}

	/// <summary>Scripts one QR sign-in: each challenge URL is surfaced in order, then the result returned.</summary>
	public void QueueQrSignIn(string[] challengeUrls, QrLoginResult result)
	{
		lock (_lock)
		{
			_qrScript.Enqueue(new FakeQrSignIn(challengeUrls, result));
		}
	}

	// --- journal (assert) -------------------------------------------------------

	/// <summary>Snapshot of every log-on attempt, in order, with the staged codes each attempt carried.</summary>
	public IReadOnlyList<FakeLoginAttempt> LoginAttempts
	{
		get
		{
			lock (_lock)
			{
				return _loginAttempts.ToArray();
			}
		}
	}

	/// <summary>Connect attempts while disconnected (early returns on an existing connection don't count).</summary>
	public int ConnectCount
	{
		get
		{
			lock (_lock)
			{
				return _connectCount;
			}
		}
	}

	public int DisconnectCount
	{
		get
		{
			lock (_lock)
			{
				return _disconnectCount;
			}
		}
	}

	/// <summary>The log-on details currently staged for an account, or null.</summary>
	public TransportLogOnDetails? StagedLogOn(string accountName)
	{
		lock (_lock)
		{
			return _stagedLogOn.TryGetValue(accountName, out var details) ? details : null;
		}
	}

	/// <summary>Every UpdateLogOnDetailsAsync call, in order.</summary>
	public IReadOnlyList<(string AccountName, string? AccessToken, string? RefreshToken)> TokenStages
	{
		get
		{
			lock (_lock)
			{
				return _tokenStages.ToArray();
			}
		}
	}

	/// <summary>Every SetAccountProxyAsync call, in order.</summary>
	public IReadOnlyList<(string AccountName, string? Proxy)> ProxiesStaged
	{
		get
		{
			lock (_lock)
			{
				return _proxiesStaged.ToArray();
			}
		}
	}

	// --- ISteamTransport: connection lifecycle ----------------------------------

	public Task<TransportLogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		return Task.FromResult(StagedLogOn(accountName));
	}

	public Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken)
	{
		lock (_lock)
		{
			_tokenStages.Add((accountName, accessToken, refreshToken));
			_stagedLogOn.TryGetValue(accountName, out var existing);
			_stagedLogOn[accountName] = new TransportLogOnDetails(
				accountName,
				existing?.Password ?? string.Empty,
				existing?.AuthCode,
				existing?.TwoFactorCode,
				// Either token enables a token log-on; the read shape surfaces it as
				// AccessToken, mirroring the real transport's BuildLogOnDetails token
				// fold (AccessToken ?? RefreshToken).
				accessToken ?? refreshToken ?? existing?.AccessToken,
				ShouldRememberPassword: false);
		}

		return Task.CompletedTask;
	}

	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		TaskCompletionSource? gate;
		Exception? failure;
		lock (_lock)
		{
			if (_connected)
			{
				return;
			}

			_connectCount++;
			gate = _connectGate;
			_connectGate = null;
			_connectStarted?.TrySetResult();
			failure = _connectFailure;
			_connectFailure = null;
			if (gate == null)
			{
				_connected = true;
			}
		}

		if (gate != null)
		{
			await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			lock (_lock)
			{
				_connected = true;
			}
			return;
		}

		if (failure != null)
		{
			throw failure;
		}
	}

	public Task SetAccountProxyAsync(string accountName, string? proxy, CancellationToken cancellationToken = default)
	{
		lock (_lock)
		{
			_proxiesStaged.Add((accountName, proxy));
		}

		return Task.CompletedTask;
	}

	public Task DisconnectAsync()
	{
		lock (_lock)
		{
			_connected = false;
			_disconnectCount++;
		}

		return Task.CompletedTask;
	}

	public Task<bool> IsConnectedAsync()
	{
		lock (_lock)
		{
			return Task.FromResult(_connected);
		}
	}

	// --- ISteamTransport: log-on flow -------------------------------------------

	public Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
	{
		lock (_lock)
		{
			_stagedLogOn.TryGetValue(accountName, out var staged);
			_loginAttempts.Add(new FakeLoginAttempt(
				accountName,
				password,
				staged?.AuthCode,
				staged?.TwoFactorCode,
				TokenLogOn: staged is not null && staged.AccessToken is not null));

			var outcome = _loginScript.Count > 0 ? _loginScript.Dequeue() : FakeLoginOutcome.Success;
			return outcome switch
			{
				FakeLoginOutcome.AuthCodeRequired => Task.FromException(new SteamAuthCodeRequiredException(AuthCodeRequiredMessage)),
				FakeLoginOutcome.TwoFactorRequired => Task.FromException(new SteamTwoFactorCodeRequiredException(TwoFactorRequiredMessage)),
				FakeLoginOutcome.Failure => Task.FromException(new InvalidOperationException(
					_failureMessages.Count > 0 ? _failureMessages.Dequeue() : "Steam login failed: Fail")),
				_ => Task.CompletedTask,
			};
		}
	}

	public void SetAuthCode(string accountName, string code)
	{
		lock (_lock)
		{
			_stagedLogOn.TryGetValue(accountName, out var existing);
			_stagedLogOn[accountName] = new TransportLogOnDetails(
				accountName,
				existing?.Password ?? string.Empty,
				code,
				existing?.TwoFactorCode,
				existing?.AccessToken,
				ShouldRememberPassword: false);
		}
	}

	public void SetTwoFactorCode(string accountName, string code)
	{
		lock (_lock)
		{
			_stagedLogOn.TryGetValue(accountName, out var existing);
			_stagedLogOn[accountName] = new TransportLogOnDetails(
				accountName,
				existing?.Password ?? string.Empty,
				existing?.AuthCode,
				code,
				existing?.AccessToken,
				ShouldRememberPassword: false);
		}
	}

	public Task<QrLoginResult> BeginQrLoginAsync(string accountName, Action<string> onChallengeUrl, CancellationToken cancellationToken = default)
	{
		FakeQrSignIn? scripted;
		lock (_lock)
		{
			scripted = _qrScript.Count > 0 ? _qrScript.Dequeue() : null;
		}

		if (scripted == null)
		{
			return Task.FromResult(new QrLoginResult(false, "no QR sign-in is scripted on the fake"));
		}

		foreach (var url in scripted.ChallengeUrls)
		{
			onChallengeUrl(url);
		}

		return Task.FromResult(scripted.Result);
	}

	public void RunCallbacks()
	{
	}

	public Task<bool> RefreshAccessTokenAsync(string accountName, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(false);
	}

	// --- ISteamTransport: platform actions (inert defaults; the state-machine
	//     scenarios never cross these, the shapes mirror MockSteamClientManager) --

	public Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<RedeemKeyResult?>(null);
	}

	public Task<FreeLicenseResult?> RequestFreeLicenseAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<FreeLicenseResult?>(new FreeLicenseResult(SteamResult.OK, appIds.ToList(), []));
	}

	public Task<PointsShopSummary?> GetPointsShopSummaryAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult<PointsShopSummary?>(new PointsShopSummary(1000, 1500, 500));
	}

	public Task<IReadOnlyList<PointsShopItemInfo>?> QueryPointsShopItemsAsync(IReadOnlyCollection<uint> definitionIds, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<IReadOnlyList<PointsShopItemInfo>?>(definitionIds
			.Select(id => new PointsShopItemInfo(id, 753, 3, $"fake item {id}", 0, true, 0))
			.ToList());
	}

	public Task<RedeemPointsResult?> RedeemPointsShopItemAsync(uint definitionId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<RedeemPointsResult?>(new RedeemPointsResult(SteamResult.OK, definitionId));
	}

	public Task<UserStatsLoadResult?> LoadUserStatsAsync(uint appId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<UserStatsLoadResult?>(new UserStatsLoadResult(SteamResult.OK, new UserStatsLoad(0, [], [])));
	}

	public Task<UserStatsStoreResult?> StoreUserStatsAsync(uint appId, uint crcStats, IReadOnlyList<UserStatsEntry> stats, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<UserStatsStoreResult?>(new UserStatsStoreResult(SteamResult.OK, false, []));
	}

	public Task<AchievementNamesResult?> GetGameAchievementNamesAsync(uint appId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<AchievementNamesResult?>(new AchievementNamesResult(SteamResult.OK, []));
	}

	public Task<AchievementWriteResult?> SetAchievementStatesAsync(uint appId, IReadOnlyList<string> names, bool unlock, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<AchievementWriteResult?>(null);
	}

	public void PlayGames(HashSet<uint> appIds)
	{
		lock (_lock)
		{
			_playingGames.Clear();
			foreach (var appId in appIds)
			{
				_playingGames.Add(appId);
			}
		}
	}

	public IReadOnlySet<uint> GetPlayingGames()
	{
		lock (_lock)
		{
			return _playingGames.ToHashSet();
		}
	}
}

/// <summary>One scripted QR sign-in: challenge URLs surfaced in order, then the result.</summary>
public sealed record FakeQrSignIn(string[] ChallengeUrls, QrLoginResult Result);
