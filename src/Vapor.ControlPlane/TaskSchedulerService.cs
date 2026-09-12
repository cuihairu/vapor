using Microsoft.Extensions.Hosting;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed class TaskSchedulerService : BackgroundService {
	private readonly AgentRegistry _agents;
	private readonly IJobStore _store;
	private readonly IEventBroker _events;
	private readonly Config _cfg;
	private DateTimeOffset _lastRequeueAt = DateTimeOffset.MinValue;

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

	internal async Task DispatchOnce(CancellationToken cancellationToken) {
		if (DateTimeOffset.UtcNow - _lastRequeueAt >= TimeSpan.FromSeconds(5)) {
			_ = await _store.RequeueStaleRunningTasks(TimeSpan.FromSeconds(_cfg.TaskLeaseSeconds), cancellationToken).ConfigureAwait(false);
			_lastRequeueAt = DateTimeOffset.UtcNow;
		}

		foreach (string region in _agents.Regions()) {
			const int maxPerTick = 25;

			for (int i = 0; i < maxPerTick; i++) {
				JobTask? task = await _store.ClaimNextQueuedTask(region, cancellationToken).ConfigureAwait(false);
				if (task == null) {
					break;
				}

				var agent = _agents.Pick(region, task.Action);
				if (agent == null) {
					await HandleUndispatchableTaskAsync(task, "task.dispatch_failed", "no capable agent available", cancellationToken).ConfigureAwait(false);
					break;
				}

				if (!agent.EnqueueTask(task)) {
					await HandleUndispatchableTaskAsync(task, "task.enqueue_failed", $"agent {agent.Hello.AgentId} send queue unavailable", cancellationToken, agent.Hello.AgentId).ConfigureAwait(false);
					break;
				}

				_events.Publish(task.JobId, "task.dispatched", new Dictionary<string, object?> { ["taskId"] = task.Id, ["agentId"] = agent.Hello.AgentId });
			}
		}
	}

	/// <summary>
	/// Handles a claimed task that could not be handed to any agent: retry with a delay while
	/// attempts remain, otherwise fail the task permanently so it cannot block the queue forever.
	/// </summary>
	private async Task HandleUndispatchableTaskAsync(JobTask task, string failureEvent, string error, CancellationToken cancellationToken, string? agentId = null) {
		if (_cfg.HasDispatchAttemptLimit && task.Attempt >= _cfg.TaskMaxDispatchAttempts) {
			(JobTask failedTask, Job job) = await _store.FailRunningTask(task.Id, $"dispatch failed after {task.Attempt} attempts: {error}", cancellationToken).ConfigureAwait(false);
			_events.Publish(failedTask.JobId, "task.failed", new Dictionary<string, object?> { ["taskId"] = failedTask.Id, ["error"] = failedTask.Error, ["job"] = job.Status.ToString() });
			return;
		}

		TimeSpan? retryDelay = _cfg.TaskDispatchRetryDelayMs > 0 ? TimeSpan.FromMilliseconds(_cfg.TaskDispatchRetryDelayMs) : null;
		await _store.RequeueTask(task.Id, retryDelay, cancellationToken).ConfigureAwait(false);

		var payload = new Dictionary<string, object?> { ["taskId"] = task.Id, ["attempt"] = task.Attempt, ["error"] = error };
		if (agentId != null) {
			payload["agentId"] = agentId;
		}

		_events.Publish(task.JobId, failureEvent, payload);
	}
}

