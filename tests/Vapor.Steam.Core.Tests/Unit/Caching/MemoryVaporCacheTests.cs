using Vapor.Steam.Core.Caching;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Caching;

public sealed class MemoryVaporCacheTests
{
	private DateTimeOffset _now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private MemoryVaporCache Create(MemoryVaporCacheOptions? options = null) =>
		new(options, () => _now);

	[Fact]
	public async Task SetAndGet_RoundTripsValue()
	{
		using var cache = Create();

		await cache.SetAsync("game:730", new FakePayload { Value = "cs2" });

		var value = await cache.GetAsync<FakePayload>("game:730");

		Assert.NotNull(value);
		Assert.Equal("cs2", value.Value);
	}

	[Fact]
	public async Task Get_ForMissingKey_ReturnsNullAndCountsMiss()
	{
		using var cache = Create();

		var value = await cache.GetAsync<FakePayload>("missing");

		Assert.Null(value);
		Assert.Equal(1, cache.Misses);
		Assert.Equal(0, cache.Hits);
	}

	[Fact]
	public async Task Get_ForWrongType_ReturnsNull()
	{
		using var cache = Create();

		await cache.SetAsync("key", new FakePayload { Value = "x" });

		var value = await cache.GetAsync<OtherPayload>("key");

		Assert.Null(value);
	}

	[Fact]
	public async Task ExplicitTtl_ExpiresEntry()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });

		await cache.SetAsync("key", new FakePayload { Value = "x" }, TimeSpan.FromMinutes(5));

		_now += TimeSpan.FromMinutes(4);
		Assert.NotNull(await cache.GetAsync<FakePayload>("key"));

		_now += TimeSpan.FromMinutes(2);
		Assert.Null(await cache.GetAsync<FakePayload>("key"));
	}

	[Fact]
	public async Task DefaultTtl_AppliesWhenNoExplicitTtlGiven()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = TimeSpan.FromMinutes(1) });

		await cache.SetAsync("key", new FakePayload { Value = "x" });

		_now += TimeSpan.FromSeconds(30);
		Assert.NotNull(await cache.GetAsync<FakePayload>("key"));

		_now += TimeSpanMinutes(45);
		Assert.Null(await cache.GetAsync<FakePayload>("key"));
	}

	[Fact]
	public async Task NullDefaultTtl_KeepsEntryForever()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });

		await cache.SetAsync("key", new FakePayload { Value = "x" });

		_now += TimeSpan.FromDays(365);

		Assert.NotNull(await cache.GetAsync<FakePayload>("key"));
	}

	[Fact]
	public async Task CapacityEviction_RemovesLeastRecentlyUsed()
	{
		using var cache = Create(new MemoryVaporCacheOptions { Capacity = 2, DefaultTtl = null });

		await cache.SetAsync("a", new FakePayload { Value = "a" });
		await cache.SetAsync("b", new FakePayload { Value = "b" });
		await cache.GetAsync<FakePayload>("a"); // touch "a" so "b" becomes LRU
		await cache.SetAsync("c", new FakePayload { Value = "c" }); // evicts "b"

		Assert.Equal(2, cache.Count);
		Assert.NotNull(await cache.GetAsync<FakePayload>("a"));
		Assert.NotNull(await cache.GetAsync<FakePayload>("c"));
		Assert.Null(await cache.GetAsync<FakePayload>("b"));
	}

	[Fact]
	public async Task Update_ExistingKey_OverwritesValue()
	{
		using var cache = Create();

		await cache.SetAsync("key", new FakePayload { Value = "v1" });
		await cache.SetAsync("key", new FakePayload { Value = "v2" });

		Assert.Equal("v2", (await cache.GetAsync<FakePayload>("key"))!.Value);
		Assert.Equal(1, cache.Count);
	}

	[Fact]
	public async Task Remove_DeletesEntry()
	{
		using var cache = Create();

		await cache.SetAsync("key", new FakePayload { Value = "x" });

		Assert.True(cache.Remove("key"));
		Assert.False(cache.Remove("key"));
		Assert.Null(await cache.GetAsync<FakePayload>("key"));
	}

	[Fact]
	public async Task Clear_EmptiesCache()
	{
		using var cache = Create();

		await cache.SetAsync("key", new FakePayload { Value = "x" });

		cache.Clear();

		Assert.Equal(0, cache.Count);
	}

	[Fact]
	public async Task GetOrSet_CachesFactoryResult()
	{
		using var cache = Create();
		int invocations = 0;

		Task<FakePayload?> Factory(CancellationToken ct)
		{
			invocations++;
			return Task.FromResult(new FakePayload { Value = "computed" })!;
		}

		var first = await cache.GetOrSetAsync("key", Factory);
		var second = await cache.GetOrSetAsync("key", Factory);

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.Equal(1, invocations);
		Assert.Equal(1, cache.Hits);
	}

	[Fact]
	public async Task GetOrSet_DoesNotCacheNullResults()
	{
		using var cache = Create();
		int invocations = 0;

		async Task<FakePayload?> Factory(CancellationToken _)
		{
			invocations++;
			return await Task.FromResult(invocations == 1 ? null : new FakePayload { Value = "later" });
		}

		var first = await cache.GetOrSetAsync("key", Factory);
		var second = await cache.GetOrSetAsync("key", Factory);
		var third = await cache.GetOrSetAsync("key", Factory);

		Assert.Null(first);
		Assert.NotNull(second);
		Assert.Equal("later", third!.Value); // served from cache
		Assert.Equal(2, invocations);
		Assert.Equal(1, cache.Count);
	}

	[Fact]
	public async Task GetOrSet_ConcurrentRequests_ShareSingleFactoryInvocation()
	{
		using var cache = Create();
		int invocations = 0;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		async Task<FakePayload?> Factory(CancellationToken ct)
		{
			Interlocked.Increment(ref invocations);
			await gate.Task;
			return new FakePayload { Value = "shared" };
		}

		var first = cache.GetOrSetAsync("key", Factory);
		var second = cache.GetOrSetAsync("key", Factory);
		gate.SetResult();

		await Task.WhenAll(first, second);

		Assert.Equal("shared", (await first)!.Value);
		Assert.Equal("shared", (await second)!.Value);
		Assert.Equal(1, invocations);
	}

	[Fact]
	public void Constructor_WithInvalidCapacity_Throws()
	{
		Assert.Throws<InvalidOperationException>(
			() => new MemoryVaporCache(new MemoryVaporCacheOptions { Capacity = 0 }));
	}

	[Fact]
	public async Task ConcurrentSetAndGet_RemainsConsistent()
	{
		using var cache = Create(new MemoryVaporCacheOptions { Capacity = 64 });

		var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
		{
			for (int j = 0; j < 200; j++)
			{
				string key = $"key:{i}:{j % 16}";
				await cache.SetAsync(key, new FakePayload { Value = $"{i}-{j}" });
				await cache.GetAsync<FakePayload>($"key:{(i + 1) % 8}:{j % 16}");
			}
		}));

		await Task.WhenAll(tasks);

		Assert.True(cache.Count <= 64);
		Assert.Equal(8 * 200, cache.Hits + cache.Misses + 0 * cache.Count);
	}

	private static TimeSpan TimeSpanMinutes(double minutes) => TimeSpan.FromMinutes(minutes);

	// --- Stale-while-revalidate ---

	[Fact]
	public async Task StaleWhileRevalidate_ServesStaleWithinGraceWindowAndRefreshesInBackground()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });
		int factoryCalls = 0;

		await cache.SetStaleWhileRevalidateAsync("price:730", new FakePayload { Value = "v1" }, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

		_now += TimeSpan.FromMinutes(6); // fresh TTL lapsed, stale window active

		var served = await cache.GetOrSetStaleWhileRevalidateAsync(
			"price:730",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "v2" }); },
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));

		// Stale value is served instantly; the refresh runs in the background.
		Assert.NotNull(served);
		Assert.Equal("v1", served.Value);
		Assert.Equal(1, cache.StaleHits);

		await WaitForAsync(() => factoryCalls > 0);
		await WaitForAsync(async () => (await cache.GetAsync<FakePayload>("price:730"))?.Value == "v2");

		// Once refreshed the entry is fresh again and factory is not called again.
		var now = await cache.GetOrSetStaleWhileRevalidateAsync(
			"price:730",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "v3" }); },
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));
		Assert.Equal("v2", now!.Value);
		Assert.Equal(1, factoryCalls);
	}

	[Fact]
	public async Task StaleWhileRevalidate_FreshEntryServedWithoutFactoryCall()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });
		int factoryCalls = 0;

		await cache.SetStaleWhileRevalidateAsync("key", new FakePayload { Value = "fresh" }, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

		var served = await cache.GetOrSetStaleWhileRevalidateAsync(
			"key",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(null); },
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));

		Assert.Equal("fresh", served!.Value);
		Assert.Equal(0, factoryCalls);
		Assert.Equal(1, cache.Hits);
		Assert.Equal(0, cache.StaleHits);
	}

	[Fact]
	public async Task StaleWhileRevalidate_AfterGraceWindow_FetchesFreshSynchronously()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });
		int factoryCalls = 0;

		await cache.SetStaleWhileRevalidateAsync("key", new FakePayload { Value = "old" }, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

		_now += TimeSpan.FromMinutes(20); // beyond fresh TTL and stale window

		var served = await cache.GetOrSetStaleWhileRevalidateAsync(
			"key",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "new" }); },
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));

		Assert.Equal("new", served!.Value);
		Assert.Equal(1, factoryCalls);
		Assert.Equal(0, cache.StaleHits);
		Assert.Equal(1, cache.Misses);
	}

	[Fact]
	public async Task StaleWhileRevalidate_BackgroundRefreshFailure_KeepsStaleUntilWindowEnds()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });
		bool failRefresh = true;

		await cache.SetStaleWhileRevalidateAsync("key", new FakePayload { Value = "stale" }, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

		_now += TimeSpan.FromMinutes(6);

		var served = await cache.GetOrSetStaleWhileRevalidateAsync(
			"key",
			ct => failRefresh ? throw new InvalidOperationException("store down") : Task.FromResult<FakePayload?>(new FakePayload { Value = "new" }),
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));

		// The refresh failed, yet the stale value is still served.
		Assert.Equal("stale", served!.Value);

		// A later read inside the stale window still serves stale and retries the refresh;
		// once the factory is allowed to succeed the entry converges.
		failRefresh = false;
		var retried = await cache.GetOrSetStaleWhileRevalidateAsync(
			"key",
			ct => failRefresh ? throw new InvalidOperationException("store down") : Task.FromResult<FakePayload?>(new FakePayload { Value = "new" }),
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));
		Assert.Equal("stale", retried!.Value);

		await WaitForAsync(async () => (await cache.GetAsync<FakePayload>("key"))?.Value == "new");

		_now += TimeSpan.FromMinutes(20);
		var after = await cache.GetOrSetStaleWhileRevalidateAsync(
			"key",
			ct => Task.FromResult<FakePayload?>(new FakePayload { Value = "newest" }),
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(10));
		Assert.Equal("newest", after!.Value);
	}

	[Fact]
	public async Task GetOrSetAsync_EntriesWithoutStaleWindow_ExpireDirectly()
	{
		using var cache = Create(new MemoryVaporCacheOptions { DefaultTtl = null });
		int factoryCalls = 0;

		var first = await cache.GetOrSetAsync(
			"key",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "v1" }); },
			TimeSpan.FromMinutes(5));

		_now += TimeSpan.FromMinutes(6); // expired, no stale window was set

		var second = await cache.GetOrSetAsync(
			"key",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "v2" }); },
			TimeSpan.FromMinutes(5));

		Assert.Equal("v1", first!.Value);
		Assert.Equal("v2", second!.Value);
		Assert.Equal(2, factoryCalls);
		Assert.Equal(0, cache.StaleHits);
	}

	// --- Prefix invalidation ---

	[Fact]
	public async Task RemoveByPrefix_RemovesOnlyMatchingKeysAndReturnsCount()
	{
		using var cache = Create();

		await cache.SetAsync("price:730:us", new FakePayload { Value = "a" });
		await cache.SetAsync("price:730:de", new FakePayload { Value = "b" });
		await cache.SetAsync("game:730", new FakePayload { Value = "c" });

		int removed = cache.RemoveByPrefix("price:730");

		Assert.Equal(2, removed);
		Assert.Null(await cache.GetAsync<FakePayload>("price:730:us"));
		Assert.Null(await cache.GetAsync<FakePayload>("price:730:de"));
		Assert.NotNull(await cache.GetAsync<FakePayload>("game:730"));

		Assert.Equal(0, cache.RemoveByPrefix("price:730"));
	}

	private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
		{
			await Task.Delay(10);
		}

		Assert.True(condition(), "Condition not met within timeout");
	}

	private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!await condition() && sw.ElapsedMilliseconds < timeoutMs)
		{
			await Task.Delay(10);
		}

		Assert.True(await condition(), "Condition not met within timeout");
	}

	[Fact]
	public async Task GetOrSet_SharedFillCanceledByInitiatingCaller_SecondCallerRetries()
	{
		using var cache = Create();
		int factoryCalls = 0;
		using var initiatorCts = new CancellationTokenSource();

		Task<FakePayload?> first = cache.GetOrSetAsync<FakePayload>("key", async ct =>
		{
			Interlocked.Increment(ref factoryCalls);
			await Task.Delay(Timeout.Infinite, ct);
			return null;
		}, cancellationToken: initiatorCts.Token);
		while (Volatile.Read(ref factoryCalls) == 0)
		{
			await Task.Delay(10);
		}

		Task<FakePayload?> second = cache.GetOrSetAsync("key", ct => Task.FromResult<FakePayload?>(new FakePayload { Value = "retry" }));
		await Task.Delay(100); // let the second caller attach to the shared in-flight fill
		initiatorCts.Cancel();

		Assert.Equal("retry", (await second)!.Value);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
	}

	private sealed class FakePayload
	{
		public string Value { get; init; } = string.Empty;
	}

	private sealed class OtherPayload
	{
		public string Value { get; init; } = string.Empty;
	}
}
