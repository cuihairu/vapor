using System.Text.Json;
using Vapor.Steam.Core.Caching;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Caching;

/// <summary>
/// Pure-logic tests for the Redis envelope: no Redis server required.
/// End-to-end behavior against a live server is covered by the
/// VAPOR_TEST_REDIS-gated integration tests.
/// </summary>
public sealed class RedisCacheEntryTests
{
	[Fact]
	public void Encode_Decode_RoundTripsPayloadAndExpiries()
	{
		DateTimeOffset fresh = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);
		DateTimeOffset stale = fresh + TimeSpan.FromMinutes(30);

		string json = RedisCacheEntry.Encode(new FakePayload { Value = "cs2" }, fresh, stale);

		Assert.True(RedisCacheEntry.TryDecode(json, out string? payload, out long? freshMs, out long? staleMs));
		Assert.Equal(fresh.ToUnixTimeMilliseconds(), freshMs);
		Assert.Equal(stale.ToUnixTimeMilliseconds(), staleMs);

		var value = JsonSerializer.Deserialize<FakePayload>(payload!);
		Assert.Equal("cs2", value!.Value);
	}

	[Fact]
	public void Encode_WithoutExpiries_DecodesAsNulls()
	{
		string json = RedisCacheEntry.Encode(new FakePayload { Value = "x" }, freshExpiresAt: null, staleExpiresAt: null);

		Assert.True(RedisCacheEntry.TryDecode(json, out _, out long? freshMs, out long? staleMs));
		Assert.Null(freshMs);
		Assert.Null(staleMs);

		// A null fresh expiry never lapses.
		Assert.True(RedisCacheEntry.IsFresh(freshMs, DateTimeOffset.UtcNow + TimeSpan.FromDays(365)));
		Assert.False(RedisCacheEntry.IsStaleServable(freshMs, staleMs, DateTimeOffset.UtcNow));
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json at all")]
	[InlineData("123")]
	[InlineData("\"a string\"")]
	[InlineData("{\"unexpected\":true}")]
	[InlineData("{\"v\":123}")]
	public void TryDecode_RejectsMalformedPayloads(string json)
	{
		Assert.False(RedisCacheEntry.TryDecode(json, out _, out _, out _));
	}

	[Fact]
	public void IsFresh_TrueWithinFreshWindow()
	{
		long freshMs = Epoch(10);
		Assert.True(RedisCacheEntry.IsFresh(freshMs, At(9)));
		Assert.True(RedisCacheEntry.IsFresh(freshMs, At(10) - TimeSpan.FromMilliseconds(1)));
	}

	[Fact]
	public void IsFresh_FalseOnceFreshWindowLapses()
	{
		long freshMs = Epoch(10);
		Assert.False(RedisCacheEntry.IsFresh(freshMs, At(10)));
		Assert.False(RedisCacheEntry.IsFresh(freshMs, At(11)));
	}

	[Fact]
	public void IsStaleServable_TrueOnlyBetweenFreshAndStaleWindows()
	{
		long freshMs = Epoch(10);
		long staleMs = Epoch(15);

		Assert.False(RedisCacheEntry.IsStaleServable(freshMs, staleMs, At(9)));   // still fresh
		Assert.True(RedisCacheEntry.IsStaleServable(freshMs, staleMs, At(10)));   // stale window open
		Assert.True(RedisCacheEntry.IsStaleServable(freshMs, staleMs, At(15) - TimeSpan.FromMilliseconds(1)));
		Assert.False(RedisCacheEntry.IsStaleServable(freshMs, staleMs, At(15)));  // window closed
		Assert.False(RedisCacheEntry.IsStaleServable(freshMs, null, At(12)));     // entry without stale window
	}

	private static long Epoch(int seconds) => new DateTimeOffset(2026, 1, 1, 0, 0, seconds, TimeSpan.Zero).ToUnixTimeMilliseconds();

	private static DateTimeOffset At(int seconds) => new(2026, 1, 1, 0, 0, seconds, TimeSpan.Zero);

	private sealed class FakePayload
	{
		public string Value { get; init; } = string.Empty;
	}
}
