using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class TaskSchedulerServiceTests {
	[Fact]
	public async Task DispatchOnce_DispatchesQueuedTaskToCapableAgent() {
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(
			new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["login"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "login"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "local", "local" }, store.ClaimRegions);
		Assert.Empty(store.RequeuedTaskIds);
		Assert.Single(events.Events);
		Assert.Equal("task.dispatched", events.Events[0].Type);
		Assert.Equal("job-1", events.Events[0].JobId);
		Assert.Equal("task-1", events.Events[0].Payload!["taskId"]?.ToString());
		Assert.Equal("agent-1", events.Events[0].Payload!["agentId"]?.ToString());
	}

	[Fact]
	public async Task DispatchOnce_RequeuesTaskAndPublishesDispatchFailureWhenNoCapableAgentExists() {
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		registry.Register(
			new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "login"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "task-1" }, store.RequeuedTaskIds);
		Assert.Single(events.Events);
		Assert.Equal("task.dispatch_failed", events.Events[0].Type);
		Assert.Equal("no capable agent available", events.Events[0].Payload!["error"]?.ToString());
	}

	[Fact]
	public async Task DispatchOnce_RequeuesTaskAndPublishesEnqueueFailureWhenAgentQueueRejectsTask() {
		var registry = new AgentRegistry();
		AddAgent(registry, CreateFullAgent("agent-1", "local", "login"));

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "login"));
		var events = new RecordingEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, events, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Equal(new[] { "task-1" }, store.RequeuedTaskIds);
		Assert.Single(events.Events);
		Assert.Equal("task.enqueue_failed", events.Events[0].Type);
		Assert.Equal("agent-1", events.Events[0].Payload!["agentId"]?.ToString());
	}

	[Fact]
	public async Task DispatchOnce_RequeuesStaleTasksOnlyOnceWithinFiveSecondWindow() {
		var store = new FakeJobStore();
		var scheduler = new TaskSchedulerService(new AgentRegistry(), store, new RecordingEventBroker(), CreateConfig());
		SetLastRequeueAt(scheduler, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));

		await scheduler.DispatchOnce(CancellationToken.None);
		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.Single(store.StaleRequeueLeases);
		Assert.Equal(TimeSpan.FromSeconds(300), store.StaleRequeueLeases[0]);
	}

	private static Config CreateConfig() => new("", new HashSet<string>(StringComparer.Ordinal), "test.db", 300, false);

	private static JobTask CreateTask(string taskId, string jobId, string region, string action) {
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new JobTask(
			taskId,
			jobId,
			"target-1",
			action,
			region,
			null,
			JobTaskStatus.Queued,
			0,
			now,
			now);
	}

	private static void AddAgent(AgentRegistry registry, ConnectedAgent agent) {
		FieldInfo field = typeof(AgentRegistry).GetField("_agents", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing agent registry field.");
		var agents = (System.Collections.Concurrent.ConcurrentDictionary<string, ConnectedAgent>)field.GetValue(registry)!;
		agents[agent.Hello.AgentId] = agent;
	}

	private static void SetLastRequeueAt(TaskSchedulerService scheduler, DateTimeOffset value) {
		FieldInfo field = typeof(TaskSchedulerService).GetField("_lastRequeueAt", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing scheduler field.");
		field.SetValue(scheduler, value);
	}

	private static ConnectedAgent CreateFullAgent(string agentId, string region, string supportedAction) {
		var agent = new ConnectedAgent(
			new AgentHello(agentId, region, new Dictionary<string, bool> { [supportedAction] = true }, null),
			new NoopWebSocket());

		FieldInfo field = typeof(ConnectedAgent).GetField("_send", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing send channel field.");
		var channel = (System.Threading.Channels.Channel<WSMessage>)field.GetValue(agent)!;
		channel.Writer.TryComplete();

		return agent;
	}

	private sealed class FakeJobStore : IJobStore {
		public Queue<JobTask> QueuedTasks { get; init; } = new();
		public List<string> ClaimRegions { get; } = [];
		public List<string> RequeuedTaskIds { get; } = [];
		public List<TimeSpan> StaleRequeueLeases { get; } = [];

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListJobs(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) {
			ClaimRegions.Add(region);
			if (QueuedTasks.Count == 0) {
				return Task.FromResult<JobTask?>(null);
			}

			return Task.FromResult<JobTask?>(QueuedTasks.Dequeue());
		}

		public Task RequeueTask(string taskId, CancellationToken cancellationToken) {
			RequeuedTaskIds.Add(taskId);
			return Task.CompletedTask;
		}

		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) {
			StaleRequeueLeases.Add(taskLease);
			return Task.FromResult(0);
		}
	}

	private sealed class RecordingEventBroker : IEventBroker {
		public List<Event> Events { get; } = [];

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload) {
			Events.Add(new Event(Guid.NewGuid().ToString("N"), jobId, type, DateTimeOffset.UtcNow, payload));
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null) => throw new NotSupportedException();
		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null) => throw new NotSupportedException();
		public IAsyncEnumerable<Event> Subscribe(CancellationToken cancellationToken, string jobId) => throw new NotSupportedException();
		public IAsyncEnumerable<SessionEvent> SubscribeSessions(CancellationToken cancellationToken, string? accountName = null) => throw new NotSupportedException();
		public IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges(CancellationToken cancellationToken, string? accountName = null) => throw new NotSupportedException();
	}

	private class NoopWebSocket : WebSocket {
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort() {
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
			return Task.CompletedTask;
		}

		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) {
			return Task.CompletedTask;
		}

		public override void Dispose() {
		}

		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.AsSpan());
			return Task.FromResult(new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) {
			var payload = Encoding.UTF8.GetBytes("{}");
			payload.AsSpan().CopyTo(buffer.Span);
			return ValueTask.FromResult(new ValueWebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
		}

		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
			return Task.CompletedTask;
		}

		public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
			return ValueTask.CompletedTask;
		}
	}

}
