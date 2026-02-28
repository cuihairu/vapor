namespace Vapor.Protocol;

public sealed record ActionParamSchema(
	string Name,
	string Type,
	bool Required = false,
	string? Description = null
);

public sealed record ActionDescriptor(
	string Name,
	string Summary,
	IReadOnlyList<ActionParamSchema>? Params = null,
	PermissionLevel Permission = PermissionLevel.Operator,
	IReadOnlyList<string>? Tags = null
);
