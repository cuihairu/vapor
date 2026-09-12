using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class ScheduleClockTests
{
	[Fact]
	public void Validate_AcceptsIntervalOrCronAlone()
	{
		ScheduleClock.Validate(new JobSchedule(IntervalSeconds: 300));
		ScheduleClock.Validate(new JobSchedule(Cron: "0 12 * * *"));
	}

	[Theory]
	[InlineData(0, null)]
	[InlineData(-5, null)]
	[InlineData(4, null)]
	[InlineData(300, "* * * * *")]
	public void Validate_RejectsInvalidCombinations(int interval, string? cron) =>
		Assert.Throws<ArgumentException>(() => ScheduleClock.Validate(new JobSchedule(interval, cron)));

	[Theory]
	[InlineData("* * * * *")]
	[InlineData("0 12 * * *")]
	[InlineData("*/5 * * * *")]
	[InlineData("0 9-17 * * 1-5")]
	[InlineData("@daily")]
	public void Validate_AcceptsKnownCronForms(string cron) =>
		ScheduleClock.Validate(new JobSchedule(Cron: cron));

	[Fact]
	public void Validate_RejectsMalformedCron() =>
		Assert.Throws<ArgumentException>(() => ScheduleClock.Validate(new JobSchedule(Cron: "* * *")));

	[Fact]
	public void NextRun_Interval_IsStrictlyAfterAnchor()
	{
		DateTimeOffset anchor = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

		Assert.Equal(anchor.AddSeconds(300), ScheduleClock.NextRun(new JobSchedule(IntervalSeconds: 300), anchor));
	}

	[Fact]
	public void NextRun_Cron_ResolvesUtcExpressionPoints()
	{
		// 12:00 daily UTC; anchor 2026-09-12 08:00 → same day 12:00.
		DateTimeOffset anchor = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

		Assert.Equal(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero), ScheduleClock.NextRun(new JobSchedule(Cron: "0 12 * * *"), anchor));
	}

	[Fact]
	public void NextRun_Cron_RollsToNextMatch()
	{
		// Mondays only; 2026-09-12 is a Saturday → next Monday is 2026-09-14.
		DateTimeOffset anchor = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

		Assert.Equal(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), ScheduleClock.NextRun(new JobSchedule(Cron: "0 0 * * 1"), anchor));
	}

	[Fact]
	public void NextRun_CronWithoutFutureMatch_ReturnsNull()
	{
		DateTimeOffset anchor = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

		Assert.Null(ScheduleClock.NextRun(new JobSchedule(Cron: "0 0 30 2 *"), anchor));
	}

	[Fact]
	public void CountTriggerPoints_CountsHalfOpenInterval()
	{
		DateTimeOffset from = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
		var schedule = new JobSchedule(IntervalSeconds: 60);

		// (8:00, 8:05] contains 8:01..8:05 — five points.
		Assert.Equal(5, ScheduleClock.CountTriggerPoints(schedule, from, from.AddMinutes(5)));
		// Exactly one step → one point.
		Assert.Equal(1, ScheduleClock.CountTriggerPoints(schedule, from, from.AddSeconds(60)));
		// Nothing elapsed → no points.
		Assert.Equal(0, ScheduleClock.CountTriggerPoints(schedule, from, from));
	}
}
