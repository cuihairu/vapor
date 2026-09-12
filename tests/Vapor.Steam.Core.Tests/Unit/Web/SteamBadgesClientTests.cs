using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class SteamBadgesClientTests
{
	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		public List<Uri> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.RequestUri!);
			return Task.FromResult(Responder(request));
		}
	}

	private static (SteamBadgesClient Client, FakeHttpMessageHandler Fake) Create()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (new SteamBadgesClient(webHandler, NullLogger<SteamBadgesClient>.Instance), fake);
	}

	private static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) => new(status)
	{
		Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
	};

	private static string Row(string title, string appIdCarrier, string? progressSpan, bool titleAsLink = false, string runLink = "steam://run/220")
	{
		var titleHtml = titleAsLink
			? $"<div class=\"badge_title\"><a href=\"https://steamcommunity.com/my/gamecards/440/\">{title}</a></div>"
			: $"<div class=\"badge_title\">{title}</div>";
		var carrier = appIdCarrier.Length > 0 ? appIdCarrier : string.Empty;
		var progress = progressSpan is null ? string.Empty : progressSpan;
		var playgame = runLink.Length > 0
			? $"<div class=\"badge_title_playgame\"><a class=\"btn_green_white_innerfade btn_medium\" href=\"{runLink}\"><span>Play</span></a></div>"
			: string.Empty;
		return $"""
			<div class="badge_row">
				<div class="badge_row_inner">
					<div class="badge_title_row">
						{titleHtml}
						<div class="badge_title_stats">
							<div class="badge_title_stats_content">
								{carrier}
								{progress}
							</div>
							{playgame}
						</div>
					</div>
				</div>
			</div>
			""";
	}

	// --- ParseBadgePage variants ---

	[Fact]
	public void Parse_SingularDropText_CountsOneDrop()
	{
		var html = $"<div class=\"profile_badges_body\">{Row("Portal 2", "<div class=\"card_drop_info_dialog\" id=\"card_drop_info_dialog_620\"></div>", "<span class=\"progress_info_bold\">1 card drop remaining</span>")}</div>";

		var result = SteamBadgesClient.ParseBadgePage(html);

		var drop = Assert.Single(result.CardDrops);
		Assert.Equal(620U, drop.AppId);
		Assert.Equal(1, drop.DropsRemaining);
		Assert.Equal(1, result.MaxPage);
	}

	[Theory]
	[InlineData("2,000 card drops remaining", 2000)]
	[InlineData("6 card drops remaining", 6)]
	public void Parse_DropCountFormats_ParseCleanly(string progressText, int expected)
	{
		var html = Row("Half-Life 2", "card_drop_info_dialog_220", $"<span class=\"progress_info_bold\">{progressText}</span>");

		var result = SteamBadgesClient.ParseBadgePage(html);

		Assert.Equal(expected, result.CardDrops[0].DropsRemaining);
	}

	[Fact]
	public void Parse_ZeroRemaining_Skipped()
	{
		var html = Row("Half-Life 2", "card_drop_info_dialog_220", "<span class=\"progress_info_bold\">0 card drops remaining</span>");

		var result = SteamBadgesClient.ParseBadgePage(html);

		Assert.Empty(result.CardDrops);
	}

	[Fact]
	public void Parse_EventBadgeProgressText_Ignored()
	{
		// Event badges count "items"/other units — must not be read as card drops.
		var html = Row("Summer Sale", string.Empty, "<span class=\"progress_info_bold\">3 of 9 items unlocked</span>");

		var result = SteamBadgesClient.ParseBadgePage(html);

		Assert.Empty(result.CardDrops);
	}

	[Fact]
	public void Parse_MissingAppIdCarrier_Skipped()
	{
		var html = Row("Some Event", string.Empty, "<span class=\"progress_info_bold\">4 card drops remaining</span>", runLink: string.Empty);

		var result = SteamBadgesClient.ParseBadgePage(html);

		Assert.Empty(result.CardDrops);
	}

	[Fact]
	public void Parse_RunLinkFallback_ResolvesAppId()
	{
		// Row with no card_drop_info_dialog id — app id only via steam://run/.
		var html = Row("Portal 2", string.Empty, "<span class=\"progress_info_bold\">1 card drop remaining</span>");

		var result = SteamBadgesClient.ParseBadgePage(html);

		var drop = Assert.Single(result.CardDrops);
		Assert.Equal(220U, drop.AppId);
	}

	[Fact]
	public void Parse_LinkedTitle_ExtractsName()
	{
		var html = Row("Team Fortress 2", "card_drop_info_dialog_440", "<span class=\"progress_info_bold\">2 card drops remaining</span>", titleAsLink: true);

		var result = SteamBadgesClient.ParseBadgePage(html);

		var drop = Assert.Single(result.CardDrops);
		Assert.Equal(440U, drop.AppId);
		Assert.Equal("Team Fortress 2", drop.Name);
	}

	[Fact]
	public void Parse_MultipleRows_SplitsOnOuterBadgeRow()
	{
		var html = new System.Text.StringBuilder("<div class=\"profile_badges_body\">")
			.Append(Row("Half-Life 2", "card_drop_info_dialog_220", "<span class=\"progress_info_bold\">6 card drops remaining</span>"))
			.Append(Row("Portal 2", "card_drop_info_dialog_620", "<span class=\"progress_info_bold\">1 card drop remaining</span>"))
			.Append("</div>")
			.ToString();

		var result = SteamBadgesClient.ParseBadgePage(html);

		Assert.Equal(2, result.CardDrops.Count);
		Assert.Equal(220U, result.CardDrops[0].AppId);
		Assert.Equal(620U, result.CardDrops[1].AppId);
	}

	// --- Client behavior ---

	private static string Page2Html =>
		File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "badges_page_p2.html"));

	[Fact]
	public async Task GetCardDrops_PaginatesAndMergesPages()
	{
		var (client, fake) = Create();
		string page1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "badges_page_p1.html"));
		fake.Responder = request =>
			request.RequestUri!.Query.Contains("p=2")
				? Html(Page2Html)
				: Html(page1);

		var drops = await client.GetCardDropsAsync(76561198000000000UL);

		Assert.Equal(3, drops.Count);
		Assert.Equal([220U, 620U, 550U], drops.Select(d => d.AppId).ToArray());
	}

	[Fact]
	public async Task GetCardDrops_RequestsEnglishLocaleAndPages()
	{
		var (client, fake) = Create();
		string page1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "badges_page_p1.html"));
		fake.Responder = request =>
			request.RequestUri!.Query.Contains("p=2")
				? Html(Page2Html)
				: Html(page1);

		await client.GetCardDropsAsync(76561198000000000UL);

		Assert.All(fake.Requests, uri =>
		{
			Assert.Contains("l=english", uri.Query, StringComparison.Ordinal);
			Assert.Contains("/profiles/76561198000000000/badges/", uri.AbsolutePath, StringComparison.Ordinal);
		});
		Assert.Equal(2, fake.Requests.Count);
		Assert.Contains("p=1", fake.Requests[0].Query, StringComparison.Ordinal);
		Assert.Contains("p=2", fake.Requests[1].Query, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetCardDrops_LaterPageFailure_SkipsMissingPage()
	{
		var (client, fake) = Create();
		string page1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "badges_page_p1.html"));
		fake.Responder = request =>
			request.RequestUri!.Query.Contains("p=2")
				? Html("<server error>", HttpStatusCode.InternalServerError)
				: Html(page1);

		var drops = await client.GetCardDropsAsync(76561198000000000UL);

		// Page 1 results survive even when a later page fails.
		Assert.Equal(2, drops.Count);
		Assert.DoesNotContain(drops, d => d.AppId == 550U);
	}

	[Fact]
	public async Task GetCardDrops_FirstPageFailure_Throws()
	{
		var (client, fake) = Create();
		fake.Responder = _ => Html("<error>", HttpStatusCode.NotFound);

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => client.GetCardDropsAsync(76561198000000000UL));
	}
}
