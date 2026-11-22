using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Task lifecycle tests: claim, heartbeat, requeue, fail, set-result, stale-runner recovery and recompute rollback.</summary>
public sealed partial class SqliteJobStoreTests
{
	[Fact]
	public async Task HeartbeatTaskReturnsTrueOnlyForMatchingRunningAttempt()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		bool matchingAttempt = await store.HeartbeatTask(claimed!.Id, claimed.Attempt, cts.Token);
		bool wrongAttempt = await store.HeartbeatTask(claimed.Id, claimed.Attempt + 1, cts.Token);

		Assert.True(matchingAttempt);
		Assert.False(wrongAttempt);
	}

	[Fact]
	public async Task SetTaskResult_OnQueuedNeverClaimedTask_ThrowsNotFound()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);
		var task = Assert.Single(created.Tasks);

		// The result transition only applies to Running tasks; reporting against a
		// task that was never claimed must fail loudly instead of silently updating.
		await Assert.ThrowsAsync<NotFoundException>(() => store.SetTaskResult(
			new TaskResult(task.Id, Success: true, Error: null, Output: null, DateTimeOffset.UtcNow),
			cts.Token));
	}

	[Fact]
	public async Task SetTaskResultMarksSuccessfulSingleTaskJobAsFinished()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], new Dictionary<string, object?> { ["message"] = "hello" }, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		DateTimeOffset beforeUpdate = claimed!.UpdatedAt;
		(JobTask task, Job job) = await store.SetTaskResult(
			new TaskResult(
				claimed.Id,
				true,
				null,
				new Dictionary<string, object?> { ["ok"] = true },
				DateTimeOffset.UtcNow,
				claimed.Attempt),
			cts.Token);

		Assert.Equal(JobTaskStatus.Finished, task.Status);
		Assert.Equal(claimed.Attempt, task.Attempt);
		Assert.NotEqual(beforeUpdate, task.UpdatedAt);
		Assert.Equal(JobStatus.Finished, job.Status);

		JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Finished, refreshed.Job.Status);
		Assert.Collection(refreshed.Tasks, refreshedTask => Assert.Equal(JobTaskStatus.Finished, refreshedTask.Status));
	}

	[Fact]
	public async Task SetTaskResultMarksJobFailedWhenAnyTaskFails()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1", "acct-2"], null, null),
			cts.Token);

		JobTask? first = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(first);

		await store.SetTaskResult(
			new TaskResult(first!.Id, false, "boom", null, DateTimeOffset.UtcNow, first.Attempt),
			cts.Token);

		JobWithTasks updated = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Failed, updated.Job.Status);
		Assert.Equal(1, updated.Tasks.Count(task => task.Status == JobTaskStatus.Failed));
		Assert.Equal(1, updated.Tasks.Count(task => task.Status == JobTaskStatus.Queued));
	}

	[Fact]
	public async Task SetTaskResultRejectsAttemptMismatch()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		await Assert.ThrowsAsync<NotFoundException>(() => store.SetTaskResult(
			new TaskResult(claimed!.Id, true, null, null, DateTimeOffset.UtcNow, claimed.Attempt + 1),
			cts.Token));
	}

	[Fact]
	public async Task RequeueTask_UnknownTask_IsNoop()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		// Must complete without throwing: an unknown task id simply has nothing to requeue.
		await store.RequeueTask("does-not-exist", TimeSpan.FromSeconds(5), cts.Token);
	}

	// ── row-vanished arms: another process can claim/finish a row between two of
	// the store's statements on a shared file. A BEFORE trigger that RAISE(IGNORE)s
	// the matching UPDATE makes the store's own statement report zero affected rows
	// — the deterministic form of that race (no wall-clock interleave). ──
	[Fact]
	public async Task ClaimNextQueuedTask_ClaimUpdateTouchesNothing_YieldsNullKeepingTaskQueued()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			JobWithTasks created = await store.CreateJob(
				new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);

			SabotageTaskUpdates(dbPath, "claim_sabotage", "NEW.status = 'Running'");

			Assert.Null(await store.ClaimNextQueuedTask("local", cts.Token));

			// The task is untouched: still queued for the next worker.
			JobWithTasks job = await store.GetJob(created.Job.Id, cts.Token);
			Assert.Equal(JobTaskStatus.Queued, job.Tasks.Single().Status);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	[Fact]
	public async Task SetTaskResult_ResultUpdateTouchesNothing_ThrowsNotFound()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			await store.CreateJob(new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
			JobTask claimed = (await store.ClaimNextQueuedTask("local", cts.Token))!;

			SabotageTaskUpdates(dbPath, "result_sabotage", "NEW.status = 'Finished'");

			var result = new TaskResult(claimed.Id, Success: true, Error: null, Output: null,
				DateTimeOffset.UtcNow, claimed.Attempt);
			NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(
				() => store.SetTaskResult(result, cts.Token));
			Assert.Equal("task not running", ex.Message);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	/// <summary>
	/// Installs a BEFORE trigger that silently no-ops the matching task UPDATE:
	/// RAISE(IGNORE) skips the row and reports zero changes, deterministically
	/// reproducing "another process took the row first" (see tests/TESTING.md).
	/// </summary>
	private static void SabotageTaskUpdates(string dbPath, string triggerName, string whenClause)
	{
		using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText =
			$"CREATE TRIGGER {triggerName} BEFORE UPDATE ON tasks WHEN {whenClause} " +
			"BEGIN SELECT RAISE(IGNORE); END;";
		cmd.ExecuteNonQuery();
	}

	[Fact]
	public async Task SetTaskResult_QueuedTaskNeverClaimed_ThrowsNotFound()
	{
		// The result guard requires a Running task; a still-queued one is rejected
		// the same way as an unknown id ("task not running").
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() => store.SetTaskResult(
			new TaskResult(created.Tasks[0].Id, true, null, null, DateTimeOffset.UtcNow),
			cts.Token));

		Assert.Equal("task not running", ex.Message);
	}

	[Fact]
	public async Task SetTaskResult_TaskWithEmptyJobId_ThrowsNotFound()
	{
		// Corrupt row: a task whose job_id is empty cannot be recomputed, so the
		// result must be rejected instead of silently updating the task.
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		RawExec(store, """
			INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms)
			VALUES ('', 'ping', 'local', '["acct-1"]', '{}', 'Running', $now, $now);
			INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms)
			VALUES ('orphan-task', '', 'acct-1', 'ping', 'local', '{}', 'Running', 1, $now, $now);
			""", ("$now", nowMs));

		NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() => store.SetTaskResult(
			new TaskResult("orphan-task", true, null, null, DateTimeOffset.UtcNow, 1),
			cts.Token));

		Assert.Equal("task not found", ex.Message);
	}

	[Fact]
	public async Task FailRunningTask_QueuedTaskNeverClaimed_ThrowsNotFound()
	{
		// The failing UPDATE only matches Running tasks; a queued task yields 0 rows
		// and must surface as NotFound instead of a silent no-op.
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() =>
			store.FailRunningTask(created.Tasks[0].Id, "boom", cts.Token));

		Assert.Equal("task not running", ex.Message);
	}

	[Fact]
	public async Task SetTaskResult_CanceledJob_IsNotRecomputed()
	{
		// RecomputeJob must leave canceled jobs alone (a late result for an already
		// canceled job must not resurrect it into Running/Finished).
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);
		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		// Simulate the cancel landing between the claim and the result report.
		RawExec(store, "UPDATE jobs SET status = 'Canceled' WHERE id = $id;", ("$id", created.Job.Id));

		(_, Job job) = await store.SetTaskResult(
			new TaskResult(claimed!.Id, true, null, null, DateTimeOffset.UtcNow, claimed.Attempt),
			cts.Token);

		Assert.Equal(JobStatus.Canceled, job.Status);
	}

	[Fact]
	public async Task SetTaskResult_PartiallyFinishedJob_StaysRunningForRemainingTasks()
	{
		// One of two tasks finished while the other is still queued: the job cannot
		// be Finished (work remains) nor Queued (progress happened) — it stays Running.
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1", "acct-2"], null, null),
			cts.Token);

		JobTask? first = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(first);
		await store.SetTaskResult(
			new TaskResult(first!.Id, true, null, null, DateTimeOffset.UtcNow, first.Attempt),
			cts.Token);

		JobWithTasks updated = await store.GetJob(created.Job.Id, cts.Token);

		Assert.Equal(JobStatus.Running, updated.Job.Status);
		Assert.Equal(1, updated.Tasks.Count(t => t.Status == JobTaskStatus.Finished));
		Assert.Equal(1, updated.Tasks.Count(t => t.Status == JobTaskStatus.Queued));
	}

	[Fact]
	public async Task SetTaskResult_RollsBackTaskUpdateWhenJobRecomputeFails()
	{
		// P2 atomicity: the task status flip and the job recomputation must be one
		// transaction. If RecomputeJob fails (here via a trigger that aborts the
		// UPDATE on jobs), the already-written task update must roll back — the task
		// stays Running and the job stays Running instead of a Finished/Failed task
		// next to a Running job.
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			JobWithTasks created = await store.CreateJob(
				new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
			JobTask claimed = (await store.ClaimNextQueuedTask("local", cts.Token))!;

			using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				raw.Open();
				using var cmd = raw.CreateCommand();
				cmd.CommandText =
					"CREATE TRIGGER recompute_abort BEFORE UPDATE ON jobs " +
					"WHEN NEW.status = 'Finished' BEGIN SELECT RAISE(ABORT, 'recompute aborted'); END;";
				cmd.ExecuteNonQuery();
			}

			var result = new TaskResult(claimed.Id, Success: true, Error: null, Output: null,
				DateTimeOffset.UtcNow, claimed.Attempt);
			await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
				() => store.SetTaskResult(result, cts.Token));

			// Both halves rolled back: the task is still Running and the job still
			// Running — no partial Finished task with a Running job.
			JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
			Assert.Equal(JobStatus.Running, refreshed.Job.Status);
			Assert.Equal(JobTaskStatus.Running, refreshed.Tasks.Single().Status);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	[Fact]
	public async Task FailRunningTask_RollsBackTaskUpdateWhenJobRecomputeFails()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			JobWithTasks created = await store.CreateJob(
				new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
			JobTask claimed = (await store.ClaimNextQueuedTask("local", cts.Token))!;

			using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				raw.Open();
				using var cmd = raw.CreateCommand();
				cmd.CommandText =
					"CREATE TRIGGER recompute_abort BEFORE UPDATE ON jobs " +
					"WHEN NEW.status = 'Failed' BEGIN SELECT RAISE(ABORT, 'recompute aborted'); END;";
				cmd.ExecuteNonQuery();
			}

			await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
				() => store.FailRunningTask(claimed.Id, "boom", cts.Token));

			JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
			Assert.Equal(JobStatus.Running, refreshed.Job.Status);
			Assert.Equal(JobTaskStatus.Running, refreshed.Tasks.Single().Status);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
			}
		}
	}

	[Fact]
	public async Task RequeueTask_RollsBackTaskUpdateWhenJobRecomputeFails()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			JobWithTasks created = await store.CreateJob(
				new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
			JobTask claimed = (await store.ClaimNextQueuedTask("local", cts.Token))!;

			using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				raw.Open();
				using var cmd = raw.CreateCommand();
				cmd.CommandText =
					"CREATE TRIGGER recompute_abort BEFORE UPDATE ON jobs " +
					"WHEN NEW.status = 'Queued' BEGIN SELECT RAISE(ABORT, 'recompute aborted'); END;";
				cmd.ExecuteNonQuery();
			}

			await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
				() => store.RequeueTask(claimed.Id, null, cts.Token));

			JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
			Assert.Equal(JobStatus.Running, refreshed.Job.Status);
			Assert.Equal(JobTaskStatus.Running, refreshed.Tasks.Single().Status);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
			}
		}
	}

	[Fact]
	public async Task RequeueTask_OrphanedTaskJobMissing_ThrowsNotFoundAndRollsBack()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
		JobTask claimed = (await store.ClaimNextQueuedTask("local", cts.Token))!;

		// Detach the task from its job: FK CASCADE would normally remove the task
		// with the job row, so briefly disable FK enforcement to leave tasks.job_id
		// pointing at a deleted job. The tx-transactional RecomputeJob path must
		// surface the "job not found" NotFoundException (tx-ReadJob miss) instead of
		// committing a requeue whose job no longer exists.
		RawExec(store, "PRAGMA foreign_keys = OFF;");
		RawExec(store, "DELETE FROM jobs WHERE id = $id", ("$id", created.Job.Id));
		RawExec(store, "PRAGMA foreign_keys = ON;");

		await Assert.ThrowsAsync<NotFoundException>(
			() => store.RequeueTask(claimed.Id, null, cts.Token));

		// The exception aborts the tx before commit: the task update rolls back.
		string status = RawScalar(store, "SELECT status FROM tasks WHERE id = $id", ("$id", claimed.Id));
		Assert.Equal(JobTaskStatus.Running.ToString(), status);
	}

	[Fact]
	public async Task RequeueStaleRunningTasks_RollsBackTaskUpdateWhenJobRecomputeFails()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			JobWithTasks created = await store.CreateJob(
				new CreateJobRequest("ping", "local", ["acct-1"], null, null), cts.Token);
			JobTask claimed = (await store.ClaimNextQueuedTask("local", cts.Token))!;
			MarkTaskStale(store, claimed.Id, DateTimeOffset.UtcNow.AddMinutes(-10));

			using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				raw.Open();
				using var cmd = raw.CreateCommand();
				cmd.CommandText =
					"CREATE TRIGGER recompute_abort BEFORE UPDATE ON jobs " +
					"WHEN NEW.status = 'Queued' BEGIN SELECT RAISE(ABORT, 'recompute aborted'); END;";
				cmd.ExecuteNonQuery();
			}

			await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
				() => store.RequeueStaleRunningTasks(TimeSpan.Zero, cts.Token));

			JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
			Assert.Equal(JobStatus.Running, refreshed.Job.Status);
			Assert.Equal(JobTaskStatus.Running, refreshed.Tasks.Single().Status);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
			}
		}
	}

	[Fact]
	public async Task RequeueTaskMovesJobBackToQueuedWhenNoOtherWorkRemains()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		await store.RequeueTask(claimed!.Id, null, cts.Token);

		JobWithTasks job = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Queued, job.Job.Status);
		Assert.Collection(job.Tasks, task =>
		{
			Assert.Equal(JobTaskStatus.Queued, task.Status);
			Assert.Equal(1, task.Attempt);
		});
	}

	[Fact]
	public async Task RequeueStaleRunningTasksMovesStaleRunningTaskBackToQueued()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		MarkTaskStale(store, claimed!.Id, DateTimeOffset.UtcNow.AddMinutes(-10));

		int requeued = await store.RequeueStaleRunningTasks(TimeSpan.Zero, cts.Token);

		Assert.Equal(1, requeued);

		JobWithTasks updated = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Queued, updated.Job.Status);
		Assert.Collection(updated.Tasks, task => Assert.Equal(JobTaskStatus.Queued, task.Status));
	}

	[Fact]
	public async Task HeartbeatTask_PreventsStaleRequeue()
	{
		// ARCH-06: a dispatched task whose lease is renewed by the agent's queued-task
		// heartbeat must not be moved back to Queued by the stale-requeue sweep. This is
		// what keeps a task sitting in the agent's bounded buffer from being re-dispatched.
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);
		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		MarkTaskStale(store, claimed!.Id, DateTimeOffset.UtcNow.AddMinutes(-10));
		Assert.True(await store.HeartbeatTask(claimed.Id, claimed.Attempt, cts.Token));

		int requeued = await store.RequeueStaleRunningTasks(TimeSpan.FromSeconds(1), cts.Token);
		Assert.Equal(0, requeued);
	}

	[Fact]
	public async Task RequeueStaleRunningTasksKeepsJobRunningWhenOtherRunningTasksRemain()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1", "acct-2"], null, null),
			cts.Token);

		JobTask? first = await store.ClaimNextQueuedTask("local", cts.Token);
		JobTask? second = await store.ClaimNextQueuedTask("local", cts.Token);

		Assert.NotNull(first);
		Assert.NotNull(second);

		await store.RequeueTask(first!.Id, null, cts.Token);

		JobWithTasks updated = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Running, updated.Job.Status);
		Assert.Equal(1, updated.Tasks.Count(task => task.Status == JobTaskStatus.Running));
		Assert.Equal(1, updated.Tasks.Count(task => task.Status == JobTaskStatus.Queued));
	}

	[Fact]
	public async Task MarkTaskClaimedByAttributesRunningTasksAndCountsThemByAgent()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1", "acct-2"], null, null),
			cts.Token);

		JobTask? first = await store.ClaimNextQueuedTask("local", cts.Token);
		JobTask? second = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(first);
		Assert.NotNull(second);

		// Running tasks carry no placement signal until dispatch attributes them.
		Assert.Empty(await store.CountRunningTasksByAgent(cts.Token));

		await store.MarkTaskClaimedBy(first!.Id, first.Attempt, "agent-a", cts.Token);
		await store.MarkTaskClaimedBy(second!.Id, second.Attempt, "agent-b", cts.Token);

		Dictionary<string, int> load = await store.CountRunningTasksByAgent(cts.Token);
		Assert.Equal(new Dictionary<string, int> { ["agent-a"] = 1, ["agent-b"] = 1 }, load);
	}

	[Fact]
	public async Task MarkTaskClaimedBy_FencesByAttemptAndStatus()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		// A stale attempt number must not attribute a superseded dispatch.
		await store.MarkTaskClaimedBy(claimed!.Id, claimed.Attempt + 1, "agent-a", cts.Token);
		Assert.Empty(await store.CountRunningTasksByAgent(cts.Token));

		await store.MarkTaskClaimedBy(claimed.Id, claimed.Attempt, "agent-a", cts.Token);
		Assert.Equal(new Dictionary<string, int> { ["agent-a"] = 1 }, await store.CountRunningTasksByAgent(cts.Token));

		// A finished task is no longer load.
		await store.SetTaskResult(
			new TaskResult(claimed.Id, true, null, null, DateTimeOffset.UtcNow, claimed.Attempt),
			cts.Token);
		Assert.Empty(await store.CountRunningTasksByAgent(cts.Token));
	}

	[Fact]
	public async Task RequeueStaleRunningTasks_ClearsTheClaimAttribution()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);
		await store.MarkTaskClaimedBy(claimed!.Id, claimed.Attempt, "agent-a", cts.Token);
		Assert.Equal(new Dictionary<string, int> { ["agent-a"] = 1 }, await store.CountRunningTasksByAgent(cts.Token));

		MarkTaskStale(store, claimed.Id, DateTimeOffset.UtcNow.AddMinutes(-10));
		int requeued = await store.RequeueStaleRunningTasks(TimeSpan.Zero, cts.Token);

		Assert.Equal(1, requeued);
		// The requeue must not leave the lost agent looking busy for the next placement.
		Assert.Empty(await store.CountRunningTasksByAgent(cts.Token));
	}

	[Fact]
	public async Task ClaimAttribution_SurvivesStoreReopen()
	{
		// Placement load is the dispatch signal, so it has to survive a control-plane
		// restart: an orphaned running task still holds the worker's attention.
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		// This is the only test in the file that opens the store twice, so it does two
		// cold connection-plus-schedule passes where its siblings do one. Every other
		// test here fits the shared 3 s budget with orders of magnitude to spare; this
		// one is the only one that can exhaust it on a starved runner, which happened on
		// a Windows Release job where a sibling SQLite test also ran 13 s instead of
		// ~1 ms. The deadline is still a deadline, just with room for cold work.
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		try
		{
			using (var store = new SqliteJobStore(dbPath))
			{
				await store.CreateJob(
					new CreateJobRequest("ping", "local", ["acct-1"], null, null),
					cts.Token);

				JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
				Assert.NotNull(claimed);
				await store.MarkTaskClaimedBy(claimed!.Id, claimed.Attempt, "agent-a", cts.Token);
			}

			using (var reopened = new SqliteJobStore(dbPath))
			{
				Dictionary<string, int> load = await reopened.CountRunningTasksByAgent(cts.Token);
				Assert.Equal(new Dictionary<string, int> { ["agent-a"] = 1 }, load);
			}
		}
		finally
		{
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	[Fact]
	public async Task RequeueTaskWithDelay_PostponesNextClaimUntilDelayElapses()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		await store.RequeueTask(claimed!.Id, TimeSpan.FromMinutes(5), cts.Token);

		// While the retry delay is in the future the task must not be claimable.
		Assert.Null(await store.ClaimNextQueuedTask("local", cts.Token));

		// Simulate the delay elapsing.
		MarkNextAttemptAt(store, claimed.Id, DateTimeOffset.UtcNow.AddSeconds(-1));

		JobTask? reclaimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(reclaimed);
		Assert.Equal(claimed.Id, reclaimed!.Id);
		Assert.Equal(2, reclaimed.Attempt);
	}

	[Fact]
	public async Task FailRunningTask_MarksTaskFailedWithErrorAndFailsJob()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		(JobTask failed, Job job) = await store.FailRunningTask(claimed!.Id, "no capable agent available", cts.Token);

		Assert.Equal(JobTaskStatus.Failed, failed.Status);
		Assert.Equal("no capable agent available", failed.Error);

		Assert.Equal(JobStatus.Failed, job.Status);

		JobWithTasks persisted = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Failed, persisted.Job.Status);
		Assert.Collection(persisted.Tasks, task =>
		{
			Assert.Equal(JobTaskStatus.Failed, task.Status);
			Assert.Equal("no capable agent available", task.Error);
		});
	}

	[Fact]
	public async Task FailRunningTask_RejectsNonRunningTask()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		// The task is still queued and was never claimed.
		await Assert.ThrowsAsync<NotFoundException>(() => store.FailRunningTask("does-not-exist", "boom", cts.Token));
	}

	[Fact]
	public async Task SetTaskResult_PersistsOutputAndErrorAcrossReopen()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			JobWithTasks created;
			string taskId;
			int attempt;
			using (var store = new SqliteJobStore(dbPath))
			{
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

				created = await store.CreateJob(
					new CreateJobRequest("ping", "local", ["acct-1"], null, null),
					cts.Token);

				JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
				Assert.NotNull(claimed);
				taskId = claimed!.Id;
				attempt = claimed.Attempt;

				await store.SetTaskResult(
					new TaskResult(
						taskId,
						false,
						"login refused",
						new Dictionary<string, object?> { ["errorCode"] = 42, ["detail"] = "rate limited" },
						DateTimeOffset.UtcNow,
						attempt),
					cts.Token);
			}

			// Reopen the database to prove output/error survive a restart.
			using (var reopened = new SqliteJobStore(dbPath))
			{
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
				JobWithTasks refreshed = await reopened.GetJob(created.Job.Id, cts.Token);

				JobTask task = Assert.Single(refreshed.Tasks);
				Assert.Equal(JobTaskStatus.Failed, task.Status);
				Assert.Equal("login refused", task.Error);
				Assert.NotNull(task.Output);
				Assert.Equal(42, ((System.Text.Json.JsonElement?)task.Output!["errorCode"])!.Value.GetInt32());
				Assert.Equal("rate limited", task.Output!["detail"]?.ToString());
			}
		}
		finally
		{
			try
			{
				if (File.Exists(dbPath))
				{
					File.Delete(dbPath);
				}
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	[Fact]
	public async Task SetTaskResult_SuccessfulTaskWithoutOutput_StaysNull()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(claimed);

		await store.SetTaskResult(
			new TaskResult(claimed!.Id, true, null, null, DateTimeOffset.UtcNow, claimed.Attempt),
			cts.Token);

		JobWithTasks refreshed = await store.GetJob(created.Job.Id, cts.Token);
		JobTask task = Assert.Single(refreshed.Tasks);
		Assert.Equal(JobTaskStatus.Finished, task.Status);
		Assert.Null(task.Error);
		Assert.Null(task.Output);
	}

	private static void MarkNextAttemptAt(SqliteJobStore store, string taskId, DateTimeOffset nextAttemptAt)
	{
		var connectionField = typeof(SqliteJobStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(connectionField);
		var connection = (Microsoft.Data.Sqlite.SqliteConnection?)connectionField!.GetValue(store);
		Assert.NotNull(connection);

		using var cmd = connection!.CreateCommand();
		cmd.CommandText = "UPDATE tasks SET next_attempt_at_ms = $next WHERE id = $id;";
		cmd.Parameters.AddWithValue("$next", nextAttemptAt.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$id", taskId);
		cmd.ExecuteNonQuery();
	}

	private static void MarkTaskStale(SqliteJobStore store, string taskId, DateTimeOffset updatedAt)
	{
		var connectionField = typeof(SqliteJobStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(connectionField);
		var connection = (Microsoft.Data.Sqlite.SqliteConnection?)connectionField!.GetValue(store);
		Assert.NotNull(connection);

		using var cmd = connection!.CreateCommand();
		cmd.CommandText = "UPDATE tasks SET updated_at_ms = $updated WHERE id = $id;";
		cmd.Parameters.AddWithValue("$updated", updatedAt.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$id", taskId);
		cmd.ExecuteNonQuery();
	}

	[Fact]
	public async Task FailRunningTask_ForTaskNotRunning_ThrowsNotFound()
	{
		// A queued (never-claimed) task is not running: the fail transition must reject
		// it instead of corrupting the state machine.
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		await Assert.ThrowsAsync<NotFoundException>(() =>
			store.FailRunningTask(created.Tasks[0].Id, "boom", cts.Token));
	}

}
