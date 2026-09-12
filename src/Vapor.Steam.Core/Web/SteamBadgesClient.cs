using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// One app's remaining trading card drop count, as reported by the community
/// badges overview page. This page is the only source for "how many drops are
/// left" — the Web API has no equivalent field.
/// </summary>
public sealed record CardDropInfo(uint AppId, string? Name, int DropsRemaining);

/// <summary>
/// Result of parsing a single badges page: the card games found on it plus the
/// total page count advertised by the pagination control.
/// </summary>
public sealed record BadgePageResult(IReadOnlyList<CardDropInfo> CardDrops, int MaxPage);

/// <summary>
/// Fetches and parses the community badges overview page
/// (<c>steamcommunity.com/profiles/{steamId}/badges</c>) to enumerate games
/// with remaining card drops. Requires a logged-in web session on the
/// underlying <see cref="SteamWebHandler"/>; the page is not publicly
/// accessible. The request forces <c>l=english</c> so the drop-count text
/// matches the parser regardless of the account's display language.
/// </summary>
public sealed partial class SteamBadgesClient
{
	// Defensive cap so a broken pagination marker can't cause unbounded paging.
	private const int MaxPagesPerFetch = 50;

	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamBadgesClient> _logger;

	public SteamBadgesClient(SteamWebHandler webHandler, ILogger<SteamBadgesClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// Collects card drop info across all badges pages for the given SteamID.
	/// Page 1 failure throws; failures on later pages are logged and skipped
	/// so a single flaky page doesn't discard the whole result.
	/// </summary>
	public async Task<IReadOnlyList<CardDropInfo>> GetCardDropsAsync(
		ulong steamId,
		CancellationToken cancellationToken = default)
	{
		var firstPage = await TryFetchPageAsync(steamId, 1, cancellationToken).ConfigureAwait(false)
			?? throw new InvalidOperationException($"Failed to fetch badges page (page 1) for SteamID {steamId}");

		var results = new List<CardDropInfo>(firstPage.CardDrops);
		int lastPage = Math.Min(firstPage.MaxPage, MaxPagesPerFetch);

		for (int page = 2; page <= lastPage; page++)
		{
			var nextPage = await TryFetchPageAsync(steamId, page, cancellationToken).ConfigureAwait(false);
			if (nextPage != null)
			{
				results.AddRange(nextPage.CardDrops);
			}
		}

		_logger.LogInformation(
			"Collected {Count} apps with remaining card drops across {Pages} page(s) for SteamID {SteamId}",
			results.Count, lastPage, steamId);
		return results;
	}

	private async Task<BadgePageResult?> TryFetchPageAsync(ulong steamId, int page, CancellationToken cancellationToken)
	{
		var url = new Uri(
			$"https://steamcommunity.com/profiles/{steamId.ToString(CultureInfo.InvariantCulture)}/badges/?l=english&p={page.ToString(CultureInfo.InvariantCulture)}");
		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			_logger.LogWarning(
				"Badges page {Page} fetch failed for SteamID {SteamId} (HTTP {Status})",
				page, steamId, response.StatusCodeNumber);
			return null;
		}

		var result = ParseBadgePage(response.Body);
		_logger.LogDebug(
			"Parsed badges page {Page}/{MaxPage} for SteamID {SteamId}: {Count} apps with drops",
			page, result.MaxPage, steamId, result.CardDrops.Count);
		return result;
	}

	/// <summary>
	/// Parses one badges overview page. The HTML structure is cross-confirmed
	/// against ArchiSteamFarm's CardsFarmer.cs, zevnda/steam-game-idler's
	/// card_farming scraper.rs and the "Steam :: Badge - Remaining Card Drops"
	/// userscript; see the TestData fixture for the pinned skeleton.
	/// Non-card badges (event, level, collection) are skipped: they either lack
	/// both app-id carriers or their progress text doesn't say "card drops
	/// remaining". Rows are split on <c>badge_row</c> class starts — the word
	/// boundary keeps nested <c>badge_row_inner</c> etc. from splitting rows.
	/// </summary>
	internal static BadgePageResult ParseBadgePage(string html)
	{
		ArgumentNullException.ThrowIfNull(html);

		var drops = new List<CardDropInfo>();
		foreach (var row in SplitBadgeRows(html))
		{
			uint appId = 0;
			var dialogMatch = DialogAppIdRegex().Match(row);
			if (dialogMatch.Success)
			{
				uint.TryParse(dialogMatch.Groups["appid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out appId);
			}

			if (appId == 0)
			{
				// Fallback carrier: the green "Play" button's steam://run link.
				var runMatch = RunLinkAppIdRegex().Match(row);
				if (runMatch.Success)
				{
					uint.TryParse(runMatch.Groups["appid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out appId);
				}
			}

			if (appId == 0)
			{
				// No app-id carrier: not a card-dropping game badge.
				continue;
			}

			var dropMatch = CardDropsRemainingRegex().Match(row);
			if (!dropMatch.Success)
			{
				// No remaining-drop text: badge fully earned or not a card drop counter.
				continue;
			}

			int remaining = int.Parse(
				dropMatch.Groups["drops"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
				NumberStyles.Integer, CultureInfo.InvariantCulture);
			if (remaining <= 0)
			{
				continue;
			}

			drops.Add(new CardDropInfo(appId, ExtractName(row), remaining));
		}

		int maxPage = 1;
		foreach (Match match in PageLinkNumberRegex().Matches(html))
		{
			if (int.TryParse(match.Groups["page"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pageNumber)
				&& pageNumber > maxPage)
			{
				maxPage = pageNumber;
			}
		}

		return new BadgePageResult(drops, maxPage);
	}

	private static IEnumerable<string> SplitBadgeRows(string html)
	{
		var starts = BadgeRowStartRegex().Matches(html);
		for (int i = 0; i < starts.Count; i++)
		{
			int start = starts[i].Index;
			int end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
			yield return html.Substring(start, end - start);
		}
	}

	/// <summary>
	/// Best-effort name extraction from the row's <c>badge_title</c> block:
	/// prefers the inner link text, falls back to loose text before the next
	/// tag; returns null when neither yields usable text. The parsers this
	/// mirrors (steam-game-idler) also strip the overlay "View details" text.
	/// </summary>
	private static string? ExtractName(string rowHtml)
	{
		string? raw = null;
		var linkMatch = BadgeTitleLinkRegex().Match(rowHtml);
		if (linkMatch.Success)
		{
			raw = linkMatch.Groups["name"].Value;
		}
		else
		{
			var textMatch = BadgeTitleTextRegex().Match(rowHtml);
			if (textMatch.Success)
			{
				raw = textMatch.Groups["name"].Value;
			}
		}

		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		raw = raw.Replace("View details", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
		return raw.Length == 0 ? null : raw;
	}

	// Row boundary: the outer badge_row div start. \b prevents matching the
	// badge_row_inner / badge_row_actions variants (underscore is a word char).
	[GeneratedRegex(@"<div[^>]*class=""[^""]*\bbadge_row\b[^""]*""", RegexOptions.CultureInvariant)]
	private static partial Regex BadgeRowStartRegex();

	// App-id carriers (ASF uses the dialog id; steam-game-idler the run link):
	// <div class="card_drop_info_dialog" id="card_drop_info_dialog_220_...">,
	// <a class="btn_green_white_innerfade ..." href="steam://run/220">.
	[GeneratedRegex(@"card_drop_info_dialog_(?<appid>\d+)", RegexOptions.CultureInvariant)]
	private static partial Regex DialogAppIdRegex();

	[GeneratedRegex(@"steam://run/(?<appid>\d+)", RegexOptions.CultureInvariant)]
	private static partial Regex RunLinkAppIdRegex();

	// Drop counter text: <span class="progress_info_bold">6 card drops
	// remaining</span> (singular "card drop" included).
	[GeneratedRegex(@"progress_info_bold[^>]*>[^<]*?(?<drops>\d[\d,]*)\s+card\s+drops?\s+remaining", RegexOptions.CultureInvariant)]
	private static partial Regex CardDropsRemainingRegex();

	[GeneratedRegex(@"badge_title\b[^>]*>\s*<a[^>]*>(?<name>[^<]{1,120}?)</a>", RegexOptions.CultureInvariant)]
	private static partial Regex BadgeTitleLinkRegex();

	[GeneratedRegex(@"badge_title\b[^>]*>\s*(?<name>[^<]{1,120}?)\s*<", RegexOptions.CultureInvariant)]
	private static partial Regex BadgeTitleTextRegex();

	// Pagination: <a class="pagelink" href="?p=2">2</a> (container "pageLinks"
	// doesn't match — different casing).
	[GeneratedRegex(@"class=""[^""]*\bpagelink\b[^""]*""[^>]*>\s*(?<page>\d+)", RegexOptions.CultureInvariant)]
	private static partial Regex PageLinkNumberRegex();
}
