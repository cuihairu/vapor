using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Read surface: single-job, list and row projection helpers.</summary>
public sealed partial class SqliteJobStore
{
	public async Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<JobTask> tasks = await ReadTasks(jobId, cancellationToken).ConfigureAwait(false);
			return new JobWithTasks(job, tasks);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			if (string.IsNullOrWhiteSpace(account))
			{
				cmd.CommandText = """
					SELECT id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, schedule_json, schedule_next_run_ms
					FROM jobs
					ORDER BY created_at_ms DESC
					LIMIT $limit;
					""";
			}
			else
			{
				cmd.CommandText = """
					SELECT DISTINCT j.id, j.action, j.region, j.targets_json, j.meta_json, j.status, j.created_at_ms, j.updated_at_ms, j.schedule_json, j.schedule_next_run_ms
					FROM jobs j
					JOIN tasks t ON t.job_id = j.id
					WHERE t.target = $account
					ORDER BY j.created_at_ms DESC
					LIMIT $limit;
					""";
				cmd.Parameters.AddWithValue("$account", account.Trim());
			}

			cmd.Parameters.AddWithValue("$limit", limit);

			List<Job> jobs = new(limit);
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

	/// <inheritdoc />
	public async Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms, error, output_json
				FROM tasks
				WHERE target = $target
				ORDER BY created_at_ms DESC
				LIMIT $limit;
				""";
			cmd.Parameters.AddWithValue("$target", target);
			cmd.Parameters.AddWithValue("$limit", limit);

			List<JobTask> tasks = new();
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				tasks.Add(ReadTaskRow(reader));
			}

			return tasks;
		}
		finally
		{
			_mutex.Release();
		}
	}

	private async Task<string?> ReadTaskJobId(string taskId, CancellationToken cancellationToken)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT job_id FROM tasks WHERE id = $id;";
		cmd.Parameters.AddWithValue("$id", taskId);
		object? value = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return value?.ToString();
	}

	private async Task<Job> ReadJob(string jobId, CancellationToken cancellationToken)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			SELECT id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, schedule_json, schedule_next_run_ms
			FROM jobs
			WHERE id = $id;
			""";
		cmd.Parameters.AddWithValue("$id", jobId);

		using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			throw new NotFoundException("job not found");
		}

		return ReadJobRow(reader);
	}

	private async Task<Job> ReadJob(string jobId, CancellationToken cancellationToken, SqliteTransaction? tx)
	{
		using var cmd = _connection.CreateCommand();
		if (tx is not null)
		{
			cmd.Transaction = tx;
		}

		cmd.CommandText = """
			SELECT id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, schedule_json, schedule_next_run_ms
			FROM jobs
			WHERE id = $id;
			""";
		cmd.Parameters.AddWithValue("$id", jobId);

		using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			throw new NotFoundException("job not found");
		}

		return ReadJobRow(reader);
	}

	private static Job ReadJobRow(SqliteDataReader reader)
	{
		string id = reader.GetString(0);
		string action = reader.GetString(1);
		string region = reader.GetString(2);
		string targetsJson = reader.GetString(3);
		string metaJson = reader.GetString(4);
		string statusRaw = reader.GetString(5);
		long createdAtMs = reader.GetInt64(6);
		long updatedAtMs = reader.GetInt64(7);
		int scheduleOrdinal = reader.GetOrdinal("schedule_json");
		int nextRunOrdinal = reader.GetOrdinal("schedule_next_run_ms");
		string? scheduleJson = reader.IsDBNull(scheduleOrdinal) ? null : reader.GetString(scheduleOrdinal);
		long? nextRunMs = reader.IsDBNull(nextRunOrdinal) ? null : reader.GetInt64(nextRunOrdinal);

		List<string> targets = JsonSerializer.Deserialize<List<string>>(targetsJson, JsonDefaults.Options) ?? [];
		Dictionary<string, string>? meta = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonDefaults.Options);
		JobSchedule? schedule = string.IsNullOrEmpty(scheduleJson)
			? null
			: JsonSerializer.Deserialize<JobSchedule>(scheduleJson, JsonDefaults.Options);
		Enum.TryParse<JobStatus>(statusRaw, true, out var status);

		return new Job(
			Id: id,
			Action: action,
			Region: string.IsNullOrEmpty(region) ? null : region,
			Targets: targets,
			Meta: meta,
			Status: status,
			CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs),
			UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs),
			Schedule: schedule,
			NextRunAt: nextRunMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(nextRunMs.Value) : null
		);
	}

	private async Task<IReadOnlyList<JobTask>> ReadTasks(string jobId, CancellationToken cancellationToken)
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			SELECT id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms, error, output_json
			FROM tasks
			WHERE job_id = $jobId
			ORDER BY created_at_ms ASC;
			""";
		cmd.Parameters.AddWithValue("$jobId", jobId);

		using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<JobTask> tasks = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			tasks.Add(ReadTaskRow(reader));
		}

		return tasks;
	}

	private static JobTask ReadTaskRow(SqliteDataReader reader)
	{
		string id = reader.GetString(0);
		string jobId = reader.GetString(1);
		string target = reader.GetString(2);
		string action = reader.GetString(3);
		string region = reader.GetString(4);
		string payloadJson = reader.GetString(5);
		string statusRaw = reader.GetString(6);
		int attempt = reader.GetInt32(7);
		long createdAtMs = reader.GetInt64(8);
		long updatedAtMs = reader.GetInt64(9);
		string? error = reader.IsDBNull(10) ? null : reader.GetString(10);
		string? outputJson = reader.IsDBNull(11) ? null : reader.GetString(11);

		Dictionary<string, object?>? payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson, JsonDefaults.Options);
		Dictionary<string, object?>? output = string.IsNullOrEmpty(outputJson)
			? null
			: JsonSerializer.Deserialize<Dictionary<string, object?>>(outputJson, JsonDefaults.Options);
		Enum.TryParse<JobTaskStatus>(statusRaw, true, out var status);

		return new JobTask(
			Id: id,
			JobId: jobId,
			Target: target,
			Action: action,
			Region: string.IsNullOrEmpty(region) ? null : region,
			Payload: payload,
			Status: status,
			Attempt: attempt,
			CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs),
			UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs),
			Error: error,
			Output: output
		);
	}
}
