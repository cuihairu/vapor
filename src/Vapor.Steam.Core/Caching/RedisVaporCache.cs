using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using StackExchange.Redis;

namespace Vapor.Steam.Core.Caching;

/// <summary>
/// Options for <see cref="RedisVaporCache"/>.
/// </summary>
public sealed record RedisVaporCacheOptions
{
	/// <summary>
	/// StackExchange.Redis configuration string. The default disables
	/// <c>abortConnect</c> so callers can construct the cache before Redis is
	/// reachable; operations then fail fast until the connection comes up.
	/// </summary>
	public string Configuration { get; init; } = "localhost:6379,abortConnect=false";

	/// <summary>Prefix applied to every cache key. Change it to namespace caches (or test runs).</summary>
	public string KeyPrefix { get; init; } = "vapor:cache:";

	/// <summary>
	/// TTL applied when <see cref="IVaporCache.SetAsync{T}"/> is called without an explicit TTL.
	/// Null means entries never expire. Defaults to 10 minutes (matches MemoryVaporCache).
	/// </summary>
	public TimeSpan? DefaultTtl { get; init; } = TimeSpan.FromMinutes(10);

	/// <summary>How long a stale-while-revalidate refresh lock may be held before it expires server-side.</summary>
	public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>SCAN page size used by prefix invalidation.</summary>
	public int ScanPageSize { get; init; } = 250;

	/// <summary>Key of the shared index set used for <see cref="IVaporCache.Count"/> and <see cref="IVaporCache.Clear"/>.</summary>
	public string IndexKey => KeyPrefix + "__index";

	/// <summary>Prefix of the short-lived lock keys guarding cross-instance stale-while-revalidate refreshes.</summary>
	public string LockKeyPrefix => KeyPrefix + "__lock:";
}

/// <summary>
/// Redis-backed <see cref="IVaporCache"/>. Entries are stored as JSON envelopes
/// (<see cref="RedisCacheEntry"/>) under <c>{KeyPrefix}{key}</c>; the Redis key TTL
/// is the outer safety net while fresh/stale semantics come from absolute expiry
/// timestamps inside the envelope.
///
/// Hit/miss/stale counters are local to the process instance (like MemoryVaporCache);
/// <see cref="IVaporCache.Count"/> is approximate (size of the shared index set, which
/// naturally drifts from Redis-side expiries until a removal or clear touches the key).
/// A stale-while-revalidate refresh takes a short-lived cross-instance lock
/// (<c>SET NX PX</c> with token-checked release) so only one instance repopulates a key.
/// </summary>
public sealed class RedisVaporCache : IVaporCache, IDisposable
{
	private readonly RedisVaporCacheOptions _options;
	private readonly IConnectionMultiplexer _multiplexer;
	private readonly IDatabase _database;
	private readonly bool _ownsMultiplexer;
	private readonly object _inFlightGate = new();
	private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new(StringComparer.Ordinal);
	private long _hits;
	private long _misses;
	private long _staleHits;

	public RedisVaporCache(IConnectionMultiplexer multiplexer, RedisVaporCacheOptions? options = null, bool ownsMultiplexer = false)
	{
		ArgumentNullException.ThrowIfNull(multiplexer);

		_options = options ?? new RedisVaporCacheOptions();
		_multiplexer = multiplexer;
		_database = _multiplexer.GetDatabase();
		_ownsMultiplexer = ownsMultiplexer;
	}

	/// <summary>
	/// Creates a cache that owns its connection. Any <see cref="RedisVaporCacheOptions.Configuration"/>
	/// in <paramref name="options"/> is overridden by <paramref name="configuration"/>;
	/// use the options to adjust <see cref="RedisVaporCacheOptions.KeyPrefix"/> and friends.
	/// </summary>
	public static RedisVaporCache CreateFromConnectionString(string configuration, RedisVaporCacheOptions? options = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

		RedisVaporCacheOptions resolved = (options ?? new RedisVaporCacheOptions()) with { Configuration = configuration };
		return new RedisVaporCache(ConnectionMultiplexer.Connect(resolved.Configuration), resolved, ownsMultiplexer: true);
	}

	public int Count => checked((int)_database.SetLength(_options.IndexKey));

	public long Hits => Interlocked.Read(ref _hits);

	public long Misses => Interlocked.Read(ref _misses);

	public long StaleHits => Interlocked.Read(ref _staleHits);

	public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		cancellationToken.ThrowIfCancellationRequested();

		RedisValue raw = await _database.StringGetAsync(Key(key)).ConfigureAwait(false);
		if (!TryReadEntry(raw, out string? payload, out long? freshMs, out long? staleMs)
			|| !RedisCacheEntry.IsFresh(freshMs, DateTimeOffset.UtcNow))
		{
			Interlocked.Increment(ref _misses);
			return null;
		}

		Interlocked.Increment(ref _hits);
		return Deserialize<T>(payload);
	}

	public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);
		cancellationToken.ThrowIfCancellationRequested();

		return SetEntryAsync(key, value, ttl ?? _options.DefaultTtl, staleTtl: null, cancellationToken);
	}

	public Task SetStaleWhileRevalidateAsync<T>(
		string key,
		T value,
		TimeSpan ttl,
		TimeSpan staleTtl,
		CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);
		cancellationToken.ThrowIfCancellationRequested();

		return SetEntryAsync(key, value, ttl, staleTtl, cancellationToken);
	}

	public async Task<T?> GetOrSetAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan? ttl = null,
		CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(factory);

		T? cached = await GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
		return cached ?? await FillAsync(key, factory, ttl, staleTtl: null, cancellationToken).ConfigureAwait(false);
	}

	public async Task<T?> GetOrSetStaleWhileRevalidateAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan ttl,
		TimeSpan staleTtl,
		CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(factory);

		RedisValue raw = await _database.StringGetAsync(Key(key)).ConfigureAwait(false);
		DateTimeOffset now = DateTimeOffset.UtcNow;

		if (TryReadEntry(raw, out string? payload, out long? freshMs, out long? staleMs))
		{
			if (RedisCacheEntry.IsFresh(freshMs, now))
			{
				Interlocked.Increment(ref _hits);
				return Deserialize<T>(payload);
			}

			if (RedisCacheEntry.IsStaleServable(freshMs, staleMs, now))
			{
				// Serve the stale value instantly and refresh in the background; the
				// refresh takes a cross-instance lock so only one instance repopulates.
				Interlocked.Increment(ref _staleHits);
				T? stale = Deserialize<T>(payload);
				_ = RefreshInBackgroundAsync<T>(key, factory, ttl, staleTtl);
				return stale;
			}

			// Beyond the stale window: treat as missing and clean up best-effort.
			Interlocked.Increment(ref _misses);
			await _database.KeyDeleteAsync(Key(key)).ConfigureAwait(false);
		}
		else
		{
			Interlocked.Increment(ref _misses);
		}

		return await FillAsync(key, factory, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
	}

	public bool Remove(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);

		bool deleted = _database.KeyDelete(Key(key));
		_ = _database.SetRemove(_options.IndexKey, Key(key));
		return deleted;
	}

	public int RemoveByPrefix(string prefix)
	{
		ArgumentException.ThrowIfNullOrEmpty(prefix);

		string pattern = Key(prefix) + "*";
		IServer server = ResolveServer();

		List<RedisKey> matches = [];
		List<string> indexValues = [];
		foreach (RedisKey redisKey in server.Keys(_database.Database, pattern, _options.ScanPageSize))
		{
			matches.Add(redisKey);
			indexValues.Add((string?)redisKey ?? string.Empty);
		}

		if (matches.Count == 0)
		{
			return 0;
		}

		_ = _database.KeyDelete(matches.ToArray());
		_ = _database.SetRemove(_options.IndexKey, indexValues.Select(static value => (RedisValue)value).ToArray());
		return matches.Count;
	}

	public void Clear()
	{
		RedisValue[] members = _database.SetMembers(_options.IndexKey);
		if (members.Length > 0)
		{
			// Delete only keys this cache indexed; never FLUSHDB (the server may be shared).
			_ = _database.KeyDelete(members.Select(static member => (RedisKey)((string?)member ?? string.Empty)).ToArray());
		}

		_ = _database.KeyDelete(_options.IndexKey);
	}

	public void Dispose()
	{
		if (_ownsMultiplexer)
		{
			_multiplexer.Dispose();
		}
	}

	private async Task SetEntryAsync<T>(string key, T value, TimeSpan? ttl, TimeSpan? staleTtl, CancellationToken cancellationToken) where T : class
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		DateTimeOffset? freshExpiresAt = ttl.HasValue ? now + ttl.Value : null;
		DateTimeOffset? staleExpiresAt = freshExpiresAt.HasValue && staleTtl.HasValue ? freshExpiresAt.Value + staleTtl.Value : null;

		string json = RedisCacheEntry.Encode(value, freshExpiresAt, staleExpiresAt);
		TimeSpan? redisTtl = staleExpiresAt.HasValue ? staleExpiresAt.Value - now : freshExpiresAt.HasValue ? freshExpiresAt.Value - now : null;

		await _database.StringSetAsync(Key(key), json, redisTtl, When.Always, CommandFlags.None).ConfigureAwait(false);
		await _database.SetAddAsync(_options.IndexKey, Key(key)).ConfigureAwait(false);
	}

	/// <summary>Synchronous fill path with per-instance single-flight deduplication.</summary>
	private async Task<T?> FillAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan? ttl,
		TimeSpan? staleTtl,
		CancellationToken cancellationToken) where T : class
	{
		while (true)
		{
			Task<object?> task;
			bool isShared;

			lock (_inFlightGate)
			{
				if (_inFlight.TryGetValue(key, out Task<object?>? existing) && !existing.IsCompleted)
				{
					// Another caller is already filling this key; share its result.
					task = existing;
					isShared = true;
				}
				else
				{
					task = CreateAndCacheAsync<T>(key, factory, ttl, staleTtl, cancellationToken);
					_inFlight[key] = task;
					isShared = false;
				}
			}

			try
			{
				return await task.ConfigureAwait(false) is T typed ? typed : null;
			}
			catch (OperationCanceledException) when (isShared)
			{
				// The shared task was cancelled by its initiating caller; retry so this
				// caller can find a fresh value or start a new fill.
			}
		}
	}

	private async Task<object?> CreateAndCacheAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan? ttl,
		TimeSpan? staleTtl,
		CancellationToken cancellationToken) where T : class
	{
		try
		{
			T? value = await factory(cancellationToken).ConfigureAwait(false);
			if (value is not null)
			{
				await SetEntryAsync(key, value, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
			}

			return value;
		}
		finally
		{
			lock (_inFlightGate)
			{
				_ = _inFlight.TryRemove(key, out _);
			}
		}
	}

	/// <summary>
	/// Fire-and-forget stale-while-revalidate refresh. A short-lived Redis lock
	/// (SET NX PX + token-checked release) deduplicates across instances; failure
	/// keeps the stale entry servable until its stale window lapses.
	/// </summary>
	private async Task RefreshInBackgroundAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan ttl,
		TimeSpan staleTtl) where T : class
	{
		try
		{
			string lockKey = _options.LockKeyPrefix + key;
			string token = Guid.NewGuid().ToString("N");
			if (!await _database.LockTakeAsync(lockKey, token, _options.LockTimeout).ConfigureAwait(false))
			{
				return; // Another instance is already refreshing this key.
			}

			try
			{
				T? value = await factory(CancellationToken.None).ConfigureAwait(false);
				if (value is not null)
				{
					await SetEntryAsync(key, value, ttl, staleTtl, CancellationToken.None).ConfigureAwait(false);
				}
			}
			finally
			{
				await _database.LockReleaseAsync(lockKey, token).ConfigureAwait(false);
			}
		}
		catch
		{
			// Swallow: the stale entry remains servable until the stale window ends.
		}
	}

	private IServer ResolveServer()
	{
		EndPoint[] endpoints = _multiplexer.GetEndPoints(configuredOnly: true);
		foreach (EndPoint endpoint in endpoints)
		{
			IServer server = _multiplexer.GetServer(endpoint);
			if (server.IsConnected)
			{
				return server;
			}
		}

		return _multiplexer.GetServer(endpoints[0]);
	}

	private static bool TryReadEntry(RedisValue raw, out string? payload, out long? freshMs, out long? staleMs)
	{
		if (raw.IsNull || !RedisCacheEntry.TryDecode(raw.ToString(), out payload, out freshMs, out staleMs))
		{
			payload = null;
			freshMs = null;
			staleMs = null;
			return false;
		}

		return true;
	}

	private static T? Deserialize<T>(string? payload) where T : class
	{
		if (payload is null)
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<T>(payload);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private string Key(string key) => _options.KeyPrefix + key;
}
