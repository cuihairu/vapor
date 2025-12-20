using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public sealed class AgentRegistry {
	private readonly ConcurrentDictionary<string, ConnectedAgent> _agents = new(StringComparer.Ordinal);

	public IReadOnlyList<AgentHello> List() => _agents.Values.Select(a => a.Hello).OrderBy(a => a.Region, StringComparer.Ordinal).ThenBy(a => a.AgentId, StringComparer.Ordinal).ToList();

	public IReadOnlyList<string> Regions() => _agents.Values.Select(a => a.Hello.Region).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();

	public ConnectedAgent? Pick(string region) {
		var candidates = _agents.Values.Where(a => string.Equals(a.Hello.Region, region, StringComparison.Ordinal)).OrderBy(a => a.Hello.AgentId, StringComparer.Ordinal).ToList();
		if (candidates.Count == 0) {
			return null;
		}

		// Simple deterministic pick; later replace with RR + health/capacity.
		return candidates[0];
	}

	public ConnectedAgent Register(AgentHello hello, WebSocket socket, CancellationToken cancellationToken) {
		var agent = new ConnectedAgent(hello, socket);
		_agents[hello.AgentId] = agent;
		agent.StartSendLoop(cancellationToken);

		return agent;
	}

	public void Unregister(string agentId) => _agents.TryRemove(agentId, out _);
}

public sealed class ConnectedAgent {
	public AgentHello Hello { get; }
	public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

	private readonly WebSocket _socket;
	private readonly Channel<WSMessage> _send = Channel.CreateBounded<WSMessage>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

	public ConnectedAgent(AgentHello hello, WebSocket socket) {
		Hello = hello;
		_socket = socket;
	}

	public bool EnqueueTask(JobTask task) {
		return _send.Writer.TryWrite(new WSMessage(Type: "task", Hello: null, Task: task, TaskResult: null));
	}

	public void StartSendLoop(CancellationToken cancellationToken) {
		_ = Task.Run(async () => {
			try {
				while (await _send.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) {
					while (_send.Reader.TryRead(out WSMessage? msg)) {
						await WebSocketJson.Send(_socket, msg, cancellationToken).ConfigureAwait(false);
					}
				}
			} catch {
				// Socket loop stops; control plane will unregister on read loop exit.
			}
		}, cancellationToken);
	}
}
