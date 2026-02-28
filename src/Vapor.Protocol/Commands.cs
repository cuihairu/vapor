namespace Vapor.Protocol;

public enum PermissionLevel {
	Guest,
	Operator,
	Admin
}

public sealed record CommandRequest(
	string Command,
	IReadOnlyList<string>? Targets = null,
	IReadOnlyDictionary<string, object?>? Args = null
);

public sealed record CommandResult(
	bool Success,
	string? Error = null,
	IReadOnlyDictionary<string, object?>? Output = null
);
