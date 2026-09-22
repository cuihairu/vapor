using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

public sealed class TradeRateLimiterTests : IDisposable
{
	private DateTimeOffset _now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private TradeRateLimiter CreateLimiter(TradeRateLimiterOptions options) =>
		new(options, () => _now);

	public void Dispose()
	{
		_now = _now.AddYears(100); // expire any pending waits logically
	}

	[Fact]
	public async Task Acquire_AndDispose_AllowsSubsequentOperations()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5)
		});

		var lease = await limiter.AcquireAsync("acct");
		Assert.NotNull(lease);
		lease!.Dispose();

		var second = await limiter.AcquireAsync("acct");
		Assert.NotNull(second);
		second!.Dispose();
	}

	[Fact]
	public void Dispose_WithoutAnyAcquire_NullReleaseIsSafe()
	{
		// Disposing before a concurrency slot was ever acquired: the pending
		// release callback is still null and must be skipped, not invoked.
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5)
		});
	}

	[Fact]
	public async Task Acquire_WhenConcurrencySlotHeld_ReturnsNullAfterTimeout()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5),
			AcquireTimeout = TimeSpan.FromMilliseconds(100)
		});

		var first = await limiter.AcquireAsync("acct");
		Assert.NotNull(first);

		var second = await limiter.AcquireAsync("acct");

		Assert.Null(second);
		first!.Dispose();
	}

	[Fact]
	public async Task Acquire_AfterRelease_WhenConcurrencyFreed_Succeeds()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5),
			// Budget only covers pool scheduling of the completion; the slot is
			// already free so the wait returns immediately in healthy paths.
			AcquireTimeout = TimeSpan.FromSeconds(30)
		});

		var first = await limiter.AcquireAsync("acct");
		first!.Dispose();

		var second = await limiter.AcquireAsync("acct");
		Assert.NotNull(second);
		second!.Dispose();
	}

	[Fact]
	public async Task Lease_DisposeAfterLimiterDisposed_DoesNotThrow()
	{
		var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5)
		});

		var lease = await limiter.AcquireAsync("acct");
		Assert.NotNull(lease);
		limiter.Dispose(); // tears down the per-account semaphore under the live lease

		// Releasing into a disposed semaphore is swallowed by the limiter.
		lease!.Dispose();
	}

	[Fact]
	public void Dispose_Twice_SecondIsNoOp()
	{
		var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5)
		});

		limiter.Dispose();
		limiter.Dispose(); // idempotent
	}

	[Fact]
	public async Task Acquire_WhenWindowQuotaExhausted_ReturnsNull()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(10),
			AcquireTimeout = TimeSpan.FromMilliseconds(100)
		});

		var first = await limiter.AcquireAsync("acct");
		Assert.NotNull(first);
		first!.Dispose();

		// Window quota used, clock has not advanced: the next acquire must time out.
		var second = await limiter.AcquireAsync("acct");

		Assert.Null(second);
	}

	[Fact]
	public async Task Acquire_AfterWindowElapses_SucceedsAgain()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(10),
			// Window already elapsed on the logical clock; budget only covers
			// pool scheduling of the completion.
			AcquireTimeout = TimeSpan.FromSeconds(30)
		});

		var first = await limiter.AcquireAsync("acct");
		first!.Dispose();

		_now += TimeSpan.FromMinutes(11);

		var second = await limiter.AcquireAsync("acct");
		Assert.NotNull(second);
		second!.Dispose();
	}

	[Fact]
	public async Task Acquire_WithDifferentKeys_DoesNotInterfere()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(10),
			AcquireTimeout = TimeSpan.FromMilliseconds(100)
		});

		var a = await limiter.AcquireAsync("acct-a");
		a!.Dispose();

		var b = await limiter.AcquireAsync("acct-b");

		Assert.NotNull(b);
		b!.Dispose();
	}

	[Fact]
	public async Task Acquire_WithHigherConcurrency_AllowsParallelLeases()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 10,
			Window = TimeSpan.FromMinutes(5),
			MaxConcurrentOperations = 2
		});

		var first = await limiter.AcquireAsync("acct");
		var second = await limiter.AcquireAsync("acct");

		Assert.NotNull(first);
		Assert.NotNull(second);

		first!.Dispose();
		second!.Dispose();
	}

	[Fact]
	public async Task Acquire_WithCancelledToken_ReturnsNull()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(10)
		});

		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
		var first = await limiter.AcquireAsync("acct");
		// Hold the concurrency slot; the second acquire should observe cancellation.

		var second = await limiter.AcquireAsync("acct", cts.Token);

		Assert.Null(second);
		first!.Dispose();
	}

	[Fact]
	public async Task Acquire_WhenDeadlineAlreadyElapsedBeforeWaiting_ReturnsNull()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 5,
			Window = TimeSpan.FromMinutes(5),
			AcquireTimeout = TimeSpan.Zero
		});

		var first = await limiter.AcquireAsync("acct");
		Assert.NotNull(first);

		// Slot held and the zero deadline already passed: fail before the timed wait.
		var second = await limiter.AcquireAsync("acct");

		Assert.Null(second);
		first!.Dispose();
	}

	[Fact]
	public async Task Acquire_WhenConcurrencyFreedDuringTimedWait_Succeeds()
	{
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 10,
			Window = TimeSpan.FromMinutes(5),
			AcquireTimeout = TimeSpan.FromSeconds(30)
		});

		var first = await limiter.AcquireAsync("acct");
		Assert.NotNull(first);

		// The slot is held, so the second acquire cannot complete before the
		// release: it is either already parked in its timed wait or about to
		// park. SemaphoreSlim turns a pre-park release into available count,
		// so both interleavings succeed — no Task.Delay guesswork about when
		// the waiter parks. The budget only covers pool scheduling.
		var waiter = limiter.AcquireAsync("acct");
		first!.Dispose();

		var second = await waiter.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.NotNull(second);
		second!.Dispose();
	}

	[Fact]
	public async Task Acquire_WhenWindowWaitBelowOneMillisecond_ClampsToMinimum()
	{
		// Frozen logical clock 0.4ms before the window frees: the wait is always
		// sub-millisecond (clamped to 1ms) and the window never actually frees,
		// so the acquire keeps looping until the real deadline gives up.
		_now += TimeSpan.FromMinutes(1);
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromSeconds(10),
			MaxConcurrentOperations = 2,
			AcquireTimeout = TimeSpan.FromMilliseconds(120)
		});

		var first = await limiter.AcquireAsync("acct");
		Assert.NotNull(first);

		_now += TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(4000);

		var second = await limiter.AcquireAsync("acct");

		Assert.Null(second);
		first!.Dispose();
	}

	[Fact]
	public async Task Acquire_WhenCanceledDuringWindowWait_ReturnsNull()
	{
		// Slot is free but the window is full: the cancellation must hit the
		// window Task.Delay (not the semaphore wait) and still yield null.
		using var limiter = CreateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(10),
			MaxConcurrentOperations = 2,
			AcquireTimeout = TimeSpan.FromSeconds(30)
		});

		var first = await limiter.AcquireAsync("acct");
		Assert.NotNull(first);

		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
		var second = await limiter.AcquireAsync("acct", cts.Token);

		Assert.Null(second);
		first!.Dispose();
	}

	[Fact]
	public void Constructor_WithInvalidOptions_Throws()
	{
		Assert.Throws<InvalidOperationException>(
			() => new TradeRateLimiter(new TradeRateLimiterOptions { MaxOperationsPerWindow = 0 }));
		Assert.Throws<InvalidOperationException>(
			() => new TradeRateLimiter(new TradeRateLimiterOptions { Window = TimeSpan.Zero }));
		Assert.Throws<InvalidOperationException>(
			() => new TradeRateLimiter(new TradeRateLimiterOptions { MaxConcurrentOperations = 0 }));
	}

	[Fact]
	public async Task Acquire_OnDisposedLimiter_Throws()
	{
		var limiter = CreateLimiter(new TradeRateLimiterOptions());
		limiter.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => limiter.AcquireAsync("acct"));
	}
}
