using System.Diagnostics;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.ControlPlane.Tests.Performance;

/// <summary>
/// Capacity profile — the same workload measured at 100 / 1,000 / 10,000
/// stored rows (job store, audit store) or published events (event broker),
/// the round-28 residual. The question is how per-operation latency moves as the
/// dataset grows, not raw throughput (covered by <see cref="ControlPlaneBenchmarks"/>).
///
/// SQLite reads are index-backed, so they should stay flat across scales;
/// the event broker is documented at-most-once for bursts larger than its
/// bounded channel (256, DropOldest), so the deterministic delivery floor
/// is min(256, scale) — that floor is what the assertion pins.
///
/// Assertions use generous upper bounds so slow CI machines never flake;
/// the numbers they print are the actual baselines to watch over time
/// (recorded in docs/performance.md).
/// </summary>
public sealed class CapacityProfileBenchmarks
{
	private const int BrokerChannelCapacity = 256;
	private static readonly int[] Scales = [100, 1000, 10000];

	private readonly ITestOutputHelper _output;

	public CapacityProfileBenchmarks(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public async Task JobStore_VolumeProfile_ListGetAndHeadClaim()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

		foreach (int scale in Scales)
		{
			using SqliteJobStore store = new(":memory:");

			var seedSw = Stopwatch.StartNew();
			string? headJobId = null;
			for (int i = 0; i < scale; i++)
			{
				JobWithTasks created = await store.CreateJob(
					new CreateJobRequest("ping", "local", [$"acct-{i}"], null, null),
					cts.Token);
				headJobId ??= created.Job.Id;
			}
			seedSw.Stop();
			Assert.True(seedSw.Elapsed.TotalSeconds < 120, $"seed {scale} took {seedSw.Elapsed.TotalSeconds:F1}s");

			List<long> listMs = [];
			for (int i = 0; i < 200; i++)
			{
				var sw = Stopwatch.StartNew();
				IReadOnlyList<Job> jobs = await store.ListJobs(100, null, cts.Token);
				sw.Stop();
				listMs.Add(sw.ElapsedMilliseconds);
				Assert.Equal(Math.Min(100, scale), jobs.Count);
			}

			List<long> getMs = [];
			for (int i = 0; i < 200; i++)
			{
				var sw = Stopwatch.StartNew();
				JobWithTasks job = await store.GetJob(headJobId!, cts.Token);
				sw.Stop();
				getMs.Add(sw.ElapsedMilliseconds);
				Assert.Equal(JobStatus.Queued, job.Job.Status);
			}

			var claimSw = Stopwatch.StartNew();
			for (int i = 0; i < 100; i++)
			{
				if (await store.ClaimNextQueuedTask("local", cts.Token) is not { } task)
				{
					throw new InvalidOperationException($"claim {i} at scale {scale}: queue unexpectedly empty");
				}

				await store.SetTaskResult(
					new TaskResult(task.Id, true, null, null, DateTimeOffset.UtcNow, task.Attempt),
					cts.Token);
			}
			claimSw.Stop();
			Assert.True(claimSw.Elapsed.TotalSeconds < 60, $"claim x100 at {scale} took {claimSw.Elapsed.TotalSeconds:F1}s");

			long listP50 = Percentile(listMs, 0.50);
			long listP95 = Percentile(listMs, 0.95);
			_output.WriteLine($"jobs n={scale}: seed {seedSw.ElapsedMilliseconds} ms, " +
				$"list(100) p50 {listP50} ms / p95 {Percentile(listMs, 0.95)} ms, " +
				$"get p50 {Percentile(getMs, 0.50)} ms, claim+finish x100 {claimSw.ElapsedMilliseconds} ms");
			Assert.True(listP95 < 5000, $"list p95 at {scale} was {listP95} ms");
		}
	}

	[Fact]
	public async Task AuditStore_VolumeProfile_QueryAtScale()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

		foreach (int scale in Scales)
		{
			using SqliteAuditStore store = new(":memory:");

			var seedSw = Stopwatch.StartNew();
			for (int i = 0; i < scale; i++)
			{
				await store.RecordAsync(AuditStoreExtensions.CreateEntry("bench.event", "bench"), cts.Token);
			}
			seedSw.Stop();
			Assert.True(seedSw.Elapsed.TotalSeconds < 120, $"seed {scale} took {seedSw.Elapsed.TotalSeconds:F1}s");

			List<long> queryMs = [];
			for (int i = 0; i < 200; i++)
			{
				var sw = Stopwatch.StartNew();
				IReadOnlyList<AuditEntry> entries = await store.QueryAsync(new AuditQuery(), cts.Token);
				sw.Stop();
				queryMs.Add(sw.ElapsedMilliseconds);
				Assert.Equal(Math.Min(100, scale), entries.Count);
			}

			long queryP50 = Percentile(queryMs, 0.50);
			long queryP95 = Percentile(queryMs, 0.95);
			_output.WriteLine($"audit n={scale}: seed {seedSw.ElapsedMilliseconds} ms, " +
				$"query(100) p50 {queryP50} ms / p95 {queryP95} ms");
			Assert.True(queryP95 < 5000, $"query p95 at {scale} was {queryP95} ms");
		}
	}

	[Fact]
	public async Task EventBroker_BurstDrainProfile()
	{
		foreach (int scale in Scales)
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			var broker = new EventBroker();
			int expected = Math.Min(BrokerChannelCapacity, scale);
			int seen = 0;

			Task drain = Task.Run(async () =>
			{
				try
				{
					await foreach (Protocol.Event _ in broker.Subscribe(cts.Token, "*"))
					{
						if (Interlocked.Increment(ref seen) >= expected)
						{
							return;
						}
					}
				}
				catch (OperationCanceledException)
				{
					// subscriber shut down after the burst was measured
				}
			});

			// let the subscription register before the burst starts
			await Task.Delay(100, cts.Token);

			var publishSw = Stopwatch.StartNew();
			for (int i = 0; i < scale; i++)
			{
				broker.Publish(null, "bench.event", null);
			}
			publishSw.Stop();

			await drain.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
			_output.WriteLine($"events n={scale}: publish {publishSw.ElapsedMilliseconds} ms, " +
				$"delivered {seen} (floor {expected})");
			Assert.True(seen >= expected, $"delivered {seen} < floor {expected} at burst {scale}");
			await cts.CancelAsync();
		}
	}

	private static long Percentile(List<long> source, double fraction)
	{
		long[] sorted = [.. source.OrderBy(static v => v)];
		int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
		return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
	}
}
