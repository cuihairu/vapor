using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;
using Vapor.Steam.Core.Utilities;

namespace Vapor.ControlPlane;

/// <summary>
/// SQLite-backed durable audit log.
/// Sensitive detail values are redacted before persistence.
/// </summary>
public sealed class SqliteAuditStore : IAuditStore, IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);
	private readonly bool _inMemory;

	public SqliteAuditStore(string dbPath)
	{
		if (string.IsNullOrWhiteSpace(dbPath))
		{
			throw new ArgumentException("DB path is required", nameof(dbPath));
		}

		_inMemory = string.Equals(dbPath, ":memory:", StringComparison.Ordinal);

		if (!_inMemory)
		{
			string? dir = Path.GetDirectoryName(dbPath);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}

		_connection = new SqliteConnection($"Data Source={dbPath}");
		_connection.Open();

		Migrate();
	}

	public void Dispose()
	{
		_connection.Dispose();
		_mutex.Dispose();
	}

	public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);

		if (string.IsNullOrWhiteSpace(entry.Action))
		{
			throw new ArgumentException("audit action is required", nameof(entry));
		}

		string detailsJson = JsonSerializer.Serialize(entry.Details ?? new Dictionary<string, object?>(), JsonDefaults.Options);
		string redactedDetails = SensitiveDataRedactor.Redact(detailsJson);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO audit_logs (id, ts_ms, action, actor, remote_ip, account_name, job_id, details_json)
				VALUES ($id, $ts, $action, $actor, $remoteIp, $account, $jobId, $details);
				""";
			cmd.Parameters.AddWithValue("$id", entry.Id);
			cmd.Parameters.AddWithValue("$ts", entry.Timestamp.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$action", entry.Action);
			cmd.Parameters.AddWithValue("$actor", string.IsNullOrWhiteSpace(entry.Actor) ? "unknown" : entry.Actor);
			cmd.Parameters.AddWithValue("$remoteIp", (object?)entry.RemoteIp ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$account", (object?)entry.AccountName ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$jobId", (object?)entry.JobId ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$details", redactedDetails);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		(int limit, int offset) = NormalizePaging(query);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			BuildQueryCommand(cmd, query, limit, offset, withPaging: true);

			List<AuditEntry> entries = [];
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				entries.Add(ReadEntryRow(reader));
			}

			return entries;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			BuildQueryCommand(cmd, query, 0, 0, withPaging: false);
			object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			return result is long count ? (int)count : 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	private static (int Limit, int Offset) NormalizePaging(AuditQuery query)
	{
		int limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, 500);
		int offset = Math.Max(query.Offset, 0);
		return (limit, offset);
	}

	private static void BuildQueryCommand(SqliteCommand cmd, AuditQuery query, int limit, int offset, bool withPaging)
	{
		var where = new List<string>();
		var order = withPaging ? "ORDER BY ts_ms DESC, id DESC" : "";

		if (!string.IsNullOrWhiteSpace(query.Action))
		{
			where.Add("action = $action");
			cmd.Parameters.AddWithValue("$action", query.Action.Trim());
		}

		if (!string.IsNullOrWhiteSpace(query.AccountName))
		{
			where.Add("account_name = $account");
			cmd.Parameters.AddWithValue("$account", query.AccountName.Trim());
		}

		if (!string.IsNullOrWhiteSpace(query.JobId))
		{
			where.Add("job_id = $jobId");
			cmd.Parameters.AddWithValue("$jobId", query.JobId.Trim());
		}

		if (query.From is { } from)
		{
			where.Add("ts_ms >= $fromMs");
			cmd.Parameters.AddWithValue("$fromMs", from.ToUnixTimeMilliseconds());
		}

		if (query.To is { } to)
		{
			where.Add("ts_ms <= $toMs");
			cmd.Parameters.AddWithValue("$toMs", to.ToUnixTimeMilliseconds());
		}

		string whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";

		if (withPaging)
		{
			// CA2100 suppressed: every interpolated fragment (whereClause/order) is a
			// compile-time constant built above; all user input rides as $parameters.
#pragma warning disable CA2100
			cmd.CommandText = $"""
				SELECT id, ts_ms, action, actor, remote_ip, account_name, job_id, details_json
				FROM audit_logs
				{whereClause}
				{order}
				LIMIT $limit OFFSET $offset;
				""";
#pragma warning restore CA2100
			cmd.Parameters.AddWithValue("$limit", limit);
			cmd.Parameters.AddWithValue("$offset", offset);
		}
		else
		{
			// CA2100 suppressed: whereClause is built from compile-time constants above;
			// user input rides as $parameters.
#pragma warning disable CA2100
			cmd.CommandText = $"""
				SELECT COUNT(*)
				FROM audit_logs
				{whereClause};
				""";
#pragma warning restore CA2100
		}
	}

	private static AuditEntry ReadEntryRow(SqliteDataReader reader)
	{
		string id = reader.GetString(0);
		long tsMs = reader.GetInt64(1);
		string action = reader.GetString(2);
		string actor = reader.GetString(3);
		string? remoteIp = reader.IsDBNull(4) ? null : reader.GetString(4);
		string? accountName = reader.IsDBNull(5) ? null : reader.GetString(5);
		string? jobId = reader.IsDBNull(6) ? null : reader.GetString(6);
		string detailsJson = reader.GetString(7);

		Dictionary<string, object?>? details = JsonSerializer.Deserialize<Dictionary<string, object?>>(detailsJson, JsonDefaults.Options);

		return new AuditEntry(
			Id: id,
			Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(tsMs),
			Action: action,
			Actor: actor,
			RemoteIp: remoteIp,
			AccountName: accountName,
			JobId: jobId,
			Details: details
		);
	}

	private void Migrate()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS audit_logs (
				id TEXT PRIMARY KEY,
				ts_ms INTEGER NOT NULL,
				action TEXT NOT NULL,
				actor TEXT NOT NULL,
				remote_ip TEXT,
				account_name TEXT,
				job_id TEXT,
				details_json TEXT NOT NULL
			);
			CREATE INDEX IF NOT EXISTS idx_audit_logs_ts ON audit_logs (ts_ms DESC);
			CREATE INDEX IF NOT EXISTS idx_audit_logs_action ON audit_logs (action);
			CREATE INDEX IF NOT EXISTS idx_audit_logs_account ON audit_logs (account_name);
			""";
		cmd.ExecuteNonQuery();
	}
}
