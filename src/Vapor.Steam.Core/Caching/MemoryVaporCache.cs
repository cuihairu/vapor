namespace Vapor.Steam.Core.Caching;

/// <summary>
/// Thread-safe in-memory cache with per-entry TTL, LRU eviction, hit/miss
/// counters and single-flight factory deduplication (cache stampede protection).
/// </summary>
public sealed class MemoryVaporCache : IVaporCache, IDisposable
{
	private readonly MemoryVaporCacheOptions _options;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly object _gate = new();
	private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
	private readonly LinkedList<string> _lru = new(); // most-recently-used first
	private readonly Dictionary<string, Task<object?>> _inFlight = new(StringComparer.Ordinal);

	public MemoryVaporCache(MemoryVaporCacheOptions? options = null, Func<DateTimeOffset>? utcNow = null)
	{
		_options = options ?? new MemoryVaporCacheOptions();
		_options.Validate();
		_utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
	}

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _entries.Count;
			}
		}
	}

	public long Hits { get; private set; }

	public long Misses { get; private set; }

	public long StaleHits { get; private set; }

	public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);

		lock (_gate)
		{
			if (TryGetEntryLocked(key, out object? value))
			{
				Hits++;
				return Task.FromResult(value as T);
			}

			Misses++;
		}

		return Task.FromResult<T?>(null);
	}

	public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(value);

		cancellationToken.ThrowIfCancellationRequested();

		return SetEntryAsync(key, value, ttl, staleTtl: null, cancellationToken);
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

	private Task SetEntryAsync<T>(string key, T value, TimeSpan? ttl, TimeSpan? staleTtl, CancellationToken cancellationToken) where T : class
	{
		DateTimeOffset now = _utcNow();
		DateTimeOffset? expiresAt = ResolveExpiry(ttl, now);
		DateTimeOffset? staleExpiresAt = expiresAt.HasValue && staleTtl.HasValue ? expiresAt.Value + staleTtl.Value : null;

		lock (_gate)
		{
			if (_entries.TryGetValue(key, out var existing))
			{
				existing.Value = value;
				existing.ExpiresAt = expiresAt;
				existing.StaleExpiresAt = staleExpiresAt;
				_lru.Remove(existing.Node);
				_lru.AddFirst(existing.Node);
			}
			else
			{
				var node = new LinkedListNode<string>(key);
				_entries[key] = new CacheEntry(value, expiresAt, staleExpiresAt, node);
				_lru.AddFirst(node);

				EvictBeyondCapacityLocked();
			}
		}

		return Task.CompletedTask;
	}

	public async Task<T?> GetOrSetAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan? ttl = null,
		CancellationToken cancellationToken = default) where T : class
	{
		ArgumentException.ThrowIfNullOrEmpty(key);
		ArgumentNullException.ThrowIfNull(factory);

		while (true)
		{
			Task<object?> task;
			bool isShared;

			lock (_gate)
			{
				if (TryGetEntryLocked(key, out object? value))
				{
					Hits++;
					return value as T;
				}

				Misses++;

				if (_inFlight.TryGetValue(key, out var existing) && !existing.IsCompleted)
				{
					// Another caller is already filling this key; share its result.
					task = existing;
					isShared = true;
				}
				else
				{
					task = CreateAndCacheAsync(key, factory, ttl, cancellationToken);
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
				// The shared task was cancelled by its initiating caller; retry so
				// this caller can find a fresh value or start a new creation.
			}
		}
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

		DateTimeOffset now = _utcNow();
		object? staleValue = null;
		Task<object?>? fillTask = null;

		lock (_gate)
		{
			if (_entries.TryGetValue(key, out var entry))
			{
				if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value > now)
				{
					// Fresh: serve directly and touch LRU.
					Hits++;
					_lru.Remove(entry.Node);
					_lru.AddFirst(entry.Node);
					return entry.Value as T;
				}

				if (entry.StaleExpiresAt.HasValue && entry.StaleExpiresAt.Value > now)
				{
					// Stale but servable: return it and refresh in the background.
					StaleHits++;
					staleValue = entry.Value;
					if (!_inFlight.TryGetValue(key, out var existing) || existing.IsCompleted)
					{
						_ = StartBackgroundRefreshAsync(key, factory, ttl, staleTtl);
					}
				}
				else
				{
					// Beyond the stale window: treat as missing.
					_entries.Remove(key);
					_lru.Remove(entry.Node);
					Misses++;
				}
			}
			else
			{
				Misses++;
			}

			if (staleValue is null)
			{
				// Synchronous fill path (same single-flight machinery as GetOrSetAsync).
				if (!_inFlight.TryGetValue(key, out var pending) || pending.IsCompleted)
				{
					pending = CreateAndCacheAsync(key, factory, ttl, cancellationToken, staleTtl);
					_inFlight[key] = pending;
				}

				fillTask = pending;
			}
		}

		if (staleValue is not null)
		{
			return staleValue as T;
		}

		return await fillTask!.ConfigureAwait(false) is T typed ? typed : null;
	}

	/// <summary>Fire-and-forget SWR refresh; failure keeps the stale entry until its stale window lapses.</summary>
	private async Task StartBackgroundRefreshAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan ttl,
		TimeSpan staleTtl) where T : class
	{
		Task<object?> refresh = RunBackgroundRefreshAsync(key, factory, ttl, staleTtl);
		_inFlight[key] = refresh;

		try
		{
			await refresh.ConfigureAwait(false);
		}
		catch
		{
			// Swallow: the stale entry stays servable until the stale window ends.
		}
		finally
		{
			lock (_gate)
			{
				if (_inFlight.TryGetValue(key, out var current) && current == refresh)
				{
					_inFlight.Remove(key);
				}
			}
		}
	}

	private async Task<object?> RunBackgroundRefreshAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan ttl,
		TimeSpan staleTtl) where T : class
	{
		T? value = await factory(CancellationToken.None).ConfigureAwait(false);
		if (value is not null)
		{
			await SetEntryAsync(key, value, ttl, staleTtl, CancellationToken.None).ConfigureAwait(false);
		}

		return value;
	}

	public int RemoveByPrefix(string prefix)
	{
		ArgumentException.ThrowIfNullOrEmpty(prefix);

		lock (_gate)
		{
			List<string> matches = _entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
			foreach (string key in matches)
			{
				if (_entries.Remove(key, out var entry))
				{
					_lru.Remove(entry.Node);
				}
			}

			return matches.Count;
		}
	}

	public bool Remove(string key)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);

		lock (_gate)
		{
			if (_entries.Remove(key, out var entry))
			{
				_lru.Remove(entry.Node);
				return true;
			}
		}

		return false;
	}

	public void Clear()
	{
		lock (_gate)
		{
			_entries.Clear();
			_lru.Clear();
		}
	}

	public void Dispose()
	{
		Clear();
	}

	private async Task<object?> CreateAndCacheAsync<T>(
		string key,
		Func<CancellationToken, Task<T?>> factory,
		TimeSpan? ttl,
		CancellationToken cancellationToken,
		TimeSpan? staleTtl = null) where T : class
	{
		try
		{
			T? value = await factory(cancellationToken).ConfigureAwait(false);

			if (value != null)
			{
				await SetEntryAsync(key, value, ttl, staleTtl, cancellationToken).ConfigureAwait(false);
			}

			return value;
		}
		finally
		{
			lock (_gate)
			{
				_inFlight.Remove(key);
			}
		}
	}

	private bool TryGetEntryLocked(string key, out object? value)
	{
		if (_entries.TryGetValue(key, out var entry))
		{
			if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= _utcNow())
			{
				// Lazy expiration.
				_entries.Remove(key);
				_lru.Remove(entry.Node);
			}
			else
			{
				value = entry.Value;
				_lru.Remove(entry.Node);
				_lru.AddFirst(entry.Node);
				return true;
			}
		}

		value = null;
		return false;
	}

	private DateTimeOffset? ResolveExpiry(TimeSpan? ttl, DateTimeOffset now)
	{
		TimeSpan? effective = ttl ?? _options.DefaultTtl;
		return effective.HasValue ? now + effective.Value : null;
	}

	private void EvictBeyondCapacityLocked()
	{
		while (_entries.Count > _options.Capacity)
		{
			// _entries and _lru move in lockstep under the lock and Capacity is
			// validated positive, so the LRU is never empty while eviction runs.
			LinkedListNode<string> oldest = _lru.Last!;

			_lru.RemoveLast();
			_entries.Remove(oldest.Value);
		}
	}

	private sealed class CacheEntry(object value, DateTimeOffset? expiresAt, DateTimeOffset? staleExpiresAt, LinkedListNode<string> node)
	{
		public object Value { get; set; } = value;

		public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;

		/// <summary>Stale-while-revalidate window end; null for entries written without a stale window.</summary>
		public DateTimeOffset? StaleExpiresAt { get; set; } = staleExpiresAt;

		public LinkedListNode<string> Node { get; } = node;
	}
}
