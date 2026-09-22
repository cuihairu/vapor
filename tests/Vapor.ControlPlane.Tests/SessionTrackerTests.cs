using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SessionTrackerTests
{
	[Fact]
	public void Get_BlankAccountName_ReturnsNull()
	{
		var tracker = new SessionTracker();
		tracker.Update("alice", "state_changed", "Connected", null);

		Assert.Null(tracker.Get(""));
		Assert.Null(tracker.Get("   "));
	}

	[Fact]
	public void Get_TrimsAccountNameAndMatchesCaseInsensitively()
	{
		var tracker = new SessionTracker();
		tracker.Update("Alice", "state_changed", "Connected", null);

		Assert.Equal("Connected", tracker.Get("  alice ")!.State);
	}

	[Fact]
	public void Update_ExplicitUpdatedAt_IsPreservedVerbatim()
	{
		var tracker = new SessionTracker();

		tracker.Update("alice", "state_changed", "Connected", "hello", DateTimeOffset.UnixEpoch);

		Assert.Equal(DateTimeOffset.UnixEpoch, tracker.Get("alice")!.UpdatedAt);
	}
}
