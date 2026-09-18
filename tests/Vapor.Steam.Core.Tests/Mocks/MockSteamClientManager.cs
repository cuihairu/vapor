using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Mocks;

/// <summary>
/// Mock implementation of ISteamClientManager for testing.
/// </summary>
public sealed class MockSteamClientManager : ISteamClientManager
{
	private RedeemKeyResult? _redeemKeyResult;
	private FreeLicenseResult? _freeLicenseResult;
	private PointsShopSummary? _pointsShopSummary;
	private IReadOnlyList<PointsShopItemInfo>? _pointsShopItems;
	private RedeemPointsResult? _redeemPointsResult;
	private TaskCompletionSource<bool>? _connectTcs;
	private bool _isConnected;

	public Task<TransportLogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		return Task.FromResult<TransportLogOnDetails?>(new TransportLogOnDetails(
			accountName,
			"mock_password",
			AuthCode: null,
			TwoFactorCode: null,
			AccessToken: null,
			ShouldRememberPassword: false));
	}

	public Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken)
	{
		return Task.CompletedTask;
	}

	public Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		_connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_isConnected = true;
		_connectTcs.TrySetResult(true);
		return Task.CompletedTask;
	}

	public Task DisconnectAsync()
	{
		_isConnected = false;
		return Task.CompletedTask;
	}

	public Task<bool> IsConnectedAsync()
	{
		return Task.FromResult(_isConnected);
	}

	public Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public void SetAuthCode(string accountName, string code) { }

	public void SetTwoFactorCode(string accountName, string code) { }

	public Task<QrLoginResult> BeginQrLoginAsync(string accountName, Action<string> onChallengeUrl, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new QrLoginResult(false, "QR sign-in is not configured on this mock"));
	}

	public void RunCallbacks() { }

	public Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<RedeemKeyResult?>(_redeemKeyResult);
	}

	public Task<FreeLicenseResult?> RequestFreeLicenseAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<FreeLicenseResult?>(_freeLicenseResult ?? new FreeLicenseResult(SteamResult.OK, appIds.ToList(), []));
	}

	public Task<PointsShopSummary?> GetPointsShopSummaryAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult<PointsShopSummary?>(_pointsShopSummary ?? new PointsShopSummary(1000, 1500, 500));
	}

	public Task<IReadOnlyList<PointsShopItemInfo>?> QueryPointsShopItemsAsync(IReadOnlyCollection<uint> definitionIds, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<IReadOnlyList<PointsShopItemInfo>?>(_pointsShopItems ?? definitionIds
			.Select(id => new PointsShopItemInfo(id, 753, 3, $"mock item {id}", 0, true, 0))
			.ToList());
	}

	public Task<RedeemPointsResult?> RedeemPointsShopItemAsync(uint definitionId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<RedeemPointsResult?>(_redeemPointsResult ?? new RedeemPointsResult(SteamResult.OK, definitionId));
	}

	public Task<UserStatsLoadResult?> LoadUserStatsAsync(uint appId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<UserStatsLoadResult?>(_userStatsLoad ?? new UserStatsLoadResult(SteamResult.OK, new UserStatsLoad(0, [], [])));
	}

	public Task<UserStatsStoreResult?> StoreUserStatsAsync(uint appId, uint crcStats, IReadOnlyList<UserStatsEntry> stats, CancellationToken cancellationToken = default)
	{
		StoreCalls.Add((appId, crcStats, stats.ToList()));
		return Task.FromResult<UserStatsStoreResult?>(_userStatsStore ?? new UserStatsStoreResult(SteamResult.OK, false, []));
	}

	public Task<AchievementNamesResult?> GetGameAchievementNamesAsync(uint appId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<AchievementNamesResult?>(_achievementNames ?? new AchievementNamesResult(SteamResult.OK, []));
	}

	public Task<AchievementWriteResult?> SetAchievementStatesAsync(uint appId, IReadOnlyList<string> names, bool unlock, CancellationToken cancellationToken = default)
	{
		SetAchievementStatesCalls.Add((appId, names.ToList(), unlock));
		return Task.FromResult(_achievementWriteResult);
	}

	internal List<(uint AppId, uint CrcStats, List<UserStatsEntry> Stats)> StoreCalls { get; } = [];

	internal List<(uint AppId, List<string> Names, bool Unlock)> SetAchievementStatesCalls { get; } = [];

	private UserStatsLoadResult? _userStatsLoad;
	private UserStatsStoreResult? _userStatsStore;
	private AchievementNamesResult? _achievementNames;
	private AchievementWriteResult? _achievementWriteResult;

	/// <summary>Sets the result to return from LoadUserStatsAsync.</summary>
	public void SetUserStatsLoad(UserStatsLoadResult result)
	{
		_userStatsLoad = result;
	}

	/// <summary>Sets the result to return from StoreUserStatsAsync.</summary>
	public void SetUserStatsStore(UserStatsStoreResult result)
	{
		_userStatsStore = result;
	}

	/// <summary>Sets the result to return from GetGameAchievementNamesAsync.</summary>
	public void SetAchievementNames(AchievementNamesResult result)
	{
		_achievementNames = result;
	}

	/// <summary>Sets the result to return from SetAchievementStatesAsync.</summary>
	public void SetAchievementWriteResult(AchievementWriteResult? result)
	{
		_achievementWriteResult = result;
	}

	public void PlayGames(HashSet<uint> appIds) { }

	public IReadOnlySet<uint> GetPlayingGames()
	{
		return new HashSet<uint>();
	}

	public Task<bool> RefreshAccessTokenAsync(string accountName, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(false);
	}

	/// <summary>
	/// Sets the result to return from RedeemKeyAsync.
	/// </summary>
	public void SetRedeemKeyResult(RedeemKeyResult result)
	{
		_redeemKeyResult = result;
	}

	/// <summary>
	/// Sets the result to return from RequestFreeLicenseAsync.
	/// </summary>
	public void SetFreeLicenseResult(FreeLicenseResult result)
	{
		_freeLicenseResult = result;
	}

	/// <summary>Sets the summary to return from GetPointsShopSummaryAsync.</summary>
	public void SetPointsShopSummary(PointsShopSummary summary)
	{
		_pointsShopSummary = summary;
	}

	/// <summary>Sets the definitions to return from QueryPointsShopItemsAsync (null = per-id free-item defaults).</summary>
	public void SetPointsShopItems(IReadOnlyList<PointsShopItemInfo> items)
	{
		_pointsShopItems = items;
	}

	/// <summary>Sets the result to return from RedeemPointsShopItemAsync.</summary>
	public void SetRedeemPointsResult(RedeemPointsResult result)
	{
		_redeemPointsResult = result;
	}

	/// <summary>
	/// Sets whether the mock is connected.
	/// </summary>
	public void SetConnected(bool connected)
	{
		_isConnected = connected;
	}
}
