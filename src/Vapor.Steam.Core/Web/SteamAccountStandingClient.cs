using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// An account's ban/standing snapshot from Steam's official endpoints:
/// ISteamUser/GetPlayerBans (VAC / community / game bans, economy ban state,
/// days since last ban) plus IPlayerService/GetSteamLevel (a level of 0 marks
/// a limited account). <see cref="Limited"/>/<see cref="SteamLevel"/> are null
/// when the level lookup failed — the ban data is the authoritative part.
/// </summary>
public sealed record AccountStanding(
	ulong SteamId,
	bool VacBanned,
	int NumberOfVacBans,
	int NumberOfGameBans,
	int DaysSinceLastBan,
	bool CommunityBanned,
	string EconomyBan,
	bool? Limited,
	int? SteamLevel);

/// <summary>
/// Fetches <see cref="AccountStanding"/> snapshots through a logged-on web
/// session. Both endpoints are key-gated, so the key is resolved from the
/// session first; a missing key or a failed ban lookup throws, while a failed
/// level lookup only clears <see cref="AccountStanding.Limited"/>.
/// </summary>
public sealed class SteamAccountStandingClient
{
	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamAccountStandingClient> _logger;

	public SteamAccountStandingClient(SteamWebHandler webHandler, ILogger<SteamAccountStandingClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// Queries the standing for the given SteamID. Throws
	/// <see cref="InvalidOperationException"/> when the Web API key is
	/// unavailable or the ban endpoint fails.
	/// </summary>
	public async Task<AccountStanding> GetStandingAsync(
		ulong steamId,
		CancellationToken cancellationToken = default)
	{
		var apiKey = await SteamWebApiKeyFetcher.FetchAsync(_webHandler, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrEmpty(apiKey))
		{
			throw new InvalidOperationException(
				$"Failed to obtain the Steam Web API key for the standing query (SteamID {steamId})");
		}

		var bans = await FetchBansAsync(apiKey, steamId, cancellationToken).ConfigureAwait(false);
		var (level, limited) = await TryFetchLevelAsync(apiKey, steamId, cancellationToken).ConfigureAwait(false);

		return new AccountStanding(
			steamId,
			bans.VacBanned,
			bans.NumberOfVacBans,
			bans.NumberOfGameBans,
			bans.DaysSinceLastBan,
			bans.CommunityBanned,
			bans.EconomyBan,
			limited,
			level);
	}

	private async Task<BanSnapshot> FetchBansAsync(string apiKey, ulong steamId, CancellationToken cancellationToken)
	{
		var url = "https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?" +
			$"key={Uri.EscapeDataString(apiKey)}&steamids={steamId.ToString(CultureInfo.InvariantCulture)}";
		var response = await _webHandler.GetAsync(new Uri(url), null, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			throw new InvalidOperationException(
				$"GetPlayerBans failed for SteamID {steamId} (HTTP {(int)response.StatusCode})");
		}

		using var document = JsonDocument.Parse(response.Body);
		if (!document.RootElement.TryGetProperty("players", out var players)
			|| players.ValueKind != JsonValueKind.Array
			|| players.GetArrayLength() == 0)
		{
			throw new InvalidOperationException($"GetPlayerBans returned no player entry for SteamID {steamId}");
		}

		var player = players[0];
		return new BanSnapshot(
			VacBanned: player.TryGetProperty("VACBanned", out var vac) && vac.ValueKind == JsonValueKind.True,
			NumberOfVacBans: player.TryGetProperty("NumberOfVACBans", out var nVac) && nVac.TryGetInt32(out int nVacBans) ? nVacBans : 0,
			NumberOfGameBans: player.TryGetProperty("NumberOfGameBans", out var nGame) && nGame.TryGetInt32(out int nGameBans) ? nGameBans : 0,
			DaysSinceLastBan: player.TryGetProperty("DaysSinceLastBan", out var days) && days.TryGetInt32(out int d) ? d : 0,
			CommunityBanned: player.TryGetProperty("CommunityBanned", out var comm) && comm.ValueKind == JsonValueKind.True,
			EconomyBan: player.TryGetProperty("EconomyBan", out var econ) && econ.ValueKind == JsonValueKind.String ? econ.GetString() ?? "unknown" : "unknown");
	}

	private async Task<(int? Level, bool? Limited)> TryFetchLevelAsync(string apiKey, ulong steamId, CancellationToken cancellationToken)
	{
		try
		{
			var url = "https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?" +
				$"key={Uri.EscapeDataString(apiKey)}&steamid={steamId.ToString(CultureInfo.InvariantCulture)}";
			var response = await _webHandler.GetAsync(new Uri(url), null, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
			{
				_logger.LogWarning("GetSteamLevel failed for SteamID {SteamId} (HTTP {StatusCode}); limited state unknown",
					steamId, response.StatusCode);
				return (null, null);
			}

			using var document = JsonDocument.Parse(response.Body);
			if (document.RootElement.TryGetProperty("response", out var resp)
				&& resp.TryGetProperty("player_level", out var levelElement)
				&& levelElement.TryGetInt32(out int level))
			{
				return (level, level == 0);
			}

			_logger.LogWarning("GetSteamLevel response missing player_level for SteamID {SteamId}; limited state unknown", steamId);
			return (null, null);
		}
		catch (JsonException ex)
		{
			_logger.LogWarning(ex, "GetSteamLevel returned malformed JSON for SteamID {SteamId}; limited state unknown", steamId);
			return (null, null);
		}
	}

	private sealed record BanSnapshot(
		bool VacBanned,
		int NumberOfVacBans,
		int NumberOfGameBans,
		int DaysSinceLastBan,
		bool CommunityBanned,
		string EconomyBan);
}
