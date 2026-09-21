using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Steam;

public sealed class AchievementStatsBitmapTests
{
	[Theory]
	[InlineData(0u, 0u, 1u)]
	[InlineData(5u, 0u, 32u)]
	[InlineData(31u, 0u, 2147483648u)] // highest bit of the first entry
	[InlineData(32u, 1u, 1u)]          // wraps into the second entry
	[InlineData(70u, 2u, 64u)]
	public void StatIdAndBit_MathFollowsTheIdShiftEncoding(uint id, uint expectedStatId, uint expectedBit)
	{
		Assert.Equal(expectedStatId, AchievementStatsBitmap.StatIdFor(id));
		Assert.Equal(expectedBit, AchievementStatsBitmap.BitFor(id));
	}

	[Fact]
	public void IsSet_AbsentEntryCountsAsZero()
	{
		Assert.False(AchievementStatsBitmap.IsSet([], 3));
		Assert.False(AchievementStatsBitmap.IsSet([new UserStatsEntry(5, 0b1)], 3)); // different stat id: no entry for it
		Assert.False(AchievementStatsBitmap.IsSet([new UserStatsEntry(0, 0b1011)], 2)); // bit 2 not set
		Assert.True(AchievementStatsBitmap.IsSet([new UserStatsEntry(0, 0b1011)], 3));
	}

	[Fact]
	public void Apply_UnlockOnEmptyBlob_CreatesOnlyTheTouchedEntries()
	{
		var patched = AchievementStatsBitmap.Apply([], [0, 31, 32], unlock: true);

		Assert.Equal(2, patched.Count); // stat 0 (bits 0+31) and stat 1 (bit 0)
		Assert.Equal(0u, patched[0].StatId);
		Assert.Equal((1u | 2147483648u), patched[0].StatValue);
		Assert.Equal(1u, patched[1].StatId);
		Assert.Equal(1u, patched[1].StatValue);
	}

	[Fact]
	public void Apply_MergesOntoExistingEntries_PreservingUntouchedStats()
	{
		var blob = new List<UserStatsEntry>
		{
			new(0, 0b0001),   // achievement 0 already unlocked
			new(4, 123456789) // an unrelated numeric stat
		};

		var patched = AchievementStatsBitmap.Apply(blob, [1, 32], unlock: true);

		Assert.Equal(new uint[] { 0, 1, 4 }, patched.Select(e => e.StatId));
		Assert.Equal(0b0011u, patched.Single(e => e.StatId == 0).StatValue);
		Assert.Equal(1u, patched.Single(e => e.StatId == 1).StatValue);
		Assert.Equal(123456789u, patched.Single(e => e.StatId == 4).StatValue); // untouched
	}

	[Fact]
	public void Apply_Reset_ClearsOnlyTheTargetBits()
	{
		var blob = new List<UserStatsEntry> { new(0, 0b1111) };

		var patched = AchievementStatsBitmap.Apply(blob, [1], unlock: false);

		Assert.Equal(0b1101u, patched.Single(e => e.StatId == 0).StatValue);
		Assert.False(AchievementStatsBitmap.IsSet(patched, 1));
		Assert.True(AchievementStatsBitmap.IsSet(patched, 0));
		Assert.True(AchievementStatsBitmap.IsSet(patched, 2));
	}

	[Fact]
	public void Apply_ResetOnAbsentEntry_WritesAnExplicitZeroEntry()
	{
		var patched = AchievementStatsBitmap.Apply([], [64], unlock: false);

		var entry = Assert.Single(patched);
		Assert.Equal(2u, entry.StatId);
		Assert.Equal(0u, entry.StatValue);
	}
}
