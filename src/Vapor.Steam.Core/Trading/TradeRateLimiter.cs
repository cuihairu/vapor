namespace Vapor.Steam.Core.Trading;

/// <summary>
/// Options for <see cref="TradeRateLimiter"/>.
/// </summary>
public sealed record TradeRateLimiterOptions
{
	/// <summary>Maximum trade operations per account within the sliding window.</summary>
	public int MaxOperationsPerWindow { get; init; } = 5;

	/// <summary>Sliding window length. Defaults to 5 minutes.</summary>
	public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>How many trade operations a single account may run concurrently.</summary>
	public int MaxConcurrentOperations { get; init; } = 1;

	/// <summary>Maximum time to wait for a lease before giving up. Defaults to 30 seconds.</summary>
	public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);

	public void Validate()
	{
		if (MaxOperationsPerWindow <= 0)
		{
			throw new InvalidOperationException("MaxOperationsPerWindow must be positive");
		}

		if (Window <= TimeSpan.Zero)
		{
			throw new InvalidOperationException("Window must be positive");
		}

		if (MaxConcurrentOperations <= 0)
		{
			throw new InvalidOperationException("MaxConcurrentOperations must be positive");
		}
	}
}

/// <summary>
/// Per-account trade throttling: a sliding-window operation quota combined with
/// a concurrency gate, so a single account never issues concurrent trade calls
/// or exceeds a configurable operations-per-window rate.
/// </summary>
public sealed class TradeRateLimiter : IDisposable
{
	private readonly TradeRateLimiterOptions _options;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly object _gate = new();
	private readonly Dictionary<string, AccountLimiter> _limiters = new(StringComparer.Ordinal);
	private bool _disposed;

	public TradeRateLimiter(TradeRateLimiterOptions? options = null, Func<DateTimeOffset>? utcNow = null)
	{
		_options = options ?? new TradeRateLimiterOptions();
		_options.Validate();
		_utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
	}

	/// <summary>
	/// Acquires a trade-operation lease for the given account. Waits for a free
	/// concurrency slot and for window capacity, bounded by the configured timeout.
	/// Returns null when the lease could not be acquired in time.
	/// The caller must dispose the lease (typically via `using`).
	/// </summary>
	public async Task<TradeRateLease?> AcquireAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);
		ObjectDisposedException.ThrowIf(_disposed, this);

		AccountLimiter limiter = GetLimiter(accountName);

		// The acquire deadline is tracked with the real clock so that injected
		// logical clocks (used for window accounting in tests) cannot stall it.
		DateTime realDeadlineUtc = DateTime.UtcNow + _options.AcquireTimeout;

		if (!await limiter.Semaphore.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
		{
			var remaining = realDeadlineUtc - DateTime.UtcNow;
			if (remaining <= TimeSpan.Zero)
			{
				return null;
			}

			try
			{
				if (!await limiter.Semaphore.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
				{
					return null;
				}
			}
			catch (OperationCanceledException)
			{
				return null;
			}
		}

		// Concurrency slot acquired; ensure it is released unless a lease is returned.
		bool leased = false;
		try
		{
			while (true)
			{
				DateTimeOffset now = _utcNow();

				TradeRateLease? lease = null;
				lock (limiter.Timestamps)
				{
					PruneWindow(limiter.Timestamps, now);

					if (limiter.Timestamps.Count < _options.MaxOperationsPerWindow)
					{
						limiter.Timestamps.Enqueue(now);
						lease = new TradeRateLease(() => Release(limiter));
					}
				}

				if (lease != null)
				{
					leased = true;
					return lease;
				}

				DateTimeOffset windowStart = PeekEarliest(limiter.Timestamps);
				var wait = windowStart + _options.Window - now;
				if (wait < TimeSpan.FromMilliseconds(1))
				{
					wait = TimeSpan.FromMilliseconds(1);
				}

				var untilDeadline = realDeadlineUtc - DateTime.UtcNow;
				if (untilDeadline <= TimeSpan.Zero)
				{
					return null;
				}

				if (wait > untilDeadline)
				{
					wait = untilDeadline;
				}

				try
				{
					await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return null;
				}
			}
		}
		finally
		{
			if (!leased)
			{
				Release(limiter);
			}
		}
	}

	private static void Release(AccountLimiter limiter)
	{
		try
		{
			limiter.Semaphore.Release();
		}
		catch (ObjectDisposedException)
		{
			// Limiter already torn down; nothing to release.
		}
	}

	private AccountLimiter GetLimiter(string accountName)
	{
		lock (_gate)
		{
			if (!_limiters.TryGetValue(accountName, out var limiter))
			{
				limiter = new AccountLimiter(_options.MaxConcurrentOperations);
				_limiters[accountName] = limiter;
			}

			return limiter;
		}
	}

	private void PruneWindow(Queue<DateTimeOffset> timestamps, DateTimeOffset now)
	{
		var cutoff = now - _options.Window;
		while (timestamps.Count > 0 && timestamps.Peek() <= cutoff)
		{
			timestamps.Dequeue();
		}
	}

	private DateTimeOffset PeekEarliest(Queue<DateTimeOffset> timestamps)
	{
		lock (timestamps)
		{
			return timestamps.Count > 0 ? timestamps.Peek() : _utcNow();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		lock (_gate)
		{
			foreach (var limiter in _limiters.Values)
			{
				limiter.Semaphore.Dispose();
			}

			_limiters.Clear();
		}

		_disposed = true;
	}

	private sealed class AccountLimiter(int concurrency)
	{
		public SemaphoreSlim Semaphore { get; } = new(concurrency, concurrency);

		public Queue<DateTimeOffset> Timestamps { get; } = new();
	}
}

/// <summary>
/// A held trade-operation lease. Dispose to release the concurrency slot.
/// </summary>
public sealed class TradeRateLease : IDisposable
{
	private Action? _release;
	private bool _disposed;

	internal TradeRateLease(Action release)
	{
		_release = release;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_release?.Invoke();
		_release = null;
	}
}
