using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Contract tests: replay the badges overview page fixture
/// (TestData/badges_page_p1.html / p2) through the parser. The live page is
/// login-gated, so the fixture is a constructed skeleton whose structure is
/// cross-confirmed against ASF / steam-game-idler / userscript parsers (see
/// the fixture header). A failing test here means the upstream structure
/// drifted or the parser broke — re-record a real page and update the fixture.
/// </summary>
public sealed class SteamBadgesPageContractTests
{
	private const string Page1Fixture = "badges_page_p1.html";
	private const string Page2Fixture = "badges_page_p2.html";

	private static BadgePageResult Replay(string fileName) =>
		SteamBadgesClient.ParseBadgePage(
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName)));

	[Fact]
	public void Page1Contract_ParsesCardGamesAndSkipsNonCardBadges()
	{
		var result = Replay(Page1Fixture);

		// Half-Life 2 (dialog id + run link carriers, 6 drops) and Portal 2
		// (run-link-only carrier, singular "1 card drop remaining").
		Assert.Equal(2, result.CardDrops.Count);

		Assert.Equal(220U, result.CardDrops[0].AppId);
		Assert.Equal("Half-Life 2", result.CardDrops[0].Name);
		Assert.Equal(6, result.CardDrops[0].DropsRemaining);

		Assert.Equal(620U, result.CardDrops[1].AppId);
		Assert.Equal("Portal 2", result.CardDrops[1].Name);
		Assert.Equal(1, result.CardDrops[1].DropsRemaining);

		Assert.Equal(2, result.MaxPage);
	}

	[Fact]
	public void Page1Contract_SkipsCompletedGameAndEventBadgeRows()
	{
		var result = Replay(Page1Fixture);

		// Completed TF2 row has an app-id carrier but no "card drops remaining"
		// text; the event badge row counts "3 of 9 items unlocked". Neither is
		// a card game with remaining drops.
		Assert.DoesNotContain(result.CardDrops, d => d.AppId == 440U);
		Assert.DoesNotContain(result.CardDrops, d => d.AppId == 0U);
		Assert.DoesNotContain(result.CardDrops, d => d.Name != null && d.Name.Contains("Summer Sale"));
	}

	[Fact]
	public void Page2Contract_ParsesRemainingPage()
	{
		var result = Replay(Page2Fixture);

		var l4d2 = Assert.Single(result.CardDrops);
		Assert.Equal(550U, l4d2.AppId);
		Assert.Equal("Left 4 Dead 2", l4d2.Name);
		Assert.Equal(2, l4d2.DropsRemaining);
		Assert.Equal(2, result.MaxPage);
	}
}
