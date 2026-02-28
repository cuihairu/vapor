namespace Vapor.Protocol;

public sealed record JobEvent(
	string Id,
	string JobId,
	string Type,
	DateTimeOffset Ts,
	IReadOnlyDictionary<string, object?>? Payload = null
);

public sealed record TaskEvent(
	string Id,
	string TaskId,
	string JobId,
	string Type,
	DateTimeOffset Ts,
	IReadOnlyDictionary<string, object?>? Payload = null
);

public sealed record SessionEvent(
	string Id,
	string AccountName,
	string EventType,
	string State,
	string? Message,
	DateTimeOffset Timestamp
);

public sealed record AuthChallengeEvent(
	string Id,
	string AccountName,
	string ChallengeType,
	string? Message,
	string? Code,
	DateTimeOffset Timestamp,
	string? JobId
);

public sealed record PluginEvent(
	string Id,
	string PluginId,
	string Type,
	DateTimeOffset Ts,
	IReadOnlyDictionary<string, object?>? Payload = null
);
