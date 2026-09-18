using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// One app's total lifetime playtime, as reported by the profile games tab.
/// Never-played games report zero hours.
/// </summary>
public sealed record GamePlaytime(uint AppId, string? Name, double Hours);

/// <summary>
/// Fetches and parses the community profile games tab
/// (<c>steamcommunity.com/profiles/{steamId}/games?tab=all</c>) to report
/// total playtime per owned game. The Web API equivalent
/// (IPlayerService/GetOwnedGames) needs a site-wide API key, which Vapor does
/// not hold; the games tab is reachable with the account's own logged-in web
/// session. The page embeds the game list as a JSON array assigned to
/// <c>var rgGames</c>; the request forces <c>l=english</c> so the embedded
/// values match the parser regardless of the account's display language.
/// </summary>
public sealed class SteamProfileGamesClient
{
	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamProfileGamesClient> _logger;

	public SteamProfileGamesClient(SteamWebHandler webHandler, ILogger<SteamProfileGamesClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// Collects per-game playtime for the given SteamID. Throws when the page
	/// cannot be fetched or no embedded <c>rgGames</c> payload is found — the
	/// callers schedule playtime-targeted boosting, and a silently empty list
	/// would read as "every target already met".
	/// </summary>
	public async Task<IReadOnlyList<GamePlaytime>> GetPlaytimesAsync(
		ulong steamId,
		CancellationToken cancellationToken = default)
	{
		var url = new Uri(
			$"https://steamcommunity.com/profiles/{steamId.ToString(CultureInfo.InvariantCulture)}/games/?tab=all&l=english");
		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			throw new InvalidOperationException(
				$"Failed to fetch games tab for SteamID {steamId} (HTTP {response.StatusCodeNumber})");
		}

		var result = ParseEmbeddedGamesJson(response.Body);
		_logger.LogInformation(
			"Parsed {Count} games with playtime data for SteamID {SteamId}",
			result.Count, steamId);
		return result;
	}

	/// <summary>
	/// Extracts the JSON array embedded as <c>var rgGames = [...];</c> and maps
	/// it to playtime records. Every occurrence of the assignment marker is a
	/// candidate — prose elsewhere on the page may quote the marker — and a
	/// candidate wins only if a JSON array follows. The array itself is located
	/// by a bracket/quote-balanced scan rather than slicing at the first
	/// terminator: game names may contain semicolons, brackets or escaped
	/// quotes. Structure cross-confirmed (2026-09-18) against independent
	/// consumers of the live page — see the TestData fixture header.
	/// </summary>
	internal static IReadOnlyList<GamePlaytime> ParseEmbeddedGamesJson(string html)
	{
		ArgumentNullException.ThrowIfNull(html);

		const string marker = "var rgGames = ";
		int searchFrom = 0;
		while (true)
		{
			int markerIndex = html.IndexOf(marker, searchFrom, StringComparison.Ordinal);
			if (markerIndex < 0)
			{
				throw new InvalidOperationException(
					"Games tab payload not found: no 'var rgGames =' followed by a JSON array — structure drifted?");
			}

			int start = markerIndex + marker.Length;
			int arrayEnd;
			try
			{
				arrayEnd = ScanBalancedJsonArray(html, start);
			}
			catch (InvalidOperationException)
			{
				// Not an array here; keep looking for the real payload.
				searchFrom = start;
				continue;
			}

			using var document = JsonDocument.Parse(html[start..arrayEnd]);

			var games = new List<GamePlaytime>();
			foreach (var element in document.RootElement.EnumerateArray())
			{
				if (element.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				long appId = element.TryGetProperty("appid", out var appIdProperty)
					&& appIdProperty.ValueKind == JsonValueKind.Number
					&& appIdProperty.TryGetInt64(out var parsedAppId)
						? parsedAppId
						: 0;
				if (appId <= 0 || appId > uint.MaxValue)
				{
					continue;
				}

				string? name = element.TryGetProperty("name", out var nameProperty)
					&& nameProperty.ValueKind == JsonValueKind.String
						? nameProperty.GetString()
						: null;

				// hours_forever is display-formatted ("1,234.5"); missing or
				// null means never played.
				double hours = 0;
				if (element.TryGetProperty("hours_forever", out var hoursProperty)
					&& hoursProperty.ValueKind == JsonValueKind.String
					&& hoursProperty.GetString() is { } raw
					&& double.TryParse(
						raw.Replace(",", string.Empty, StringComparison.Ordinal),
						NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHours))
				{
					hours = parsedHours;
				}

				games.Add(new GamePlaytime((uint)appId, name, hours));
			}

			return games;
		}
	}

	/// <summary>
	/// Returns the index just past the JSON array that starts at
	/// <paramref name="start"/> (leading whitespace skipped), tracking string
	/// literals and escapes so payload values can safely contain '[', ']' and
	/// ';'. Throws when the value is not an array or never closes.
	/// </summary>
	private static int ScanBalancedJsonArray(string html, int start)
	{
		int index = start;
		while (index < html.Length && char.IsWhiteSpace(html[index]))
		{
			index++;
		}

		if (index >= html.Length || html[index] != '[')
		{
			throw new InvalidOperationException(
				"Games tab payload is not a JSON array — structure drifted?");
		}

		int depth = 0;
		bool inString = false;
		bool escaped = false;
		for (; index < html.Length; index++)
		{
			char c = html[index];
			if (inString)
			{
				if (escaped)
				{
					escaped = false;
				}
				else if (c == '\\')
				{
					escaped = true;
				}
				else if (c == '"')
				{
					inString = false;
				}
			}
			else if (c == '"')
			{
				inString = true;
			}
			else if (c == '[')
			{
				depth++;
			}
			else if (c == ']')
			{
				depth--;
				if (depth == 0)
				{
					return index + 1;
				}
			}
		}

		throw new InvalidOperationException("Games tab payload array is never closed — structure drifted?");
	}
}
