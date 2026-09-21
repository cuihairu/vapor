using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Fires recurring job templates (<see cref="JobStatus.Scheduled"/>) whose next trigger point is
/// due: for each one it atomically creates a child job with the template's action/targets/payload
/// (<c>meta.scheduledFrom</c>) and advances the template. Downtime gaps follow the template's
/// missed policy (<see cref="ScheduleMissedPolicy.Skip"/> drops them,
/// <see cref="ScheduleMissedPolicy.RunOnce"/> fires one catch-up run); an in-flight previous run
/// follows the overlap policy (<see cref="ScheduleOverlapPolicy.Skip"/> defers, Allow runs in
/// parallel). Canceling the template job stops the recurrence.
/// </summary>
public sealed class RecurringJobScheduler : BackgroundService
{
	private const int MaxTemplatesPerTick = 50;

	private readonly IJobStore _store;
	private readonly IEventBroker _events;
	private readonly ILogger<RecurringJobScheduler> _logger;

	/// <summary>Runs created on time or as catch-up.</summary>
	public long TriggeredRuns => Interlocked.Read(ref _triggered);
	/// <summary>Trigger points dropped because a previous run was still in flight.</summary>
	public long SkippedOverlaps => Interlocked.Read(ref _skippedOverlap);
	/// <summary>Trigger points dropped by the missed=skip policy after downtime.</summary>
	public long MissedDropped => Interlocked.Read(ref _missedDropped);
	/// <summary>Catch-up runs created by the missed=run_once policy.</summary>
	public long MissedCatchUps => Interlocked.Read(ref _missedCatchUps);

	private long _triggered;
	private long _skippedOverlap;
	private long _missedDropped;
	private long _missedCatchUps;

	// Test hooks.
	internal Func<DateTimeOffset> Clock = static () => DateTimeOffset.UtcNow;

	public RecurringJobScheduler(IJobStore store, IEventBroker events, ILogger<RecurringJobScheduler> logger)
	{
		_store = store;
		_events = events;
		_logger = logger;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
		=> TimerLoopAsync(stoppingToken);

	/// <summary>
	/// The PeriodicTimer loop proper. Excluded from coverage: a PeriodicTimer that
	/// is disposed or cancelled while awaited always throws from
	/// WaitForNextTickAsync, so the loop can only exit through that throw — its
	/// closing brace is unreachable by construction (see tests/TESTING.md).
	/// </summary>
	[ExcludeFromCodeCoverage]
	private async Task TimerLoopAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
		{
			await TickAsync(stoppingToken).ConfigureAwait(false);
		}
	}

	/// <summary>Single scheduler pass over all due templates (internal for tests).</summary>
	internal async Task TickAsync(CancellationToken cancellationToken)
	{
		DateTimeOffset now = Clock();
		IReadOnlyList<Job> due = await _store.ListDueScheduledJobs(now, MaxTemplatesPerTick, cancellationToken).ConfigureAwait(false);

		foreach (Job template in due)
		{
			if (template.Schedule == null || template.NextRunAt == null)
			{
				continue; // Corrupt row; leave it for manual inspection rather than hot-looping.
			}

			await ProcessTemplateAsync(template, now, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ProcessTemplateAsync(Job template, DateTimeOffset now, CancellationToken cancellationToken)
	{
		JobSchedule schedule = template.Schedule!;
		DateTimeOffset lastDue = template.NextRunAt!.Value;

		// Chain-advance from the due point through every trigger point that already elapsed
		// (downtime, backpressure). Afterwards cursor is the newest elapsed point and
		// missed counts the extra ones beyond it; future is the next point that hasn't fired.
		DateTimeOffset cursor = lastDue;
		int missed = 0;
		while (ScheduleClock.NextRun(schedule, cursor) is { } candidate && candidate <= now)
		{
			cursor = candidate;
			missed++;
		}

		DateTimeOffset? future = ScheduleClock.NextRun(schedule, cursor);

		if (missed > 0)
		{
			// future is never null here: a cron that produced the missed trigger points
			// (5-field, no year field) always has a later match, and interval schedules
			// by construction return a next point.
			DateTimeOffset next = future!.Value;
			if (schedule.Missed == ScheduleMissedPolicy.Skip)
			{
				Interlocked.Add(ref _missedDropped, missed);
				await _store.AdvanceSchedule(template.Id, next, cancellationToken).ConfigureAwait(false);
				Publish(template.Id, "job.scheduled_skipped", new Dictionary<string, object?>
				{
					["reason"] = "missed",
					["missedCount"] = missed
				});
				return;
			}

			// Missed=RunOnce: fire the newest elapsed point as a catch-up, then stay on schedule.
			Job? catchUp = await _store.TriggerScheduledJob(template.Id, next, new Dictionary<string, string>
			{
				["scheduledMissedCount"] = missed.ToString(System.Globalization.CultureInfo.InvariantCulture)
			}, cancellationToken).ConfigureAwait(false);
			if (catchUp != null)
			{
				Interlocked.Increment(ref _missedCatchUps);
				Interlocked.Increment(ref _triggered);
				Publish(template.Id, "job.scheduled_triggered", new Dictionary<string, object?>
				{
					["jobId"] = catchUp.Id,
					["missedCount"] = missed
				});
				_logger.LogInformation("Scheduled job {TemplateId} caught up {Missed} missed trigger(s) with run {JobId}", template.Id, missed, catchUp.Id);
			}
			return;
		}

		if (schedule.Overlap == ScheduleOverlapPolicy.Skip &&
			await _store.HasActiveChildJob(template.Id, cancellationToken).ConfigureAwait(false))
		{
			Interlocked.Increment(ref _skippedOverlap);
			await _store.AdvanceSchedule(template.Id, future!.Value, cancellationToken).ConfigureAwait(false);
			Publish(template.Id, "job.scheduled_skipped", new Dictionary<string, object?>
			{
				["reason"] = "overlap",
				["nextRunAt"] = future!.Value.ToUnixTimeMilliseconds()
			});
			return;
		}

		if (future == null)
		{
			await RetireAsync(template, cancellationToken).ConfigureAwait(false);
			return;
		}

		Job? run = await _store.TriggerScheduledJob(template.Id, future.Value, null, cancellationToken).ConfigureAwait(false);
		if (run == null)
		{
			return; // Canceled concurrently between listing and triggering.
		}

		Interlocked.Increment(ref _triggered);
		Publish(template.Id, "job.scheduled_triggered", new Dictionary<string, object?>
		{
			["jobId"] = run.Id,
			["missedCount"] = 0
		});
		_logger.LogDebug("Scheduled job {TemplateId} triggered run {JobId}", template.Id, run.Id);
	}

	private async Task RetireAsync(Job template, CancellationToken cancellationToken)
	{
		await _store.CancelJob(template.Id, cancellationToken).ConfigureAwait(false);
		Publish(template.Id, "job.scheduled_completed", new Dictionary<string, object?>
		{
			["reason"] = "schedule_exhausted"
		});
		_logger.LogInformation("Scheduled job {TemplateId} retired: cron schedule has no future occurrences", template.Id);
	}

	private void Publish(string templateJobId, string type, Dictionary<string, object?> payload)
	{
		payload["templateJobId"] = templateJobId;
		_events.Publish(templateJobId, type, payload);
	}
}
