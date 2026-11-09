using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Recompute a job's aggregate status from its tasks.</summary>
public sealed partial class SqliteJobStore
{
	private async Task<bool> RecomputeJob(string jobId, CancellationToken cancellationToken, SqliteTransaction? tx = null)
	{
		Job job = await ReadJob(jobId, cancellationToken, tx).ConfigureAwait(false);
		if (job.Status == JobStatus.Canceled)
		{
			// A canceled job is terminal: the caller's task update still commits
			// (the late result is recorded), but the job must not be resurrected.
			// Inside a transaction this is a no-op for the jobs row, so the caller
			// commits the task update and the job stays Canceled.
			return false;
		}

		Dictionary<JobTaskStatus, int> counts = new();
		using (var cmd = _connection.CreateCommand())
		{
			if (tx is not null)
			{
				cmd.Transaction = tx;
			}
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

		// No "all tasks canceled" branch: every RecomputeJob caller has just moved a
		// task to Queued, Finished or Failed, so at least one of those counts is
		// always positive here. Canceled jobs are terminal (see the early return).

		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		using var update = _connection.CreateCommand();
		if (tx is not null)
		{
			update.Transaction = tx;
		}
		update.CommandText = "UPDATE jobs SET status = $status, updated_at_ms = $updated WHERE id = $id;";
		update.Parameters.AddWithValue("$status", newStatus.ToString());
		update.Parameters.AddWithValue("$updated", nowMs);
		update.Parameters.AddWithValue("$id", jobId);
		await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		return true;
	}

}
