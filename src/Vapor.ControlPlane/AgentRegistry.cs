using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed class AgentRegistry
{
	private readonly ConcurrentDictionary<string, ConnectedAgent> _agents = new(StringComparer.Ordinal);

	public IReadOnlyList<AgentHello> List() => _agents.Values.Select(a => a.Hello).OrderBy(a => a.Region, StringComparer.Ordinal).ThenBy(a => a.AgentId, StringComparer.Ordinal).ToList();

	public IReadOnlyList<ConnectedAgent> ListConnected() => _agents.Values.OrderBy(a => a.Hello.Region, StringComparer.Ordinal).ThenBy(a => a.Hello.AgentId, StringComparer.Ordinal).ToList();

	public IReadOnlyList<string> Regions() => _agents.Values.Select(a => a.Hello.Region).Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();

	/// <summary>Looks a connected agent up by id (host-targeted dispatch).</summary>
	public ConnectedAgent? Get(string agentId) => _agents.TryGetValue(agentId, out var agent) ? agent : null;

	public ConnectedAgent? Pick(string region) => Pick(region, action: null, load: null);

	public ConnectedAgent? Pick(string region, string action) => Pick(region, action, null);

	/// <summary>
	/// Placement matcher (roadmap §40.8-⑥): region match, capability filter, then
	/// least-loaded with the agent id as the deterministic tie-break — the same
	/// ordering the account reconciler applies. Load is the running-task count per
	/// agent attributed in the task store (see <c>CountRunningTasksByAgent</c>); a
	/// null load keeps the pure agent-id order. Health is implicit: only connected
	/// agents are in the registry at all.
	/// </summary>
	public ConnectedAgent? Pick(string region, string? action, IReadOnlyDictionary<string, int>? load)
	{
		IEnumerable<ConnectedAgent> candidates = _agents.Values
			.Where(a => string.Equals(a.Hello.Region, region, StringComparison.Ordinal));
		if (action != null)
		{
			candidates = candidates.Where(a => a.SupportsAction(action));
		}

		var ordered = candidates
			.OrderBy(a => load is not null && load.TryGetValue(a.Hello.AgentId, out int n) ? n : 0)
			.ThenBy(a => a.Hello.AgentId, StringComparer.Ordinal)
			.ToList();
		if (ordered.Count == 0)
		{
			return null;
		}

		return ordered[0];
	}

	public ConnectedAgent Register(AgentHello hello, WebSocket socket, CancellationToken cancellationToken)
	{
		var agent = new ConnectedAgent(hello, socket);
		_agents[hello.AgentId] = agent;
		agent.StartSendLoop(cancellationToken);

		return agent;
	}

	public void Unregister(string agentId) => _agents.TryRemove(agentId, out _);
}

public sealed class ConnectedAgent
{
	public AgentHello Hello { get; }
	public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

	private readonly WebSocket _socket;
	private readonly Channel<WSMessage> _send = Channel.CreateBounded<WSMessage>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

	public ConnectedAgent(AgentHello hello, WebSocket socket)
	{
		Hello = hello;
		_socket = socket;
	}

	public bool EnqueueTask(JobTask task, IReadOnlyDictionary<string, string>? traceHeaders = null)
	{
		return _send.Writer.TryWrite(new WSMessage(Type: "task", Hello: null, Task: task, TaskResult: null, TraceHeaders: traceHeaders));
	}

	public bool EnqueueTaskCancel(TaskCancel cancel)
	{
		return _send.Writer.TryWrite(new WSMessage(Type: "task_cancel", Hello: null, Task: null, TaskResult: null, TaskHeartbeat: null, TaskCancel: cancel));
	}

	public bool SupportsAction(string action)
	{
		if (Hello.Capabilities is not { Count: > 0 })
		{
			return true;
		}

		foreach (var kvp in Hello.Capabilities)
		{
			if (kvp.Value && string.Equals(kvp.Key, action, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	public void StartSendLoop(CancellationToken cancellationToken)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				while (await _send.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
				{
					while (_send.Reader.TryRead(out WSMessage? msg))
					{
						await WebSocketJson.Send(_socket, msg, cancellationToken).ConfigureAwait(false);
					}
				}
			}
			catch
			{
				// Socket loop stops; control plane will unregister on read loop exit.
			}
		}, cancellationToken);
	}
}

