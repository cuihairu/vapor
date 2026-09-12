using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed class SqliteJobStore : IJobStore, IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteJobStore(string dbPath)
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

	public async Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
	{
		if (request.Schedule != null)
		{
			ScheduleClock.Validate(request.Schedule);
			return await CreateScheduledJobTemplate(request, cancellationToken).ConfigureAwait(false);
		}

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			long nowMs = now.ToUnixTimeMilliseconds();

			string jobId = Id.New();
			string region = request.Region ?? "";
			string targetsJson = JsonSerializer.Serialize(request.Targets, JsonDefaults.Options);
			string metaJson = JsonSerializer.Serialize(request.Meta ?? new Dictionary<string, string>(), JsonDefaults.Options);

			using var tx = _connection.BeginTransaction();
			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms)
					VALUES ($id, $action, $region, $targets, $meta, $status, $created, $updated);
					""";
				cmd.Parameters.AddWithValue("$id", jobId);
				cmd.Parameters.AddWithValue("$action", request.Action);
				cmd.Parameters.AddWithValue("$region", region);
				cmd.Parameters.AddWithValue("$targets", targetsJson);
				cmd.Parameters.AddWithValue("$meta", metaJson);
				cmd.Parameters.AddWithValue("$status", JobStatus.Queued.ToString());
				cmd.Parameters.AddWithValue("$created", nowMs);
				cmd.Parameters.AddWithValue("$updated", nowMs);
				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			List<JobTask> tasks = await InsertTasksAsync(tx, jobId, request.Action, region, request.Targets, request.Payload, now, cancellationToken).ConfigureAwait(false);

			tx.Commit();

			Job job = new(
				Id: jobId,
				Action: request.Action,
				Region: string.IsNullOrEmpty(region) ? null : region,
				Targets: request.Targets,
				Meta: request.Meta,
				Status: JobStatus.Queued,
				CreatedAt: now,
				UpdatedAt: now
			);

			return new JobWithTasks(job, tasks);
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Creates a recurring job template: no tasks, <see cref="JobStatus.Scheduled"/> status, persisted schedule and first trigger point.</summary>
	private async Task<JobWithTasks> CreateScheduledJobTemplate(CreateJobRequest request, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			DateTimeOffset now = DateTimeOffset.UtcNow;
			DateTimeOffset? next = ScheduleClock.NextRun(request.Schedule!, now);
			if (next == null)
			{
				throw new ArgumentException("schedule cron expression never matches");
			}

			string jobId = Id.New();
			string region = request.Region ?? "";
			string targetsJson = JsonSerializer.Serialize(request.Targets, JsonDefaults.Options);
			string metaJson = JsonSerializer.Serialize(request.Meta ?? new Dictionary<string, string>(), JsonDefaults.Options);
			string payloadJson = JsonSerializer.Serialize(request.Payload ?? new Dictionary<string, object?>(), JsonDefaults.Options);
			string scheduleJson = JsonSerializer.Serialize(request.Schedule, JsonDefaults.Options);

			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json, schedule_json, schedule_next_run_ms)
				VALUES ($id, $action, $region, $targets, $meta, $status, $created, $updated, $payload, $schedule, $nextRun);
				""";
			cmd.Parameters.AddWithValue("$id", jobId);
			cmd.Parameters.AddWithValue("$action", request.Action);
			cmd.Parameters.AddWithValue("$region", region);
			cmd.Parameters.AddWithValue("$targets", targetsJson);
			cmd.Parameters.AddWithValue("$meta", metaJson);
			cmd.Parameters.AddWithValue("$status", JobStatus.Scheduled.ToString());
			cmd.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
			cmd.Parameters.AddWithValue("$payload", payloadJson);
			cmd.Parameters.AddWithValue("$schedule", scheduleJson);
			cmd.Parameters.AddWithValue("$nextRun", next.Value.ToUnixTimeMilliseconds());
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

			Job job = new(
				Id: jobId,
				Action: request.Action,
				Region: string.IsNullOrEmpty(region) ? null : region,
				Targets: request.Targets,
				Meta: request.Meta,
				Status: JobStatus.Scheduled,
				CreatedAt: now,
				UpdatedAt: now,
				Schedule: request.Schedule,
				NextRunAt: next
			);

			return new JobWithTasks(job, []);
		}
		finally
		{
			_mutex.Release();
		}
	}

	private async Task<List<JobTask>> InsertTasksAsync(
		SqliteTransaction tx,
		string jobId,
		string action,
		string region,
		IReadOnlyList<string> targets,
		IReadOnlyDictionary<string, object?>? payload,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		long nowMs = now.ToUnixTimeMilliseconds();
		string payloadJson = JsonSerializer.Serialize(payload ?? new Dictionary<string, object?>(), JsonDefaults.Options);

		List<JobTask> tasks = new(targets.Count);
		foreach (string target in targets)
		{
			string taskId = Id.New();

			using var cmd = _connection.CreateCommand();
			cmd.Transaction = tx;
			cmd.CommandText = """
				INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms)
				VALUES ($id, $jobId, $target, $action, $region, $payload, $status, $attempt, $created, $updated);
				""";
			cmd.Parameters.AddWithValue("$id", taskId);
			cmd.Parameters.AddWithValue("$jobId", jobId);
			cmd.Parameters.AddWithValue("$target", target);
			cmd.Parameters.AddWithValue("$action", action);
			cmd.Parameters.AddWithValue("$region", region);
			cmd.Parameters.AddWithValue("$payload", payloadJson);
			cmd.Parameters.AddWithValue("$status", JobTaskStatus.Queued.ToString());
			cmd.Parameters.AddWithValue("$attempt", 0);
			cmd.Parameters.AddWithValue("$created", nowMs);
			cmd.Parameters.AddWithValue("$updated", nowMs);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

			tasks.Add(new JobTask(
				Id: taskId,
				JobId: jobId,
				Target: target,
				Action: action,
				Region: string.IsNullOrEmpty(region) ? null : region,
				Payload: payload,
				Status: JobTaskStatus.Queued,
				Attempt: 0,
				CreatedAt: now,
				UpdatedAt: now
			));
		}

		return tasks;
	}

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

	public async Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var now = DateTimeOffset.UtcNow;
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			using var tx = _connection.BeginTransaction();

			long updated;
			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = "UPDATE jobs SET status = $status, updated_at_ms = $updated WHERE id = $id;";
				cmd.Parameters.AddWithValue("$status", JobStatus.Canceled.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$id", jobId);
				updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			if (updated == 0)
			{
				throw new NotFoundException("job not found");
			}

			List<TaskCancel> running = [];
			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					SELECT id, attempt
					FROM tasks
					WHERE job_id = $jobId AND status = $running;
					""";
				cmd.Parameters.AddWithValue("$jobId", jobId);
				cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
				using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					string taskId = reader.GetString(0);
					int attempt = reader.GetInt32(1);
					running.Add(new TaskCancel(taskId, attempt, now, "job canceled"));
				}
			}

			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					UPDATE tasks
					SET status = $status, updated_at_ms = $updated
					WHERE job_id = $jobId AND status IN ($queued, $running);
					""";
				cmd.Parameters.AddWithValue("$status", JobTaskStatus.Canceled.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$jobId", jobId);
				cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
				cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			tx.Commit();
			return running;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var tx = _connection.BeginTransaction();

			JobTask? task = null;
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					SELECT id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms, error, output_json
					FROM tasks
					WHERE status = $queued AND (region = '' OR region = $region) AND next_attempt_at_ms <= $now
					ORDER BY created_at_ms ASC
					LIMIT 1;
					""";
				cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
				cmd.Parameters.AddWithValue("$region", region);
				cmd.Parameters.AddWithValue("$now", nowMs);

				using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					task = ReadTaskRow(reader);
				}
			}

			if (task == null)
			{
				tx.Commit();
				return null;
			}

			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					UPDATE tasks
					SET status = $running, attempt = attempt + 1, updated_at_ms = $updated
					WHERE id = $id AND status = $queued;
					""";
				cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$id", task.Id);
				cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());

				long updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				if (updated == 0)
				{
					tx.Commit();
					return null;
				}
			}

			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = """
					UPDATE jobs
					SET status = $running, updated_at_ms = $updated
					WHERE id = $id AND status != $canceled;
					""";
				cmd.Parameters.AddWithValue("$running", JobStatus.Running.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$id", task.JobId);
				cmd.Parameters.AddWithValue("$canceled", JobStatus.Canceled.ToString());
				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			tx.Commit();

			// Return the in-memory view of the updated task (attempt/status/updatedAt).
			return task with
			{
				Status = JobTaskStatus.Running,
				Attempt = task.Attempt + 1,
				UpdatedAt = DateTimeOffset.UtcNow
			};
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			string? jobId = await ReadTaskJobId(taskId, cancellationToken).ConfigureAwait(false);
			if (jobId == null)
			{
				return;
			}

			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long nextAttemptAtMs = nowMs + (long)(retryDelay?.TotalMilliseconds ?? 0);
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE tasks
				SET status = $queued, updated_at_ms = $updated, next_attempt_at_ms = $nextAttemptAt
				WHERE id = $id AND status = $running;
				""";
			cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
			cmd.Parameters.AddWithValue("$updated", nowMs);
			cmd.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAtMs);
			cmd.Parameters.AddWithValue("$id", taskId);
			cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());

			long updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (updated > 0)
			{
				await RecomputeJob(jobId, cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long cutoffMs = nowMs - (long)taskLease.TotalMilliseconds;
			HashSet<string> affectedJobIds = [];

			using (var select = _connection.CreateCommand())
			{
				select.CommandText = """
					SELECT DISTINCT job_id
					FROM tasks
					WHERE status = $running AND updated_at_ms < $cutoff;
					""";
				select.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
				select.Parameters.AddWithValue("$cutoff", cutoffMs);

				using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					affectedJobIds.Add(reader.GetString(0));
				}
			}

			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE tasks
				SET status = $queued, updated_at_ms = $updated
				WHERE status = $running AND updated_at_ms < $cutoff;
				""";
			cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
			cmd.Parameters.AddWithValue("$updated", nowMs);
			cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
			cmd.Parameters.AddWithValue("$cutoff", cutoffMs);

			long updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (updated > 0)
			{
				foreach (string jobId in affectedJobIds)
				{
					await RecomputeJob(jobId, cancellationToken).ConfigureAwait(false);
				}
			}

			return (int)updated;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE tasks
				SET updated_at_ms = $updated
				WHERE id = $id AND status = $running AND attempt = $attempt;
				""";
			cmd.Parameters.AddWithValue("$updated", nowMs);
			cmd.Parameters.AddWithValue("$id", taskId);
			cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
			cmd.Parameters.AddWithValue("$attempt", attempt);

			long updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			return updated > 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Dictionary<JobTaskStatus, int> counts = new();
			using (var cmd = _connection.CreateCommand())
			{
				cmd.CommandText = "SELECT status, COUNT(*) FROM tasks GROUP BY status;";

				using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					string raw = reader.GetString(0);
					int n = reader.GetInt32(1);
					if (Enum.TryParse<JobTaskStatus>(raw, true, out var st))
					{
						counts[st] = n;
					}
				}
			}

			return counts;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			JobTaskStatus newStatus = result.Success ? JobTaskStatus.Finished : JobTaskStatus.Failed;

			string jobId;
			int currentAttempt;
			string currentStatusRaw;
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "SELECT job_id, status, attempt FROM tasks WHERE id = $id;";
				cmd.Parameters.AddWithValue("$id", result.TaskId);
				using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					throw new NotFoundException("task not found");
				}

				jobId = reader.GetString(0);
				currentStatusRaw = reader.GetString(1);
				currentAttempt = reader.GetInt32(2);
			}

			if (string.IsNullOrEmpty(jobId))
			{
				throw new NotFoundException("task not found");
			}

			if (!Enum.TryParse<JobTaskStatus>(currentStatusRaw, true, out var currentStatus) || currentStatus != JobTaskStatus.Running)
			{
				throw new NotFoundException("task not running");
			}

			if (result.Attempt > 0 && currentAttempt != result.Attempt)
			{
				throw new NotFoundException("task attempt mismatch");
			}

			{
				string outputJson = result.Output is null
					? string.Empty
					: JsonSerializer.Serialize(result.Output, JsonDefaults.Options);
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = """
					UPDATE tasks
					SET status = $status, updated_at_ms = $updated, error = $error, output_json = $outputJson
					WHERE id = $id AND status = $running;
					""";
				cmd.Parameters.AddWithValue("$status", newStatus.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$outputJson", outputJson);
				cmd.Parameters.AddWithValue("$id", result.TaskId);
				cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
				long updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				if (updated == 0)
				{
					throw new NotFoundException("task not running");
				}
			}

			await RecomputeJob(jobId, cancellationToken).ConfigureAwait(false);

			Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<JobTask> tasks = await ReadTasks(jobId, cancellationToken).ConfigureAwait(false);
			JobTask task = tasks.First(t => t.Id == result.TaskId);

			return (task, job);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			string jobId;

			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "SELECT job_id FROM tasks WHERE id = $id;";
				cmd.Parameters.AddWithValue("$id", taskId);
				using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					throw new NotFoundException("task not found");
				}

				jobId = reader.GetString(0);
			}

			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = """
					UPDATE tasks
					SET status = $failed, error = $error, updated_at_ms = $updated
					WHERE id = $id AND status = $running;
					""";
				cmd.Parameters.AddWithValue("$failed", JobTaskStatus.Failed.ToString());
				cmd.Parameters.AddWithValue("$error", error);
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$id", taskId);
				cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());

				long updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
				if (updated == 0)
				{
					throw new NotFoundException("task not running");
				}
			}

			await RecomputeJob(jobId, cancellationToken).ConfigureAwait(false);

			Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<JobTask> tasks = await ReadTasks(jobId, cancellationToken).ConfigureAwait(false);
			JobTask task = tasks.First(t => t.Id == taskId);

			return (task, job);
		}
		finally
		{
			_mutex.Release();
		}
	}

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
	}

	private async Task RecomputeJob(string jobId, CancellationToken cancellationToken)
	{
		Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
		if (job.Status == JobStatus.Canceled)
		{
			return;
		}

		Dictionary<JobTaskStatus, int> counts = new();
		using (var cmd = _connection.CreateCommand())
		{
			cmd.CommandText = "SELECT status, COUNT(*) FROM tasks WHERE job_id = $jobId GROUP BY status;";
			cmd.Parameters.AddWithValue("$jobId", jobId);

			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				string raw = reader.GetString(0);
				int n = reader.GetInt32(1);
				if (Enum.TryParse<JobTaskStatus>(raw, true, out var st))
				{
					counts[st] = n;
				}
			}
		}

		int queued = counts.GetValueOrDefault(JobTaskStatus.Queued);
		int running = counts.GetValueOrDefault(JobTaskStatus.Running);
		int finished = counts.GetValueOrDefault(JobTaskStatus.Finished);
		int failed = counts.GetValueOrDefault(JobTaskStatus.Failed);
		int canceled = counts.GetValueOrDefault(JobTaskStatus.Canceled);

		JobStatus newStatus = JobStatus.Finished;
		if (running > 0)
		{
			newStatus = JobStatus.Running;
		}
		else if (failed > 0)
		{
			newStatus = JobStatus.Failed;
		}
		else if (queued > 0 && (finished > 0 || canceled > 0))
		{
			newStatus = JobStatus.Running;
		}
		else if (queued > 0)
		{
			newStatus = JobStatus.Queued;
		}
		else if (canceled > 0 && finished == 0 && failed == 0)
		{
			newStatus = JobStatus.Canceled;
		}

		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		using var update = _connection.CreateCommand();
		update.CommandText = "UPDATE jobs SET status = $status, updated_at_ms = $updated WHERE id = $id;";
		update.Parameters.AddWithValue("$status", newStatus.ToString());
		update.Parameters.AddWithValue("$updated", nowMs);
		update.Parameters.AddWithValue("$id", jobId);
		await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
