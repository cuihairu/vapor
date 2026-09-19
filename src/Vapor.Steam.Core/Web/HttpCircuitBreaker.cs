namespace Vapor.Steam.Core.Web;

/// <summary>
/// Circuit breaker states.
/// </summary>
public enum CircuitBreakerState
{
	Closed = 0,
	Open = 1,
	HalfOpen = 2
}

/// <summary>
/// Failure-driven circuit breaker protecting outbound HTTP calls.
/// Transitions: Closed →(N consecutive failures)→ Open →(open duration)→ HalfOpen
/// →(probe success)→ Closed / (probe failure)→ Open.
/// </summary>
public sealed class HttpCircuitBreaker
{
	private readonly object _gate = new();
	private readonly Func<DateTimeOffset> _utcNow;

	private CircuitBreakerState _state = CircuitBreakerState.Closed;
	private int _consecutiveFailures;
	private DateTimeOffset _openedAt;
	private bool _halfOpenProbeInFlight;

	public HttpCircuitBreaker(int failureThreshold = 10, TimeSpan? openDuration = null, Func<DateTimeOffset>? utcNow = null)
	{
		if (failureThreshold <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(failureThreshold));
		}

		FailureThreshold = failureThreshold;
		OpenDuration = openDuration ?? TimeSpan.FromSeconds(30);
		_utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
	}

	public int FailureThreshold { get; }

	public TimeSpan OpenDuration { get; }

	public CircuitBreakerState State
	{
		get
		{
			lock (_gate)
			{
				return GetEffectiveStateLocked();
			}
		}
	}

	/// <summary>
	/// Whether a request may proceed. In HalfOpen only a single probe is allowed at a time.
	/// </summary>
	public bool TryAllowRequest()
	{
		lock (_gate)
		{
			var state = GetEffectiveStateLocked();
			if (state == CircuitBreakerState.Closed)
			{
				return true;
			}

			if (state == CircuitBreakerState.HalfOpen && !_halfOpenProbeInFlight)
			{
				_halfOpenProbeInFlight = true;
				return true;
			}

			// Open, or a half-open probe already in flight.
			return false;
		}
	}

	public void RecordSuccess()
	{
		lock (_gate)
		{
			_consecutiveFailures = 0;
			_state = CircuitBreakerState.Closed;
			_halfOpenProbeInFlight = false;
		}
	}

	public void RecordFailure()
	{
		lock (_gate)
		{
			// A failed half-open probe lands here too: the count is already at the
			// threshold, so the increment re-opens immediately with a fresh window.
			_consecutiveFailures++;
			if (_consecutiveFailures >= FailureThreshold)
			{
				_state = CircuitBreakerState.Open;
				_openedAt = _utcNow();
			}
		}
	}

	/// <summary>
	/// Resets the breaker to Closed (used by tests and manual recovery).
	/// </summary>
	public void Reset()
	{
		lock (_gate)
		{
			_state = CircuitBreakerState.Closed;
			_consecutiveFailures = 0;
			_halfOpenProbeInFlight = false;
		}
	}

	private CircuitBreakerState GetEffectiveStateLocked()
	{
		if (_state == CircuitBreakerState.Open && _utcNow() - _openedAt >= OpenDuration)
		{
			return CircuitBreakerState.HalfOpen;
		}

		return _state;
	}
}

/// <summary>
/// Aggregated request counters for observability.
/// </summary>
public sealed class WebRequestMetrics
{
	private long _totalRequests;
	private long _successes;
	private long _rateLimited429;
	private long _serverErrors5xx;
	private long _clientErrors4xx;
	private long _networkFailures;
	private long _retries;
	private long _circuitBreakerRejections;

	public long TotalRequests => _totalRequests;
	public long Successes => _successes;
	public long RateLimited429 => _rateLimited429;
	public long ServerErrors5xx => _serverErrors5xx;
	public long ClientErrors4xx => _clientErrors4xx;
	public long NetworkFailures => _networkFailures;
	public long Retries => _retries;
	public long CircuitBreakerRejections => _circuitBreakerRejections;

	public WebRequestMetricsSnapshot Snapshot() => new(
		_totalRequests, _successes, _rateLimited429, _serverErrors5xx, _clientErrors4xx,
		_networkFailures, _retries, _circuitBreakerRejections);

	internal void RecordTotal() => Interlocked.Increment(ref _totalRequests);
	internal void RecordSuccess() => Interlocked.Increment(ref _successes);
	internal void RecordRateLimited() => Interlocked.Increment(ref _rateLimited429);
	internal void RecordServerError() => Interlocked.Increment(ref _serverErrors5xx);
	internal void RecordClientError() => Interlocked.Increment(ref _clientErrors4xx);
	internal void RecordNetworkFailure() => Interlocked.Increment(ref _networkFailures);
	internal void RecordRetry() => Interlocked.Increment(ref _retries);
	internal void RecordCircuitBreakerRejection() => Interlocked.Increment(ref _circuitBreakerRejections);
}

public sealed record WebRequestMetricsSnapshot(
	long TotalRequests,
	long Successes,
	long RateLimited429,
	long ServerErrors5xx,
	long ClientErrors4xx,
	long NetworkFailures,
	long Retries,
	long CircuitBreakerRejections)
{
	public double FailureRate => TotalRequests == 0
		? 0
		: (double)(NetworkFailures + RateLimited429 + ServerErrors5xx) / TotalRequests;
}

/// <summary>
/// Thrown when the circuit breaker is open and the request was rejected without hitting the network.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
	public CircuitBreakerOpenException() { }

	public CircuitBreakerOpenException(string message) : base(message) { }

	public CircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException) { }
}
