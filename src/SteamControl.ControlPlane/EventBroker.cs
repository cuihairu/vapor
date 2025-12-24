using System.Collections.Concurrent;
using System.Threading.Channels;
using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public sealed class EventBroker : IEventBroker {
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<Event>, byte>> _subscribers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<SessionEvent>, byte>> _sessionSubscribers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<AuthChallengeEvent>, byte>> _authSubscribers = new(StringComparer.Ordinal);
	private readonly Channel<SessionEvent> _globalSessionChannel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = false });
	private readonly Channel<AuthChallengeEvent> _globalAuthChannel = Channel.CreateUnbounded<AuthChallengeEvent>(new UnboundedChannelOptions { SingleReader = false });

	public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload) {
		if (string.IsNullOrEmpty(jobId)) {
			return;
		}

		if (!_subscribers.TryGetValue(jobId, out var subs) || subs.IsEmpty) {
			return;
		}

		Event e = new(
			Id: Guid.NewGuid().ToString("N"),
			JobId: jobId,
			Type: type,
			Ts: DateTimeOffset.UtcNow,
			Payload: payload
		);

		foreach (var entry in subs.Keys) {
			_ = entry.Writer.TryWrite(e);
		}
	}

	public void PublishSession(string accountName, string eventType, string state, string? message = null) {
		var sessionEvent = new SessionEvent(
			Id: Guid.NewGuid().ToString("N"),
			AccountName: accountName,
			EventType: eventType,
			State: state,
			Message: message,
			Timestamp: DateTimeOffset.UtcNow
		);

		// Publish to global channel
		_ = _globalSessionChannel.Writer.TryWrite(sessionEvent);

		// Publish to account-specific subscribers
		if (_sessionSubscribers.TryGetValue(accountName, out var subs)) {
			foreach (var entry in subs.Keys) {
				_ = entry.Writer.TryWrite(sessionEvent);
			}
		}

		// Publish to "all" subscribers
		if (_sessionSubscribers.TryGetValue("*", out var allSubs)) {
			foreach (var entry in allSubs.Keys) {
				_ = entry.Writer.TryWrite(sessionEvent);
			}
		}
	}

	public void PublishAuthChallenge(string accountName, string challengeType, string? message = null) {
		var authEvent = new AuthChallengeEvent(
			Id: Guid.NewGuid().ToString("N"),
			AccountName: accountName,
			ChallengeType: challengeType,
			Message: message,
			Timestamp: DateTimeOffset.UtcNow,
			JobId: null
		);

		// Publish to global channel
		_ = _globalAuthChannel.Writer.TryWrite(authEvent);

		// Publish to account-specific subscribers
		if (_authSubscribers.TryGetValue(accountName, out var subs)) {
			foreach (var entry in subs.Keys) {
				_ = entry.Writer.TryWrite(authEvent);
			}
		}

		// Publish to "all" subscribers
		if (_authSubscribers.TryGetValue("*", out var allSubs)) {
			foreach (var entry in allSubs.Keys) {
				_ = entry.Writer.TryWrite(authEvent);
			}
		}
	}

	public async IAsyncEnumerable<Event> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, string jobId) {
		Channel<Event> channel = Channel.CreateBounded<Event>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
		var subs = _subscribers.GetOrAdd(jobId, _ => new ConcurrentDictionary<Channel<Event>, byte>());
		subs.TryAdd(channel, 0);

		try {
			while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) {
				while (channel.Reader.TryRead(out Event? e)) {
					yield return e;
				}
			}
		} finally {
			subs.TryRemove(channel, out _);
			channel.Writer.TryComplete();

			if (subs.IsEmpty) {
				_subscribers.TryRemove(jobId, out _);
			}
		}
	}

	public async IAsyncEnumerable<SessionEvent> SubscribeSessions([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null) {
		var key = string.IsNullOrEmpty(accountName) ? "*" : accountName;
		Channel<SessionEvent> channel = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
		var subs = _sessionSubscribers.GetOrAdd(key, _ => new ConcurrentDictionary<Channel<SessionEvent>, byte>());
		subs.TryAdd(channel, 0);

		try {
			while (!cancellationToken.IsCancellationRequested) {
				bool hasData = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);

				while (channel.Reader.TryRead(out SessionEvent? e)) {
					yield return e;
				}

				if (!hasData) {
					break;
				}
			}
		} finally {
			subs.TryRemove(channel, out _);
			channel.Writer.TryComplete();

			if (subs.IsEmpty) {
				_sessionSubscribers.TryRemove(key, out _);
			}
		}
	}

	public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null) {
		var key = string.IsNullOrEmpty(accountName) ? "*" : accountName;
		Channel<AuthChallengeEvent> channel = Channel.CreateBounded<AuthChallengeEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
		var subs = _authSubscribers.GetOrAdd(key, _ => new ConcurrentDictionary<Channel<AuthChallengeEvent>, byte>());
		subs.TryAdd(channel, 0);

		try {
			while (!cancellationToken.IsCancellationRequested) {
				bool hasData = await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);

				while (channel.Reader.TryRead(out AuthChallengeEvent? e)) {
					yield return e;
				}

				if (!hasData) {
					break;
				}
			}
		} finally {
			subs.TryRemove(channel, out _);
			channel.Writer.TryComplete();

			if (subs.IsEmpty) {
				_authSubscribers.TryRemove(key, out _);
			}
		}
	}
}

