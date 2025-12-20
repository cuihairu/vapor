using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public interface IEventBroker {
	void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload);
	IAsyncEnumerable<Event> Subscribe(CancellationToken cancellationToken, string jobId);
}

