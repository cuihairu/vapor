using System.Text.Json;
using Microsoft.Data.Sqlite;
using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public sealed class SqliteJobStore : IJobStore, IDisposable {
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteJobStore(string dbPath) {
		if (string.IsNullOrWhiteSpace(dbPath)) {
			throw new ArgumentException("DB path is required", nameof(dbPath));
		}

		if (!string.Equals(dbPath, ":memory:", StringComparison.Ordinal)) {
			string? dir = Path.GetDirectoryName(dbPath);
			if (!string.IsNullOrEmpty(dir)) {
				Directory.CreateDirectory(dir);
			}
		}

		_connection = new SqliteConnection($"Data Source={dbPath}");
		_connection.Open();

		Migrate();
	}

	public void Dispose() {
		_connection.Dispose();
		_mutex.Dispose();
	}

	public async Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			DateTimeOffset now = DateTimeOffset.UtcNow;
			long nowMs = now.ToUnixTimeMilliseconds();

			string jobId = Id.New();
			string region = request.Region ?? "";
			string targetsJson = JsonSerializer.Serialize(request.Targets, JsonDefaults.Options);
			string metaJson = JsonSerializer.Serialize(request.Meta ?? new Dictionary<string, string>(), JsonDefaults.Options);

			using var tx = _connection.BeginTransaction();
			using (var cmd = _connection.CreateCommand()) {
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

			List<JobTask> tasks = new(request.Targets.Count);
			foreach (string target in request.Targets) {
				string taskId = Id.New();
				string payloadJson = JsonSerializer.Serialize(request.Payload ?? new Dictionary<string, object?>(), JsonDefaults.Options);

				using var cmd = _connection.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = """
					INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms)
					VALUES ($id, $jobId, $target, $action, $region, $payload, $status, $attempt, $created, $updated);
					""";
				cmd.Parameters.AddWithValue("$id", taskId);
				cmd.Parameters.AddWithValue("$jobId", jobId);
				cmd.Parameters.AddWithValue("$target", target);
				cmd.Parameters.AddWithValue("$action", request.Action);
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
					Action: request.Action,
					Region: string.IsNullOrEmpty(region) ? null : region,
					Payload: request.Payload,
					Status: JobTaskStatus.Queued,
					Attempt: 0,
					CreatedAt: now,
					UpdatedAt: now
				));
			}

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
		} finally {
			_mutex.Release();
		}
	}

	public async Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<JobTask> tasks = await ReadTasks(jobId, cancellationToken).ConfigureAwait(false);
			return new JobWithTasks(job, tasks);
		} finally {
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyList<Job>> ListJobs(int limit, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms
				FROM jobs
				ORDER BY created_at_ms DESC
				LIMIT $limit;
				""";
			cmd.Parameters.AddWithValue("$limit", limit);

			List<Job> jobs = new(limit);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
				jobs.Add(ReadJobRow(reader));
			}

			return jobs;
		} finally {
			_mutex.Release();
		}
	}

	public async Task CancelJob(string jobId, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			using var tx = _connection.BeginTransaction();

			long updated;
			using (var cmd = _connection.CreateCommand()) {
				cmd.Transaction = tx;
				cmd.CommandText = "UPDATE jobs SET status = $status, updated_at_ms = $updated WHERE id = $id;";
				cmd.Parameters.AddWithValue("$status", JobStatus.Canceled.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$id", jobId);
				updated = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			if (updated == 0) {
				throw new NotFoundException("job not found");
			}

			using (var cmd = _connection.CreateCommand()) {
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
		} finally {
			_mutex.Release();
		}
	}

	public async Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			using var tx = _connection.BeginTransaction();

			JobTask? task = null;
			using (var cmd = _connection.CreateCommand()) {
				cmd.Transaction = tx;
				cmd.CommandText = """
					SELECT id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms
					FROM tasks
					WHERE status = $queued AND (region = '' OR region = $region)
					ORDER BY created_at_ms ASC
					LIMIT 1;
					""";
				cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
				cmd.Parameters.AddWithValue("$region", region);

				using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
					task = ReadTaskRow(reader);
				}
			}

			if (task == null) {
				tx.Commit();
				return null;
			}

			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			using (var cmd = _connection.CreateCommand()) {
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
				if (updated == 0) {
					tx.Commit();
					return null;
				}
			}

			using (var cmd = _connection.CreateCommand()) {
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
			return task with {
				Status = JobTaskStatus.Running,
				Attempt = task.Attempt + 1,
				UpdatedAt = DateTimeOffset.UtcNow
			};
		} finally {
			_mutex.Release();
		}
	}

	public async Task RequeueTask(string taskId, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE tasks
				SET status = $queued, updated_at_ms = $updated
				WHERE id = $id AND status = $running;
				""";
			cmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
			cmd.Parameters.AddWithValue("$updated", nowMs);
			cmd.Parameters.AddWithValue("$id", taskId);
			cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());

			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		} finally {
			_mutex.Release();
		}
	}

	public async Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long cutoffMs = nowMs - (long) taskLease.TotalMilliseconds;

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
			return (int) updated;
		} finally {
			_mutex.Release();
		}
	}

	public async Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) {
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

			JobTaskStatus newStatus = result.Success ? JobTaskStatus.Finished : JobTaskStatus.Failed;

			string jobId;
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "SELECT job_id FROM tasks WHERE id = $id;";
				cmd.Parameters.AddWithValue("$id", result.TaskId);
				jobId = (string?) await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? "";
			}

			if (string.IsNullOrEmpty(jobId)) {
				throw new NotFoundException("task not found");
			}

			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "UPDATE tasks SET status = $status, updated_at_ms = $updated WHERE id = $id;";
				cmd.Parameters.AddWithValue("$status", newStatus.ToString());
				cmd.Parameters.AddWithValue("$updated", nowMs);
				cmd.Parameters.AddWithValue("$id", result.TaskId);
				await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}

			await RecomputeJob(jobId, cancellationToken).ConfigureAwait(false);

			Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
			IReadOnlyList<JobTask> tasks = await ReadTasks(jobId, cancellationToken).ConfigureAwait(false);
			JobTask task = tasks.First(t => t.Id == result.TaskId);

			return (task, job);
		} finally {
			_mutex.Release();
		}
	}

	private void Migrate() {
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
				updated_at_ms INTEGER NOT NULL
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
				FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
			);

			CREATE INDEX IF NOT EXISTS idx_tasks_status_region_created ON tasks(status, region, created_at_ms);
			CREATE INDEX IF NOT EXISTS idx_tasks_job ON tasks(job_id);
			CREATE INDEX IF NOT EXISTS idx_jobs_created ON jobs(created_at_ms);
			""";
		cmd.ExecuteNonQuery();
	}

	private async Task RecomputeJob(string jobId, CancellationToken cancellationToken) {
		Job job = await ReadJob(jobId, cancellationToken).ConfigureAwait(false);
		if (job.Status == JobStatus.Canceled) {
			return;
		}

		Dictionary<JobTaskStatus, int> counts = new();
		using (var cmd = _connection.CreateCommand()) {
			cmd.CommandText = "SELECT status, COUNT(*) FROM tasks WHERE job_id = $jobId GROUP BY status;";
			cmd.Parameters.AddWithValue("$jobId", jobId);

			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
				string raw = reader.GetString(0);
				int n = reader.GetInt32(1);
				if (Enum.TryParse<JobTaskStatus>(raw, true, out var st)) {
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
		if (running > 0) {
			newStatus = JobStatus.Running;
		} else if (queued > 0 && (finished > 0 || failed > 0 || canceled > 0)) {
			newStatus = JobStatus.Running;
		} else if (queued > 0) {
			newStatus = JobStatus.Queued;
		} else if (failed > 0) {
			newStatus = JobStatus.Failed;
		} else if (canceled > 0 && finished == 0 && failed == 0) {
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

	private async Task<Job> ReadJob(string jobId, CancellationToken cancellationToken) {
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			SELECT id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms
			FROM jobs
			WHERE id = $id;
			""";
		cmd.Parameters.AddWithValue("$id", jobId);

		using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
			throw new NotFoundException("job not found");
		}

		return ReadJobRow(reader);
	}

	private static Job ReadJobRow(SqliteDataReader reader) {
		string id = reader.GetString(0);
		string action = reader.GetString(1);
		string region = reader.GetString(2);
		string targetsJson = reader.GetString(3);
		string metaJson = reader.GetString(4);
		string statusRaw = reader.GetString(5);
		long createdAtMs = reader.GetInt64(6);
		long updatedAtMs = reader.GetInt64(7);

		List<string> targets = JsonSerializer.Deserialize<List<string>>(targetsJson, JsonDefaults.Options) ?? [];
		Dictionary<string, string>? meta = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, JsonDefaults.Options);
		Enum.TryParse<JobStatus>(statusRaw, true, out var status);

		return new Job(
			Id: id,
			Action: action,
			Region: string.IsNullOrEmpty(region) ? null : region,
			Targets: targets,
			Meta: meta,
			Status: status,
			CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs),
			UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs)
		);
	}

	private async Task<IReadOnlyList<JobTask>> ReadTasks(string jobId, CancellationToken cancellationToken) {
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			SELECT id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms
			FROM tasks
			WHERE job_id = $jobId
			ORDER BY created_at_ms ASC;
			""";
		cmd.Parameters.AddWithValue("$jobId", jobId);

		using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<JobTask> tasks = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
			tasks.Add(ReadTaskRow(reader));
		}

		return tasks;
	}

	private static JobTask ReadTaskRow(SqliteDataReader reader) {
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

		Dictionary<string, object?>? payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(payloadJson, JsonDefaults.Options);
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
			UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(updatedAtMs)
		);
	}
}
