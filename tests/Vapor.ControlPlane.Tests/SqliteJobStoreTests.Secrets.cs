using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Secret-payload tests: purge dry-run/scrub/backup, strip malformed and non-dictionary payloads, template-secret isolation.</summary>
public sealed partial class SqliteJobStoreTests
{
	[Fact]
	public async Task TriggerScheduledJob_ChildTaskDoesNotInheritTemplateSecrets()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks template = await store.CreateJob(
			new CreateJobRequest(
				"login",
				"local",
				["acct-1"],
				new Dictionary<string, object?> { ["password"] = "template-secret", ["message"] = "keep" },
				null,
				new JobSchedule(IntervalSeconds: 60)),
			cts.Token);

		Job? child = await store.TriggerScheduledJob(
			template.Job.Id,
			DateTimeOffset.UtcNow.AddSeconds(60),
			extraMeta: null,
			cts.Token);

		Assert.NotNull(child);
		string templateJson = RawScalar(store, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", template.Job.Id));
		Assert.DoesNotContain("template-secret", templateJson, StringComparison.Ordinal);
		string childJson = RawScalar(store, "SELECT payload_json FROM tasks WHERE job_id = $jobId", ("$jobId", child!.Id));
		Assert.DoesNotContain("template-secret", childJson, StringComparison.Ordinal);
		Assert.Contains("keep", childJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PurgeSecretPayloads_DryRunCountsWithoutWriting()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		// Synthetic historical rows written directly, as pre-ARCH-01 code did.
		RawExec(store, "INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json) VALUES ($id, 'login', '', '[\"a\"]', '{}', 'Finished', 1, 1, $payload)",
			("$id", "job-old"), ("$payload", """{"password":"p1","refreshToken":"r1","message":"keep"}"""));
		RawExec(store, "INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms) VALUES ($id, 'job-old', 'a', 'login', '', $payload, 'Finished', 0, 1, 1)",
			("$id", "task-old"), ("$payload", """{"access_token":"t1","two_factor_code":"c1","keep":true}"""));
		// A clean row must be untouched.
		RawExec(store, "INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms) VALUES ($id, 'job-old', 'a', 'login', '', $payload, 'Finished', 0, 1, 1)",
			("$id", "task-clean"), ("$payload", """{"message":"no secret"}"""));

		(int jobs, int tasks) = await store.PurgeSecretPayloadsAsync(dryRun: true, backupPath: null, cts.Token);

		Assert.Equal(1, jobs);
		Assert.Equal(1, tasks);
		// Dry run must not write: secrets remain in the rows.
		Assert.Contains("p1", RawScalar(store, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", "job-old")), StringComparison.Ordinal);
		Assert.Contains("t1", RawScalar(store, "SELECT payload_json FROM tasks WHERE id = $id", ("$id", "task-old")), StringComparison.Ordinal);
	}

	[Fact]
	public async Task PurgeSecretPayloads_ScrubsHistoricalRowsAndLeavesCleanRows()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		RawExec(store, "INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json) VALUES ($id, 'login', '', '[\"a\"]', '{}', 'Finished', 1, 1, $payload)",
			("$id", "job-old"), ("$payload", """{"password":"p1","refreshToken":"r1","message":"keep"}"""));
		RawExec(store, "INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms) VALUES ($id, 'job-old', 'a', 'login', '', $payload, 'Finished', 0, 1, 1)",
			("$id", "task-old"), ("$payload", """{"access_token":"t1","two_factor_code":"c1","keep":true}"""));
		RawExec(store, "INSERT INTO tasks (id, job_id, target, action, region, payload_json, status, attempt, created_at_ms, updated_at_ms) VALUES ($id, 'job-old', 'a', 'login', '', $payload, 'Finished', 0, 1, 1)",
			("$id", "task-clean"), ("$payload", """{"message":"no secret"}"""));

		(int jobs, int tasks) = await store.PurgeSecretPayloadsAsync(dryRun: false, backupPath: null, cts.Token);

		Assert.Equal(1, jobs);
		Assert.Equal(1, tasks);

		string jobJson = RawScalar(store, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", "job-old"));
		Assert.DoesNotContain("p1", jobJson, StringComparison.Ordinal);
		Assert.DoesNotContain("r1", jobJson, StringComparison.Ordinal);
		Assert.Contains("keep", jobJson, StringComparison.Ordinal);

		string taskJson = RawScalar(store, "SELECT payload_json FROM tasks WHERE id = $id", ("$id", "task-old"));
		Assert.DoesNotContain("t1", taskJson, StringComparison.Ordinal);
		Assert.DoesNotContain("c1", taskJson, StringComparison.Ordinal);
		Assert.Contains("keep", taskJson, StringComparison.Ordinal);

		string cleanJson = RawScalar(store, "SELECT payload_json FROM tasks WHERE id = $id", ("$id", "task-clean"));
		Assert.Equal("""{"message":"no secret"}""", cleanJson);
	}

	[Fact]
	public async Task PurgeSecretPayloads_WritesBackupSnapshotBeforeScrub()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-arch02-{Guid.NewGuid():N}.db");
		string backupPath = Path.Combine(Path.GetTempPath(), $"vapor-arch02-backup-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			RawExec(store, "INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json) VALUES ($id, 'login', '', '[\"a\"]', '{}', 'Finished', 1, 1, $payload)",
				("$id", "job-old"), ("$payload", """{"password":"p1"}"""));

			(int jobs, int _) = await store.PurgeSecretPayloadsAsync(dryRun: false, backupPath, cts.Token);
			Assert.Equal(1, jobs);

			// The backup snapshot must exist and still contain the pre-scrub secret.
			Assert.True(File.Exists(backupPath));
			using var backup = new SqliteJobStore(backupPath);
			Assert.Contains("p1", RawScalar(backup, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", "job-old")), StringComparison.Ordinal);

			// The live database is scrubbed.
			Assert.DoesNotContain("p1", RawScalar(store, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", "job-old")), StringComparison.Ordinal);
		}
		finally
		{
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			if (File.Exists(dbPath)) File.Delete(dbPath);
			if (File.Exists(backupPath)) File.Delete(backupPath);
		}
	}

	[Fact]
	public void StripSecretPayload_NonDictionaryPayload_RemovesSecretKeys()
	{
		// The scrub path always hands a Dictionary, so the non-dictionary arm of
		// StripSecretPayload (a defensive IReadOnlyDictionary branch) is otherwise
		// never exercised; a ReadOnlyDictionary wrapper is a non-Dictionary
		// IReadOnlyDictionary and reaches it deterministically.
		IReadOnlyDictionary<string, object?> payload =
			new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
				new Dictionary<string, object?> { ["password"] = "p1", ["message"] = "keep" });

		Dictionary<string, object?> scrubbed = SqliteJobStore.StripSecretPayload(payload);

		Assert.False(scrubbed.ContainsKey("password"));
		Assert.Equal("keep", scrubbed["message"]);
	}

	[Fact]
	public async Task PurgeSecretPayloads_InMemoryStore_WithBackupPath_DoesNotThrow()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		RawExec(store, "INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json) VALUES ($id, 'login', '', '[\"a\"]', '{}', 'Finished', 1, 1, $payload)",
			("$id", "job-old"), ("$payload", """{"password":"p1"}"""));

		// A :memory: store has no live file to compare against the backup path, so the
		// guard returns early instead of rejecting it; the backup still runs.
		string backupPath = Path.Combine(Path.GetTempPath(), $"vapor-mem-backup-{Guid.NewGuid():N}.db");
		try
		{
			(int jobs, int _) = await store.PurgeSecretPayloadsAsync(dryRun: false, backupPath, cts.Token);
			Assert.Equal(1, jobs);
		}
		finally
		{
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			if (File.Exists(backupPath)) File.Delete(backupPath);
		}
	}

	[Fact]
	public async Task PurgeSecretPayloads_BackupPathEqualsLivePath_Throws()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-samepath-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

			await Assert.ThrowsAsync<InvalidOperationException>(
				() => store.PurgeSecretPayloadsAsync(dryRun: false, dbPath, cts.Token));
		}
		finally
		{
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			if (File.Exists(dbPath)) File.Delete(dbPath);
		}
	}

	[Fact]
	public async Task PurgeSecretPayloads_MalformedJson_IsLeftUntouched()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		RawExec(store, "INSERT INTO jobs (id, action, region, targets_json, meta_json, status, created_at_ms, updated_at_ms, payload_json) VALUES ($id, 'login', '', '[\"a\"]', '{}', 'Finished', 1, 1, $payload)",
			("$id", "job-old"), ("$payload", """{not valid json}"""));

		// A malformed historical row must be skipped, not destroyed or thrown over.
		(int jobs, int _) = await store.PurgeSecretPayloadsAsync(dryRun: false, backupPath: null, cts.Token);
		Assert.Equal(0, jobs);

		Assert.Equal("""{not valid json}""", RawScalar(store, "SELECT payload_json FROM jobs WHERE id = $id", ("$id", "job-old")));
	}

}
