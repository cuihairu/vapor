using System.Diagnostics;
using Vapor.Steam.Core.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.Steam.Core.Tests.Performance;

/// <summary>
/// Cache-layer baseline: concurrent hot-path reads through
/// <see cref="MemoryVaporCache.GetOrSetStaleWhileRevalidateAsync"/> (the tier
/// every store data action and crawl batch fetch goes through). Reads hit
/// fresh entries, so the factory must never run during the measured window —
/// that is the assertion, alongside throughput and per-read allocations.
///
/// Assertions use generous upper bounds so slow CI machines never flake;
/// the printed numbers are the actual baselines to watch over time.
/// </summary>
public class CacheBenchmarks
{
	private const int Concurrency = 8;
	private const int KeyCount = 100;
	private const int ReadsPerWorker = 500;

	private readonly ITestOutputHelper _output;

	public CacheBenchmarks(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public async Task MemoryCache_ConcurrentFreshHits()
	{
		using var cache = new MemoryVaporCache();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

		TimeSpan ttl = TimeSpan.FromMinutes(10);
		TimeSpan staleTtl = TimeSpan.FromMinutes(30);

		// Warm-up pass fills every key once (also serves as the value check).
		for (int k = 0; k < KeyCount; k++)
		{
			string? value = await cache.GetOrSetStaleWhileRevalidateAsync(
				$"bench:{k}",
				_ => Task.FromResult<string?>($"value-{k}"),
				ttl,
				staleTtl,
				cts.Token);
			Assert.Equal($"value-{k}", value);
		}

		// One more factory invocation budget: zero. Everything below must be
		// served from fresh entries.
		int factoryCalls = 0;

		long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
		var gate = new TaskCompletionSource();

		async Task WorkerAsync(int workerId)
		{
			await gate.Task.WaitAsync(cts.Token);
			for (int i = 0; i < ReadsPerWorker; i++)
			{
				int key = (workerId + i) % KeyCount;
				string? value = await cache.GetOrSetStaleWhileRevalidateAsync(
					$"bench:{key}",
					_ =>
					{
						Interlocked.Increment(ref factoryCalls);
						return Task.FromResult<string?>($"value-{key}");
					},
					ttl,
					staleTtl,
					cts.Token);
				Assert.Equal($"value-{key}", value);
			}
		}

		List<Task> workers = Enumerable.Range(0, Concurrency).Select(id => WorkerAsync(id)).ToList();
		gate.SetResult();
		var sw = Stopwatch.StartNew();
		await Task.WhenAll(workers);
		sw.Stop();

		int totalReads = Concurrency * ReadsPerWorker;
		long allocatedPerRead = (GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore) / totalReads;
		double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);

		_output.WriteLine(
			$"memory cache fresh reads: {totalReads} reads over {KeyCount} keys with {Concurrency} workers in {sw.ElapsedMilliseconds} ms ({totalReads / seconds:F0} reads/s), {allocatedPerRead:N0} bytes allocated per read");

		// Correctness first, generous wall-clock second. The warm-up pass
		// misses (factory fills); every measured read must hit fresh entries.
		Assert.Equal(0, factoryCalls);
		Assert.Equal(totalReads, cache.Hits + cache.StaleHits);
		Assert.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:F1}s");
		Assert.True(allocatedPerRead < 10_000, $"allocations grew to {allocatedPerRead:N0} bytes per read");
	}

	[Fact]
	public async Task MemoryCache_CacheWrite_Throughput()
	{
		const int writeCount = 5_000;
		using var cache = new MemoryVaporCache(new MemoryVaporCacheOptions { Capacity = writeCount + 100 });
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

		var sw = Stopwatch.StartNew();
		for (int i = 0; i < writeCount; i++)
		{
			string? value = await cache.GetOrSetAsync(
				$"write:{i}",
				_ => Task.FromResult<string?>($"payload-{i}"),
				TimeSpan.FromMinutes(10),
				cts.Token);
			Assert.Equal($"payload-{i}", value);
		}
		sw.Stop();

		double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
		_output.WriteLine($"memory cache writes: {writeCount} distinct keys filled in {sw.ElapsedMilliseconds} ms ({writeCount / seconds:F0} writes/s)");

		Assert.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:F1}s");
	}
}
