using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Scheduled-template surface: due listing, child check, trigger, advance and trigger-read.</summary>
public sealed partial class SqliteJobStore
{
	public async Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, schedule_json, schedule_next_run_ms
				FROM jobs
				WHERE status = $status AND schedule_next_run_ms IS NOT NULL AND schedule_next_run_ms <= $now
				ORDER BY schedule_next_run_ms ASC
				LIMIT $limit;
				""";
			cmd.Parameters.AddWithValue("$status", JobStatus.Scheduled.ToString());
			cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$limit", limit);

			List<Job> jobs = [];
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				jobs.Add(ReadJobRow(reader));
			}

			return jobs;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT EXISTS(
					SELECT 1 FROM jobs
					WHERE parent_job_id = $template AND status IN ($queued, $running)
				);
				""";
			cmd.Parameters.AddWithValue("$template", templateJobId);
			cmd.Parameters.AddWithValue("$queued", JobStatus.Queued.ToString());
			cmd.Parameters.AddWithValue("$running", JobStatus.Running.ToString());

			object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			return Convert.ToInt64(result) != 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			using var tx = _connection.BeginTransaction();

			(string action, string region, List<string> targets, Dictionary<string, string> meta, Dictionary<string, object?> payload)? template =
				await ReadTemplateForTriggerAsync(tx, templateJobId, cancellationToken).ConfigureAwait(false);
			if (template == null)
			{
				return null;
			}

			var (action, region, targets, meta, payload) = template.Value;

			// Guarded advance: a template canceled concurrently leaves 0 rows updated.
			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					UPDATE jobs SET schedule_next_run_ms = $next, updated_at_ms = $updated
					WHERE id = $id AND status = $status;
					""";
				cmd.Parameters.AddWithValue("$next", nextRunAt.ToUnixTimeMilliseconds());
				cmd.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
				cmd.Parameters.AddWithValue("$id", templateJobId);
				cmd.Parameters.AddWithValue("$status", JobStatus.Scheduled.ToString());
				if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
				{
					return null;
				}
			}

			meta["scheduledFrom"] = templateJobId;
			if (extraMeta != null)
			{
				foreach ((string key, string value) in extraMeta)
				{
					meta[key] = value;
				}
			}

			string childJobId = Id.New();
			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json, parent_job_id)
					VALUES ($id, $action, $region, $targets, $meta, $status, $created, $updated, '{}', $parent);
					""";
				cmd.Parameters.AddWithValue("$id", childJobId);
				cmd.Parameters.AddWithValue("$action", action);
				cmd.Parameters.AddWithValue("$region", region);
				cmd.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(targets, JsonDefaults.Options));
				cmd.Parameters.AddWithValue("$meta", JsonSerializer.Serialize(meta, JsonDefaults.Options));
				cmd.Parameters.AddWithValue("$status", JobStatus.Queued.ToString());
				cmd.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
				cmd.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
				cmd.Parameters.AddWithValue("$parent", templateJobId);
				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			await InsertTasksAsync(tx, childJobId, action, region, targets, payload, now, cancellationToken).ConfigureAwait(false);

			tx.Commit();

			return new Job(
				Id: childJobId,
				Action: action,
				Region: string.IsNullOrEmpty(region) ? null : region,
				Targets: targets,
				Meta: meta,
				Status: JobStatus.Queued,
				CreatedAt: now,
				UpdatedAt: now
			);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE jobs SET schedule_next_run_ms = $next, updated_at_ms = $updated
				WHERE id = $id AND status = $status;
				""";
			cmd.Parameters.AddWithValue("$next", nextRunAt.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$id", templateJobId);
			cmd.Parameters.AddWithValue("$status", JobStatus.Scheduled.ToString());
			return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Reads a scheduled template's payload fields for a trigger; null when it is gone or no longer scheduled.</summary>
	private async Task<(string Action, string Region, List<string> Targets, Dictionary<string, string> Meta, Dictionary<string, object?> Payload)?> ReadTemplateForTriggerAsync(
		SqliteTransaction tx,
		string templateJobId,
		CancellationToken cancellationToken)
	{
		using var cmd = _connection.CreateCommand();
		cmd.Transaction = tx;
		cmd.CommandText = """
			SELECT action, region, targets_json, meta_json, payload_json
			FROM jobs
			WHERE id = $id AND status = $status;
			""";
		cmd.Parameters.AddWithValue("$id", templateJobId);
		cmd.Parameters.AddWithValue("$status", JobStatus.Scheduled.ToString());

		using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		string action = reader.GetString(0);
		string region = reader.GetString(1);
		List<string> targets = JsonSerializer.Deserialize<List<string>>(reader.GetString(2), JsonDefaults.Options) ?? [];
		Dictionary<string, string> meta = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3), JsonDefaults.Options) ?? [];
		Dictionary<string, object?> payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(reader.GetString(4), JsonDefaults.Options) ?? [];

		return (action, region, targets, meta, payload);
	}

}
