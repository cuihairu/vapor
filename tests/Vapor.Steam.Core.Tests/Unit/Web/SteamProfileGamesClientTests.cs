using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Unit tests for the rgGames payload extraction. The balancing scanner is
/// exercised against payloads that would break a naive slice-at-first-
/// terminator approach, and the fail-loudly contract (throw on missing or
/// malformed payload — never silently report zero playtime) is pinned here.
/// </summary>
public sealed class SteamProfileGamesClientTests
{
	private static IReadOnlyList<GamePlaytime> Parse(string html) =>
		SteamProfileGamesClient.ParseEmbeddedGamesJson(html);

	[Fact]
	public void MissingPayload_Throws()
	{
		// A page that fetched fine but carries no rgGames must fail loudly:
		// an empty list would read as "every playtime target already met".
		var ex = Assert.Throws<InvalidOperationException>(() => Parse("<html><body>login please</body></html>"));
		Assert.Contains("rgGames", ex.Message);
	}

	[Fact]
	public void NonArrayPayload_Throws()
	{
		Assert.Throws<InvalidOperationException>(() =>
			Parse("<script>var rgGames = {};</script>"));
	}

	[Fact]
	public void UnclosedArray_Throws()
	{
		Assert.Throws<InvalidOperationException>(() =>
			Parse("<script>var rgGames = [ {\"appid\":220</script>"));
	}

	[Fact]
	public void EmptyArray_ParsesToEmptyList()
	{
		// An owned-but-empty library is legitimate; distinguishable from a
		// missing payload (which throws).
		var result = Parse("<script>var rgGames = [];</script>");

		Assert.Empty(result);
	}

	[Fact]
	public void PayloadWithTerminatorsInNames_ParsesCompletely()
	{
		// Semicolons, brackets and escaped quotes inside string values must
		// not truncate the scan; content after the array must be ignored.
		const string html = """
			<script>
			var rgGames = [
				{"appid":1250,"name":"Killing Floor \"Gold\"; rev [1]","hours_forever":"0.2"},
				{"appid":440,"name":"Puzzle; the [\"bracket\"] game","hours_forever":"12,345.6"}
			];
			$J(document).ready( function() { Init( [440] ); } );
			</script>
			""";

		var result = Parse(html);

		Assert.Equal(2, result.Count);
		Assert.Equal(1250U, result[0].AppId);
		Assert.Equal("Killing Floor \"Gold\"; rev [1]", result[0].Name);
		Assert.Equal(0.2, result[0].Hours);
		Assert.Equal(12345.6, result[1].Hours);
	}

	[Fact]
	public void NullAndMissingHours_BecomeZero()
	{
		const string html = """
			<script>
			var rgGames = [
				{"appid":440,"name":"Team Fortress 2","hours_forever":null},
				{"appid":730,"name":"Counter-Strike 2"}
			];
			</script>
			""";

		var result = Parse(html);

		Assert.Equal(2, result.Count);
		Assert.Equal(0, result[0].Hours);
		Assert.Equal(0, result[1].Hours);
	}

	[Fact]
	public void InvalidEntries_AreSkipped()
	{
		// Missing/non-numeric/zero appids and non-object entries carry no
		// playable identity; malformed hours strings degrade to zero rather
		// than dropping the game.
		const string html = """
			<script>
			var rgGames = [
				{"name":"no appid","hours_forever":"5"},
				{"appid":"abc","hours_forever":"5"},
				{"appid":0,"hours_forever":"5"},
				"not an object",
				{"appid":21475000000,"name":"beyond uint32","hours_forever":"1"},
				{"appid":220,"name":"Half-Life 2","hours_forever":"not a number"}
			];
			</script>
			""";

		var result = Parse(html);

		var only = Assert.Single(result);
		Assert.Equal(220U, only.AppId);
		Assert.Equal(0, only.Hours);
	}

	[Fact]
	public void QuotedMarkerNotFollowedByArray_IsSkippedForRealPayload()
	{
		// Prose may quote the assignment marker verbatim (our own fixture
		// header does); only a candidate followed by a JSON array wins.
		const string html = """
			<!-- docs: located by "var rgGames = ", parses appid/name -->
			<script>var rgGames = [{"appid":620,"name":"Portal 2","hours_forever":"36.7"}];</script>
			""";

		var result = Parse(html);

		var only = Assert.Single(result);
		Assert.Equal(620U, only.AppId);
	}

	[Fact]
	public void PayloadAfterOtherScripts_IsLocatedCorrectly()
	{
		// rgGames is not the first script on the live page.
		const string html = """
			<script>var rgSupportedLanguages = {"english": 1};</script>
			<script>var rgGames = [{"appid":620,"name":"Portal 2","hours_forever":"36.7"}];</script>
			""";

		var result = Parse(html);

		var only = Assert.Single(result);
		Assert.Equal(620U, only.AppId);
		Assert.Equal(36.7, only.Hours);
	}
}
