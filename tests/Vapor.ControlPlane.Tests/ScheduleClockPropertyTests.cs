using FsCheck;
using FsCheck.Xunit;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Property-based invariants (FsCheck) over ScheduleClock's three pure
/// functions — the interval arithmetic, the half-open trigger counting and
/// the Validate acceptance domain. The example tests in ScheduleClockTests
/// pin known anchors and each rejection arm; these sweep arbitrary anchors,
/// intervals and split points, including the additivity law that ties
/// CountTriggerPoints together across both cadence branches.
/// </summary>
public sealed class ScheduleClockPropertyTests
{
	private const long BaseEpochSeconds = 631152000; // 2000-01-01T00:00:00Z, keeps every anchor inside DateTimeOffset's safe range.

	// Legal cron samples (Cronos Standard format, incl. the @daily shorthand
	// the REST layer documents) and malformed counterparts, self-labelled so
	// the Validate classification property can decide "legal" without calling
	// Cronos itself — that would make the property tautological.
	private static readonly (string Cron, bool Legal)[] CronSamples =
	{
		("* * * * *", true),
		("0 12 * * *", true),
		("*/5 * * * *", true),
		("0 9-17 * * 1-5", true),
		("0 0 * * 1", true),
		("30 3 1 * *", true),
		("@daily", true),
		("* * *", false),
		("bad", false),
		("60 * * * *", false),
		("* 25 * * *", false),
	};

	private static readonly string?[] WhitespaceCrons = { null, "", "   " };

	private static JobSchedule IntervalSchedule(int seconds) => new(IntervalSeconds: seconds);

	// Index mapping that accepts every int including negatives (the raw
	// modulo can go below zero exactly on the boundary samples FsCheck draws).
	private static int Ring(int index, int count) => ((index % count) + count) % count;

	private static int RingLegal(int index) => LegalCronIndexes[Ring(index, LegalCronIndexes.Length)];

	// Positions of the legal samples only — NextRun/CountTriggerPoints assume
	// a parseable expression, so the malformed counterparts of the Validate
	// table must stay out of this domain.
	private static readonly int[] LegalCronIndexes =
		CronSamples.Select((s, i) => (s, i)).Where(t => t.s.Legal).Select(t => t.i).ToArray();

	private static JobSchedule CronSchedule(int index) => new(Cron: CronSamples[RingLegal(index)].Cron);

	private static DateTimeOffset Anchor(int offsetSeconds) =>
		DateTimeOffset.FromUnixTimeSeconds(BaseEpochSeconds + offsetSeconds);

	// ---------- interval cadence ----------

	[Property]
	public Property Interval_NextRun_IsExactSecondsArithmetic(NonNegativeInt intervalRaw, NonNegativeInt offsetRaw)
	{
		int interval = 5 + intervalRaw.Get % 3600;
		DateTimeOffset anchor = Anchor(offsetRaw.Get);

		return (ScheduleClock.NextRun(IntervalSchedule(interval), anchor) == anchor.AddSeconds(interval)).ToProperty();
	}

	[Property]
	public Property Interval_CountTriggerPoints_MatchesTicksFormula(NonNegativeInt intervalRaw, NonNegativeInt fromRaw, NonNegativeInt spanRaw)
	{
		int interval = 5 + intervalRaw.Get % 3600;
		DateTimeOffset from = Anchor(fromRaw.Get);
		DateTimeOffset until = from.AddSeconds(spanRaw.Get % 10000);

		// Trigger points sit at from + k·interval for k ≥ 1, so the count in
		// (from, until] is exactly floor(span / interval) in tick arithmetic.
		long expected = (until - from).Ticks / ((long)interval * TimeSpan.TicksPerSecond);

		return (ScheduleClock.CountTriggerPoints(IntervalSchedule(interval), from, until) == expected).ToProperty();
	}

	// ---------- both cadences: half-open additivity ----------

	[Property]
	public Property CountTriggerPoints_IsAdditiveOverHalfOpenSplits_Interval(NonNegativeInt intervalRaw, NonNegativeInt fromRaw, NonNegativeInt stepRaw, NonNegativeInt spanRaw)
	{
		int interval = 5 + intervalRaw.Get % 3600;
		DateTimeOffset from = Anchor(fromRaw.Get);
		// Interval cadence re-anchors at every trigger — NextRun(split) is
		// split+interval, not the next point of from's sequence — so a split
		// mid-step breaks additivity by design (the counting walks two
		// independent drift chains). Splitting on from's own grid is the shape
		// a real driver uses (each "from" is the previous trigger point), and
		// there the half-open additivity law must hold.
		DateTimeOffset split = from.AddSeconds((long)(stepRaw.Get % 2000) * interval);
		DateTimeOffset until = split.AddSeconds(spanRaw.Get % 10000);

		var schedule = IntervalSchedule(interval);
		return (ScheduleClock.CountTriggerPoints(schedule, from, split)
			+ ScheduleClock.CountTriggerPoints(schedule, split, until)
			== ScheduleClock.CountTriggerPoints(schedule, from, until)).ToProperty();
	}

	[Property]
	public Property CountTriggerPoints_IsAdditiveOverHalfOpenSplits_Cron(int cronIndex, NonNegativeInt fromRaw, NonNegativeInt splitRaw, NonNegativeInt spanRaw)
	{
		DateTimeOffset from = Anchor(fromRaw.Get);
		DateTimeOffset split = from.AddSeconds(splitRaw.Get % 10000);
		DateTimeOffset until = split.AddSeconds(spanRaw.Get % 10000);

		// Cron points are an absolute set of wall-clock instants, so the
		// additivity law holds for an arbitrary split — unlike the interval
		// cadence, no grid alignment is required.
		var schedule = CronSchedule(cronIndex);
		return (ScheduleClock.CountTriggerPoints(schedule, from, split)
			+ ScheduleClock.CountTriggerPoints(schedule, split, until)
			== ScheduleClock.CountTriggerPoints(schedule, from, until)).ToProperty();
	}

	[Property]
	public Property Cron_NextRun_IsStrictlyAfterAnchorOrNever(int cronIndex, NonNegativeInt offsetRaw)
	{
		DateTimeOffset after = Anchor(offsetRaw.Get);
		DateTimeOffset? next = ScheduleClock.NextRun(CronSchedule(cronIndex), after);

		return ((next is null || next > after)).ToProperty();
	}

	// ---------- Validate acceptance domain ----------

	[Property]
	public Property Validate_AcceptsExactlyTheDocumentedDomain(int interval, int cronIndex)
	{
		// Negative index draws a null/whitespace cron (no cadence there),
		// non-negative draws a labelled sample deciding legality without Cronos.
		string? cron = cronIndex < 0
			? WhitespaceCrons[Ring(-cronIndex - 1, WhitespaceCrons.Length)]
			: CronSamples[Ring(cronIndex, CronSamples.Length)].Cron;
		bool cronPresent = cronIndex >= 0;

		bool expectAccept = cronPresent
			? interval <= 0 && CronSamples[Ring(cronIndex, CronSamples.Length)].Legal
			: interval >= 5;

		try
		{
			ScheduleClock.Validate(new JobSchedule(interval, cron));
			return expectAccept.ToProperty();
		}
		catch (ArgumentException)
		{
			return (!expectAccept).ToProperty();
		}
	}
}
