using System.Globalization;
using System.Text.Json;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// WS tunnel protocol replay tests. The recorded frames below are snapshots of
/// the exact JSON the ControlPlane and Agent exchange over the dispatch tunnel
/// (serialized with <see cref="JsonDefaults.Options"/>). Serialize-side changes
/// (field names, casing, enum or timestamp shape) break the snapshot equality;
/// deserialize-side regressions fail the field assertions — so deployed agents
/// and control planes keep speaking the same wire contract across upgrades.
/// </summary>
public sealed class WsProtocolReplayTests
{
	private const string HelloFrame =
		"""{"type":"hello","hello":{"agentId":"agent-1","region":"us-east","capabilities":{"ping":true,"play_games":true},"meta":{"version":"1.0.0"}}}""";

	private const string TaskFrame =
		"""{"type":"task","task":{"id":"task-1","jobId":"job-1","target":"alice","action":"ping","region":"us-east","payload":{"minutes":30},"status":"queued","attempt":1,"createdAt":"2026-09-12T05:40:06.123+00:00","updatedAt":"2026-09-12T05:40:06.123+00:00"},"traceHeaders":{"traceparent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"}}""";

	private const string HeartbeatFrame =
		"""{"type":"task_heartbeat","taskHeartbeat":{"taskId":"task-1","attempt":1,"ts":"2026-09-12T05:40:09.456+00:00"}}""";

	private const string ResultFrame =
		"""{"type":"task_result","taskResult":{"taskId":"task-1","success":true,"output":{"pong":true,"latency_ms":12},"finishedAt":"2026-09-12T05:40:10.789+00:00","attempt":1},"traceHeaders":{"traceparent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-02"}}""";

	private const string CancelFrame =
		"""{"type":"task_cancel","taskCancel":{"taskId":"task-1","attempt":1,"ts":"2026-09-12T05:40:12+00:00","reason":"job canceled"}}""";

	private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-09-12T05:40:06.123Z", CultureInfo.InvariantCulture);
	private static readonly DateTimeOffset HeartbeatTs = DateTimeOffset.Parse("2026-09-12T05:40:09.456Z", CultureInfo.InvariantCulture);
	private static readonly DateTimeOffset FinishedAt = DateTimeOffset.Parse("2026-09-12T05:40:10.789Z", CultureInfo.InvariantCulture);
	private static readonly DateTimeOffset CancelTs = DateTimeOffset.Parse("2026-09-12T05:40:12Z", CultureInfo.InvariantCulture);

	private static WSMessage HelloMessage() => new("hello",
		new AgentHello("agent-1", "us-east",
			new Dictionary<string, bool> { ["ping"] = true, ["play_games"] = true },
			new Dictionary<string, string> { ["version"] = "1.0.0" }),
		null, null);

	private static WSMessage TaskMessage() => new("task", null,
		new JobTask("task-1", "job-1", "alice", "ping", "us-east",
			new Dictionary<string, object?> { ["minutes"] = 30 },
			JobTaskStatus.Queued, 1, CreatedAt, CreatedAt),
		null,
		TraceHeaders: new Dictionary<string, string>
		{
			["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
		});

	private static WSMessage HeartbeatMessage() => new(
		Type: "task_heartbeat", Hello: null, Task: null, TaskResult: null,
		TaskHeartbeat: new TaskHeartbeat("task-1", 1, HeartbeatTs));

	private static WSMessage ResultMessage() => new("task_result", null, null,
		new TaskResult("task-1", Success: true, Error: null,
			Output: new Dictionary<string, object?> { ["pong"] = true, ["latency_ms"] = 12 },
			FinishedAt, 1),
		TraceHeaders: new Dictionary<string, string>
		{
			["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-02"
		});

	private static WSMessage CancelMessage() => new("task_cancel", null, null, null,
		TaskCancel: new TaskCancel("task-1", 1, CancelTs, "job canceled"));

	/// <summary>Serialize must reproduce the recorded bytes; the recorded bytes must parse back.</summary>
	private static WSMessage AssertRoundTrip(string recorded, WSMessage message)
	{
		Assert.Equal(recorded, JsonSerializer.Serialize(message, JsonDefaults.Options));

		WSMessage parsed = JsonSerializer.Deserialize<WSMessage>(recorded, JsonDefaults.Options)
			?? throw new InvalidOperationException("recorded frame failed to deserialize");
		Assert.Equal(message.Type, parsed.Type);
		return parsed;
	}

	[Fact]
	public void HelloFrame_RoundTripsRecordedSnapshot()
	{
		WSMessage parsed = AssertRoundTrip(HelloFrame, HelloMessage());

		Assert.NotNull(parsed.Hello);
		Assert.Equal("agent-1", parsed.Hello.AgentId);
		Assert.Equal("us-east", parsed.Hello.Region);
		Assert.True(parsed.Hello.Capabilities!["ping"]);
		Assert.True(parsed.Hello.Capabilities!["play_games"]);
		Assert.Equal("1.0.0", parsed.Hello.Meta!["version"]);
		Assert.Null(parsed.Task);
	}

	[Fact]
	public void TaskDispatchFrame_RoundTripsRecordedSnapshot()
	{
		WSMessage parsed = AssertRoundTrip(TaskFrame, TaskMessage());

		Assert.Null(parsed.Hello);
		Assert.NotNull(parsed.Task);
		JobTask task = parsed.Task!;
		Assert.Equal("task-1", task.Id);
		Assert.Equal("job-1", task.JobId);
		Assert.Equal("alice", task.Target);
		Assert.Equal("ping", task.Action);
		Assert.Equal("us-east", task.Region);
		Assert.Equal(JobTaskStatus.Queued, task.Status);
		Assert.Equal(1, task.Attempt);
		Assert.Equal(CreatedAt, task.CreatedAt);
		Assert.Equal(CreatedAt, task.UpdatedAt);
		Assert.Equal(30, int.Parse(task.Payload!["minutes"]!.ToString()!, CultureInfo.InvariantCulture));
		Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", parsed.TraceHeaders!["traceparent"]);
	}

	[Fact]
	public void HeartbeatFrame_RoundTripsRecordedSnapshot()
	{
		WSMessage parsed = AssertRoundTrip(HeartbeatFrame, HeartbeatMessage());

		Assert.NotNull(parsed.TaskHeartbeat);
		Assert.Equal("task-1", parsed.TaskHeartbeat.TaskId);
		Assert.Equal(1, parsed.TaskHeartbeat.Attempt);
		Assert.Equal(HeartbeatTs, parsed.TaskHeartbeat.Ts);
		Assert.Null(parsed.Task);
		Assert.Null(parsed.TraceHeaders);
	}

	[Fact]
	public void TaskResultFrame_RoundTripsRecordedSnapshot()
	{
		WSMessage parsed = AssertRoundTrip(ResultFrame, ResultMessage());

		Assert.NotNull(parsed.TaskResult);
		Assert.Equal("task-1", parsed.TaskResult.TaskId);
		Assert.True(parsed.TaskResult.Success);
		Assert.Null(parsed.TaskResult.Error);
		Assert.True(((JsonElement)parsed.TaskResult.Output!["pong"]!).GetBoolean());
		Assert.Equal(12, int.Parse(parsed.TaskResult.Output["latency_ms"]!.ToString()!, CultureInfo.InvariantCulture));
		Assert.Equal(FinishedAt, parsed.TaskResult.FinishedAt);
		Assert.Equal(1, parsed.TaskResult.Attempt);
		Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-02", parsed.TraceHeaders!["traceparent"]);
	}

	[Fact]
	public void TaskCancelFrame_RoundTripsRecordedSnapshot()
	{
		WSMessage parsed = AssertRoundTrip(CancelFrame, CancelMessage());

		Assert.NotNull(parsed.TaskCancel);
		Assert.Equal("task-1", parsed.TaskCancel.TaskId);
		Assert.Equal(1, parsed.TaskCancel.Attempt);
		Assert.Equal(CancelTs, parsed.TaskCancel.Ts);
		Assert.Equal("job canceled", parsed.TaskCancel.Reason);
		Assert.Null(parsed.TaskResult);
	}

	[Fact]
	public void RecordedSessionSequence_RoutesFramesLikeBothEnds()
	{
		// One full dispatch cycle in wire order. The routing mirrors each side's
		// handler: the control plane reacts to task_heartbeat/task_result, the
		// agent to task/task_cancel; anything else is ignored by both.
		string[] session = [HelloFrame, TaskFrame, HeartbeatFrame, ResultFrame, CancelFrame];
		var heartbeatTaskIds = new List<string>();
		var resultTaskIds = new List<string>();
		var dispatchedTaskIds = new List<string>();
		var cancelTaskIds = new List<string>();

		foreach (string frame in session)
		{
			WSMessage msg = JsonSerializer.Deserialize<WSMessage>(frame, JsonDefaults.Options)!;
			switch (msg)
			{
				case { Type: "task", Task: { } task }:
					dispatchedTaskIds.Add(task.Id); // agent side
					break;
				case { Type: "task_heartbeat", TaskHeartbeat: { } hb }:
					heartbeatTaskIds.Add(hb.TaskId); // control plane side
					break;
				case { Type: "task_result", TaskResult: { } result }:
					resultTaskIds.Add(result.TaskId); // control plane side
					break;
				case { Type: "task_cancel", TaskCancel: { } cancel }:
					cancelTaskIds.Add(cancel.TaskId); // agent side
					break;
			}
		}

		Assert.Equal(new[] { "task-1" }, dispatchedTaskIds);
		Assert.Equal(new[] { "task-1" }, heartbeatTaskIds);
		Assert.Equal(new[] { "task-1" }, resultTaskIds);
		Assert.Equal(new[] { "task-1" }, cancelTaskIds);
	}

	[Fact]
	public void UnknownFieldsAndTypes_AreIgnoredForForwardCompatibility()
	{
		// Newer peers may add fields or frame types; both ends must keep parsing
		// the recorded contract unchanged.
		string extended = """{"type":"task","task":{"id":"task-2","jobId":"job-1","target":"bob","action":"ping","region":null,"status":"queued","attempt":2,"createdAt":"2026-09-12T05:40:06.123+00:00","updatedAt":"2026-09-12T05:40:06.123+00:00","priority":"high","assignedPool":"eu"},"traceparent":"00-0"}""";
		WSMessage msg = JsonSerializer.Deserialize<WSMessage>(extended, JsonDefaults.Options)!;

		Assert.Equal("task", msg.Type);
		Assert.Equal("task-2", msg.Task!.Id);
		Assert.Equal("bob", msg.Task.Target);
		Assert.Equal(2, msg.Task.Attempt);
		Assert.Null(msg.Task.Region); // explicit JSON null must not become a value

		WSMessage unknownType = JsonSerializer.Deserialize<WSMessage>(
			"""{"type":"task_nudge","taskNudge":{"taskId":"task-2"}}""", JsonDefaults.Options)!;
		Assert.Equal("task_nudge", unknownType.Type);
		Assert.Null(unknownType.Task);
	}
}
