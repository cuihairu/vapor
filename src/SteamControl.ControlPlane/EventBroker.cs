using System.Collections.Concurrent;
using System.Threading.Channels;
using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public sealed class EventBroker : IEventBroker {
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<Event>, byte>> _subscribers = new(StringComparer.Ordinal);

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
}

