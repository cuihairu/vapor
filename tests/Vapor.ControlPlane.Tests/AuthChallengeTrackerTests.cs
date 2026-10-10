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

	[Fact]
	public void Upsert_MaterializesAttemptGenerationPerAccount()
	{
		var tracker = new AuthChallengeTracker();

		AuthChallengeEvent first = tracker.Upsert(NewEvent("alice", DateTimeOffset.UtcNow));
		AuthChallengeEvent second = tracker.Upsert(NewEvent("alice", DateTimeOffset.UtcNow.AddSeconds(5)));
		AuthChallengeEvent other = tracker.Upsert(NewEvent("bob", DateTimeOffset.UtcNow.AddSeconds(6)));
		AuthChallengeEvent third = tracker.Upsert(NewEvent("alice", DateTimeOffset.UtcNow.AddSeconds(10)));

		// The tracker is the attempt authority: an inbound attempt value never
		// leaks into the stored generation.
		AuthChallengeEvent forced = NewEvent("alice", DateTimeOffset.UtcNow.AddSeconds(15)) with { Attempt = 99 };
		AuthChallengeEvent fourth = tracker.Upsert(forced);

		Assert.Equal(1, first.Attempt);
		Assert.Equal(2, second.Attempt);
		Assert.Equal(1, other.Attempt);
		Assert.Equal(3, third.Attempt);
		Assert.Equal(4, fourth.Attempt);
		Assert.Equal(4, tracker.Get("alice")!.Attempt);
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
