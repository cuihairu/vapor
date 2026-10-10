using Microsoft.Data.Sqlite;

namespace Vapor.ControlPlane;

/// <summary>
/// One recorded webhook delivery attempt. Rows are appended per attempt (the
/// 1-based <see cref="Attempt"/> number), so a delivered-after-retries event has
/// failed rows followed by one delivered row. This is an audit trail of what
/// happened — not a replay queue: undelivered events are lost on restart.
/// </summary>
public sealed record WebhookDeliveryRecord(
	string NotificationId,
	string Category,
	string Type,
	string? JobId,
	string? AccountName,
	int Attempt,
	string Outcome,
	int? StatusCode,
	string? Error,
	long AttemptedAtMs);

/// <summary>
/// Durable delivery log for webhook notifications. Failures to record are
/// swallowed by callers (the log must never break delivery); the store itself
/// is append-only with no retention.
/// </summary>
public interface IWebhookDeliveryStore
{
	Task RecordAsync(WebhookDeliveryRecord record, CancellationToken cancellationToken);
	Task<IReadOnlyList<WebhookDeliveryRecord>> QueryAsync(string? notificationId = null, string? outcome = null, int limit = 100, int offset = 0, CancellationToken cancellationToken = default);
	Task<int> CountAsync(string? notificationId = null, string? outcome = null, CancellationToken cancellationToken = default);
}

/// <summary>SQLite-backed webhook delivery log (data/webhook.db).</summary>
public sealed class SqliteWebhookDeliveryStore : IWebhookDeliveryStore, IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteWebhookDeliveryStore(string dbPath)
	{
		if (string.IsNullOrWhiteSpace(dbPath))
		{
			throw new ArgumentException("DB path is required", nameof(dbPath));
		}

		if (!string.Equals(dbPath, ":memory:", StringComparison.Ordinal))
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

	public async Task RecordAsync(WebhookDeliveryRecord record, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentException.ThrowIfNullOrWhiteSpace(record.NotificationId);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO webhook_deliveries (notification_id, category, type, job_id, account_name, attempt, outcome, status_code, error, attempted_at_ms)
				VALUES ($notificationId, $category, $type, $jobId, $accountName, $attempt, $outcome, $statusCode, $error, $attemptedAtMs);
				""";
			cmd.Parameters.AddWithValue("$notificationId", record.NotificationId);
			cmd.Parameters.AddWithValue("$category", record.Category);
			cmd.Parameters.AddWithValue("$type", record.Type);
			cmd.Parameters.AddWithValue("$jobId", (object?)record.JobId ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$accountName", (object?)record.AccountName ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$attempt", record.Attempt);
			cmd.Parameters.AddWithValue("$outcome", record.Outcome);
			object? statusCode = record.StatusCode is int code ? code : DBNull.Value;
			cmd.Parameters.AddWithValue("$statusCode", statusCode);
			cmd.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$attemptedAtMs", record.AttemptedAtMs);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyList<WebhookDeliveryRecord>> QueryAsync(string? notificationId = null, string? outcome = null, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
	{
		int pageLimit = limit <= 0 ? 100 : Math.Min(limit, 500);
		int pageOffset = Math.Max(offset, 0);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// CA2100 suppressed: the WHERE fragments below are compile-time constants;
			// all user input rides as $parameters.
#pragma warning disable CA2100
			var where = new List<string>();
			if (!string.IsNullOrWhiteSpace(notificationId))
			{
				where.Add("notification_id = $notificationId");
			}

			if (!string.IsNullOrWhiteSpace(outcome))
			{
				where.Add("outcome = $outcome");
			}

			string whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = $"""
				SELECT notification_id, category, type, job_id, account_name, attempt, outcome, status_code, error, attempted_at_ms
				FROM webhook_deliveries
				{whereClause}
				ORDER BY attempted_at_ms DESC, rowid DESC
				LIMIT $limit OFFSET $offset;
				""";
#pragma warning restore CA2100
			BindFilters(cmd, notificationId, outcome);
			cmd.Parameters.AddWithValue("$limit", pageLimit);
			cmd.Parameters.AddWithValue("$offset", pageOffset);

			List<WebhookDeliveryRecord> records = [];
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				records.Add(ReadRow(reader));
			}

			return records;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<int> CountAsync(string? notificationId = null, string? outcome = null, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// CA2100 suppressed: the WHERE fragments are compile-time constants;
			// all user input rides as $parameters.
#pragma warning disable CA2100
			var where = new List<string>();
			if (!string.IsNullOrWhiteSpace(notificationId))
			{
				where.Add("notification_id = $notificationId");
			}

			if (!string.IsNullOrWhiteSpace(outcome))
			{
				where.Add("outcome = $outcome");
			}

			string whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = $"""
				SELECT COUNT(*)
				FROM webhook_deliveries
				{whereClause};
				""";
#pragma warning restore CA2100
			BindFilters(cmd, notificationId, outcome);

			object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			return (int)(long)result!; // the command is always SELECT COUNT(*), which SQLite returns as a boxed long (never NULL) — the is-long fallback would be an unreachable probe
		}
		finally
		{
			_mutex.Release();
		}
	}

	private static void BindFilters(SqliteCommand cmd, string? notificationId, string? outcome)
	{
		if (!string.IsNullOrWhiteSpace(notificationId))
		{
			cmd.Parameters.AddWithValue("$notificationId", notificationId.Trim());
		}

		if (!string.IsNullOrWhiteSpace(outcome))
		{
			cmd.Parameters.AddWithValue("$outcome", outcome.Trim());
		}
	}

	private static WebhookDeliveryRecord ReadRow(SqliteDataReader reader)
	{
		return new WebhookDeliveryRecord(
			NotificationId: reader.GetString(0),
			Category: reader.GetString(1),
			Type: reader.GetString(2),
			JobId: reader.IsDBNull(3) ? null : reader.GetString(3),
			AccountName: reader.IsDBNull(4) ? null : reader.GetString(4),
			Attempt: (int)reader.GetInt64(5),
			Outcome: reader.GetString(6),
			StatusCode: reader.IsDBNull(7) ? null : (int?)reader.GetInt64(7),
			Error: reader.IsDBNull(8) ? null : reader.GetString(8),
			AttemptedAtMs: reader.GetInt64(9)
		);
	}

	private void Migrate()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS webhook_deliveries (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				notification_id TEXT NOT NULL,
				category TEXT NOT NULL,
				type TEXT NOT NULL,
				job_id TEXT,
				account_name TEXT,
				attempt INTEGER NOT NULL,
				outcome TEXT NOT NULL,
				status_code INTEGER,
				error TEXT,
				attempted_at_ms INTEGER NOT NULL
			);
			CREATE INDEX IF NOT EXISTS idx_webhook_deliveries_notification ON webhook_deliveries (notification_id, attempt);
			CREATE INDEX IF NOT EXISTS idx_webhook_deliveries_attempted ON webhook_deliveries (attempted_at_ms DESC);
			""";
		cmd.ExecuteNonQuery();
	}
}
