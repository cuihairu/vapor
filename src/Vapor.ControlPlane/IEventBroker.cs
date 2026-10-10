using Vapor.Protocol;

namespace Vapor.ControlPlane;

// Event and its wire models (Event, SessionEvent, AuthChallengeEvent, PluginEvent,
// JobEvent, TaskEvent) live solely in Vapor.Protocol/Events.cs so CP, Agent and
// plugins share one record definition each.
public interface IEventBroker
{
	void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload);
	void PublishSession(string accountName, string eventType, string state, string? message = null);
	void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null, int attempt = 1);
	IAsyncEnumerable<Event> Subscribe(CancellationToken cancellationToken, string jobId);
	IAsyncEnumerable<SessionEvent> SubscribeSessions(CancellationToken cancellationToken, string? accountName = null);
	IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges(CancellationToken cancellationToken, string? accountName = null);
}

