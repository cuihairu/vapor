using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Health probe for ban/standing state: queries VAC / community / game bans
/// and the economy-ban state (GetPlayerBans) plus the limited-account marker
/// (GetSteamLevel == 0) for the session's own SteamID (or a payload
/// <c>steam_id</c> override). The aggregated <c>standing</c> output is
/// <c>banned</c> (VAC/community/game/economy ban), <c>restricted</c>
/// (economy probation) or <c>clean</c>; orchestration uses it to quarantine
/// accounts from trade/market work.
/// </summary>
public sealed class CheckAccountStandingAction : IAction
{
	private readonly ILogger<CheckAccountStandingAction> _logger;
	private readonly Func<SteamWebHandler, SteamAccountStandingClient> _standingClientFactory;

	public CheckAccountStandingAction(ILogger<CheckAccountStandingAction> logger)
	{
		_logger = logger;
		_standingClientFactory = webHandler => new SteamAccountStandingClient(
			webHandler, NullLogger<SteamAccountStandingClient>.Instance);
	}

	// Constructor for testing with custom factory.
	internal CheckAccountStandingAction(
		ILogger<CheckAccountStandingAction> logger,
		Func<SteamWebHandler, SteamAccountStandingClient> standingClientFactory)
	{
		_logger = logger;
		_standingClientFactory = standingClientFactory;
	}

	/// <summary>Test seam: bypasses the real client factory entirely.</summary>
	internal Func<ulong, CancellationToken, Task<AccountStanding>>? FetchOverride { get; set; }

	public string Name => "check_account_standing";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Check ban/standing state (VAC, community, game, economy bans; limited marker)",
		RequiresLogin: true,
		TimeoutSeconds: 60
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var webHandler = session.SteamWebHandler;
		if (webHandler == null && FetchOverride == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		ulong? steamId = ParseSteamId(PayloadReader.GetString(payload, "steam_id"));
		steamId ??= webHandler?.TryResolveOwnSteamId();
		if (steamId is not { } targetSteamId)
		{
			return new ActionResult(false, "SteamID unknown: not in payload and not resolvable from the web session", null);
		}

		AccountStanding standing;
		try
		{
			standing = FetchOverride != null
				? await FetchOverride(targetSteamId, cancellationToken).ConfigureAwait(false)
				: await _standingClientFactory(webHandler!).GetStandingAsync(targetSteamId, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Standing check failed for {AccountName} (SteamID {SteamId})", session.AccountName, targetSteamId);
			return new ActionResult(false, $"Standing check failed: {ex.Message}", null);
		}

		var summary = Classify(standing);
		var output = new Dictionary<string, object?>
		{
			["account"] = session.AccountName,
			["steamId"] = standing.SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["standing"] = summary,
			["vacBanned"] = standing.VacBanned,
			["numberOfVacBans"] = standing.NumberOfVacBans,
			["numberOfGameBans"] = standing.NumberOfGameBans,
			["daysSinceLastBan"] = standing.DaysSinceLastBan,
			["communityBanned"] = standing.CommunityBanned,
			["economyBan"] = standing.EconomyBan,
			["limited"] = standing.Limited,
			["steamLevel"] = standing.SteamLevel,
		};

		_logger.LogInformation(
			"Standing for {AccountName}: {Standing} (VAC={Vac}, community={Community}, game bans={GameBans}, economy={Economy})",
			session.AccountName, summary, standing.VacBanned, standing.CommunityBanned, standing.NumberOfGameBans, standing.EconomyBan);

		return new ActionResult(true, null, output);
	}

	/// <summary>Parses the payload's SteamID64 (string form); null when absent or malformed.</summary>
	internal static ulong? ParseSteamId(string? value) =>
		ulong.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out ulong steamId)
			&& steamId >= 76561197960265728UL
			? steamId
			: null;

	/// <summary>
	/// Aggregates the raw ban flags: any hard ban → <c>banned</c>, economy
	/// probation → <c>restricted</c>, otherwise <c>clean</c>.
	/// </summary>
	internal static string Classify(AccountStanding standing)
	{
		if (standing.VacBanned || standing.CommunityBanned || standing.NumberOfGameBans > 0
			|| string.Equals(standing.EconomyBan, "banned", StringComparison.OrdinalIgnoreCase))
		{
			return "banned";
		}

		if (string.Equals(standing.EconomyBan, "probation", StringComparison.OrdinalIgnoreCase))
		{
			return "restricted";
		}

		return "clean";
	}
}
