using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Cancellation: cancel a job and its tasks with the fence record.</summary>
public sealed partial class SqliteJobStore
{
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

}
