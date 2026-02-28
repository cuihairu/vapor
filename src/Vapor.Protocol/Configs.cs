namespace Vapor.Protocol;

/// <summary>
/// Encryption method for password storage.
/// </summary>
public enum EPasswordFormat : byte
{
	/// <summary>
	/// Plain text password.
	/// </summary>
	PlainText = 0,

	/// <summary>
	/// AES-256 encrypted password.
	/// </summary>
	AES = 1,

	/// <summary>
	/// Password from environment variable.
	/// </summary>
	EnvironmentVariable = 2,

	/// <summary>
	/// Password from external file.
	/// </summary>
	File = 3
}

public sealed record ConfigVersion(
	int Version,
	DateTimeOffset UpdatedAt,
	string? UpdatedBy = null
);

public sealed record GlobalConfig(
	ConfigVersion Version,
	string? EncryptionKey = null,
	IReadOnlyDictionary<string, object?>? Settings = null
);

public sealed record AccountConfig(
	string AccountName,
	bool Enabled,
	string? Password = null,
	EPasswordFormat PasswordFormat = EPasswordFormat.PlainText,
	string? Region = null,
	IReadOnlyList<string>? Labels = null,
	IReadOnlyDictionary<string, object?>? Settings = null,
	ConfigVersion? Version = null
);
