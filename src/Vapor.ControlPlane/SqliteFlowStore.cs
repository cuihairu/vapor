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

/// <summary>A stored script flow: an ordered orchestration over the script repository.</summary>
public sealed record ScriptFlowRecord(
	string Id,
	string Name,
	string Description,
	IReadOnlyList<FlowStep> Steps,
	long CreatedAtMs,
	long UpdatedAtMs);

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
				INSERT INTO script_flows (id, name, description, steps, created_at_ms, updated_at_ms)
				VALUES ($id, $name, $description, $steps, $created, $updated)
				ON CONFLICT(id) DO UPDATE SET
					name=$name, description=$description, steps=$steps, updated_at_ms=$updated;
				""";
			cmd.Parameters.AddWithValue("$id", flow.Id);
			cmd.Parameters.AddWithValue("$name", flow.Name);
			cmd.Parameters.AddWithValue("$description", flow.Description);
			cmd.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(flow.Steps, JsonDefaults.Options));
			cmd.Parameters.AddWithValue("$created", flow.CreatedAtMs);
			cmd.Parameters.AddWithValue("$updated", flow.UpdatedAtMs);
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
			cmd.CommandText = "SELECT id, name, description, steps, created_at_ms, updated_at_ms FROM script_flows WHERE id = $id";
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
			cmd.CommandText = "SELECT id, name, description, steps, created_at_ms, updated_at_ms FROM script_flows ORDER BY name, id";
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
		return new ScriptFlowRecord(
			Id: reader.GetString(reader.GetOrdinal("id")),
			Name: reader.GetString(reader.GetOrdinal("name")),
			Description: reader.GetString(reader.GetOrdinal("description")),
			Steps: JsonSerializer.Deserialize<List<FlowStep>>(reader.GetString(reader.GetOrdinal("steps")), JsonDefaults.Options) ?? [],
			CreatedAtMs: reader.GetInt64(reader.GetOrdinal("created_at_ms")),
			UpdatedAtMs: reader.GetInt64(reader.GetOrdinal("updated_at_ms")));
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
				updated_at_ms INTEGER NOT NULL
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
	}
}
