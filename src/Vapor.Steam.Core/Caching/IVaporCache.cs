namespace Vapor.Steam.Core.Caching;

/// <summary>
/// Async cache abstraction for Steam data (game info, item info, search results).
/// Implementations may be in-memory (default) or backed by Redis/other stores.
/// </summary>
public interface IVaporCache
{
	/// <summary>Current number of live entries.</summary>
	int Count { get; }

	/// <summary>Cache hit counter (observability).</summary>
	long Hits { get; }

	/// <summary>Cache miss counter (observability).</summary>
	long Misses { get; }

	Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

	Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default) where T : class;

	/// <summary>
	/// Returns the cached value when present and fresh; otherwise invokes
	/// <paramref name="factory"/>, caches the result and returns it.
	/// Concurrent requests for the same key share a single factory invocation.
	/// </summary>
	Task<T?> GetOrSetAsync<T>(
		string key,
		Func<CancellationToken, Task<T>> factory,
		TimeSpan? ttl = null,
		CancellationToken cancellationToken = default) where T : class;

	bool Remove(string key);

	void Clear();
}

/// <summary>
/// Options for <see cref="MemoryVaporCache"/>.
/// </summary>
public sealed record MemoryVaporCacheOptions
{
	/// <summary>Maximum number of entries before LRU eviction. Defaults to 1024.</summary>
	public int Capacity { get; init; } = 1024;

	/// <summary>
	/// TTL applied when <see cref="IVaporCache.SetAsync{T}"/> is called without an explicit TTL.
	/// Null means entries never expire unless evicted. Defaults to 10 minutes.
	/// </summary>
	public TimeSpan? DefaultTtl { get; init; } = TimeSpan.FromMinutes(10);

	public void Validate()
	{
		if (Capacity <= 0)
		{
			throw new InvalidOperationException("Capacity must be positive");
		}
	}
}
