namespace Vapor.ControlPlane;

/// <summary>
/// Opt-in per-key sliding-window rate limiter for the inbound REST surface
/// (<c>Vapor_API_RATE_LIMIT_PER_MINUTE</c>, off by default). The key is the raw
/// <c>Authorization</c> header value (requests without one share the
/// <c>anonymous</c> bucket): bearer credentials are unique per client by
/// construction, and the value is only ever used as a dictionary key, never
/// stored or logged. Rejections carry a Retry-After computed from the oldest
/// request still inside the window.
/// </summary>
public sealed class ApiKeyRateLimiter
{
	public const int WindowSeconds = 60;

	// Sweep threshold for per-key queues: creating a new key is the rare event
	// that makes an O(keys) sweep affordable, and it keeps memory bounded by the
	// keys active in the last window (an unauthenticated attacker cannot grow
	// the dictionary beyond one shared anonymous bucket; key rotation requires
	// distinct credentials, which is exactly what per-key limiting is for).
	private const int KeySweepThreshold = 1024;

	private readonly int _limitPerMinute;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly object _gate = new();
	private readonly Dictionary<string, Queue<DateTimeOffset>> _windows = new(StringComparer.Ordinal);

	public ApiKeyRateLimiter(int limitPerMinute, Func<DateTimeOffset>? utcNow = null)
	{
		_limitPerMinute = limitPerMinute;
		_utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
	}

	public bool IsEnabled => _limitPerMinute > 0;

	/// <summary>
	/// Records one request for the key. Returns whether it is allowed and, when
	/// rejected, how many seconds (rounded up, at least 1) until the oldest
	/// request in the window expires.
	/// </summary>
	public (bool Allowed, int RetryAfterSeconds) TryAcquire(string key)
	{
		if (!IsEnabled)
		{
			return (true, 0);
		}

		DateTimeOffset now = _utcNow();
		lock (_gate)
		{
			if (!_windows.TryGetValue(key, out Queue<DateTimeOffset>? window))
			{
				window = new Queue<DateTimeOffset>();
				if (_windows.Count >= KeySweepThreshold)
				{
					SweepExpired(_windows, now);
				}

				_windows[key] = window;
			}

			Prune(window, now);
			if (window.Count < _limitPerMinute)
			{
				window.Enqueue(now);
				return (true, 0);
			}

			// Sliding window: the oldest entry expires WindowSeconds after it was
			// recorded; round up and clamp so a boundary race never advertises 0.
			int retryAfter = (int)Math.Ceiling((window.Peek().AddSeconds(WindowSeconds) - now).TotalSeconds);
			return (false, Math.Max(retryAfter, 1));
		}
	}

	private static void Prune(Queue<DateTimeOffset> window, DateTimeOffset now)
	{
		DateTimeOffset horizon = now.AddSeconds(-WindowSeconds);
		while (window.Count > 0 && window.Peek() <= horizon)
		{
			window.Dequeue();
		}
	}

	private static void SweepExpired(Dictionary<string, Queue<DateTimeOffset>> windows, DateTimeOffset now)
	{
		List<string> expired = new();
		foreach ((string key, Queue<DateTimeOffset> window) in windows)
		{
			Prune(window, now);
			if (window.Count == 0)
			{
				expired.Add(key);
			}
		}

		foreach (string key in expired)
		{
			windows.Remove(key);
		}
	}
}
