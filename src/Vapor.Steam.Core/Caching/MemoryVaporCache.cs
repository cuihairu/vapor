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

		DateTimeOffset? expiresAt = ResolveExpiry(ttl);

		lock (_gate)
		{
			if (_entries.TryGetValue(key, out var existing))
			{
				existing.Value = value;
				existing.ExpiresAt = expiresAt;
				_lru.Remove(existing.Node);
				_lru.AddFirst(existing.Node);
			}
			else
			{
				var node = new LinkedListNode<string>(key);
				_entries[key] = new CacheEntry(value, expiresAt, node);
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
		CancellationToken cancellationToken) where T : class
	{
		try
		{
			T? value = await factory(cancellationToken).ConfigureAwait(false);

			if (value != null)
			{
				await SetAsync(key, value, ttl, cancellationToken).ConfigureAwait(false);
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

	private DateTimeOffset? ResolveExpiry(TimeSpan? ttl)
	{
		TimeSpan? effective = ttl ?? _options.DefaultTtl;
		return effective.HasValue ? _utcNow() + effective.Value : null;
	}

	private void EvictBeyondCapacityLocked()
	{
		while (_entries.Count > _options.Capacity)
		{
			LinkedListNode<string>? oldest = _lru.Last;
			if (oldest == null)
			{
				break;
			}

			_lru.RemoveLast();
			_entries.Remove(oldest.Value);
		}
	}

	private sealed class CacheEntry(object value, DateTimeOffset? expiresAt, LinkedListNode<string> node)
	{
		public object Value { get; set; } = value;

		public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;

		public LinkedListNode<string> Node { get; } = node;
	}
}
