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

		var first = await cache.GetOrSetAsync("key", ct =>
		{
			invocations++;
			return Task.FromResult(new FakePayload { Value = "computed" });
		});

		var second = await cache.GetOrSetAsync("key", ct =>
		{
			invocations++;
			return Task.FromResult(new FakePayload { Value = "computed" });
		});

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

		Task<FakePayload> Factory(CancellationToken _)
		{
			invocations++;
			return Task.FromResult(invocations == 1 ? null! : new FakePayload { Value = "later" });
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

		Task<FakePayload> Factory(CancellationToken ct)
		{
			Interlocked.Increment(ref invocations);
			return gate.Task.ContinueWith(_ => new FakePayload { Value = "shared" }, TaskScheduler.Default);
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

	private sealed class FakePayload
	{
		public string Value { get; init; } = string.Empty;
	}

	private sealed class OtherPayload
	{
		public string Value { get; init; } = string.Empty;
	}
}
