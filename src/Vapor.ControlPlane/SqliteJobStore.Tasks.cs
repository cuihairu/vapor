using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Task lifecycle: claim, requeue, stale requeue, heartbeat, agent claim, running counts and result/fail settle.</summary>
public sealed partial class SqliteJobStore
{
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
			using var tx = _connection.BeginTransaction();
			using var updateCmd = _connection.CreateCommand();
			updateCmd.Transaction = tx;
			updateCmd.CommandText = """
				UPDATE tasks
				SET status = $queued, updated_at_ms = $updated, next_attempt_at_ms = $nextAttemptAt
				WHERE id = $id AND status = $running;
				""";
			updateCmd.Parameters.AddWithValue("$queued", JobTaskStatus.Queued.ToString());
			updateCmd.Parameters.AddWithValue("$updated", nowMs);
			updateCmd.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAtMs);
			updateCmd.Parameters.AddWithValue("$id", taskId);
			updateCmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());

			long updated = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (updated > 0)
			{
				await RecomputeJob(jobId, cancellationToken, tx).ConfigureAwait(false);
			}
			tx.Commit();
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

			using var tx = _connection.BeginTransaction();
			using (var select = _connection.CreateCommand())
			{
				select.Transaction = tx;
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
			cmd.Transaction = tx;
			cmd.CommandText = """
				UPDATE tasks
				SET status = $queued, updated_at_ms = $updated, claimed_by_agent_id = NULL
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
					await RecomputeJob(jobId, cancellationToken, tx).ConfigureAwait(false);
				}
			}

			tx.Commit();

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

	/// <summary>
	/// Attributes a claimed (running) task to the agent it was dispatched to.
	/// Fenced by attempt like every other result write: a stale dispatch of a
	/// superseded attempt must not overwrite the newer attempt's attribution.
	/// A no-op when the task moved on (requeued, canceled or finished) — the
	/// dispatch itself has already happened, so there is nothing to surface.
	/// </summary>
	public async Task MarkTaskClaimedBy(string taskId, int attempt, string agentId, CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				UPDATE tasks
				SET claimed_by_agent_id = $agent
				WHERE id = $id AND status = $running AND attempt = $attempt;
				""";
			cmd.Parameters.AddWithValue("$agent", agentId);
			cmd.Parameters.AddWithValue("$id", taskId);
			cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());
			cmd.Parameters.AddWithValue("$attempt", attempt);

			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>
	/// Running tasks per agent (dispatch placement load signal). Only attributed
	/// rows count: unattributed running rows (claimed by an older build or a
	/// caller that did not attribute) read as zero load instead of blocking a
	/// candidate forever.
	/// </summary>
	public async Task<Dictionary<string, int>> CountRunningTasksByAgent(CancellationToken cancellationToken)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Dictionary<string, int> load = new(StringComparer.Ordinal);
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT claimed_by_agent_id, COUNT(*)
				FROM tasks
				WHERE status = $running AND claimed_by_agent_id IS NOT NULL
				GROUP BY claimed_by_agent_id;
				""";
			cmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());

			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				load[reader.GetString(0)] = reader.GetInt32(1);
			}

			return load;
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

			// The task status flip and the job recomputation are one atomic unit: a
			// crash between them would otherwise leave the task Finished/Failed while
			// the job still reports Running, contradicting the durable queue's
			// invariant that every result has a matching job state.
			using (var tx = _connection.BeginTransaction())
			{
				string outputJson = result.Output is null
					? string.Empty
					: JsonSerializer.Serialize(result.Output, JsonDefaults.Options);
				using var cmd = _connection.CreateCommand();
				cmd.Transaction = tx;
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

				await RecomputeJob(jobId, cancellationToken, tx).ConfigureAwait(false);
				tx.Commit();
			}

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

			using var tx = _connection.BeginTransaction();
			using var updateCmd = _connection.CreateCommand();
			updateCmd.Transaction = tx;
			updateCmd.CommandText = """
				UPDATE tasks
				SET status = $failed, error = $error, updated_at_ms = $updated
				WHERE id = $id AND status = $running;
				""";
			updateCmd.Parameters.AddWithValue("$failed", JobTaskStatus.Failed.ToString());
			updateCmd.Parameters.AddWithValue("$error", error);
			updateCmd.Parameters.AddWithValue("$updated", nowMs);
			updateCmd.Parameters.AddWithValue("$id", taskId);
			updateCmd.Parameters.AddWithValue("$running", JobTaskStatus.Running.ToString());

			long updated = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			if (updated == 0)
			{
				throw new NotFoundException("task not running");
			}

			await RecomputeJob(jobId, cancellationToken, tx).ConfigureAwait(false);
			tx.Commit();

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

}
