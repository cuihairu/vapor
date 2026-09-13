using Vapor.Steam.Core.Caching;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.Steam.Core.Tests.Integration;

/// <summary>
/// End-to-end tests against a live Redis server. They are skipped unless the
/// VAPOR_TEST_REDIS environment variable holds a StackExchange.Redis connection
/// string (CI runs them in a dedicated job with a Redis service container).
/// </summary>
public sealed class RedisVaporCacheIntegrationTests
{
	private readonly ITestOutputHelper _output;

	public RedisVaporCacheIntegrationTests(ITestOutputHelper output)
	{
		_output = output;
	}

	/// <summary>Logs and reports a skip when no Redis server is configured for testing.</summary>
	private bool EnsureRedisAvailable()
	{
		if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VAPOR_TEST_REDIS")))
		{
			_output.WriteLine("Skipped: VAPOR_TEST_REDIS is not set");
			return false;
		}

		return true;
	}

	// One keyspace per test (xUnit creates a new instance per test method). Every cache
	// instance created inside a test must share it — they stand in for separate processes.
	private readonly string _keyPrefix = $"vapor:test:{Guid.NewGuid():N}:";

	private RedisVaporCache CreateCache() => RedisVaporCache.CreateFromConnectionString(
		Environment.GetEnvironmentVariable("VAPOR_TEST_REDIS")!,
		new RedisVaporCacheOptions { KeyPrefix = _keyPrefix });

	[Fact]
	public async Task SetAndGet_RoundTripsAcrossInstances()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var writer = CreateCache();
		using var reader = CreateCache();

		await writer.SetAsync("game:730", new FakePayload { Value = "cs2" });

		var value = await reader.GetAsync<FakePayload>("game:730");

		Assert.NotNull(value);
		Assert.Equal("cs2", value.Value);
		Assert.Equal(1, reader.Hits);
		Assert.Equal(0, reader.Misses);
	}

	[Fact]
	public async Task Get_MissingKey_ReturnsNullAndCountsMiss()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();

		Assert.Null(await cache.GetAsync<FakePayload>("missing"));
		Assert.Equal(1, cache.Misses);
		Assert.Equal(0, cache.Count);
	}

	[Fact]
	public async Task Set_WithTtl_Expires()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();

		await cache.SetAsync("key", new FakePayload { Value = "x" }, TimeSpan.FromMilliseconds(200));

		Assert.NotNull(await cache.GetAsync<FakePayload>("key"));

		await Task.Delay(500);
		Assert.Null(await cache.GetAsync<FakePayload>("key"));
	}

	[Fact]
	public async Task GetOrSet_CachesFactoryResult()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();
		int invocations = 0;

		var first = await cache.GetOrSetAsync("key", ct => { invocations++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "computed" }); });
		var second = await cache.GetOrSetAsync("key", ct => { invocations++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "computed" }); });

		Assert.Equal("computed", first!.Value);
		Assert.Equal("computed", second!.Value);
		Assert.Equal(1, invocations);
		Assert.Equal(1, cache.Hits);
	}

	[Fact]
	public async Task GetOrSet_DoesNotCacheNullResults()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();
		int invocations = 0;

		var first = await cache.GetOrSetAsync<FakePayload>("key", ct => { invocations++; return Task.FromResult<FakePayload?>(null); });
		var second = await cache.GetOrSetAsync<FakePayload>("key", ct => { invocations++; return Task.FromResult<FakePayload?>(null); });

		Assert.Null(first);
		Assert.Null(second);
		Assert.Equal(2, invocations);
	}

	[Fact]
	public async Task GetOrSet_ConcurrentRequests_ShareSingleFactoryInvocation()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();
		int invocations = 0;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		Task<FakePayload?> Factory(CancellationToken ct)
		{
			Interlocked.Increment(ref invocations);
			return gate.Task.ContinueWith<FakePayload?>(_ => new FakePayload { Value = "shared" }, ct);
		}

		var requesters = Enumerable.Range(0, 5).Select(_ => cache.GetOrSetAsync("key", Factory)).ToList();
		await Task.Delay(200); // let every requester pile onto the shared fill
		gate.SetResult();
		var results = await Task.WhenAll(requesters);

		Assert.All(results, value => Assert.Equal("shared", value!.Value));
		Assert.Equal(1, invocations);
	}

	[Fact]
	public async Task StaleWhileRevalidate_ServesStaleInstantlyThenRefreshesInBackground()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();
		int factoryCalls = 0;

		await cache.SetStaleWhileRevalidateAsync("price:730", new FakePayload { Value = "v1" }, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30));
		await Task.Delay(400); // fresh TTL lapsed, stale window still open

		var served = await cache.GetOrSetStaleWhileRevalidateAsync(
			"price:730",
			ct => { Interlocked.Increment(ref factoryCalls); return Task.FromResult<FakePayload?>(new FakePayload { Value = "v2" }); },
			TimeSpan.FromMilliseconds(200),
			TimeSpan.FromSeconds(30));

		// The stale value is served without waiting for the factory; the refresh
		// (which takes the cross-instance lock) runs in the background.
		Assert.Equal("v1", served!.Value);
		Assert.Equal(1, cache.StaleHits);

		await WaitForAsync(async () => (await cache.GetAsync<FakePayload>("price:730"))?.Value == "v2");

		// Once refreshed the entry is fresh again and the factory is not called again.
		var now = await cache.GetOrSetStaleWhileRevalidateAsync(
			"price:730",
			ct => { Interlocked.Increment(ref factoryCalls); return Task.FromResult<FakePayload?>(new FakePayload { Value = "v3" }); },
			TimeSpan.FromSeconds(60),
			TimeSpan.FromSeconds(30));
		Assert.Equal("v2", now!.Value);
		Assert.Equal(1, Volatile.Read(ref factoryCalls));
	}

	[Fact]
	public async Task StaleWhileRevalidate_AfterGraceWindow_FetchesFreshSynchronously()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();
		int factoryCalls = 0;

		await cache.SetStaleWhileRevalidateAsync("key", new FakePayload { Value = "old" }, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300));
		await Task.Delay(900); // beyond fresh TTL and stale window

		var served = await cache.GetOrSetStaleWhileRevalidateAsync(
			"key",
			ct => { factoryCalls++; return Task.FromResult<FakePayload?>(new FakePayload { Value = "new" }); },
			TimeSpan.FromSeconds(60),
			TimeSpan.FromSeconds(30));

		Assert.Equal("new", served!.Value);
		Assert.Equal(1, factoryCalls);
		Assert.Equal(0, cache.StaleHits);
	}

	[Fact]
	public async Task Remove_DeletesEntry()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();

		await cache.SetAsync("key", new FakePayload { Value = "x" });
		Assert.Equal(1, cache.Count);

		Assert.True(cache.Remove("key"));
		Assert.False(cache.Remove("key"));
		Assert.Null(await cache.GetAsync<FakePayload>("key"));
		Assert.Equal(0, cache.Count);
	}

	[Fact]
	public async Task RemoveByPrefix_RemovesOnlyMatchingKeys()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();

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

	[Fact]
	public async Task Clear_RemovesIndexedEntriesOnly()
	{
		if (!EnsureRedisAvailable())
		{
			return;
		}

		using var cache = CreateCache();

		await cache.SetAsync("key1", new FakePayload { Value = "1" });
		await cache.SetAsync("key2", new FakePayload { Value = "2" });

		cache.Clear();

		Assert.Equal(0, cache.Count);
		Assert.Null(await cache.GetAsync<FakePayload>("key1"));
		Assert.Null(await cache.GetAsync<FakePayload>("key2"));
	}

	private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 10000)
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (!await condition() && sw.ElapsedMilliseconds < timeoutMs)
		{
			await Task.Delay(20);
		}

		Assert.True(await condition(), "Condition not met within timeout");
	}

	private sealed class FakePayload
	{
		public string Value { get; init; } = string.Empty;
	}
}
