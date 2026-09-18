using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// One achievement of one game, as listed by the community per-game stats page.
/// <see cref="ApiName"/> is inferred from the row image's file name (the page
/// itself only carries display names); a Steam schema where the icon file name
/// diverges from the achievement API name yields a name Steam will reject at
/// write time — the write paths report that as a per-item failure.
/// </summary>
public sealed record AchievementInfo(
	string ApiName,
	string? DisplayName,
	string? Description,
	bool Unlocked,
	string? IconUrl);

/// <summary>
/// Result of parsing one achievements page: every achievement row plus the
/// header summary ("N of M (P%) achievements earned").
/// </summary>
public sealed record AchievementPageResult(
	IReadOnlyList<AchievementInfo> Achievements,
	int UnlockedCount,
	int TotalCount);

/// <summary>
/// Fetches and parses the community per-game achievements page
/// (<c>steamcommunity.com/profiles/{steamId}/stats/{appId}?tab=achievements</c>).
/// The page lists every achievement of the game with its unlock state (locked
/// rows use the greyscale <c>_bw</c> icon variant); no Web API key is involved.
/// Requires a logged-in web session on the underlying <see cref="SteamWebHandler"/>
/// (the request forces <c>l=english</c> so names match the parser regardless of
/// the account's display language).
/// </summary>
public sealed partial class SteamAchievementsClient
{
	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamAchievementsClient> _logger;

	public SteamAchievementsClient(SteamWebHandler webHandler, ILogger<SteamAchievementsClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// Fetches the achievements page for the given SteamID and app. A failed
	/// fetch throws (the action layer turns that into a task failure); a
	/// parse-level anomaly (no rows, no summary) is reported as an empty page,
	/// never a thrown exception.
	/// </summary>
	public async Task<AchievementPageResult> GetAchievementsAsync(
		ulong steamId,
		uint appId,
		CancellationToken cancellationToken = default)
	{
		var url = new Uri(
			$"https://steamcommunity.com/profiles/{steamId.ToString(CultureInfo.InvariantCulture)}/stats/{appId.ToString(CultureInfo.InvariantCulture)}?tab=achievements&l=english");
		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning(
				"Achievements page fetch failed for SteamID {SteamId} app {AppId} (HTTP {Status})",
				steamId, appId, response.IsSuccess ? "empty body" : response.StatusCode.ToString());
			throw new InvalidOperationException(
				$"Failed to fetch achievements page for SteamID {steamId} app {appId}");
		}

		var result = ParseAchievementsPage(response.Body);
		_logger.LogInformation(
			"Parsed {Unlocked}/{Total} achievements for SteamID {SteamId} app {AppId}",
			result.UnlockedCount, result.TotalCount, steamId, appId);
		return result;
	}

	/// <summary>
	/// Parses the achievements region of a per-game stats page: the
	/// "N of M (P%) achievements earned" summary line and one row per
	/// achievement. Rows without an image yield no API name and are skipped
	/// (the write paths need the name; a display-only row is unusable here).
	/// </summary>
	public static AchievementPageResult ParseAchievementsPage(string html)
	{
		int unlocked = 0, total = 0;
		var summary = SummaryRegex().Match(html);
		if (summary.Success)
		{
			unlocked = int.TryParse(summary.Groups["unlocked"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedUnlocked) ? parsedUnlocked : 0;
			total = int.TryParse(summary.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedTotal) ? parsedTotal : 0;
		}

		var achievements = new List<AchievementInfo>();
		foreach (string rowHtml in SplitAchievementRows(html))
		{
			var img = RowImageRegex().Match(rowHtml);
			if (!img.Success)
			{
				continue;
			}

			// Unlock state comes from the achieveUnlockTime block (present only
			// on unlocked rows, carrying the "Unlocked <date>" text) — NOT from
			// the icon: progress-type achievements keep a colored icon even
			// when locked, so a "_bw"-absence check would misread them.
			// API name stays an inference from the icon file name (the "_bw"
			// greyscale suffix stripped) — hash-named icons yield a name Steam
			// will reject at write time, reported as a per-item failure there.
			string fileName = img.Groups["file"].Value;
			bool isLockedVariant = fileName.EndsWith("_bw", StringComparison.OrdinalIgnoreCase);
			string stem = isLockedVariant ? fileName[..^3] : fileName;

			achievements.Add(new AchievementInfo(
				ApiName: stem.ToUpperInvariant(),
				DisplayName: ExtractText(rowHtml, RowNameRegex()),
				Description: ExtractText(rowHtml, RowDescriptionRegex()),
				Unlocked: UnlockTimeRegex().IsMatch(rowHtml),
				IconUrl: img.Groups["url"].Value));
		}

		if (total == 0 && achievements.Count > 0)
		{
			// No usable summary line — the rows themselves are the count.
			total = achievements.Count;
			unlocked = achievements.Count(a => a.Unlocked);
		}

		return new AchievementPageResult(achievements, unlocked, total);
	}

	private static string? ExtractText(string rowHtml, Regex regex)
	{
		var match = regex.Match(rowHtml);
		if (!match.Success)
		{
			return null;
		}

		string raw = match.Groups["text"].Value.Trim();
		return raw.Length == 0 ? null : raw;
	}

	private static IEnumerable<string> SplitAchievementRows(string html)
	{
		var starts = AchievementRowStartRegex().Matches(html);
		for (int i = 0; i < starts.Count; i++)
		{
			int start = starts[i].Index;
			int end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
			yield return html.Substring(start, end - start);
		}
	}

	// Header summary: "0 of 15 (0%) achievements earned:".
	[GeneratedRegex(@"(?<unlocked>\d+)\s+of\s+(?<total>\d+)\s+\(\d+%\)\s+achievements\s+earned", RegexOptions.CultureInvariant)]
	private static partial Regex SummaryRegex();

	// Unlocked rows carry <div class="achieveUnlockTime">Unlocked May 12, 2010 @ 6:33pm...</div>;
	// locked rows never do (l=english keeps the "Unlocked" wording fixed).
	[GeneratedRegex(@"class=""achieveUnlockTime""[^>]*>\s*Unlocked\s", RegexOptions.CultureInvariant)]
	private static partial Regex UnlockTimeRegex();

	// Row boundary: the outer achieveRow div start (\b keeps achieveImgHolder
	// and similar from matching — underscore is a word character).
	[GeneratedRegex(@"<div[^>]*class=""[^""]*\bachieveRow\b[^""]*""", RegexOptions.CultureInvariant)]
	private static partial Regex AchievementRowStartRegex();

	// Row icon: <img src=".../community_assets/images/apps/400/portal_beat_game_bw.jpg">.
	// The file name (without extension) is the API-name hint; the "_bw"
	// suffix marks the locked greyscale variant.
	[GeneratedRegex(@"<img[^>]*src=""(?<url>[^""]*/apps/\d+/(?<file>[^""/]+?)\.(?:jpg|png))""", RegexOptions.CultureInvariant)]
	private static partial Regex RowImageRegex();

	[GeneratedRegex(@"<h3[^>]*>(?<text>[^<]{1,200}?)</h3>", RegexOptions.CultureInvariant)]
	private static partial Regex RowNameRegex();

	[GeneratedRegex(@"<h5[^>]*>(?<text>[^<]{1,400}?)</h5>", RegexOptions.CultureInvariant)]
	private static partial Regex RowDescriptionRegex();
}
