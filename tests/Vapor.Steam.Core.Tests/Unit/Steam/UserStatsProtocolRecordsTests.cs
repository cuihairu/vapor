using SteamKit2;
using SteamKit2.Internal;
using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Steam;

/// <summary>
/// The stats-protocol payload records are the wire contract between the
/// transport and the achievements/stats features; these tests pin each
/// record's construction surface plus the pairing rules callers rely on
/// (OK carries a load body, a protocol answer carries exactly one body).
/// </summary>
public class UserStatsProtocolRecordsTests
{
	[Fact]
	public void AchievementUnlockBlock_ExposesConstructionSurface()
	{
		uint[] times = [1735689600, 1735776000];
		var block = new AchievementUnlockBlock(1861, times);

		Assert.Equal(1861u, block.AchievementId);
		Assert.Same(times, block.UnlockTimes);
	}

	[Fact]
	public void AchievementUnlockBlock_Equality_FollowsFieldValues()
	{
		uint[] times = [1735689600];

		Assert.Equal(new AchievementUnlockBlock(1861, times), new AchievementUnlockBlock(1861, times));
		Assert.NotEqual(new AchievementUnlockBlock(1861, times), new AchievementUnlockBlock(1862, times));
	}

	[Fact]
	public void UserStatsLoad_ExposesConstructionSurface()
	{
		UserStatsEntry[] stats = [new(1, 100), new(2, 200)];
		AchievementUnlockBlock[] blocks = [new(1861, [1735689600])];
		var load = new UserStatsLoad(0x1234ABCD, stats, blocks);

		Assert.Equal(0x1234ABCDu, load.CrcStats);
		Assert.Same(stats, load.Stats);
		Assert.Same(blocks, load.AchievementBlocks);
	}

	[Fact]
	public void UserStatsLoad_EmptyBlob_FormIsSupported()
	{
		// The transport's fallback for a game with no stored blob is an empty
		// load with crc 0 — callers treat it as a fresh start, not an error.
		var load = new UserStatsLoad(0, [], []);

		Assert.Equal(0u, load.CrcStats);
		Assert.Empty(load.Stats);
		Assert.Empty(load.AchievementBlocks);
	}

	[Fact]
	public void UserStatsLoadResult_OkCarriesLoad_FailureDoesNot()
	{
		var load = new UserStatsLoad(7, [], []);

		var ok = new UserStatsLoadResult(SteamResult.OK, load);
		var failure = new UserStatsLoadResult(SteamResult.Fail, null);

		Assert.Equal(SteamResult.OK, ok.Result);
		Assert.Same(load, ok.Load);
		Assert.Equal(SteamResult.Fail, failure.Result);
		Assert.Null(failure.Load);
	}

	[Fact]
	public void UserStatsLoadResult_ToString_IncludesResultName()
	{
		var text = new UserStatsLoadResult(SteamResult.Timeout, null).ToString();

		Assert.Contains("Timeout", text);
	}

	[Fact]
	public void UserStatsStoreResult_ExposesConstructionSurface()
	{
		uint[] failedIds = [5, 9];
		var result = new UserStatsStoreResult(SteamResult.OK, StatsOutOfDate: true, failedIds);

		Assert.Equal(SteamResult.OK, result.Result);
		Assert.True(result.StatsOutOfDate);
		Assert.Same(failedIds, result.FailedValidationStatIds);
	}

	[Fact]
	public void AchievementNamesResult_ExposesConstructionSurface()
	{
		string[] names = ["ach_kill_1", "ach_win_1"];
		var result = new AchievementNamesResult(SteamResult.OK, names);

		Assert.Equal(SteamResult.OK, result.Result);
		Assert.Same(names, result.InternalNames);
	}

	[Fact]
	public void UserStatsProtocolResponse_CarriesExactlyOneBody()
	{
		var getResponse = new CMsgClientGetUserStatsResponse();
		var getAnswer = new UserStatsProtocolResponse(EResult.OK, getResponse, null);

		Assert.Equal(EResult.OK, getAnswer.Result);
		Assert.Same(getResponse, getAnswer.GetResponse);
		Assert.Null(getAnswer.StoreResponse);

		var storeResponse = new CMsgClientStoreUserStatsResponse();
		var storeAnswer = new UserStatsProtocolResponse(EResult.OK, null, storeResponse);

		Assert.Null(storeAnswer.GetResponse);
		Assert.Same(storeResponse, storeAnswer.StoreResponse);
	}
}
