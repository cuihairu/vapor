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
