using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SqliteJobStoreTests {
	[Fact]
	public async Task HeartbeatTaskReturnsTrueOnlyForMatchingRunningAttempt() {
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
	public async Task SetTaskResultMarksSuccessfulSingleTaskJobAsFinished() {
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
	public async Task SetTaskResultMarksJobFailedWhenAnyTaskFails() {
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
	public async Task SetTaskResultRejectsAttemptMismatch() {
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
	public async Task RequeueTaskMovesJobBackToQueuedWhenNoOtherWorkRemains() {
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
		Assert.Collection(job.Tasks, task => {
			Assert.Equal(JobTaskStatus.Queued, task.Status);
			Assert.Equal(1, task.Attempt);
		});
	}

	[Fact]
	public async Task RequeueStaleRunningTasksMovesStaleRunningTaskBackToQueued() {
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
	public async Task RequeueStaleRunningTasksKeepsJobRunningWhenOtherRunningTasksRemain() {
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
	public async Task RequeueTaskWithDelay_PostponesNextClaimUntilDelayElapses() {
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
	public async Task FailRunningTask_MarksTaskFailedWithErrorAndFailsJob() {
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
		Assert.Collection(persisted.Tasks, task => {
			Assert.Equal(JobTaskStatus.Failed, task.Status);
			Assert.Equal("no capable agent available", task.Error);
		});
	}

	[Fact]
	public async Task FailRunningTask_RejectsNonRunningTask() {
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		// The task is still queued and was never claimed.
		await Assert.ThrowsAsync<NotFoundException>(() => store.FailRunningTask("does-not-exist", "boom", cts.Token));
	}

	[Fact]
	public async Task Migrate_AddsNewColumnsToLegacyDatabase() {
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		string jobId = "legacy-job";
		string taskId = "legacy-task";
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		try {
			// Build a pre-migration database: tasks table without error / next_attempt_at_ms.
			using (var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")) {
				setup.Open();
				using var cmd = setup.CreateCommand();
				cmd.CommandText = """
					CREATE TABLE jobs (
						id TEXT PRIMARY KEY, action TEXT NOT NULL, region TEXT NOT NULL DEFAULT '',
						targets_json TEXT NOT NULL, meta_json TEXT NOT NULL DEFAULT '{}',
						status TEXT NOT NULL, created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL
					);
					CREATE TABLE tasks (
						id TEXT PRIMARY KEY, job_id TEXT NOT NULL, target TEXT NOT NULL, action TEXT NOT NULL,
						region TEXT NOT NULL DEFAULT '', payload_json TEXT NOT NULL DEFAULT '{}',
						status TEXT NOT NULL, attempt INTEGER NOT NULL, created_at_ms INTEGER NOT NULL,
						updated_at_ms INTEGER NOT NULL
					);
					INSERT INTO jobs VALUES ($jobId, 'ping', 'local', '["acct-1"]', '{}', 'Queued', $now, $now);
					INSERT INTO tasks VALUES ($taskId, $jobId, 'acct-1', 'ping', 'local', '{}', 'Queued', 0, $now, $now);
					""";
				cmd.Parameters.AddWithValue("$jobId", jobId);
				cmd.Parameters.AddWithValue("$taskId", taskId);
				cmd.Parameters.AddWithValue("$now", nowMs);
				cmd.ExecuteNonQuery();
			}

			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			// Legacy rows must survive the migration and stay schedulable.
			JobTask? claimed = await store.ClaimNextQueuedTask("local", cts.Token);
			Assert.NotNull(claimed);
			Assert.Equal(taskId, claimed!.Id);
			Assert.Null(claimed.Error);

			(JobTask failed, Job job) = await store.FailRunningTask(taskId, "no capable agent available", cts.Token);
			Assert.Equal(JobTaskStatus.Failed, failed.Status);
			Assert.Equal(JobStatus.Failed, job.Status);
		} finally {
			File.Delete(dbPath);
		}
	}

	private static void MarkNextAttemptAt(SqliteJobStore store, string taskId, DateTimeOffset nextAttemptAt) {
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

	private static void MarkTaskStale(SqliteJobStore store, string taskId, DateTimeOffset updatedAt) {
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
}
