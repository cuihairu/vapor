using Microsoft.Data.Sqlite;
using System.Text.Json;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// One ordered step of a script flow: which stored script runs on which agent,
/// and what happens to the remaining steps when it fails ("stop" halts the run,
/// "continue" keeps walking).
/// </summary>
public sealed record FlowStep(string ScriptId, string AgentId, string OnFailure);

/// <summary>
/// A stored script flow: an ordered orchestration over the script repository.
/// <paramref name="Schedule"/> is the optional recurring plan (same protocol shape as
/// scheduled job templates): when set, the recurring flow scheduler fires the flow per
/// plan with <see cref="ScriptFlowRecord.NextRunAtMs"/> as the due cursor; null means
/// the flow only runs through the manual run endpoint.
/// </summary>
public sealed record ScriptFlowRecord(
	string Id,
	string Name,
	string Description,
	IReadOnlyList<FlowStep> Steps,
	long CreatedAtMs,
	long UpdatedAtMs,
	JobSchedule? Schedule = null,
	long? NextRunAtMs = null);

/// <summary>
/// Terminal status of one executed step: "succeeded", "failed", "pending"
/// (the dispatched job never resolved inside the wait window) or "skipped"
/// (not attempted because the run halted before it).
/// </summary>
public sealed record FlowRunStep(
	int Index,
	string ScriptId,
	string AgentId,
	string Status,
	string? JobId = null,
	string? Error = null,
	IReadOnlyDictionary<string, object?>? Output = null);

/// <summary>
/// One execution of a script flow. Runs walk the steps sequentially on the
/// control plane; the terminal run status is "completed" (every step
/// succeeded), "completed_with_failures" (continued past failures), "failed"
/// (halted by a stop-policy failure) or "pending" (a step never resolved —
/// its job stays pollable via the jobs surface).
/// </summary>
public sealed record ScriptFlowRunRecord(
	string Id,
	string FlowId,
	string FlowName,
	string Status,
	IReadOnlyList<FlowRunStep> Steps,
	long StartedAtMs,
	long? FinishedAtMs = null);

/// <summary>
/// SQLite-backed script flow repository. Flows and their runs share the script
/// repository database (data/scripts.db): both aggregates belong to the same
/// operator-tooling feature and are always used together.
/// </summary>
public sealed class SqliteFlowStore : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteFlowStore(string dbPath)
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

	public async Task UpsertFlowAsync(ScriptFlowRecord flow, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(flow);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO script_flows (id, name, description, steps, created_at_ms, updated_at_ms, schedule, next_run_at_ms)
				VALUES ($id, $name, $description, $steps, $created, $updated, $schedule, $nextRunAt)
				ON CONFLICT(id) DO UPDATE SET
					name=$name, description=$description, steps=$steps, updated_at_ms=$updated,
					schedule=$schedule, next_run_at_ms=$nextRunAt;
				""";
			cmd.Parameters.AddWithValue("$id", flow.Id);
			cmd.Parameters.AddWithValue("$name", flow.Name);
			cmd.Parameters.AddWithValue("$description", flow.Description);
			cmd.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(flow.Steps, JsonDefaults.Options));
			cmd.Parameters.AddWithValue("$created", flow.CreatedAtMs);
			cmd.Parameters.AddWithValue("$updated", flow.UpdatedAtMs);
			cmd.Parameters.AddWithValue("$schedule", flow.Schedule is null ? DBNull.Value : JsonSerializer.Serialize(flow.Schedule, JsonDefaults.Options));
			cmd.Parameters.AddWithValue("$nextRunAt", flow.NextRunAtMs ?? (object)DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<ScriptFlowRecord?> GetFlowAsync(string id, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT id, name, description, steps, created_at_ms, updated_at_ms, schedule, next_run_at_ms FROM script_flows WHERE id = $id";
			cmd.Parameters.AddWithValue("$id", id);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadFlow(reader) : null;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>All flows ordered by name (ordinal), then id for stable ties.</summary>
	public async Task<List<ScriptFlowRecord>> ListFlowsAsync(CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT id, name, description, steps, created_at_ms, updated_at_ms, schedule, next_run_at_ms FROM script_flows ORDER BY name, id";
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			var flows = new List<ScriptFlowRecord>();
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				flows.Add(ReadFlow(reader));
			}

			return flows;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Removes the flow definition; past runs are kept as history. Returns false when the id is unknown.</summary>
	public async Task<bool> DeleteFlowAsync(string id, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM script_flows WHERE id = $id";
			cmd.Parameters.AddWithValue("$id", id);
			return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>
	/// Armed flows whose recurring cursor is due, oldest trigger point first. Only
	/// rows with both a schedule and a cursor qualify: <c>next_run_at_ms &lt;= $now</code>
	/// excludes a NULL cursor on its own (an armed flow whose cursor was nulled —
	/// disarmed, or a never-matching cron — never comes back on its own).
	/// </summary>
	public async Task<List<ScriptFlowRecord>> ListDueFlowsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT id, name, description, steps, created_at_ms, updated_at_ms, schedule, next_run_at_ms
				FROM script_flows
				WHERE schedule IS NOT NULL AND next_run_at_ms <= $now
				ORDER BY next_run_at_ms, id
				LIMIT $limit
				""";
			cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$limit", limit);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			var flows = new List<ScriptFlowRecord>();
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				flows.Add(ReadFlow(reader));
			}

			return flows;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>
	/// Moves one flow's recurring cursor forward after a decision about its due point
	/// (fired, skipped as overlap or downtime gap). Targeted update on purpose: the
	/// scheduler must not rewrite the whole row a concurrent PUT may be changing.
	/// Returns false when the flow is unknown.
	/// </summary>
	public async Task<bool> AdvanceFlowScheduleAsync(string id, DateTimeOffset nextRunAt, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "UPDATE script_flows SET next_run_at_ms = $nextRunAt WHERE id = $id";
			cmd.Parameters.AddWithValue("$nextRunAt", nextRunAt.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$id", id);
			return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>True while the flow has a run with status "running" — the overlap policy's in-flight signal.</summary>
	public async Task<bool> HasActiveFlowRunAsync(string flowId, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT 1 FROM script_flow_runs WHERE flow_id = $flowId AND status = 'running' LIMIT 1";
			cmd.Parameters.AddWithValue("$flowId", flowId);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task CreateRunAsync(ScriptFlowRunRecord run, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(run);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO script_flow_runs (id, flow_id, flow_name, status, steps, started_at_ms, finished_at_ms)
				VALUES ($id, $flowId, $flowName, $status, $steps, $started, $finished);
				""";
			cmd.Parameters.AddWithValue("$id", run.Id);
			cmd.Parameters.AddWithValue("$flowId", run.FlowId);
			cmd.Parameters.AddWithValue("$flowName", run.FlowName);
			cmd.Parameters.AddWithValue("$status", run.Status);
			cmd.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(run.Steps, JsonDefaults.Options));
			cmd.Parameters.AddWithValue("$started", run.StartedAtMs);
			cmd.Parameters.AddWithValue("$finished", run.FinishedAtMs ?? (object)DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Replaces the mutable columns of a run (status, steps, finish time); no-op when the id is unknown.</summary>
	public async Task UpdateRunAsync(ScriptFlowRunRecord run, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(run);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE script_flow_runs
				SET status=$status, steps=$steps, finished_at_ms=$finished
				WHERE id = $id;
				""";
			cmd.Parameters.AddWithValue("$id", run.Id);
			cmd.Parameters.AddWithValue("$status", run.Status);
			cmd.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(run.Steps, JsonDefaults.Options));
			cmd.Parameters.AddWithValue("$finished", run.FinishedAtMs ?? (object)DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<ScriptFlowRunRecord?> GetRunAsync(string id, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT id, flow_id, flow_name, status, steps, started_at_ms, finished_at_ms FROM script_flow_runs WHERE id = $id";
			cmd.Parameters.AddWithValue("$id", id);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Runs of one flow, most recent first (id as stable tiebreaker).</summary>
	public async Task<List<ScriptFlowRunRecord>> ListRunsAsync(string flowId, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT id, flow_id, flow_name, status, steps, started_at_ms, finished_at_ms FROM script_flow_runs WHERE flow_id = $flowId ORDER BY started_at_ms DESC, id";
			cmd.Parameters.AddWithValue("$flowId", flowId);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			var runs = new List<ScriptFlowRunRecord>();
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				runs.Add(ReadRun(reader));
			}

			return runs;
		}
		finally
		{
			_mutex.Release();
		}
	}

	private static ScriptFlowRecord ReadFlow(SqliteDataReader reader)
	{
		// Single reader helper keeps the column mapping in exactly one place.
		int scheduleOrdinal = reader.GetOrdinal("schedule");
		int nextRunOrdinal = reader.GetOrdinal("next_run_at_ms");
		return new ScriptFlowRecord(
			Id: reader.GetString(reader.GetOrdinal("id")),
			Name: reader.GetString(reader.GetOrdinal("name")),
			Description: reader.GetString(reader.GetOrdinal("description")),
			Steps: JsonSerializer.Deserialize<List<FlowStep>>(reader.GetString(reader.GetOrdinal("steps")), JsonDefaults.Options) ?? [],
			CreatedAtMs: reader.GetInt64(reader.GetOrdinal("created_at_ms")),
			UpdatedAtMs: reader.GetInt64(reader.GetOrdinal("updated_at_ms")),
			Schedule: reader.IsDBNull(scheduleOrdinal)
				? null
				: JsonSerializer.Deserialize<JobSchedule>(reader.GetString(scheduleOrdinal), JsonDefaults.Options),
			NextRunAtMs: reader.IsDBNull(nextRunOrdinal) ? null : reader.GetInt64(nextRunOrdinal));
	}

	private static ScriptFlowRunRecord ReadRun(SqliteDataReader reader)
	{
		int finishedOrdinal = reader.GetOrdinal("finished_at_ms");
		return new ScriptFlowRunRecord(
			Id: reader.GetString(reader.GetOrdinal("id")),
			FlowId: reader.GetString(reader.GetOrdinal("flow_id")),
			FlowName: reader.GetString(reader.GetOrdinal("flow_name")),
			Status: reader.GetString(reader.GetOrdinal("status")),
			Steps: JsonSerializer.Deserialize<List<FlowRunStep>>(reader.GetString(reader.GetOrdinal("steps")), JsonDefaults.Options) ?? [],
			StartedAtMs: reader.GetInt64(reader.GetOrdinal("started_at_ms")),
			FinishedAtMs: reader.IsDBNull(finishedOrdinal) ? null : reader.GetInt64(finishedOrdinal));
	}

	private void Migrate()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			PRAGMA journal_mode = WAL;
			PRAGMA synchronous = NORMAL;

			CREATE TABLE IF NOT EXISTS script_flows (
				id TEXT PRIMARY KEY,
				name TEXT NOT NULL,
				description TEXT NOT NULL,
				steps TEXT NOT NULL,
				created_at_ms INTEGER NOT NULL,
				updated_at_ms INTEGER NOT NULL,
				schedule TEXT,
				next_run_at_ms INTEGER
			);
			CREATE TABLE IF NOT EXISTS script_flow_runs (
				id TEXT PRIMARY KEY,
				flow_id TEXT NOT NULL,
				flow_name TEXT NOT NULL,
				status TEXT NOT NULL,
				steps TEXT NOT NULL,
				started_at_ms INTEGER NOT NULL,
				finished_at_ms INTEGER
			);
			""";
		cmd.ExecuteNonQuery();

		// Pre-schedule flow stores keep their rows; both new columns are nullable
		// so an existing flow stays a manual-only flow until it is re-armed.
		EnsureColumn("script_flows", "schedule", "TEXT");
		EnsureColumn("script_flows", "next_run_at_ms", "INTEGER");
	}

	private void EnsureColumn(string table, string column, string definition)
	{
		// CA2100 suppressed: table/column/definition come only from Migrate()'s
		// compile-time literals; no external input reaches the schema.
#pragma warning disable CA2100
		HashSet<string> existing = new(StringComparer.Ordinal);
		using (var cmd = _connection.CreateCommand())
		{
			cmd.CommandText = $"PRAGMA table_info({table});";
			using var reader = cmd.ExecuteReader();
			while (reader.Read())
			{
				existing.Add(reader.GetString(1));
			}
		}

		if (!existing.Contains(column))
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
			cmd.ExecuteNonQuery();
		}
#pragma warning restore CA2100
	}
}
