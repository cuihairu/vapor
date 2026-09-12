using System.Diagnostics;
using System.Reflection;
using Vapor.Agent;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class TracingTests
{
	[Fact]
	public async Task DispatchedTunnelMessage_CarriesTraceparentForAgentExecution()
	{
		using ActivityListener listener = new()
		{
			ShouldListenTo = source => source.Name is VaporTracing.SourceName or VaporAgentTracing.SourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
		};
		ActivitySource.AddActivityListener(listener);

		var registry = new AgentRegistry();
		// Insert the agent via reflection so no send loop drains the tunnel channel.
		ConnectedAgent agent = new(
			new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["login"] = true }, null),
			new NoopWebSocket());
		AddAgent(registry, agent);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "login"));
		var scheduler = new TaskSchedulerService(registry, store, new RecordingNoopEventBroker(), CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		WSMessage dispatched = ReadQueuedMessage(agent);
		Assert.NotNull(dispatched.TraceHeaders);

		// The header must be a well-formed W3C traceparent (version-traceid-spanid-flags).
		string traceparent = dispatched.TraceHeaders!["traceparent"];
		string[] parts = traceparent.Split('-');
		Assert.Equal(4, parts.Length);
		Assert.Equal(32, parts[1].Length);
		Assert.Equal(16, parts[2].Length);

		// The agent-side execution span must continue the same trace under the dispatch span.
		using Activity? execute = VaporAgentTracing.StartExecuteSpan(
			dispatched.Task!, dispatched.TraceHeaders);
		Assert.NotNull(execute);
		Assert.Equal(parts[1], execute!.TraceId.ToString());
		Assert.Equal(parts[2], execute.ParentSpanId.ToString());
		Assert.Equal("task-1", execute.GetTagItem("vapor.task_id")?.ToString());
	}

	[Fact]
	public async Task DispatchFailures_AreCountedByReasonForMetrics()
	{
		var registry = new AgentRegistry();
		using var cts = new CancellationTokenSource();
		// Only supports "ping": a "login" task finds no capable agent.
		ConnectedAgent agent = registry.Register(
			new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new NoopWebSocket(),
			cts.Token);

		var store = new FakeJobStore();
		store.QueuedTasks.Enqueue(CreateTask("task-1", "job-1", "local", "login", attempt: 10));
		store.QueuedTasks.Enqueue(CreateTask("task-2", "job-2", "local", "login"));
		var broker = new RecordingNoopEventBroker();
		var scheduler = new TaskSchedulerService(registry, store, broker, CreateConfig());

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.True(registry.Pick("local", "login") is null, "Pick should not find a capable agent for login");
		Assert.False(agent.SupportsAction("login"), "agent must not support login");

		// Task-1 exhausts its attempt limit and fails permanently.
		Assert.Equal(["task-1"], store.FailedTaskIds);
		Assert.Equal(["task.failed"], broker.Types);
		Assert.Equal(1, scheduler.DispatchAttemptsExhausted);

		// A second pass picks up task-2, which still retries without a capable agent.
		await scheduler.DispatchOnce(CancellationToken.None);
		Assert.Equal(["task.failed", "task.dispatch_failed"], broker.Types);
		Assert.Equal(1, scheduler.DispatchNoCapableAgent);
		Assert.Equal(0, scheduler.DispatchEnqueueFailed);
	}

	[Fact]
	public void TryExtractContext_RejectsMalformedTraceparent()
	{
		Assert.False(VaporTracing.TryExtractContext(new Dictionary<string, string> { ["traceparent"] = "not-a-traceparent" }, out _));
		Assert.False(VaporTracing.TryExtractContext(null, out _));
		Assert.False(VaporTracing.TryExtractContext(new Dictionary<string, string>(), out _));
	}

	[Fact]
	public void StartExecuteSpan_WithoutUsableTraceparent_StartsIndependentSpan()
	{
		using ActivityListener listener = new()
		{
			ShouldListenTo = source => source.Name == VaporAgentTracing.SourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
		};
		ActivitySource.AddActivityListener(listener);

		var task = CreateTask("task-1", "job-1", "local", "login");
		Activity? previous = Activity.Current;
		try
		{
			Activity.Current = null;

			using (Activity? malformed = VaporAgentTracing.StartExecuteSpan(
				task, new Dictionary<string, string> { ["traceparent"] = "garbage" }))
			{
				Assert.NotNull(malformed);
				Assert.Equal(default, malformed!.ParentSpanId);
			}

			using (Activity? missing = VaporAgentTracing.StartExecuteSpan(task, null))
			{
				Assert.NotNull(missing);
				Assert.Equal(default, missing!.ParentSpanId);
				Assert.Equal("task-1", missing.GetTagItem("vapor.task_id")?.ToString());
			}
		}
		finally
		{
			Activity.Current = previous;
		}
	}

	private static WSMessage ReadQueuedMessage(ConnectedAgent agent)
	{
		FieldInfo field = typeof(ConnectedAgent).GetField("_send", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Missing send channel field.");
		var channel = (System.Threading.Channels.Channel<WSMessage>)field.GetValue(agent)!;
		Assert.True(channel.Reader.TryRead(out WSMessage? message));
		return message;
	}

	private static void AddAgent(AgentRegistry registry, ConnectedAgent agent)
	{
		FieldInfo field = typeof(AgentRegistry).GetField("_agents", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Missing agent registry field.");
		var agents = (System.Collections.Concurrent.ConcurrentDictionary<string, ConnectedAgent>)field.GetValue(registry)!;
		agents[agent.Hello.AgentId] = agent;
	}

	private static Config CreateConfig() => new("", new HashSet<string>(StringComparer.Ordinal), "test.db", 300, false);

	private static JobTask CreateTask(string taskId, string jobId, string region, string action, int attempt = 0)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new JobTask(
			taskId,
			jobId,
			"target-1",
			action,
			region,
			null,
			JobTaskStatus.Queued,
			attempt,
			now,
			now);
	}

	private sealed class RecordingNoopEventBroker : IEventBroker
	{
		public List<string> Types { get; } = [];

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
			Types.Add(type);
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
		{
		}

		public IAsyncEnumerable<Event> Subscribe(CancellationToken cancellationToken, string jobId) => throw new NotSupportedException();
		public IAsyncEnumerable<SessionEvent> SubscribeSessions(CancellationToken cancellationToken, string? accountName = null) => throw new NotSupportedException();
		public IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges(CancellationToken cancellationToken, string? accountName = null) => throw new NotSupportedException();
	}

	private sealed class FakeJobStore : IJobStore
	{
		public Queue<JobTask> QueuedTasks { get; init; } = new();
		public List<string> FailedTaskIds { get; } = [];

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListJobs(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<Vapor.Protocol.JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) =>
			Task.FromResult(QueuedTasks.Count == 0 ? null : QueuedTasks.Dequeue());

		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);

		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken)
		{
			FailedTaskIds.Add(taskId);
			return Task.FromResult((CreateTask(taskId, "job-1", "local", "login") with
			{
				Status = JobTaskStatus.Failed,
				Error = error,
			}, new Job("job-1", "login", "local", [], null, JobStatus.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
		}
	}

	private sealed class NoopWebSocket : System.Net.WebSockets.WebSocket
	{
		public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose()
		{
		}

		public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public override ValueTask<System.Net.WebSockets.ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
		public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
