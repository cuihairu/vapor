using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Migration test: adding new columns to a legacy database.</summary>
public sealed partial class SqliteJobStoreTests
{
	[Fact]
	public async Task Migrate_AddsNewColumnsToLegacyDatabase()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		string jobId = "legacy-job";
		string taskId = "legacy-task";
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		try
		{
			// Build a pre-migration database: tasks table without error / next_attempt_at_ms.
			using (var setup = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
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

}
