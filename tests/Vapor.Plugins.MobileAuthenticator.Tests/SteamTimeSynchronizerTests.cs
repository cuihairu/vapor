using Xunit;
using Vapor.Plugins.MobileAuthenticator;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class SteamTimeSynchronizerTests
{
	[Fact]
	public void GetCurrentSteamTime_UsesLocalTimeBeforeSync()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(0L), timeProvider);

		Assert.False(synchronizer.HasSynced);
		Assert.Equal(0L, synchronizer.OffsetSeconds);
		Assert.Equal(timeProvider.GetUtcNow().ToUnixTimeSeconds(), synchronizer.GetCurrentSteamTime());
	}

	[Fact]
	public async Task SyncAsync_ComputesOffset()
	{
		var localNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(localNow);
		var serverTime = localNow.ToUnixTimeSeconds() + 42;

		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(serverTime), timeProvider);
		await synchronizer.SyncAsync();

		Assert.True(synchronizer.HasSynced);
		Assert.Equal(42L, synchronizer.OffsetSeconds);
		Assert.Equal(serverTime, synchronizer.GetCurrentSteamTime());
		Assert.NotNull(synchronizer.LastSyncedAt);
	}

	[Fact]
	public async Task SyncAsync_NegativeOffset()
	{
		var localNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(localNow);
		var serverTime = localNow.ToUnixTimeSeconds() - 7;

		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(serverTime), timeProvider);
		await synchronizer.SyncAsync();

		Assert.Equal(-7L, synchronizer.OffsetSeconds);
	}

	[Fact]
	public async Task SyncAsync_Failure_KeepsPreviousOffset()
	{
		var localNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(localNow);
		var serverTime = localNow.ToUnixTimeSeconds() + 10;
		var fail = false;

		var synchronizer = new SteamTimeSynchronizer(_ => fail
			? Task.FromException<long>(new HttpRequestException("network down"))
			: Task.FromResult(serverTime), timeProvider);

		await synchronizer.SyncAsync();
		Assert.Equal(10L, synchronizer.OffsetSeconds);

		fail = true;
		await Assert.ThrowsAsync<HttpRequestException>(() => synchronizer.SyncAsync());

		// Previous offset is preserved.
		Assert.Equal(10L, synchronizer.OffsetSeconds);
	}

	[Fact]
	public async Task SyncAsync_UsesMidpointOfRoundTrip()
	{
		var start = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(start);
		var serverTime = start.ToUnixTimeSeconds() + 100;

		var synchronizer = new SteamTimeSynchronizer(_ =>
		{
			// Simulate a 4-second round trip.
			timeProvider.Advance(TimeSpan.FromSeconds(4));
			return Task.FromResult(serverTime);
		}, timeProvider);

		await synchronizer.SyncAsync();

		// Midpoint = start + 2s => offset = 100 - 2.
		Assert.Equal(98L, synchronizer.OffsetSeconds);
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private DateTimeOffset _now;

		public FakeTimeProvider(DateTimeOffset now) => _now = now;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan delta) => _now = _now.Add(delta);
	}
}
