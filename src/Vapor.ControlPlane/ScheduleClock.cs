using Cronos;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Computes trigger points for recurring job schedules. Interval schedules tick on a fixed
/// cadence; cron schedules (5-field, UTC) are resolved through Cronos. Isolating Cronos here
/// keeps it out of the store and lets tests pass an arbitrary anchor time.
/// </summary>
public static class ScheduleClock
{
	/// <summary>
	/// Validates a schedule, throwing <see cref="ArgumentException"/> when neither cadence is set
	/// or the cron expression is malformed (REST layer translates this into a 400).
	/// </summary>
	public static void Validate(JobSchedule schedule)
	{
		if (schedule.IntervalSeconds <= 0 && string.IsNullOrWhiteSpace(schedule.Cron))
		{
			throw new ArgumentException("schedule requires intervalSeconds > 0 or a cron expression");
		}

		if (schedule.IntervalSeconds > 0 && !string.IsNullOrWhiteSpace(schedule.Cron))
		{
			throw new ArgumentException("schedule accepts intervalSeconds or cron, not both");
		}

		if (schedule.IntervalSeconds > 0 && schedule.IntervalSeconds < 5)
		{
			throw new ArgumentException("schedule intervalSeconds must be at least 5");
		}

		if (!string.IsNullOrWhiteSpace(schedule.Cron))
		{
			try
			{
				ParseCron(schedule.Cron);
			}
			catch (CronFormatException ex)
			{
				throw new ArgumentException($"invalid cron expression: {ex.Message}", ex);
			}
		}
	}

	/// <summary>First trigger point strictly after <paramref name="after"/>, or null when the cron never matches again.</summary>
	public static DateTimeOffset? NextRun(JobSchedule schedule, DateTimeOffset after)
	{
		if (!string.IsNullOrWhiteSpace(schedule.Cron))
		{
			CronExpression expr = ParseCron(schedule.Cron);
			return expr.GetNextOccurrence(after, TimeZoneInfo.Utc);
		}

		return after.AddSeconds(schedule.IntervalSeconds);
	}

	/// <summary>Number of trigger points in the half-open interval (from, until] — used to detect missed triggers.</summary>
	public static int CountTriggerPoints(JobSchedule schedule, DateTimeOffset from, DateTimeOffset until)
	{
		int count = 0;
		DateTimeOffset cursor = from;
		while (NextRun(schedule, cursor) is { } next && next <= until)
		{
			count++;
			cursor = next;
		}

		return count;
	}

	private static CronExpression ParseCron(string? expression) =>
		CronExpression.Parse(expression!.Trim(), CronFormat.Standard);
}
