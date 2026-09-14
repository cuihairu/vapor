using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class AuthChallengeTrackerTests
{
	[Fact]
	public void List_OrdersByTimestampDescending()
	{
		// List's OrderByDescending only executes against a non-empty tracker.
		var tracker = new AuthChallengeTracker();
		DateTimeOffset baseTime = DateTimeOffset.UtcNow;
		tracker.Upsert(NewEvent("alice", baseTime));
		tracker.Upsert(NewEvent("bob", baseTime.AddSeconds(10)));

		IReadOnlyList<AuthChallengeEvent> listed = tracker.List();

		Assert.Equal(new[] { "bob", "alice" }, listed.Select(e => e.AccountName).ToArray());
	}

	[Fact]
	public void Get_BlankAccountName_ReturnsNull()
	{
		var tracker = new AuthChallengeTracker();
		tracker.Upsert(NewEvent("alice", DateTimeOffset.UtcNow));

		Assert.Null(tracker.Get(""));
		Assert.Null(tracker.Get("   "));
	}

	[Fact]
	public void Upsert_ReplacesPendingChallengeForSameAccount()
	{
		var tracker = new AuthChallengeTracker();
		tracker.Upsert(NewEvent("alice", DateTimeOffset.UtcNow, challengeType: "email"));
		tracker.Upsert(NewEvent("alice", DateTimeOffset.UtcNow, challengeType: "totp"));

		Assert.Equal("totp", tracker.Get("alice")!.ChallengeType);
	}

	private static AuthChallengeEvent NewEvent(string accountName, DateTimeOffset timestamp, string challengeType = "email") =>
		new(
			Id: Guid.NewGuid().ToString("N"),
			AccountName: accountName,
			ChallengeType: challengeType,
			Message: null,
			Code: null,
			Timestamp: timestamp,
			JobId: null);
}
