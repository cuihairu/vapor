namespace Vapor.Steam.Core.Steam;

/// <summary>
/// Protocol-agnostic view of the Steam network transport: connection lifecycle,
/// logon flow, and the platform actions (redeem, play). Isolates the SteamKit2
/// wire protocol behind this interface — session, action and agent code depend
/// only on the shapes here, so a protocol upgrade (SteamKit2 major version or a
/// replacement stack) is confined to the adapter implementation.
/// </summary>
public interface ISteamTransport
{
	/// <summary>Returns the pending log-on details for an account, or null when none are staged.</summary>
	Task<TransportLogOnDetails?> GetLogOnDetailsAsync(string accountName);

	/// <summary>Stores renewed access/refresh tokens for the next logon.</summary>
	Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken);

	/// <summary>Connects the transport to the Steam network, waiting until the connection is up.</summary>
	Task ConnectAsync(CancellationToken cancellationToken = default);

	/// <summary>Drops the connection to the Steam network.</summary>
	Task DisconnectAsync();

	/// <summary>Whether the transport currently holds a connection to the Steam network.</summary>
	Task<bool> IsConnectedAsync();

	/// <summary>Logs an account on. Throws the auth-challenge exceptions when Steam Guard intervenes.</summary>
	Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default);

	/// <summary>Stages an email Steam Guard code for the next logon attempt.</summary>
	void SetAuthCode(string accountName, string code);

	/// <summary>Stages a 2FA code for the next logon attempt.</summary>
	void SetTwoFactorCode(string accountName, string code);

	/// <summary>
	/// Begins a QR sign-in challenge for the given account and polls until the
	/// phone-side approval arrives, the challenge expires or the caller cancels.
	/// The challenge URL is surfaced through <paramref name="onChallengeUrl"/> —
	/// once up front, then again whenever Steam rotates it. The poll request key
	/// never leaves the transport. Requires the transport to be connected.
	/// </summary>
	Task<QrLoginResult> BeginQrLoginAsync(string accountName, Action<string> onChallengeUrl, CancellationToken cancellationToken = default);

	/// <summary>Pumps the transport's callback queue; required for async flows to progress.</summary>
	void RunCallbacks();

	/// <summary>Redeems a product key on the logged-on account.</summary>
	Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default);

	/// <summary>Requests free licenses for the given apps on the logged-on account (client protocol). Returns null when not connected or Steam did not answer.</summary>
	Task<FreeLicenseResult?> RequestFreeLicenseAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken = default);

	/// <summary>Gets the logged-on account's points shop balance summary. Returns null when not connected, not logged on, or Steam did not answer.</summary>
	Task<PointsShopSummary?> GetPointsShopSummaryAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Queries points shop reward definitions by definition id (cursor pagination
	/// handled internally). Returns null when not connected or Steam did not answer.
	/// </summary>
	Task<IReadOnlyList<PointsShopItemInfo>?> QueryPointsShopItemsAsync(IReadOnlyCollection<uint> definitionIds, CancellationToken cancellationToken = default);

	/// <summary>Redeems one points shop reward definition on the logged-on account. Returns null only when not connected; a Steam-side rejection comes back as the result code.</summary>
	Task<RedeemPointsResult?> RedeemPointsShopItemAsync(uint definitionId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Loads the logged-on account's stats blob for one game — the protocol
	/// precondition for any achievement write (stats must be loaded before the
	/// server accepts a store). Null when not connected or Steam did not answer.
	/// </summary>
	Task<UserStatsLoadResult?> LoadUserStatsAsync(uint appId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Stores a full stats blob back (achievement bits ride in these entries).
	/// Null when not connected or Steam did not answer; a Steam-side rejection
	/// comes back as the result code.
	/// </summary>
	Task<UserStatsStoreResult?> StoreUserStatsAsync(uint appId, uint crcStats, IReadOnlyList<UserStatsEntry> stats, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists a game's achievement API names in schema order (the list index is
	/// the achievement id the stats bitmap addresses). Null when not connected
	/// or Steam did not answer.
	/// </summary>
	Task<AchievementNamesResult?> GetGameAchievementNamesAsync(uint appId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sets the named achievements to unlocked or reset for the logged-on
	/// account: loads stats first (protocol precondition — a load failure fails
	/// the batch, never skips it), maps the names via the game schema, patches
	/// the stats bitmap, stores, then reads back and verifies every claim.
	/// Null when not connected or Steam did not answer at the load step.
	/// </summary>
	Task<AchievementWriteResult?> SetAchievementStatesAsync(uint appId, IReadOnlyList<string> names, bool unlock, CancellationToken cancellationToken = default);

	/// <summary>Plays the specified games on Steam. Pass an empty set to stop playing all games.</summary>
	void PlayGames(HashSet<uint> appIds);

	/// <summary>Gets the currently playing game AppIDs.</summary>
	IReadOnlySet<uint> GetPlayingGames();

	/// <summary>Refreshes the access token for the given account using stored credentials.</summary>
	Task<bool> RefreshAccessTokenAsync(string accountName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Session-level facade over an <see cref="ISteamTransport"/>. Kept as a separate
/// name so existing consumers and DI registrations stay stable; the transport
/// contract itself lives in <see cref="ISteamTransport"/>.
/// </summary>
public interface ISteamClientManager : ISteamTransport
{
}

/// <summary>Protocol-agnostic log-on details for a Steam account session.</summary>
public sealed record TransportLogOnDetails(
	string Username,
	string Password,
	string? AuthCode,
	string? TwoFactorCode,
	string? AccessToken,
	bool ShouldRememberPassword);

/// <summary>
/// Protocol-agnostic Steam result codes. Numeric values mirror the well-known
/// Steam EResult encoding so persisted task outputs keep their meaning across a
/// transport swap; <see cref="Other"/> covers codes without a mapped name.
/// </summary>
public enum SteamResult
{
	Other = 0,
	OK = 1,
	Fail = 2,
	InvalidParam = 8,
	Busy = 10,
	Timeout = 16,
	ServiceUnavailable = 20,
	DuplicateRequest = 29,
	AlreadyOwned = 30,
	TryAnotherCM = 48,
	AccountLogonDenied = 63,
	AccountLoginDeniedNeedTwoFactor = 85,
	RateLimitExceeded = 84
}

/// <summary>
/// Result of a key redemption attempt.
/// </summary>
public sealed record RedeemKeyResult(
	SteamResult Result,
	string? RequestId = null,
	long DurationMs = 0,
	IReadOnlyList<uint>? GrantedAppIDs = null,
	IReadOnlyList<uint>? GrantedPackageIDs = null,
	string? ReceiptDetails = null
);

/// <summary>
/// Result of a free-license request (the addlicense app path).
/// </summary>
public sealed record FreeLicenseResult(
	SteamResult Result,
	IReadOnlyList<uint> GrantedApps,
	IReadOnlyList<uint> GrantedPackages
);

/// <summary>Balance summary of the logged-on account's points shop.</summary>
public sealed record PointsShopSummary(
	long Points,
	long PointsEarned,
	long PointsSpent
);

/// <summary>
/// A points shop reward definition — an item that can be redeemed with points.
/// Free claimables (the points-shop flavor of addlicense) are the ones with
/// <see cref="PointCost"/> == 0.
/// </summary>
public sealed record PointsShopItemInfo(
	uint DefId,
	uint AppId,
	int Type,
	string? InternalDescription,
	long PointCost,
	bool Active,
	uint FreeUntilTimestamp
);

/// <summary>Outcome of a single points shop redemption.</summary>
public sealed record RedeemPointsResult(
	SteamResult Result,
	ulong CommunityItemId
);

/// <summary>
/// Outcome of a QR sign-in challenge. On success carries the freshly minted
/// refresh token, which callers stage via <see cref="ISteamTransport.UpdateLogOnDetailsAsync"/>
/// before the token log-on. The short-lived access token stays transport-internal.
/// </summary>
public sealed record QrLoginResult(
	bool Success,
	string? Error = null,
	string? RefreshToken = null
);

/// <summary>One numeric stats entry of a game's stats blob; achievement bits live in these too.</summary>
public sealed record UserStatsEntry(uint StatId, uint StatValue);

/// <summary>Steam's own record of when achievements were unlocked (an independent view of the bitmap).</summary>
public sealed record AchievementUnlockBlock(uint AchievementId, IReadOnlyList<uint> UnlockTimes);

/// <summary>A successfully loaded user stats blob (crc + entries + unlock blocks).</summary>
public sealed record UserStatsLoad(
	uint CrcStats,
	IReadOnlyList<UserStatsEntry> Stats,
	IReadOnlyList<AchievementUnlockBlock> AchievementBlocks);

/// <summary>Outcome of a stats load request; <see cref="Load"/> is non-null only when the result is OK.</summary>
public sealed record UserStatsLoadResult(SteamResult Result, UserStatsLoad? Load);

/// <summary>Outcome of a stats store request.</summary>
public sealed record UserStatsStoreResult(
	SteamResult Result,
	bool StatsOutOfDate,
	IReadOnlyList<uint> FailedValidationStatIds);

/// <summary>Schema query result: the game's achievement API names in schema order.</summary>
public sealed record AchievementNamesResult(SteamResult Result, IReadOnlyList<string> InternalNames);

/// <summary>Per-achievement write outcome — entries reflect reality, failures included.</summary>
public sealed record AchievementWriteEntry(string Name, bool Success, string? Detail);

/// <summary>
/// Aggregate outcome of one unlock/reset batch. <see cref="Verified"/> records
/// whether a post-store read-back confirmed the bitmap; entries are adjusted
/// when verification shows a claim did not stick.
/// </summary>
public sealed record AchievementWriteResult(
	bool Success,
	SteamResult Result,
	IReadOnlyList<AchievementWriteEntry> Entries,
	bool Verified);
