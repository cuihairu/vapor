using Xunit;

namespace Vapor.ControlPlane.Tests;

// ApiKeyRateLimiter — the opt-in per-key sliding window behind
// Vapor_API_RATE_LIMIT_PER_MINUTE. A fake clock drives the window math
// deterministically: admission up to the limit, Retry-After derived from the
// oldest request still inside the window, readmission once entries age out,
// per-key isolation, and the threshold sweep that bounds memory to the keys
// active in the last window.
public sealed class ApiKeyRateLimiterTests
{
	private static DateTimeOffset At(int seconds, int milliseconds = 0)
	{
		DateTimeOffset start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
		return start.AddSeconds(seconds).AddMilliseconds(milliseconds);
	}

	[Fact]
	public void DisabledLimiter_IsOffAndAlwaysAllows()
	{
		ApiKeyRateLimiter limiter = new(0);

		Assert.False(limiter.IsEnabled);
		for (int i = 0; i < 100; i++)
		{
			Assert.Equal((true, 0), limiter.TryAcquire("key"));
		}
	}

	[Fact]
	public void TryAcquire_AllowsUpToLimit_ThenRejects()
	{
		DateTimeOffset now = At(0);
		ApiKeyRateLimiter limiter = new(2, () => now);

		Assert.Equal((true, 0), limiter.TryAcquire("key"));
		now = At(10);
		Assert.Equal((true, 0), limiter.TryAcquire("key"));
		now = At(20);
		// The oldest in-window request (t=0) expires at t=60: 40s away.
		Assert.Equal((false, 40), limiter.TryAcquire("key"));
	}

	[Fact]
	public void TryAcquire_RetryAfterRoundsUpAndNeverDropsBelowOne()
	{
		DateTimeOffset now = At(0);
		ApiKeyRateLimiter limiter = new(1, () => now);

		Assert.Equal((true, 0), limiter.TryAcquire("key"));
		now = At(0, 500);
		// 59.5s remaining → rounded up to 60.
		Assert.Equal((false, 60), limiter.TryAcquire("key"));
		now = At(59, 900);
		// 0.1s remaining → ceiling is 1 (the clamp floor).
		Assert.Equal((false, 1), limiter.TryAcquire("key"));
	}

	[Fact]
	public void TryAcquire_SlidingWindowReadmitsAsEntriesExpire()
	{
		DateTimeOffset now = At(0);
		ApiKeyRateLimiter limiter = new(2, () => now);

		Assert.True(limiter.TryAcquire("key").Allowed);
		now = At(1);
		Assert.True(limiter.TryAcquire("key").Allowed);
		now = At(10);
		Assert.False(limiter.TryAcquire("key").Allowed);

		// At t=61 the horizon is t=1, so both t=0 and t=1 leave the window.
		now = At(61);
		Assert.Equal((true, 0), limiter.TryAcquire("key"));
		now = At(62);
		Assert.Equal((true, 0), limiter.TryAcquire("key"));
		now = At(70);
		// The window now holds t=61 and t=62; the oldest expires at t=121.
		Assert.Equal((false, 51), limiter.TryAcquire("key"));
	}

	[Fact]
	public void TryAcquire_CountersAreIsolatedPerKey()
	{
		DateTimeOffset now = At(0);
		ApiKeyRateLimiter limiter = new(1, () => now);

		Assert.True(limiter.TryAcquire("key-a").Allowed);
		Assert.False(limiter.TryAcquire("key-a").Allowed);
		Assert.True(limiter.TryAcquire("key-b").Allowed);
		Assert.False(limiter.TryAcquire("key-b").Allowed);
	}

	[Fact]
	public void TryAcquire_SweepsExpiredKeysAtThreshold_KeepingLiveWindows()
	{
		DateTimeOffset now = At(0);
		ApiKeyRateLimiter limiter = new(1, () => now);

		// 1024 keys whose windows will all be expired by t=61, plus one key
		// with a live window: the next new key crosses the sweep threshold and
		// must drop the empty queues while keeping the live one intact.
		for (int i = 0; i < 1024; i++)
		{
			Assert.True(limiter.TryAcquire($"expired-{i}").Allowed);
		}

		now = At(30);
		Assert.True(limiter.TryAcquire("live").Allowed);

		now = At(61);
		Assert.Equal((true, 0), limiter.TryAcquire("new"));
		// The live key's window survived the sweep; the expired keys' did not.
		Assert.Equal((false, 29), limiter.TryAcquire("live"));
		Assert.Equal((true, 0), limiter.TryAcquire("expired-0"));
	}

	[Fact]
	public void DefaultClock_ComesFromUtcNow()
	{
		ApiKeyRateLimiter limiter = new(1);

		Assert.True(limiter.IsEnabled);
		Assert.True(limiter.TryAcquire($"default-{Guid.NewGuid():N}").Allowed);
	}
}
