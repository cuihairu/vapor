using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Contract tests: replays the recorded per-game achievements page fixture
/// (TestData/achievements_page_400.html — a verbatim 2026-09-18 capture of the
/// achievements region of a public Portal stats page). A failing test here
/// means the upstream page structure drifted or the parser broke — re-record
/// a real page and update the fixture.
/// </summary>
public sealed class SteamAchievementsPageContractTests
{
	private const string LockedFixture = "achievements_page_400.html";
	private const string PartialFixture = "achievements_page_400_partial.html";

	private static AchievementPageResult Replay(string fileName)
		=> SteamAchievementsClient.ParseAchievementsPage(
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName)));

	[Fact]
	public void SummaryLine_ParsesCounts()
	{
		var result = Replay(LockedFixture);

		Assert.Equal(0, result.UnlockedCount);
		Assert.Equal(15, result.TotalCount);
	}

	[Fact]
	public void Rows_AllFifteenParsedWithInferredApiNames()
	{
		var result = Replay(LockedFixture);

		Assert.Equal(15, result.Achievements.Count);
		Assert.All(result.Achievements, a => Assert.False(string.IsNullOrEmpty(a.ApiName)));
		// The recorded page is 0/15 unlocked — no row carries an unlock-time block.
		Assert.All(result.Achievements, a => Assert.False(a.Unlocked));
	}

	[Fact]
	public void Row_FullFields_CarriesNameDescriptionIcon()
	{
		var result = Replay(LockedFixture);

		var labRat = result.Achievements.Single(a => a.DisplayName == "Lab Rat");
		Assert.Equal("PORTAL_GETPORTALGUNS", labRat.ApiName); // portal_getportalguns_bw.jpg, "_bw" stripped
		Assert.Equal("Acquire the fully powered Aperture Science Handheld Portal Device.", labRat.Description);
		Assert.False(labRat.Unlocked);
		Assert.Contains("portal_getportalguns_bw.jpg", labRat.IconUrl);
	}

	[Fact]
	public void Row_ProgressAchievement_LockedDespiteColoredIcon()
	{
		// Regression anchor: "Transmission Received" (a progress achievement)
		// keeps a colored hash-named icon even when locked — unlock state must
		// come from the unlock-time block, never from the icon variant. Its
		// inferred API name is the hash (a write attempt with it would fail on
		// Steam, reported as a per-item failure — documented limitation).
		var result = Replay(LockedFixture);

		var transmission = result.Achievements.Single(a => a.DisplayName == "Transmission Received");
		Assert.Equal("D3A7FBCA2549D043955D33CB5EAF30259DCF41AC", transmission.ApiName);
		Assert.False(transmission.Unlocked);
		Assert.Equal("..?", transmission.Description);
	}

	[Fact]
	public void PartialFixture_UnlockStateComesFromUnlockTimeBlock()
	{
		// Second recorded capture (7/15 unlocked) — the cross-sample proving
		// the unlock-time block tracks reality while icons alone do not.
		var result = Replay(PartialFixture);

		Assert.Equal(7, result.UnlockedCount);
		Assert.Equal(15, result.TotalCount);
		Assert.True(result.Achievements.Single(a => a.DisplayName == "Lab Rat").Unlocked);
		Assert.True(result.Achievements.Single(a => a.ApiName == "PORTAL_BEAT_GAME").Unlocked);
		Assert.False(result.Achievements.Single(a => a.DisplayName == "Terminal Velocity").Unlocked);
		Assert.False(result.Achievements.Single(a => a.DisplayName == "Transmission Received").Unlocked);
	}

	[Fact]
	public void Parse_UnlockedRow_CarriesUnlockTimeBlock()
	{
		// The recorded fixtures cover the live shape; this inline page pins the
		// parser's exact anchor (achieveUnlockTime) for minimal reproductions.
		const string html = """
			<div id="topSummaryAchievements"><div>1 of 2 (50%) achievements earned:</div></div>
			<div id="personalAchieve" class="achievements_list ">
				<div role="button" class="achieveRow">
					<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/portal_beat_game.jpg"></div>
					<div class="achieveTxtHolder">
						<div class="achieveTxt">
							<h3 class="ellipsis">End of Story</h3>
							<h5 class="ellipsis">You beat the game.</h5>
						</div>
						<div class="achieveUnlockTime">
							Unlocked May 12, 2010 @ 6:33pm<br/>
						</div>
					</div>
				</div>
				<div role="button" class="achieveRow">
					<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/portal_longjump_bw.jpg"></div>
					<div class="achieveTxtHolder"><div class="achieveTxt">
						<h3 class="ellipsis">Not Yet</h3>
					</div></div>
				</div>
			</div>
			""";

		var result = SteamAchievementsClient.ParseAchievementsPage(html);

		Assert.Equal(1, result.UnlockedCount);
		Assert.Equal(2, result.TotalCount);
		Assert.True(result.Achievements.Single(a => a.ApiName == "PORTAL_BEAT_GAME").Unlocked);
		Assert.False(result.Achievements.Single(a => a.ApiName == "PORTAL_LONGJUMP").Unlocked);
	}

	[Fact]
	public void Parse_EmptyPage_YieldsEmptyResult()
	{
		var result = SteamAchievementsClient.ParseAchievementsPage("<html><body>no achievements here</body></html>");

		Assert.Empty(result.Achievements);
		Assert.Equal(0, result.UnlockedCount);
		Assert.Equal(0, result.TotalCount);
	}

	[Fact]
	public void Parse_RowsWithoutSummary_FallBackToRowCount()
	{
		const string html = """
			<div id="personalAchieve" class="achievements_list ">
				<div role="button" class="achieveRow">
					<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/only_ach_bw.jpg"></div>
					<div class="achieveTxtHolder"><div class="achieveTxt"><h3 class="ellipsis">Only One</h3></div></div>
				</div>
			</div>
			""";

		var result = SteamAchievementsClient.ParseAchievementsPage(html);

		Assert.Single(result.Achievements);
		Assert.Equal(1, result.TotalCount);
		Assert.Equal(0, result.UnlockedCount); // no unlock-time block = locked
	}

	[Fact]
	public void Parse_SummaryUnlockedCountOverflows_ClampsToZero()
	{
		// An 11-digit unlocked count overflows int.TryParse → clamped to 0; the
		// summary total stays authoritative (15), so the row recount at the end
		// never runs and the genuinely unlocked row is not counted.
		const string html = """
			<div id="topSummaryAchievements"><div>99999999999 of 15 (0%) achievements earned:</div></div>
			<div id="personalAchieve" class="achievements_list ">
				<div role="button" class="achieveRow">
					<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/portal_beat_game.jpg"></div>
					<div class="achieveTxtHolder">
						<div class="achieveTxt">
							<h3 class="ellipsis">End of Story</h3>
						</div>
						<div class="achieveUnlockTime">
							Unlocked May 12, 2010 @ 6:33pm<br/>
						</div>
					</div>
				</div>
			</div>
			""";

		var result = SteamAchievementsClient.ParseAchievementsPage(html);

		Assert.Equal(0, result.UnlockedCount);
		Assert.Equal(15, result.TotalCount);
		var row = Assert.Single(result.Achievements);
		Assert.Equal("PORTAL_BEAT_GAME", row.ApiName); // row parsed…
		Assert.True(row.Unlocked); // …and genuinely unlocked, yet not recounted
	}

	[Fact]
	public void Parse_SummaryTotalCountOverflows_ClampsToZeroWithoutRecount()
	{
		// The total overflow clamps to 0 — but with zero rows the recount guard
		// (total == 0 && rows exist) must not run, keeping the clamped values.
		const string html = """
			<div id="topSummaryAchievements"><div>3 of 99999999999 (0%) achievements earned:</div></div>
			""";

		var result = SteamAchievementsClient.ParseAchievementsPage(html);

		Assert.Equal(3, result.UnlockedCount);
		Assert.Equal(0, result.TotalCount);
		Assert.Empty(result.Achievements);
	}

	[Fact]
	public void Parse_WhitespaceOnlyNameAndDescription_YieldNulls()
	{
		// A regex match whose text is whitespace-only must read as absent after
		// the Trim: both fields null while the API name stays inferred from the icon.
		const string html = """
			<div id="topSummaryAchievements"><div>1 of 1 (100%) achievements earned:</div></div>
			<div id="personalAchieve" class="achievements_list ">
				<div role="button" class="achieveRow">
					<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/portal_beat_game.jpg"></div>
					<div class="achieveTxtHolder"><div class="achieveTxt">
						<h3 class="ellipsis">   </h3>
						<h5 class="ellipsis">  </h5>
					</div></div>
				</div>
			</div>
			""";

		var result = SteamAchievementsClient.ParseAchievementsPage(html);

		var row = Assert.Single(result.Achievements);
		Assert.Null(row.DisplayName);
		Assert.Null(row.Description);
		Assert.Equal("PORTAL_BEAT_GAME", row.ApiName);
	}

	[Fact]
	public void Parse_RowWithoutIcon_IsSkippedInsteadOfCrashing()
	{
		// A row the icon regex cannot match (markup drift, ad slot, emoji-only
		// row) must be dropped — an achievement without an icon has no API name
		// hint, and inventing one would fail loudly at write time.
		const string html = """
			<div id="personalAchieve" class="achievements_list ">
				1 of 2 (50%) achievements earned:
				<div role="button" class="achieveRow">
					<div class="achieveImgHolder"><img src="https://shared.akamai.steamstatic.com/community_assets/images/apps/400/only_ach_bw.jpg"></div>
					<div class="achieveTxtHolder"><div class="achieveTxt"><h3 class="ellipsis">Only One</h3></div></div>
				</div>
				<div role="button" class="achieveRow">
					<div class="achieveTxtHolder"><div class="achieveTxt"><h3 class="ellipsis">Ghost Row</h3></div></div>
				</div>
			</div>
			""";

		var result = SteamAchievementsClient.ParseAchievementsPage(html);

		var only = Assert.Single(result.Achievements);
		Assert.Equal("Only One", only.DisplayName);
		Assert.Equal(2, result.TotalCount); // summary stays authoritative over the surviving rows
	}
}
