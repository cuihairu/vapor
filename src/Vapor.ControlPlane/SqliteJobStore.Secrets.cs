using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Secret-payload redaction helpers and the purge/scrub/backup maintenance surface.</summary>
public sealed partial class SqliteJobStore
{
	internal static Dictionary<string, object?> StripSecretPayload(IReadOnlyDictionary<string, object?>? payload)
	{
		Dictionary<string, object?> result = new(StringComparer.OrdinalIgnoreCase);
		if (payload == null)
		{
			return result;
		}

		if (payload is not Dictionary<string, object?> dictionary)
		{
			foreach ((string key, object? value) in payload)
			{
				result[key] = value;
			}
		}
		else
		{
			foreach ((string key, object? value) in dictionary)
			{
				result[key] = value;
			}
		}

		foreach (string key in result.Keys.Where(IsSecretPayloadKey).ToArray())
		{
			result.Remove(key);
		}

		return result;
	}

	private static bool IsSecretPayloadKey(string key)
	{
		string normalized = new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
		return SecretPayloadKeys.Contains(normalized);
	}

	/// <inheritdoc />
	public async Task<(int Jobs, int Tasks)> PurgeSecretPayloadsAsync(bool dryRun, string? backupPath, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!dryRun && !string.IsNullOrWhiteSpace(backupPath))
			{
				EnsureBackupPathIsNotLiveDatabase(backupPath);
				await TakeConsistentBackupAsync(backupPath, cancellationToken).ConfigureAwait(false);
			}

			// A single transaction keeps the scrub atomic: an interruption (process
			// kill or cancellation) either leaves the pre-scrub rows untouched or
			// the fully scrubbed state, never a partially scrubbed database.
			using SqliteTransaction? tx = dryRun ? null : _connection.BeginTransaction();
			int jobRows = await ScrubSecretPayloadColumnAsync("jobs", "payload_json", dryRun, tx, cancellationToken).ConfigureAwait(false);
			int taskRows = await ScrubSecretPayloadColumnAsync("tasks", "payload_json", dryRun, tx, cancellationToken).ConfigureAwait(false);
			if (tx is not null)
			{
				tx.Commit();
			}
			return (jobRows, taskRows);
		}
		finally
		{
			_mutex.Release();
		}
	}

	private void EnsureBackupPathIsNotLiveDatabase(string backupPath)
	{
		// A backup path equal to the live database would make SqliteConnection.BackupDatabase
		// copy a file onto itself, corrupting the store. Compare full paths (case-insensitively
		// on Windows) so equivalent spellings are rejected too.
		if (string.Equals(_dbPath, ":memory:", StringComparison.Ordinal))
		{
			return;
		}

		string liveFullPath = Path.GetFullPath(_dbPath);
		string backupFullPath = Path.GetFullPath(backupPath);
		// Guard as a positive block (not a ternary): the Windows-only arm is
		// unreachable on Linux CI, and a ternary's skipped arm leaves a
		// permanently-uncovered branch (see tests/TESTING.md).
		StringComparison comparison = StringComparison.OrdinalIgnoreCase;
		if (!OperatingSystem.IsWindows())
		{
			comparison = StringComparison.Ordinal;
		}
		if (string.Equals(liveFullPath, backupFullPath, comparison))
		{
			throw new InvalidOperationException("backupPath must differ from the live database path");
		}
	}

	private async Task TakeConsistentBackupAsync(string backupPath, CancellationToken cancellationToken)
	{
		string? dir = Path.GetDirectoryName(Path.GetFullPath(backupPath));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}

		// Build the connection string via the builder so backupPath is carried as a
		// value, never interpolated raw into the connection string (resource
		// injection: a hostile path such as "x.db;Mode=Memory" must not be able to
		// alter connection-string keywords).
		var destinationBuilder = new SqliteConnectionStringBuilder
		{
			DataSource = backupPath,
		};
		await using var destination = new SqliteConnection(destinationBuilder.ToString());
		await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
		_connection.BackupDatabase(destination);
	}

	private async Task<int> ScrubSecretPayloadColumnAsync(string table, string column, bool dryRun, SqliteTransaction? transaction, CancellationToken cancellationToken)
	{
		// CA2100 suppressed: table/column come only from this class's own compile-time
		// literals ("jobs"/"tasks" + "payload_json"); no external input reaches the schema.
#pragma warning disable CA2100
		List<(string Id, string PayloadJson)> rows = new();
		using (var read = _connection.CreateCommand())
		{
			read.CommandText = $"SELECT id, {column} FROM {table} WHERE {column} IS NOT NULL AND {column} != '{{}}';";
			using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				rows.Add((reader.GetString(0), reader.GetString(1)));
			}
		}

		int rewritten = 0;
		foreach ((string id, string payloadJson) in rows)
		{
			Dictionary<string, object?>? payload;
			try
			{
				payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson, JsonDefaults.Options);
			}
			catch (JsonException)
			{
				// Malformed historical JSON is left untouched: scrubbing must never
				// destroy rows it cannot safely parse.
				continue;
			}

			if (payload is null || !payload.Keys.Any(IsSecretPayloadKey))
			{
				continue;
			}

			if (!dryRun)
			{
				Dictionary<string, object?> scrubbed = StripSecretPayload(payload);
				using var update = _connection.CreateCommand();
				update.Transaction = transaction;
				update.CommandText = $"UPDATE {table} SET {column} = $payload WHERE id = $id;";
				update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(scrubbed, JsonDefaults.Options));
				update.Parameters.AddWithValue("$id", id);
				await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			rewritten++;
		}
#pragma warning restore CA2100

		return rewritten;
	}

}
