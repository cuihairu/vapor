namespace SteamControl.Protocol;

public enum JobStatus {
	Queued,
	Running,
	Finished,
	Failed,
	Canceled
}

public enum JobTaskStatus {
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
	IReadOnlyDictionary<string, string>? Meta
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
	DateTimeOffset UpdatedAt
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
	DateTimeOffset UpdatedAt
);

public sealed record TaskResult(
	string TaskId,
	bool Success,
	string? Error,
	IReadOnlyDictionary<string, object?>? Output,
	DateTimeOffset FinishedAt
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
	TaskResult? TaskResult
);
