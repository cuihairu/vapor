namespace Vapor.ControlPlane;

/// <summary>
/// A single audit record describing a security- or operations-relevant event.
/// </summary>
public sealed record AuditEntry(
	string Id,
	DateTimeOffset Timestamp,
	string Action,
	string Actor,
	string? RemoteIp = null,
	string? AccountName = null,
	string? JobId = null,
	IReadOnlyDictionary<string, object?>? Details = null
);

/// <summary>
/// Query filters for audit log retrieval.
/// </summary>
public sealed record AuditQuery(
	string? Action = null,
	string? AccountName = null,
	string? JobId = null,
	DateTimeOffset? From = null,
	DateTimeOffset? To = null,
	int Limit = 100,
	int Offset = 0
);

/// <summary>
/// Durable audit log storage. Entries are persisted with sensitive values redacted.
/// </summary>
public interface IAuditStore
{
	Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
	Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken);
	Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken);
}

public static class AuditStoreExtensions
{
	/// <summary>
	/// Creates an audit entry with a generated id and current timestamp.
	/// </summary>
	public static AuditEntry CreateEntry(
		string action,
		string actor,
		string? remoteIp = null,
		string? accountName = null,
		string? jobId = null,
		IReadOnlyDictionary<string, object?>? details = null)
	{
		return new AuditEntry(
			Id: Id.New(),
			Timestamp: DateTimeOffset.UtcNow,
			Action: action,
			Actor: actor,
			RemoteIp: remoteIp,
			AccountName: accountName,
			JobId: jobId,
			Details: details
		);
	}
}
