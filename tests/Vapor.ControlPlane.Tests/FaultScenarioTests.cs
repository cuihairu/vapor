using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Residual fault-injection scenarios (roadmap §40.8-⑩): a corrupted store file,
/// a partitioned task attempt and the concurrent-claim double-dispatch fence.
/// The dispatch/API fault planes above them have their own suites
/// (FaultInjectorTests / FaultApiTests / TaskSchedulerServiceTests); these pin the
/// failure modes that live below the scheduler — in the store and its fencing.
/// </summary>
public sealed class FaultScenarioTests
{
	[Fact]
	public void Constructor_CorruptDbFile_FailsTypedWithoutRecreatingSchema()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			// A torn write or a wrong file dropped in place must fail loudly at
			// construction: the store refuses to boot over bytes that are not a
			// database instead of silently presenting a fresh, empty control plane
			// (silent data loss is the worst outcome of a corrupt store).
			byte[] garbage = "definitely not a sqlite database"u8.ToArray();
			File.WriteAllBytes(dbPath, garbage);

			Assert.ThrowsAny<SqliteException>(() =>
			{
				using var store = new SqliteJobStore(dbPath);
			});

			// SQLite rejected the file before writing anything: the damaged bytes
			// are still on disk for the operator to inspect or restore over, not
			// clobbered by a recreated schema.
			Assert.Equal(garbage, File.ReadAllBytes(dbPath));

			// The failed construction must not leak the open handle: the file is
			// deletable immediately (Windows keeps pooled handles open otherwise),
			// so the operator can repair or replace the database before restarting.
			SqliteConnection.ClearAllPools();
			File.Delete(dbPath);
			Assert.False(File.Exists(dbPath));
		}
		finally
		{
			try
			{
				SqliteConnection.ClearAllPools();
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	[Fact]
	public async Task StaleAttemptSignals_AfterLeasePartition_CannotCorruptNewerAttempt()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			cts.Token);

		// Attempt 1 claims the task, then its partition is lost: the lease fence
		// returns it to the queue and a newer attempt claims the same task.
		JobTask? first = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(first);
		Assert.Equal(1, first!.Attempt);

		// A zero lease models "the whole lease lapsed"; the short delay only makes
		// sure updated_at_ms is strictly older than the requeue cutoff in ms.
		await Task.Delay(5, cts.Token);
		Assert.Equal(1, await store.RequeueStaleRunningTasks(TimeSpan.Zero, cts.Token));

		JobTask? second = await store.ClaimNextQueuedTask("local", cts.Token);
		Assert.NotNull(second);
		Assert.Equal(2, second!.Attempt);
		Assert.Equal(first.Id, second.Id);

		// The lost partition's signals arrive late: the stale heartbeat is
		// rejected and the stale result write throws instead of overwriting
		// attempt 2's state — one lost attempt can never corrupt the newer one.
		Assert.False(await store.HeartbeatTask(first.Id, first.Attempt, cts.Token));
		await Assert.ThrowsAsync<NotFoundException>(() => store.SetTaskResult(
			new TaskResult(first.Id, true, null, null, DateTimeOffset.UtcNow, first.Attempt),
			cts.Token));

		JobWithTasks partitioned = await store.GetJob(created.Job.Id, cts.Token);
		JobTask current = Assert.Single(partitioned.Tasks);
		Assert.Equal(JobTaskStatus.Running, current.Status);
		Assert.Equal(2, current.Attempt);

		// The live attempt finishes normally and the job closes out finished.
		await store.SetTaskResult(
			new TaskResult(second.Id, true, null, null, DateTimeOffset.UtcNow, second.Attempt),
			cts.Token);

		JobWithTasks finished = await store.GetJob(created.Job.Id, cts.Token);
		Assert.Equal(JobStatus.Finished, finished.Job.Status);
		Assert.Equal(JobTaskStatus.Finished, Assert.Single(finished.Tasks).Status);
	}

	[Fact]
	public async Task ConcurrentClaims_ClaimEachQueuedTaskExactlyOnce()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

			// Eight independent single-task jobs: the double-dispatch fence must
			// hold when parallel workers race for the same queue.
			List<string> queuedIds = [];
			for (int i = 0; i < 8; i++)
			{
				JobWithTasks created = await store.CreateJob(
					new CreateJobRequest("ping", "local", [$"acct-{i}"], null, null),
					cts.Token);
				queuedIds.Add(created.Tasks.Single().Id);
			}

			ConcurrentBag<string> claims = [];
			await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
			{
				while (await store.ClaimNextQueuedTask("local", cts.Token) is { } claimed)
				{
					claims.Add(claimed.Id);
				}
			})));

			// Every task was handed out exactly once: no double dispatch, none left.
			Assert.Equal(queuedIds.Order(), claims.Order());
		}
		finally
		{
			try
			{
				SqliteConnection.ClearAllPools();
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}
}
