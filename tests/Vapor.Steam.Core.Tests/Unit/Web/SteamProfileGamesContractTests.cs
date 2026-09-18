using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Contract tests: replay the games tab fixture (TestData/games_tab_all.html)
/// through the parser. The live page is login-gated, so the fixture is a
/// constructed skeleton whose embedded-data contract is cross-confirmed
/// against four independent consumers of the live page (see the fixture
/// header). A failing test here means the upstream structure drifted or the
/// parser broke — re-record a real page and update the fixture.
/// </summary>
public sealed class SteamProfileGamesContractTests
{
	private static IReadOnlyList<GamePlaytime> Replay() =>
		SteamProfileGamesClient.ParseEmbeddedGamesJson(
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "games_tab_all.html")));

	[Fact]
	public void Contract_ParsesAllGamesWithHours()
	{
		var result = Replay();

		Assert.Equal(5, result.Count);

		Assert.Equal(220U, result[0].AppId);
		Assert.Equal("Half-Life 2", result[0].Name);
		Assert.Equal(1234.5, result[0].Hours); // comma-formatted payload

		Assert.Equal(620U, result[1].AppId);
		Assert.Equal("Portal 2", result[1].Name);
		Assert.Equal(36.7, result[1].Hours);
	}

	[Fact]
	public void Contract_NeverPlayedGamesReportZeroHours()
	{
		var result = Replay();

		var tf2 = Assert.Single(result, g => g.AppId == 440U); // hours_forever: null
		Assert.Equal("Team Fortress 2", tf2.Name);
		Assert.Equal(0, tf2.Hours);

		var cs2 = Assert.Single(result, g => g.AppId == 730U); // field absent
		Assert.Equal("Counter-Strike 2", cs2.Name);
		Assert.Equal(0, cs2.Hours);
	}

	[Fact]
	public void Contract_AdversarialNameSurvivesBalancedScan()
	{
		var result = Replay();

		// The name carries escaped quotes, a semicolon and brackets; trailing
		// script statements after the array must be ignored.
		var killingFloor = Assert.Single(result, g => g.AppId == 1250U);
		Assert.Equal("Killing Floor \"Gold\"; rev [1]", killingFloor.Name);
		Assert.Equal(0.2, killingFloor.Hours);
	}
}
