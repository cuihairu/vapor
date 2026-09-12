namespace Vapor.Protocol;

public enum JobStatus
{
	Queued,
	Running,
	Scheduled,
	Finished,
	Failed,
	Canceled
}

/// <summary>What to do with trigger points missed while the scheduler was down or busy.</summary>
public enum ScheduleMissedPolicy
{
	/// <summary>Drop missed trigger points; only the upcoming one fires.</summary>
	Skip,
	/// <summary>Fire one catch-up job for the most recent missed trigger point.</summary>
	RunOnce
}

/// <summary>What to do when a previous scheduled run is still queued or running.</summary>
public enum ScheduleOverlapPolicy
{
	/// <summary>Skip this trigger point while a previous run is still in flight.</summary>
	Skip,
	/// <summary>Run anyway, in parallel with the previous run.</summary>
	Allow
}

/// <summary>
/// Recurring schedule for a job template. Either <see cref="IntervalSeconds"/> (fixed cadence)
/// or <see cref="Cron"/> (5-field cron expression, UTC) must be set; cron wins when both are given.
/// </summary>
public sealed record JobSchedule(
	int IntervalSeconds = 0,
	string? Cron = null,
	ScheduleMissedPolicy Missed = ScheduleMissedPolicy.Skip,
	ScheduleOverlapPolicy Overlap = ScheduleOverlapPolicy.Skip
);

public enum JobTaskStatus
{
	Queued,
	Running,
	Finished,
	Failed,
	Canceled
}

public sealed record CreateJobRequest(
	string Action,
	string? Region,
	IReadOnlyList<string> Targets,
	IReadOnlyDictionary<string, object?>? Payload,
	IReadOnlyDictionary<string, string>? Meta,
	JobSchedule? Schedule = null
);

public sealed record CreateJobResponse(Job Job);

public sealed record Job(
	string Id,
	string Action,
	string? Region,
	IReadOnlyList<string> Targets,
	IReadOnlyDictionary<string, string>? Meta,
	JobStatus Status,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	JobSchedule? Schedule = null,
	DateTimeOffset? NextRunAt = null
);

public sealed record JobTask(
	string Id,
	string JobId,
	string Target,
	string Action,
	string? Region,
	IReadOnlyDictionary<string, object?>? Payload,
	JobTaskStatus Status,
	int Attempt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	string? Error = null,
	IReadOnlyDictionary<string, object?>? Output = null
);

public sealed record TaskResult(
	string TaskId,
	bool Success,
	string? Error,
	IReadOnlyDictionary<string, object?>? Output,
	DateTimeOffset FinishedAt,
	int Attempt = 0
);

public sealed record TaskHeartbeat(
	string TaskId,
	int Attempt,
	DateTimeOffset Ts
);

public sealed record TaskCancel(
	string TaskId,
	int Attempt,
	DateTimeOffset Ts,
	string? Reason = null
);

public sealed record JobWithTasks(Job Job, IReadOnlyList<JobTask> Tasks);

public sealed record ErrorResponse(string Error);

public sealed record Event(
	string Id,
	string? JobId,
	string Type,
	DateTimeOffset Ts,
	IReadOnlyDictionary<string, object?>? Payload
);

public sealed record AgentHello(
	string AgentId,
	string Region,
	IReadOnlyDictionary<string, bool>? Capabilities,
	IReadOnlyDictionary<string, string>? Meta
);

public sealed record WSMessage(
	string Type,
	AgentHello? Hello,
	JobTask? Task,
	TaskResult? TaskResult,
	TaskHeartbeat? TaskHeartbeat = null,
	TaskCancel? TaskCancel = null,
	/// <summary>W3C trace-context headers (traceparent) for distributed tracing across the tunnel.</summary>
	IReadOnlyDictionary<string, string>? TraceHeaders = null
);

