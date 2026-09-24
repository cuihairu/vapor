using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed class TaskSchedulerService : BackgroundService
{
	private readonly AgentRegistry _agents;
	private readonly IJobStore _store;
	private readonly IEventBroker _events;
	private readonly Config _cfg;
	private DateTimeOffset _lastRequeueAt = DateTimeOffset.MinValue;

	private long _noCapableAgentFailures;
	private long _enqueueFailedFailures;
	private long _attemptsExhaustedFailures;

	/// <summary>Tasks that could not be dispatched because no agent in the region advertised the action.</summary>
	public long DispatchNoCapableAgent => Interlocked.Read(ref _noCapableAgentFailures);
	/// <summary>Tasks rejected because the selected agent's send queue was unavailable.</summary>
	public long DispatchEnqueueFailed => Interlocked.Read(ref _enqueueFailedFailures);
	/// <summary>Tasks failed permanently after exhausting the dispatch attempt limit.</summary>
	public long DispatchAttemptsExhausted => Interlocked.Read(ref _attemptsExhaustedFailures);

	// Heartbeat: when the dispatch loop last woke (UtcTicks; 0 = never ticked).
	// Written via Interlocked so /v1/system/status can read it lock-free.
	private long _lastTickTicks;
	/// <summary>When the dispatch loop last ran a tick, or null if it has not ticked yet.</summary>
	public DateTimeOffset? LastTickAt
	{
		get
		{
			long ticks = Interlocked.Read(ref _lastTickTicks);
			return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
		}
	}

	public TaskSchedulerService(AgentRegistry agents, IJobStore store, IEventBroker events, Config cfg)
	{
		_agents = agents;
		_store = store;
		_events = events;
		_cfg = cfg;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
		=> DispatchTimerLoopAsync(stoppingToken);

	/// <summary>
	/// The PeriodicTimer loop proper. Excluded from coverage: a PeriodicTimer that
	/// is disposed or cancelled while awaited always throws from
	/// WaitForNextTickAsync, so the loop can only exit through that throw — its
	/// closing brace is unreachable by construction (see tests/TESTING.md).
	/// </summary>
	[ExcludeFromCodeCoverage]
	private async Task DispatchTimerLoopAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));

		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
		{
			await DispatchOnce(stoppingToken).ConfigureAwait(false);
		}
	}

	internal async Task DispatchOnce(CancellationToken cancellationToken)
	{
		Interlocked.Exchange(ref _lastTickTicks, DateTimeOffset.UtcNow.UtcTicks); // heartbeat for /v1/system/status
		if (DateTimeOffset.UtcNow - _lastRequeueAt >= TimeSpan.FromSeconds(5))
		{
			_ = await _store.RequeueStaleRunningTasks(TimeSpan.FromSeconds(_cfg.TaskLeaseSeconds), cancellationToken).ConfigureAwait(false);
			_lastRequeueAt = DateTimeOffset.UtcNow;
		}

		foreach (string region in _agents.Regions())
		{
			const int maxPerTick = 25;

			for (int i = 0; i < maxPerTick; i++)
			{
				JobTask? task = await _store.ClaimNextQueuedTask(region, cancellationToken).ConfigureAwait(false);
				if (task == null)
				{
					break;
				}

				using Activity? dispatch = StartDispatchActivity(task, region);

				// Host-targeted tasks ("agent:{id}" — e.g. plugin lifecycle) must land on
				// the named machine: each agent has its own filesystem, so the region's
				// deterministic pick would be wrong whenever the region has >1 agent.
				ConnectedAgent? agent;
				if (HostTaskTarget.TryParseAgentId(task.Target, out string targetAgentId))
				{
					agent = _agents.Get(targetAgentId);
					if (agent is { } targeted && !targeted.SupportsAction(task.Action))
					{
						agent = null;
					}
				}
				else
				{
					agent = _agents.Pick(region, task.Action);
				}

				if (agent == null)
				{
					await HandleUndispatchableTaskAsync(task, "task.dispatch_failed", "no capable agent available", cancellationToken, dispatch: dispatch).ConfigureAwait(false);
					break;
				}

				dispatch?.SetTag("vapor.agent_id", agent.Hello.AgentId);

				if (!agent.EnqueueTask(task, VaporTracing.InjectTraceparent(dispatch)))
				{
					await HandleUndispatchableTaskAsync(task, "task.enqueue_failed", $"agent {agent.Hello.AgentId} send queue unavailable", cancellationToken, agent.Hello.AgentId, dispatch).ConfigureAwait(false);
					break;
				}

				_events.Publish(task.JobId, "task.dispatched", new Dictionary<string, object?> { ["taskId"] = task.Id, ["agentId"] = agent.Hello.AgentId });
			}
		}
	}

	private static Activity? StartDispatchActivity(JobTask task, string region)
	{
		Activity? dispatch = VaporTracing.Source.StartActivity("task.dispatch", ActivityKind.Producer);
		if (dispatch is null)
		{
			return null;
		}

		dispatch.SetTag("vapor.task_id", task.Id)
			.SetTag("vapor.job_id", task.JobId)
			.SetTag("vapor.action", task.Action)
			.SetTag("vapor.target", task.Target)
			.SetTag("vapor.region", region)
			.SetTag("vapor.attempt", task.Attempt);

		return dispatch;
	}

	/// <summary>
	/// Handles a claimed task that could not be handed to any agent: retry with a delay while
	/// attempts remain, otherwise fail the task permanently so it cannot block the queue forever.
	/// </summary>
	private async Task HandleUndispatchableTaskAsync(JobTask task, string failureEvent, string error, CancellationToken cancellationToken, string? agentId = null, Activity? dispatch = null)
	{
		if (_cfg.HasDispatchAttemptLimit && task.Attempt >= _cfg.TaskMaxDispatchAttempts)
		{
			Interlocked.Increment(ref _attemptsExhaustedFailures);
			(JobTask failedTask, Job job) = await _store.FailRunningTask(task.Id, $"dispatch failed after {task.Attempt} attempts: {error}", cancellationToken).ConfigureAwait(false);
			_events.Publish(failedTask.JobId, "task.failed", new Dictionary<string, object?> { ["taskId"] = failedTask.Id, ["error"] = failedTask.Error, ["job"] = job.Status.ToString() });

			dispatch?.SetStatus(ActivityStatusCode.Error, failedTask.Error);
			return;
		}

		if (string.Equals(failureEvent, "task.dispatch_failed", StringComparison.Ordinal))
		{
			Interlocked.Increment(ref _noCapableAgentFailures);
		}
		else if (string.Equals(failureEvent, "task.enqueue_failed", StringComparison.Ordinal))
		{
			Interlocked.Increment(ref _enqueueFailedFailures);
		}

		TimeSpan? retryDelay = _cfg.TaskDispatchRetryDelayMs > 0 ? TimeSpan.FromMilliseconds(_cfg.TaskDispatchRetryDelayMs) : null;
		await _store.RequeueTask(task.Id, retryDelay, cancellationToken).ConfigureAwait(false);

		var payload = new Dictionary<string, object?> { ["taskId"] = task.Id, ["attempt"] = task.Attempt, ["error"] = error };
		if (agentId != null)
		{
			payload["agentId"] = agentId;
		}

		_events.Publish(task.JobId, failureEvent, payload);

		dispatch?.SetStatus(ActivityStatusCode.Error, error);
	}
}
