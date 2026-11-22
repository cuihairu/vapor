using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class HostTargetedDispatchTests
{
	[Fact]
	public async Task DispatchOnce_RoutesHostTaskToNamedAgentNotRegionPick()
	{
		// Two agents in the region; the deterministic pick would always choose
		// agent-1. The host-targeted task must land on agent-2.
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(new AgentHello("agent-9", "local", new Dictionary<string, bool> { ["plugin_install"] = true }, null), new NoopWebSocket(), cts.Token);
		registry.Register(new AgentHello("agent-2", "local", new Dictionary<string, bool> { ["plugin_install"] = true }, null), new NoopWebSocket(), cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "plugin_install", target: "agent:agent-2"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig(), new FaultInjector());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Empty(store.RequeuedTaskIds);
		var published = Assert.Single(events.Events);
		Assert.Equal("task.dispatched", published.Type);
		Assert.Equal("agent-2", published.Payload!["agentId"]?.ToString());
	}

	[Fact]
	public async Task DispatchOnce_RequeuesHostTaskWhenNamedAgentIsOffline()
	{
		// Only agent-1 is connected; a task targeted at agent-2 must not run there.
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(new AgentHello("agent-9", "local", new Dictionary<string, bool> { ["plugin_install"] = true }, null), new NoopWebSocket(), cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "plugin_install", target: "agent:agent-2"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig(), new FaultInjector());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "task-1" }, store.RequeuedTaskIds);
		var published = Assert.Single(events.Events);
		Assert.Equal("task.dispatch_failed", published.Type);
	}

	[Fact]
	public async Task DispatchOnce_FailsHostTaskWhenNamedAgentLacksCapability()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(new AgentHello("agent-9", "local", new Dictionary<string, bool> { ["login"] = true }, null), new NoopWebSocket(), cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "plugin_install", target: "agent:agent-1"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig(), new FaultInjector());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "task-1" }, store.RequeuedTaskIds);
	}

	private static Config CreateConfig() => new("", new Dictionary<string, DateTimeOffset?>(), "test.db", 300, false);

	private static JobTask CreateTask(string taskId, string jobId, string region, string action, string target)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new JobTask(taskId, jobId, target, action, region, null, JobTaskStatus.Queued, 0, now, now);
	}

	private sealed class NoopWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose()
		{
		}
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			Task.FromCanceled<WebSocketReceiveResult>(cancellationToken);
		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class FakeJobStore : IJobStore
	{
		public Queue<JobTask> QueuedTasks { get; init; } = new();
		public List<string> RequeuedTaskIds { get; } = [];

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<Vapor.Protocol.JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken)
		{
			if (QueuedTasks.Count == 0)
			{
				return Task.FromResult<JobTask?>(null);
			}

			return Task.FromResult<JobTask?>(QueuedTasks.Dequeue());
		}

		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => Task.FromResult<Job?>(null);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);

		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken)
		{
			RequeuedTaskIds.Add(taskId);
			return Task.CompletedTask;
		}

		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(int Jobs, int Tasks)> PurgeSecretPayloadsAsync(bool dryRun, string? backupPath, CancellationToken cancellationToken) => Task.FromResult((0, 0));
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task MarkTaskClaimedBy(string taskId, int attempt, string agentId, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<Dictionary<string, int>> CountRunningTasksByAgent(CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, int>());
	}

	private sealed class RecordingEventBroker : IEventBroker
	{
		public List<Event> Events { get; } = [];

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
			Events.Add(new Event(Guid.NewGuid().ToString("N"), jobId, type, DateTimeOffset.UtcNow, payload));
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null, int attempt = 1)
		{
		}

		public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
		{
			await Task.CompletedTask;
			yield break;
		}

		public async IAsyncEnumerable<SessionEvent> SubscribeSessions([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			await Task.CompletedTask;
			yield break;
		}

		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			await Task.CompletedTask;
			yield break;
		}
	}
}
