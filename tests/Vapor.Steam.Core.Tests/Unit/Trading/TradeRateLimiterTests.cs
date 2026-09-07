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
			AcquireTimeout = TimeSpan.FromSeconds(5)
		});

		var first = await limiter.AcquireAsync("acct");
		first!.Dispose();

		var second = await limiter.AcquireAsync("acct");
		Assert.NotNull(second);
		second!.Dispose();
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
			AcquireTimeout = TimeSpan.FromSeconds(1)
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
