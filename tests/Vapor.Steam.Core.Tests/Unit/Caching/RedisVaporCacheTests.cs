using System.Net;
using Moq;
using StackExchange.Redis;
using Vapor.Steam.Core.Caching;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Caching;

/// <summary>
/// Offline coverage for <see cref="RedisVaporCache"/>: the whole class runs against
/// mocked <c>IConnectionMultiplexer</c>/<c>IDatabase</c>/<c>IServer</c> so the
/// envelope protocol, stale-while-revalidate locking and index maintenance are
/// exercised without a Redis server (the integration suite needs one to run).
/// </summary>
public sealed class RedisVaporCacheTests
{
	private sealed class FakeRedis
	{
		public Mock<IConnectionMultiplexer> Multiplexer { get; } = new();
		public Mock<IDatabase> Database { get; } = new();
		public Mock<IServer> Server { get; } = new();

		// Captured writes for assertions.
		public List<(string Key, string? Json, TimeSpan? Ttl)> Sets { get; } = [];
		public List<string[]> SetAdds { get; } = [];
		public List<string> DeletedKeys { get; } = [];
		public List<string[]> BatchDeletes { get; } = [];
		public List<(string Index, string[] Values)> SetRemovals { get; } = [];

		public FakeRedis()
		{
			Multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Database.Object);

			// Loose defaults: misses / successful writes, so each test only states what it varies.
			Database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
				.ReturnsAsync(RedisValue.Null);
			Database.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
				.Callback((RedisKey key, RedisValue value, TimeSpan? _, When _, CommandFlags _) =>
					Sets.Add((key.ToString()!, value.ToString()!, null)))
				.ReturnsAsync(true);
			Database.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
				.Callback((RedisKey key, RedisValue value, CommandFlags _) =>
					SetAdds.Add([key.ToString()!, value.ToString()!]))
				.ReturnsAsync(true);
			Database.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
				.Callback((RedisKey key, CommandFlags _) => DeletedKeys.Add(key.ToString()!))
				.ReturnsAsync(true);
			Database.Setup(d => d.KeyDelete(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
				.Callback((RedisKey key, CommandFlags _) => DeletedKeys.Add(key.ToString()!))
				.Returns(true);
			Database.Setup(d => d.LockTakeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()))
				.ReturnsAsync(true);
			Database.Setup(d => d.LockReleaseAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
				.ReturnsAsync(true);
		}

		public RedisVaporCache CreateCache(RedisVaporCacheOptions? options = null, bool ownsMultiplexer = false) =>
			new(Multiplexer.Object, options ?? new RedisVaporCacheOptions { KeyPrefix = "t:" }, ownsMultiplexer);

		public void SetupFreshEntry<T>(string key, T value, TimeSpan remaining) where T : class
		{
			string json = RedisCacheEntry.Encode(value, DateTimeOffset.UtcNow + remaining, null);
			Database.Setup(d => d.StringGetAsync((RedisKey)("t:" + key), It.IsAny<CommandFlags>()))
				.ReturnsAsync((RedisValue)json);
		}

		public void SetupStaleServableEntry<T>(string key, T value, TimeSpan staleRemaining) where T : class
		{
			var freshAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
			string json = RedisCacheEntry.Encode(value, freshAt, freshAt + staleRemaining);
			Database.Setup(d => d.StringGetAsync((RedisKey)("t:" + key), It.IsAny<CommandFlags>()))
				.ReturnsAsync((RedisValue)json);
		}

		public void SetupExpiredEntry<T>(string key, T value) where T : class
		{
			var expired = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
			string json = RedisCacheEntry.Encode(value, expired, expired + TimeSpan.FromMinutes(1));
			Database.Setup(d => d.StringGetAsync((RedisKey)("t:" + key), It.IsAny<CommandFlags>()))
				.ReturnsAsync((RedisValue)json);
		}
	}

	private static Task<T?> Factory<T>(T value) where T : class =>
		Task.FromResult<T?>(value);

	[Fact]
	public async Task GetAsync_FreshEntry_ReturnsValueAndCountsHit()
	{
		var redis = new FakeRedis();
		redis.SetupFreshEntry("k", new Payload("v"), TimeSpan.FromMinutes(5));
		using var cache = redis.CreateCache();

		var result = await cache.GetAsync<Payload>("k");

		Assert.Equal("v", result!.Value);
		Assert.Equal(1, cache.Hits);
		Assert.Equal(0, cache.Misses);
	}

	[Fact]
	public async Task GetAsync_MissingEntry_ReturnsNullAndCountsMiss()
	{
		using var cache = new FakeRedis().CreateCache();

		Assert.Null(await cache.GetAsync<Payload>("missing"));
		Assert.Equal(1, cache.Misses);
		Assert.Equal(0, cache.Hits);
	}

	[Fact]
	public async Task GetAsync_StaleEntry_IsTreatedAsMiss()
	{
		var redis = new FakeRedis();
		redis.SetupStaleServableEntry("k", new Payload("v"), TimeSpan.FromMinutes(9));
		using var cache = redis.CreateCache();

		Assert.Null(await cache.GetAsync<Payload>("k"));
		Assert.Equal(1, cache.Misses);
	}

	[Fact]
	public async Task GetAsync_MalformedEntry_IsTreatedAsMiss()
	{
		var redis = new FakeRedis();
		redis.Database.Setup(d => d.StringGetAsync((RedisKey)"t:bad", It.IsAny<CommandFlags>()))
			.ReturnsAsync((RedisValue)"{ not an envelope");
		using var cache = redis.CreateCache();

		Assert.Null(await cache.GetAsync<Payload>("bad"));
	}

	[Theory]
	[InlineData("")]
	[InlineData(null)]
	public async Task GetAsync_InvalidKey_Throws(string? key)
	{
		using var cache = new FakeRedis().CreateCache();

		// ThrowIfNullOrEmpty throws ArgumentNullException for null, ArgumentException for "".
		await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.GetAsync<Payload>(key!));
	}

	[Fact]
	public async Task SetAsync_EncodesEnvelopeAndIndexesKey()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();

		await cache.SetAsync("k", new Payload("v"), TimeSpan.FromMinutes(2));

		var (key, json, ttl) = Assert.Single(redis.Sets);
		Assert.Equal("t:k", key);
		Assert.True(RedisCacheEntry.TryDecode(json!, out var payload, out long? freshMs, out long? staleMs));
		Assert.Null(staleMs);
		Assert.Equal("v", System.Text.Json.JsonSerializer.Deserialize<Payload>(payload!)!.Value);
		Assert.InRange(freshMs!.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 100_000, 130_000); // ~2min
		Assert.Equal([["t:__index", "t:k"]], redis.SetAdds);
	}

	[Fact]
	public async Task SetAsync_WithoutTtl_UsesDefaultTtl()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache(new RedisVaporCacheOptions { KeyPrefix = "t:", DefaultTtl = TimeSpan.FromSeconds(30) });

		await cache.SetAsync("k", new Payload("v"));

		Assert.Single(redis.Sets);
		Assert.True(RedisCacheEntry.TryDecode(redis.Sets[0].Json!, out _, out long? freshMs, out _));
		Assert.InRange(freshMs!.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 20_000, 40_000);
	}

	[Fact]
	public async Task SetAsync_InvalidInputs_Throw()
	{
		using var cache = new FakeRedis().CreateCache();

		await Assert.ThrowsAsync<ArgumentException>(() => cache.SetAsync<Payload>("", new Payload("v")));
		await Assert.ThrowsAsync<ArgumentNullException>(() => cache.SetAsync<Payload>("k", null!));
	}

	[Fact]
	public async Task SetStaleWhileRevalidateAsync_EncodesStaleWindow()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();

		await cache.SetStaleWhileRevalidateAsync("k", new Payload("v"), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		Assert.True(RedisCacheEntry.TryDecode(redis.Sets[0].Json!, out _, out long? freshMs, out long? staleMs));
		// Stale window extends ~9min beyond the fresh expiry.
		Assert.InRange(staleMs!.Value - freshMs!.Value, 540_000 - 2_000, 540_000 + 2_000);
	}

	[Fact]
	public async Task GetOrSetAsync_CachesFactoryResult()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();
		int factoryCalls = 0;

		var result = await cache.GetOrSetAsync("k", ct => { factoryCalls++; return Factory(new Payload("made")); });

		Assert.Equal("made", result!.Value);
		Assert.Equal(1, factoryCalls);
		Assert.Single(redis.Sets); // fill wrote the entry back
	}

	[Fact]
	public async Task GetOrSetAsync_FreshHit_DoesNotCallFactory()
	{
		var redis = new FakeRedis();
		redis.SetupFreshEntry("k", new Payload("cached"), TimeSpan.FromMinutes(5));
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetAsync<Payload>("k", ct => throw new InvalidOperationException("must not run"));

		Assert.Equal("cached", result!.Value);
	}

	[Fact]
	public async Task GetOrSetAsync_FactoryReturningNull_DoesNotWrite()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();

		Assert.Null(await cache.GetOrSetAsync("k", ct => Factory<Payload>(null!)));

		Assert.Empty(redis.Sets);
		Assert.Equal(1, cache.Misses);
	}

	[Fact]
	public async Task GetOrSetStaleWhileRevalidateAsync_FreshHit_ReturnsValue()
	{
		var redis = new FakeRedis();
		redis.SetupFreshEntry("k", new Payload("fresh"), TimeSpan.FromMinutes(5));
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetStaleWhileRevalidateAsync<Payload>("k", ct => throw new InvalidOperationException(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		Assert.Equal("fresh", result!.Value);
		Assert.Equal(1, cache.Hits);
	}

	[Fact]
	public async Task GetOrSetStaleWhileRevalidateAsync_StaleHit_ServesAndRefreshesInBackground()
	{
		var redis = new FakeRedis();
		redis.SetupStaleServableEntry("k", new Payload("stale"), TimeSpan.FromMinutes(9));
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetStaleWhileRevalidateAsync("k", ct => Factory(new Payload("refreshed")), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		Assert.Equal("stale", result!.Value);
		Assert.Equal(1, cache.StaleHits);

		// The background refresh runs asynchronously: wait for the write-back + lock release.
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (redis.Sets.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25);
		}

		Assert.True(RedisCacheEntry.TryDecode(redis.Sets[0].Json!, out var payload, out _, out _));
		Assert.Equal("refreshed", System.Text.Json.JsonSerializer.Deserialize<Payload>(payload!)!.Value);
		redis.Database.Verify(d => d.LockTakeAsync((RedisKey)"t:__lock:k", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()), Times.Once);
		redis.Database.Verify(d => d.LockReleaseAsync((RedisKey)"t:__lock:k", It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Once);
	}

	[Fact]
	public async Task GetOrSetStaleWhileRevalidateAsync_LockHeldElsewhere_SkipsRefresh()
	{
		var redis = new FakeRedis();
		redis.SetupStaleServableEntry("k", new Payload("stale"), TimeSpan.FromMinutes(9));
		redis.Database.Setup(d => d.LockTakeAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan>(), It.IsAny<CommandFlags>()))
			.ReturnsAsync(false);
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetStaleWhileRevalidateAsync("k", ct => Factory(new Payload("refreshed")), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		Assert.Equal("stale", result!.Value);
		Assert.Empty(redis.Sets); // no refresh write-back
	}

	[Fact]
	public async Task GetOrSetStaleWhileRevalidateAsync_ExpiredBeyondStaleWindow_DeletesAndFills()
	{
		var redis = new FakeRedis();
		redis.SetupExpiredEntry("k", new Payload("dead"));
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetStaleWhileRevalidateAsync("k", ct => Factory(new Payload("made")), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		Assert.Equal("made", result!.Value);
		Assert.Contains("t:k", redis.DeletedKeys);
		Assert.Equal(1, cache.Misses); // the expired entry counts once; the fill doesn't re-read
	}

	[Fact]
	public async Task GetOrSetStaleWhileRevalidateAsync_AbsentKey_MissesAndFillsWithoutDelete()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetStaleWhileRevalidateAsync("k", ct => Factory(new Payload("made")), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		Assert.Equal("made", result!.Value);
		Assert.Equal(1, cache.Misses);
		Assert.Empty(redis.DeletedKeys); // nothing stale to evict — straight to the fill
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (redis.Sets.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25);
		}

		Assert.True(RedisCacheEntry.TryDecode(redis.Sets[0].Json!, out var payload, out _, out _));
		Assert.Equal("made", System.Text.Json.JsonSerializer.Deserialize<Payload>(payload!)!.Value);
	}

	[Fact]
	public async Task GetOrSetStaleWhileRevalidateAsync_FactoryThrowsDuringRefresh_IsSwallowed()
	{
		var redis = new FakeRedis();
		redis.SetupStaleServableEntry("k", new Payload("stale"), TimeSpan.FromMinutes(9));
		using var cache = redis.CreateCache();

		var result = await cache.GetOrSetStaleWhileRevalidateAsync<Payload>(
			"k", _ => throw new InvalidOperationException("refresh source down"), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9));

		// The caller still gets the stale value; the background failure is absorbed.
		Assert.Equal("stale", result!.Value);
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (redis.DeletedKeys.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25); // lock release finally-block runs even on failure
		}

		redis.Database.Verify(d => d.LockReleaseAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Once);
	}

	[Fact]
	public async Task FillAsync_SharesInFlightFactoryAcrossConcurrentCallers()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int factoryCalls = 0;

		Task<Payload?> first = cache.GetOrSetAsync("k", async ct => { factoryCalls++; await gate.Task; return new Payload("made"); });
		Task<Payload?> second = cache.GetOrSetAsync<Payload>("k", ct => throw new InvalidOperationException("must not run"));

		gate.TrySetResult();
		var results = await Task.WhenAll(first, second);

		Assert.Equal(1, factoryCalls);
		Assert.All(results, r => Assert.Equal("made", r!.Value));
	}

	[Fact]
	public void Remove_DeletesKeyAndDropsIndexEntry()
	{
		var redis = new FakeRedis();
		redis.Database.Setup(d => d.KeyDelete((RedisKey)"t:k", It.IsAny<CommandFlags>()))
			.Callback((RedisKey key, CommandFlags _) => redis.DeletedKeys.Add(key.ToString()!))
			.Returns(true);
		redis.Database.Setup(d => d.SetRemove(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
			.Callback((RedisKey index, RedisValue value, CommandFlags _) =>
				redis.SetRemovals.Add((index.ToString()!, [value.ToString()!])))
			.Returns(true);
		using var cache = redis.CreateCache();

		Assert.True(cache.Remove("k"));
		Assert.Equal(["t:k"], redis.DeletedKeys);
		var removal = redis.SetRemovals.Single();
		Assert.Equal("t:__index", removal.Index);
		Assert.Equal(["t:k"], removal.Values);
	}

	[Fact]
	public void RemoveByPrefix_ScansAndDeletesMatches()
	{
		var redis = new FakeRedis();
		redis.Multiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("redis.test", 6379)]);
		redis.Multiplexer.Setup(m => m.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(redis.Server.Object);
		redis.Server.Setup(s => s.IsConnected).Returns(true);
		redis.Server.Setup(s => s.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
			.Returns([new RedisKey("t:user:1"), new RedisKey("t:user:2")]);
		redis.Database.Setup(d => d.KeyDelete(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
			.Callback((RedisKey[] keys, CommandFlags _) => redis.BatchDeletes.Add(keys.Select(k => k.ToString()!).ToArray()))
			.Returns(2);
		redis.Database.Setup(d => d.SetRemove(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
			.Callback((RedisKey index, RedisValue[] values, CommandFlags _) =>
				redis.SetRemovals.Add((index.ToString()!, values.Select(v => v.ToString()!).ToArray())))
			.Returns(2);
		using var cache = redis.CreateCache();

		Assert.Equal(2, cache.RemoveByPrefix("user:"));

		Assert.Equal([["t:user:1", "t:user:2"]], redis.BatchDeletes);
		var removal = redis.SetRemovals.Single();
		Assert.Equal("t:__index", removal.Index);
		Assert.Equal(["t:user:1", "t:user:2"], removal.Values);
	}

	[Fact]
	public void RemoveByPrefix_NoMatches_DeletesNothing()
	{
		var redis = new FakeRedis();
		redis.Multiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("redis.test", 6379)]);
		redis.Multiplexer.Setup(m => m.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(redis.Server.Object);
		redis.Server.Setup(s => s.IsConnected).Returns(true);
		redis.Server.Setup(s => s.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
			.Returns([]);
		using var cache = redis.CreateCache();

		Assert.Equal(0, cache.RemoveByPrefix("none:"));
		Assert.Empty(redis.BatchDeletes);
	}

	[Fact]
	public void RemoveByPrefix_WhenNoServerReportsConnected_FallsBackToFirstEndpoint()
	{
		// Every endpoint reports disconnected: the scan must still run against the
		// first endpoint instead of skipping the invalidation.
		var redis = new FakeRedis();
		redis.Multiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("redis.test", 6379)]);
		redis.Multiplexer.Setup(m => m.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(redis.Server.Object);
		redis.Server.Setup(s => s.IsConnected).Returns(false);
		redis.Server.Setup(s => s.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
			.Returns([new RedisKey("t:user:1")]);
		redis.Database.Setup(d => d.KeyDelete(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
			.Callback((RedisKey[] keys, CommandFlags _) => redis.BatchDeletes.Add(keys.Select(k => k.ToString()!).ToArray()))
			.Returns(1);
		redis.Database.Setup(d => d.SetRemove(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
			.Returns(1);
		using var cache = redis.CreateCache();

		Assert.Equal(1, cache.RemoveByPrefix("user:"));
		Assert.Equal([["t:user:1"]], redis.BatchDeletes);
	}

	[Fact]
	public void Clear_DeletesIndexedKeysButNeverFlushes()
	{
		var redis = new FakeRedis();
		redis.Database.Setup(d => d.SetMembers(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
			.Returns([new RedisValue("t:a"), new RedisValue("t:b")]);
		redis.Database.Setup(d => d.KeyDelete(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
			.Callback((RedisKey[] keys, CommandFlags _) => redis.BatchDeletes.Add(keys.Select(k => k.ToString()).ToArray()))
			.Returns(2);
		using var cache = redis.CreateCache();

		cache.Clear();

		Assert.Equal([["t:a", "t:b"]], redis.BatchDeletes);
		Assert.Contains("t:__index", redis.DeletedKeys);
	}

	[Fact]
	public void Clear_WithEmptyIndex_OnlyDropsIndexKey()
	{
		var redis = new FakeRedis();
		redis.Database.Setup(d => d.SetMembers(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
			.Returns([]);
		using var cache = redis.CreateCache();

		cache.Clear();

		Assert.Empty(redis.BatchDeletes);
		Assert.Equal(["t:__index"], redis.DeletedKeys);
	}

	[Fact]
	public void Count_ReturnsIndexSetLength()
	{
		var redis = new FakeRedis();
		redis.Database.Setup(d => d.SetLength((RedisKey)"t:__index", It.IsAny<CommandFlags>())).Returns(7);
		using var cache = redis.CreateCache();

		Assert.Equal(7, cache.Count);
	}

	[Fact]
	public void Dispose_WithOwnedMultiplexer_DisposesConnection()
	{
		var redis = new FakeRedis();
		var cache = redis.CreateCache(ownsMultiplexer: true);

		cache.Dispose();

		redis.Multiplexer.Verify(m => m.Dispose(), Times.Once);
	}

	[Fact]
	public void Dispose_WithoutOwnedMultiplexer_LeavesConnectionOpen()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache(ownsMultiplexer: false);

		cache.Dispose();

		redis.Multiplexer.Verify(m => m.Dispose(), Times.Never);
	}

	[Fact]
	public void Constructor_WithoutMultiplexer_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => new RedisVaporCache(null!));
	}

	[Fact]
	public void CreateFromConnectionString_Whitespace_Throws()
	{
		Assert.Throws<ArgumentException>(() => RedisVaporCache.CreateFromConnectionString("  "));
	}

	[Fact]
	public void CreateFromConnectionString_UnreachableServer_ThrowsAfterRetries()
	{
		// An unreachable server exhausts the factory's transient-failure retries and
		// then surfaces the connection failure (short connect timeout keeps this fast).
		Assert.Throws<RedisConnectionException>(() =>
			RedisVaporCache.CreateFromConnectionString("localhost:1,connectTimeout=100"));
	}

	[Fact]
	public void Options_DeriveLockAndIndexKeys()
	{
		var options = new RedisVaporCacheOptions { KeyPrefix = "p:" };

		Assert.Equal("p:__index", options.IndexKey);
		Assert.Equal("p:__lock:", options.LockKeyPrefix);
	}

	[Fact]
	public async Task GetOrSetAsync_SharedFillCanceledByInitiatingCaller_SecondCallerRetries()
	{
		var redis = new FakeRedis();
		using var cache = redis.CreateCache();
		int factoryCalls = 0;
		using var initiatorCts = new CancellationTokenSource();

		Task<Payload?> first = cache.GetOrSetAsync<Payload>("k", async ct =>
		{
			Interlocked.Increment(ref factoryCalls);
			await Task.Delay(Timeout.Infinite, ct);
			return null;
		}, cancellationToken: initiatorCts.Token);
		while (Volatile.Read(ref factoryCalls) == 0)
		{
			await Task.Delay(10);
		}

		Task<Payload?> second = cache.GetOrSetAsync("k", ct => Factory(new Payload("retry")));
		await Task.Delay(100); // let the second caller attach to the shared in-flight fill
		initiatorCts.Cancel();

		Assert.Equal("retry", (await second)!.Value);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
	}

	[Fact]
	public async Task GetAsync_WithDecodableEnvelopeButCorruptPayload_ReturnsNullAsHit()
	{
		var redis = new FakeRedis();
		string envelope = System.Text.Json.JsonSerializer.Serialize(new
		{
			V = "{not-json",
			E = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
			S = (long?)null
		});
		redis.Database
			.Setup(d => d.StringGetAsync((RedisKey)"t:k", It.IsAny<CommandFlags>()))
			.ReturnsAsync((RedisValue)envelope);
		using var cache = redis.CreateCache();

		// The envelope decodes fine, so this counts as a fresh hit — but the inner
		// payload is not valid JSON for T and must degrade to a null value.
		var result = await cache.GetAsync<Payload>("k");

		Assert.Null(result);
		Assert.Equal(1, cache.Hits);
	}

	[Fact]
	public async Task GetAsync_WhenNoServerReportsConnected_FallsBackToFirstEndpoint()
	{
		var redis = new FakeRedis();
		// ResolveServer walks the endpoints for a connected server; with none
		// connected it must fall back to the first endpoint rather than throw.
		redis.Multiplexer.Setup(m => m.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(redis.Server.Object);
		redis.Server.Setup(s => s.IsConnected).Returns(false);
		redis.Multiplexer
			.Setup(m => m.GetEndPoints(It.IsAny<bool>()))
			.Returns([new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6379)]);
		using var cache = redis.CreateCache();

		var result = await cache.GetAsync<Payload>("k");

		Assert.Null(result);
		Assert.Equal(1, cache.Misses);
	}

	private sealed record Payload(string Value);
}
