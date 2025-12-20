using Microsoft.Extensions.Hosting;
using SteamControl.Protocol;

namespace SteamControl.ControlPlane;

public sealed class TaskSchedulerService : BackgroundService {
	private readonly AgentRegistry _agents;
	private readonly IJobStore _store;
	private readonly IEventBroker _events;
	private readonly Config _cfg;

	public TaskSchedulerService(AgentRegistry agents, IJobStore store, IEventBroker events, Config cfg) {
		_agents = agents;
		_store = store;
		_events = events;
		_cfg = cfg;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
		using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));

		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
			await DispatchOnce(stoppingToken).ConfigureAwait(false);
		}
	}

	private async Task DispatchOnce(CancellationToken cancellationToken) {
		_ = await _store.RequeueStaleRunningTasks(TimeSpan.FromSeconds(_cfg.TaskLeaseSeconds), cancellationToken).ConfigureAwait(false);

		foreach (string region in _agents.Regions()) {
			const int maxPerTick = 25;

			for (int i = 0; i < maxPerTick; i++) {
				JobTask? task = await _store.ClaimNextQueuedTask(region, cancellationToken).ConfigureAwait(false);
				if (task == null) {
					break;
				}

				var agent = _agents.Pick(region);
				if (agent == null) {
					await _store.RequeueTask(task.Id, cancellationToken).ConfigureAwait(false);
					_events.Publish(task.JobId, "task.dispatch_failed", new Dictionary<string, object?> { ["taskId"] = task.Id, ["error"] = "no agent available" });
					break;
				}

				if (!agent.EnqueueTask(task)) {
					await _store.RequeueTask(task.Id, cancellationToken).ConfigureAwait(false);
					_events.Publish(task.JobId, "task.enqueue_failed", new Dictionary<string, object?> { ["taskId"] = task.Id, ["agentId"] = agent.Hello.AgentId });
					break;
				}

				_events.Publish(task.JobId, "task.dispatched", new Dictionary<string, object?> { ["taskId"] = task.Id, ["agentId"] = agent.Hello.AgentId });
			}
		}
	}
}
