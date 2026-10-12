using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Fires scheduled script flows (<see cref="ScriptFlowRecord.Schedule"/>) whose recurring
/// cursor is due: for each one it advances the cursor and runs the flow through the same
/// <see cref="ScriptFlowRunner"/> the manual endpoint uses (audited under the actor
/// "scheduler"). Downtime gaps follow the schedule's missed policy
/// (<see cref="ScheduleMissedPolicy.Skip"/> drops them, <see cref="ScheduleMissedPolicy.RunOnce"/>
/// fires one catch-up run); a still-running previous run follows the overlap policy
/// (<see cref="ScheduleOverlapPolicy.Skip"/> defers, Allow runs in parallel). A cron whose
/// expression can never match again disarms the flow back to manual-only.
/// Runs execute inline on the scheduler loop: one slow flow delays later due flows in the
/// same tick, the same serial-tick backpressure the recurring job scheduler has. The
/// cursor advances before the run starts, so a crash mid-run loses that one trigger point
/// rather than double-firing.
/// </summary>
public sealed class RecurringFlowScheduler : BackgroundService
{
	private const int MaxFlowsPerTick = 20;

	private readonly SqliteFlowStore _flows;
	private readonly ScriptFlowRunner _runner;
	private readonly IAuditStore _audit;
	private readonly ILogger<RecurringFlowScheduler> _logger;

	/// <summary>Runs created on time or as catch-up.</summary>
	public long TriggeredRuns => Interlocked.Read(ref _triggered);
	/// <summary>Trigger points deferred because a previous run was still active.</summary>
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

	public RecurringFlowScheduler(SqliteFlowStore flows, ScriptFlowRunner runner, IAuditStore audit, ILogger<RecurringFlowScheduler> logger)
	{
		_flows = flows;
		_runner = runner;
		_audit = audit;
		_logger = logger;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
		=> TimerLoopAsync(stoppingToken);

	/// <summary>
	/// The PeriodicTimer loop proper. Excluded from coverage for the same reason as the
	/// recurring job scheduler's loop: a disposed or cancelled PeriodicTimer always throws
	/// from WaitForNextTickAsync, so the loop can only exit through that throw — its
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

	/// <summary>Single scheduler pass over all due flows (internal for tests).</summary>
	internal async Task TickAsync(CancellationToken cancellationToken)
	{
		DateTimeOffset now = Clock();
		List<ScriptFlowRecord> due = await _flows.ListDueFlowsAsync(now, MaxFlowsPerTick, cancellationToken).ConfigureAwait(false);

		foreach (ScriptFlowRecord flow in due)
		{
			await ProcessFlowAsync(flow, now, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ProcessFlowAsync(ScriptFlowRecord flow, DateTimeOffset now, CancellationToken cancellationToken)
	{
		// The due query guarantees both fields (schedule IS NOT NULL, and the
		// cursor comparison excludes NULL), so a broken row never lists instead
		// of needing a corrupt-row guard here.
		JobSchedule schedule = flow.Schedule!;
		DateTimeOffset lastDue = DateTimeOffset.FromUnixTimeMilliseconds(flow.NextRunAtMs!.Value);

		// Chain-advance from the due point through every trigger point that already
		// elapsed (downtime, backpressure) — the same cursor arithmetic the recurring
		// job scheduler runs on job templates.
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
			// future is never null here: the cron produced the missed trigger points,
			// so it always has a later match, and interval schedules by construction
			// return a next point.
			DateTimeOffset next = future!.Value;
			if (schedule.Missed == ScheduleMissedPolicy.Skip)
			{
				Interlocked.Add(ref _missedDropped, missed);
				await _flows.AdvanceFlowScheduleAsync(flow.Id, next, cancellationToken).ConfigureAwait(false);
				await AuditAsync("flow_schedule_skipped", new Dictionary<string, object?>
				{
					["flowId"] = flow.Id,
					["reason"] = "missed",
					["missedCount"] = missed
				}, cancellationToken).ConfigureAwait(false);
				return;
			}

			// Missed=RunOnce: fire the newest elapsed point as a catch-up, then stay on schedule.
			await _flows.AdvanceFlowScheduleAsync(flow.Id, next, cancellationToken).ConfigureAwait(false);
			if (await FireAsync(flow, cancellationToken).ConfigureAwait(false))
			{
				Interlocked.Increment(ref _missedCatchUps);
			}
			return;
		}

		if (schedule.Overlap == ScheduleOverlapPolicy.Skip &&
			await _flows.HasActiveFlowRunAsync(flow.Id, cancellationToken).ConfigureAwait(false))
		{
			Interlocked.Increment(ref _skippedOverlap);
			await _flows.AdvanceFlowScheduleAsync(flow.Id, future!.Value, cancellationToken).ConfigureAwait(false);
			await AuditAsync("flow_schedule_skipped", new Dictionary<string, object?>
			{
				["flowId"] = flow.Id,
				["reason"] = "overlap",
				["nextRunAtMs"] = future!.Value.ToUnixTimeMilliseconds()
			}, cancellationToken).ConfigureAwait(false);
			return;
		}

		if (future == null)
		{
			await DisarmAsync(flow, cancellationToken).ConfigureAwait(false);
			return;
		}

		await _flows.AdvanceFlowScheduleAsync(flow.Id, future.Value, cancellationToken).ConfigureAwait(false);
		if (await FireAsync(flow, cancellationToken).ConfigureAwait(false))
		{
			Interlocked.Increment(ref _triggered);
		}
	}

	/// <summary>
	/// Runs one scheduled pass of the flow. A pre-flight refusal (script deleted,
	/// agent disconnected since the schedule was armed) creates no run and is
	/// audited as a skip so the schedule does not fail silently. Returns whether
	/// a run actually started — refusals are not counted as triggers.
	/// </summary>
	private async Task<bool> FireAsync(ScriptFlowRecord flow, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Scheduled flow {FlowId} ({FlowName}) firing", flow.Id, flow.Name);
		FlowRunStart start = await _runner.RunAsync(flow, "scheduler", cancellationToken).ConfigureAwait(false);
		if (start.Error is null)
		{
			return true;
		}

		_logger.LogWarning("Scheduled flow {FlowId} skipped: {Error}", flow.Id, start.Error);
		await AuditAsync("flow_schedule_skipped", new Dictionary<string, object?>
		{
			["flowId"] = flow.Id,
			["reason"] = "preflight",
			["error"] = start.Error
		}, cancellationToken).ConfigureAwait(false);
		return false;
	}

	/// <summary>
	/// A cron that can never match again has no future trigger point: disarm the
	/// flow back to manual-only instead of hot-looping on a dead cursor. The flow
	/// definition (and its run history) stays for the operator.
	/// </summary>
	private async Task DisarmAsync(ScriptFlowRecord flow, CancellationToken cancellationToken)
	{
		await _flows.UpsertFlowAsync(flow with { Schedule = null, NextRunAtMs = null }, cancellationToken).ConfigureAwait(false);
		_logger.LogInformation("Scheduled flow {FlowId} ({FlowName}) disarmed: cron schedule has no future occurrences", flow.Id, flow.Name);
		await AuditAsync("flow_schedule_completed", new Dictionary<string, object?>
		{
			["flowId"] = flow.Id,
			["reason"] = "schedule_exhausted"
		}, cancellationToken).ConfigureAwait(false);
	}

	private async Task AuditAsync(string action, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken)
	{
		var entry = AuditStoreExtensions.CreateEntry(action, "scheduler", details: details);
		try
		{
			await _audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Audit persistence must never break the scheduler pass.
			_logger.LogError(ex, "Failed to persist audit entry {Action}", action);
		}
	}
}
