using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Job/task write path: create (inline and scheduled-template) and task-row insert.</summary>
public sealed partial class SqliteJobStore
{
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
			Dictionary<string, object?> templatePayload = StripSecretPayload(request.Payload);
			string payloadJson = JsonSerializer.Serialize(templatePayload, JsonDefaults.Options);
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
		Dictionary<string, object?> persistedPayload = StripSecretPayload(payload);
		string payloadJson = JsonSerializer.Serialize(persistedPayload, JsonDefaults.Options);

		List<JobTask> tasks = new(targets.Count);
		foreach (string target in targets)
		{
			string taskId = Id.New();

			using var updateCmd = _connection.CreateCommand();
			updateCmd.Transaction = tx;
			updateCmd.CommandText = """
				INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms)
				VALUES ($id, $jobId, $target, $action, $region, $payload, $status, $attempt, $created, $updated);
				""";
			updateCmd.Parameters.AddWithValue("$id", taskId);
			updateCmd.Parameters.AddWithValue("$jobId", jobId);
			updateCmd.Parameters.AddWithValue("$target", target);
			updateCmd.Parameters.AddWithValue("$action", action);
			updateCmd.Parameters.AddWithValue("$region", region);
			updateCmd.Parameters.AddWithValue("$payload", payloadJson);
			updateCmd.Parameters.AddWithValue("$status", JobTaskStatus.Queued.ToString());
			updateCmd.Parameters.AddWithValue("$attempt", 0);
			updateCmd.Parameters.AddWithValue("$created", nowMs);
			updateCmd.Parameters.AddWithValue("$updated", nowMs);
			await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

			tasks.Add(new JobTask(
				Id: taskId,
				JobId: jobId,
				Target: target,
				Action: action,
				Region: string.IsNullOrEmpty(region) ? null : region,
				Payload: persistedPayload,
				Status: JobTaskStatus.Queued,
				Attempt: 0,
				CreatedAt: now,
				UpdatedAt: now
			));
		}

		return tasks;
	}

}
