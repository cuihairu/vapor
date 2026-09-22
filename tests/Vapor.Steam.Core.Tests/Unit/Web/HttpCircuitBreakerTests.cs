using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class HttpCircuitBreakerTests
{
	private DateTimeOffset _now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private HttpCircuitBreaker Create(int threshold = 3, TimeSpan? openDuration = null) =>
		new(threshold, openDuration ?? TimeSpan.FromSeconds(30), () => _now);

	[Fact]
	public void Initially_ClosedAndAllowsRequests()
	{
		var breaker = Create();

		Assert.Equal(CircuitBreakerState.Closed, breaker.State);
		Assert.True(breaker.TryAllowRequest());
	}

	[Fact]
	public void Constructor_NullOpenDuration_FallsBackToDefaultThirtySeconds()
	{
		// The optional openDuration parameter falls back to 30s when omitted;
		// every other test pins the explicit value.
		var breaker = new HttpCircuitBreaker(failureThreshold: 3, openDuration: null);

		Assert.Equal(3, breaker.FailureThreshold);
		Assert.Equal(TimeSpan.FromSeconds(30), breaker.OpenDuration);
	}

	[Fact]
	public void ConsecutiveFailures_UpToThreshold_Open()
	{
		var breaker = Create(threshold: 3);

		breaker.RecordFailure();
		breaker.RecordFailure();
		Assert.Equal(CircuitBreakerState.Closed, breaker.State);

		breaker.RecordFailure();
		Assert.Equal(CircuitBreakerState.Open, breaker.State);
		Assert.False(breaker.TryAllowRequest());
	}

	[Fact]
	public void Success_ResetsFailureCount()
	{
		var breaker = Create(threshold: 3);

		breaker.RecordFailure();
		breaker.RecordFailure();
		breaker.RecordSuccess();
		breaker.RecordFailure();
		breaker.RecordFailure();

		Assert.Equal(CircuitBreakerState.Closed, breaker.State);
	}

	[Fact]
	public void AfterOpenDuration_TransitionsToHalfOpen_AndAllowsSingleProbe()
	{
		var breaker = Create(threshold: 1, openDuration: TimeSpan.FromSeconds(10));

		breaker.RecordFailure();
		Assert.Equal(CircuitBreakerState.Open, breaker.State);

		_now += TimeSpan.FromSeconds(11);

		Assert.Equal(CircuitBreakerState.HalfOpen, breaker.State);
		Assert.True(breaker.TryAllowRequest(), "half-open should allow the first probe");
		Assert.False(breaker.TryAllowRequest(), "half-open should reject concurrent probes");
	}

	[Fact]
	public void HalfOpenProbeSuccess_ClosesBreaker()
	{
		var breaker = Create(threshold: 1, openDuration: TimeSpan.FromSeconds(10));

		breaker.RecordFailure();
		_now += TimeSpan.FromSeconds(11);
		_ = breaker.TryAllowRequest();

		breaker.RecordSuccess();

		Assert.Equal(CircuitBreakerState.Closed, breaker.State);
		Assert.True(breaker.TryAllowRequest());
	}

	[Fact]
	public void HalfOpenProbeFailure_ReopensImmediately()
	{
		var breaker = Create(threshold: 1, openDuration: TimeSpan.FromSeconds(10));

		breaker.RecordFailure();
		_now += TimeSpan.FromSeconds(11);
		_ = breaker.TryAllowRequest();

		breaker.RecordFailure();

		Assert.Equal(CircuitBreakerState.Open, breaker.State);
		Assert.False(breaker.TryAllowRequest());
	}

	[Fact]
	public void Reset_ReturnsToClosed()
	{
		var breaker = Create(threshold: 1);

		breaker.RecordFailure();
		breaker.Reset();

		Assert.Equal(CircuitBreakerState.Closed, breaker.State);
	}

	[Fact]
	public void Constructor_WithInvalidThreshold_Throws()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new HttpCircuitBreaker(failureThreshold: 0));
	}
}

public sealed class WebRequestMetricsTests
{
	[Fact]
	public void Counters_AreExposedThroughGettersAndSnapshot()
	{
		var metrics = new WebRequestMetrics();
		metrics.RecordTotal();
		metrics.RecordTotal();
		metrics.RecordSuccess();
		metrics.RecordRateLimited();
		metrics.RecordServerError();
		metrics.RecordClientError();
		metrics.RecordNetworkFailure();
		metrics.RecordRetry();
		metrics.RecordCircuitBreakerRejection();

		Assert.Equal(2, metrics.TotalRequests);
		Assert.Equal(1, metrics.Successes);
		Assert.Equal(1, metrics.RateLimited429);
		Assert.Equal(1, metrics.ServerErrors5xx);
		Assert.Equal(1, metrics.ClientErrors4xx);
		Assert.Equal(1, metrics.NetworkFailures);
		Assert.Equal(1, metrics.Retries);
		Assert.Equal(1, metrics.CircuitBreakerRejections);

		WebRequestMetricsSnapshot snapshot = metrics.Snapshot();
		Assert.Equal(metrics.TotalRequests, snapshot.TotalRequests);
		Assert.Equal(metrics.Successes, snapshot.Successes);
		Assert.Equal(metrics.RateLimited429, snapshot.RateLimited429);
		Assert.Equal(metrics.ServerErrors5xx, snapshot.ServerErrors5xx);
		Assert.Equal(metrics.ClientErrors4xx, snapshot.ClientErrors4xx);
		Assert.Equal(metrics.NetworkFailures, snapshot.NetworkFailures);
		Assert.Equal(metrics.Retries, snapshot.Retries);
		Assert.Equal(metrics.CircuitBreakerRejections, snapshot.CircuitBreakerRejections);
		// (network failures + rate limits + 5xx) / total = 3 / 2
		Assert.Equal(1.5, snapshot.FailureRate);
	}

	[Fact]
	public void FailureRate_WithoutRequests_IsZero()
	{
		Assert.Equal(0, new WebRequestMetrics().Snapshot().FailureRate);
	}

	[Fact]
	public void FailureRate_WithOnlySuccesses_IsZero()
	{
		var metrics = new WebRequestMetrics();
		metrics.RecordTotal();
		metrics.RecordSuccess();

		Assert.Equal(0, metrics.Snapshot().FailureRate);
	}

	[Fact]
	public void CircuitBreakerOpenException_ConstructorChain_CarriesMessageAndInner()
	{
		// Full ctor surface contract: every overload must survive the message and
		// inner exception the code (or a future caller) hands it. The parameterless
		// overload carries the runtime's default exception message.
		Assert.StartsWith("Exception of type", new CircuitBreakerOpenException().Message, StringComparison.Ordinal);
		Assert.Equal("circuit tripped", new CircuitBreakerOpenException("circuit tripped").Message);

		var inner = new InvalidOperationException("socket died");
		var ex = new CircuitBreakerOpenException("circuit open", inner);
		Assert.Equal("circuit open", ex.Message);
		Assert.Same(inner, ex.InnerException);
	}
}
