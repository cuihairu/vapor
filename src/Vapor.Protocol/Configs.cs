namespace Vapor.Protocol;

public sealed record ConfigVersion(
	int Version,
	DateTimeOffset UpdatedAt,
	string? UpdatedBy = null
);

public sealed record GlobalConfig(
	ConfigVersion Version,
	IReadOnlyDictionary<string, object?>? Settings = null
);

public sealed record AccountConfig(
	string AccountName,
	bool Enabled,
	string? Region = null,
	IReadOnlyList<string>? Labels = null,
	IReadOnlyDictionary<string, object?>? Settings = null,
	ConfigVersion? Version = null
);
