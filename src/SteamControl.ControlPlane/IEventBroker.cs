using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public interface IEventBroker {
	void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload);
	void PublishSession(string accountName, string eventType, string state, string? message = null);
	void PublishAuthChallenge(string accountName, string challengeType, string? message = null);
	IAsyncEnumerable<Event> Subscribe(CancellationToken cancellationToken, string jobId);
	IAsyncEnumerable<SessionEvent> SubscribeSessions(CancellationToken cancellationToken, string? accountName = null);
	IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges(CancellationToken cancellationToken, string? accountName = null);
}

public sealed record SessionEvent(
	string Id,
	string AccountName,
	string EventType,
	string State,
	string? Message,
	DateTimeOffset Timestamp
);

public sealed record AuthChallengeEvent(
	string Id,
	string AccountName,
	string ChallengeType,
	string? Message,
	DateTimeOffset Timestamp,
	string? JobId
);

