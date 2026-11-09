using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Schema migration and column-add shims.</summary>
public sealed partial class SqliteJobStore
{
	private void Migrate()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			PRAGMA foreign_keys = ON;
			PRAGMA journal_mode = WAL;
			PRAGMA synchronous = NORMAL;

			CREATE TABLE IF NOT EXISTS jobs (
				id TEXT PRIMARY KEY,
				action TEXT NOT NULL,
				region TEXT NOT NULL DEFAULT '',
				targets_json TEXT NOT NULL,
				meta_json TEXT NOT NULL DEFAULT '{}',
				status TEXT NOT NULL,
				created_at_ms INTEGER NOT NULL,
				updated_at_ms INTEGER NOT NULL,
				payload_json TEXT NOT NULL DEFAULT '{}',
				schedule_json TEXT,
				schedule_next_run_ms INTEGER,
				parent_job_id TEXT
			);

			CREATE TABLE IF NOT EXISTS tasks (
				id TEXT PRIMARY KEY,
				job_id TEXT NOT NULL,
				target TEXT NOT NULL,
				action TEXT NOT NULL,
				region TEXT NOT NULL DEFAULT '',
				payload_json TEXT NOT NULL DEFAULT '{}',
				status TEXT NOT NULL,
				attempt INTEGER NOT NULL,
				created_at_ms INTEGER NOT NULL,
				updated_at_ms INTEGER NOT NULL,
				error TEXT,
				next_attempt_at_ms INTEGER NOT NULL DEFAULT 0,
				output_json TEXT,
				FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
			);

			CREATE INDEX IF NOT EXISTS idx_tasks_status_region_created ON tasks(status, region, created_at_ms);
			CREATE INDEX IF NOT EXISTS idx_tasks_job ON tasks(job_id);
			CREATE INDEX IF NOT EXISTS idx_jobs_created ON jobs(created_at_ms);
			""";
		cmd.ExecuteNonQuery();

		// Migrations for stores created before these columns existed. These must run
		// before the index block below — it references the schedule/parent columns.
		EnsureColumn("tasks", "error", "TEXT");
		EnsureColumn("tasks", "next_attempt_at_ms", "INTEGER NOT NULL DEFAULT 0");
		EnsureColumn("tasks", "output_json", "TEXT");
		EnsureColumn("tasks", "claimed_by_agent_id", "TEXT");
		EnsureColumn("jobs", "payload_json", "TEXT NOT NULL DEFAULT '{}'");
		EnsureColumn("jobs", "schedule_json", "TEXT");
		EnsureColumn("jobs", "schedule_next_run_ms", "INTEGER");
		EnsureColumn("jobs", "parent_job_id", "TEXT");

		using var indexes = _connection.CreateCommand();
		indexes.CommandText = """
			CREATE INDEX IF NOT EXISTS idx_jobs_schedule_due ON jobs(status, schedule_next_run_ms);
			CREATE INDEX IF NOT EXISTS idx_jobs_parent ON jobs(parent_job_id);
			""";
		indexes.ExecuteNonQuery();
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
