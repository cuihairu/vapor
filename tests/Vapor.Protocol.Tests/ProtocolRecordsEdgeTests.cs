using System.Text.Json;
using Xunit;

namespace Vapor.Protocol.Tests;

/// <summary>
/// Round-trip coverage for the remaining protocol records: action descriptor metadata,
/// plugin events, tunnel task frames and websocket envelopes. Field-by-field assertions
/// (record equality would compare collection members by reference) plus a JSON round trip
/// under the shared <see cref="JsonDefaults.Options"/> contract.
/// </summary>
public sealed class ProtocolRecordsEdgeTests
{
	private static T RoundTrip<T>(T value)
	{
		string json = JsonSerializer.Serialize(value, JsonDefaults.Options);
		return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)!;
	}

	private static DateTimeOffset Ts { get; } = DateTimeOffset.Parse("2026-09-12T06:00:00.250Z");

	// --- Actions.cs ---

	[Fact]
	public void ActionParamSchema_RoundTrips_WithDefaults()
	{
		var param = new ActionParamSchema("app_id", "string");

		Assert.Equal("app_id", param.Name);
		Assert.Equal("string", param.Type);
		Assert.False(param.Required);
		Assert.Null(param.Description);

		ActionParamSchema parsed = RoundTrip(param);

		Assert.Equal("app_id", parsed.Name);
		Assert.Equal("string", parsed.Type);
		Assert.False(parsed.Required);
		Assert.Null(parsed.Description);
	}

	[Fact]
	public void ActionParamSchema_RoundTrips_FullyPopulated()
	{
		var param = new ActionParamSchema("threshold", "number", Required: true, Description: "Percent change trigger");

		Assert.True(param.Required);
		Assert.Equal("Percent change trigger", param.Description);

		ActionParamSchema parsed = RoundTrip(param);

		Assert.True(parsed.Required);
		Assert.Equal("Percent change trigger", parsed.Description);
	}

	[Fact]
	public void ActionDescriptor_RoundTrips_WithDefaults()
	{
		var descriptor = new ActionDescriptor("ping", "Answers with pong");

		Assert.Equal("ping", descriptor.Name);
		Assert.Equal("Answers with pong", descriptor.Summary);
		Assert.Null(descriptor.Params);
		Assert.Equal(PermissionLevel.Operator, descriptor.Permission);
		Assert.Null(descriptor.Tags);

		ActionDescriptor parsed = RoundTrip(descriptor);

		Assert.Equal("ping", parsed.Name);
		Assert.Equal(PermissionLevel.Operator, parsed.Permission);
	}

	[Fact]
	public void ActionDescriptor_RoundTrips_FullyPopulated()
	{
		var descriptor = new ActionDescriptor(
			"play_games",
			"Starts farming on the target sessions",
			[
				new ActionParamSchema("minutes", "number", Required: true),
				new ActionParamSchema("app_ids", "array")
			],
			PermissionLevel.Admin,
			["farming", "beta"]);

		ActionDescriptor parsed = RoundTrip(descriptor);

		Assert.Equal("play_games", parsed.Name);
		Assert.Equal(PermissionLevel.Admin, parsed.Permission);
		Assert.Equal(2, parsed.Params!.Count);
		Assert.Equal("minutes", parsed.Params[0].Name);
		Assert.True(parsed.Params[0].Required);
		Assert.Equal(new[] { "farming", "beta" }, parsed.Tags!.ToArray());
	}

	// --- Events.cs ---

	[Fact]
	public void PluginEvent_RoundTrips_WithAndWithoutPayload()
	{
		var evt = new PluginEvent("pe-1", "vapor.market-watch", "price_alert", Ts,
			new Dictionary<string, object?> { ["appId"] = 570 });

		Assert.Equal("pe-1", evt.Id);
		Assert.Equal("vapor.market-watch", evt.PluginId);
		Assert.Equal("price_alert", evt.Type);
		Assert.Equal(Ts, evt.Ts);
		Assert.Equal("570", evt.Payload!["appId"]!.ToString());

		PluginEvent parsed = RoundTrip(evt);

		Assert.Equal("pe-1", parsed.Id);
		Assert.Equal("vapor.market-watch", parsed.PluginId);
		Assert.Equal("price_alert", parsed.Type);
		Assert.Equal(Ts, parsed.Ts);
		Assert.Equal("570", parsed.Payload!["appId"]!.ToString());

		var bare = new PluginEvent("pe-2", "vapor.monitoring", "snapshot", Ts);
		Assert.Null(bare.Payload);
		Assert.Null(RoundTrip(bare).Payload);
	}

	// --- Models.cs task/tunnel frames ---

	[Fact]
	public void TaskResult_RoundTrips()
	{
		var result = new TaskResult(
			"task-1", Success: true, Error: null,
			new Dictionary<string, object?> { ["played"] = 2 },
			Ts, Attempt: 2);

		Assert.Equal("task-1", result.TaskId);
		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal(2, int.Parse(result.Output!["played"]!.ToString()!));
		Assert.Equal(Ts, result.FinishedAt);
		Assert.Equal(2, result.Attempt);

		TaskResult parsed = RoundTrip(result);

		Assert.Equal("task-1", parsed.TaskId);
		Assert.True(parsed.Success);
		Assert.Null(parsed.Error);
		Assert.Equal(2, int.Parse(parsed.Output!["played"]!.ToString()!));
		Assert.Equal(Ts, parsed.FinishedAt);
		Assert.Equal(2, parsed.Attempt);

		var failed = new TaskResult("task-2", Success: false, "timeout", null, Ts);
		Assert.Equal("timeout", failed.Error);
		Assert.Equal(0, failed.Attempt);
	}

	[Fact]
	public void TaskHeartbeat_RoundTrips()
	{
		var heartbeat = new TaskHeartbeat("task-1", 3, Ts);

		Assert.Equal("task-1", heartbeat.TaskId);
		Assert.Equal(3, heartbeat.Attempt);
		Assert.Equal(Ts, heartbeat.Ts);

		TaskHeartbeat parsed = RoundTrip(heartbeat);

		Assert.Equal("task-1", parsed.TaskId);
		Assert.Equal(3, parsed.Attempt);
		Assert.Equal(Ts, parsed.Ts);
	}

	[Fact]
	public void TaskCancel_RoundTrips_WithAndWithoutReason()
	{
		var cancel = new TaskCancel("task-1", 1, Ts, "user requested");

		Assert.Equal("task-1", cancel.TaskId);
		Assert.Equal(1, cancel.Attempt);
		Assert.Equal(Ts, cancel.Ts);
		Assert.Equal("user requested", cancel.Reason);

		TaskCancel parsed = RoundTrip(cancel);

		Assert.Equal("user requested", parsed.Reason);

		var bare = new TaskCancel("task-2", 1, Ts);
		Assert.Null(bare.Reason);
		Assert.Null(RoundTrip(bare).Reason);
	}

	[Fact]
	public void JobWithTasks_RoundTrips()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-12T06:00:00Z");
		var job = new Job("job-1", "ping", null, ["alice"], null, JobStatus.Running, now, now);
		var tasks = new[]
		{
			new JobTask("task-1", "job-1", "alice", "ping", null, null, JobTaskStatus.Running, 1, now, now)
		};
		var combined = new JobWithTasks(job, tasks);

		Assert.Equal("job-1", combined.Job.Id);
		Assert.Single(combined.Tasks);

		JobWithTasks parsed = RoundTrip(combined);

		Assert.Equal("job-1", parsed.Job.Id);
		Assert.Equal("task-1", Assert.Single(parsed.Tasks).Id);
	}

	[Fact]
	public void ErrorResponse_SerializesAsCamelCaseAndRoundTrips()
	{
		var error = new ErrorResponse("job not found");

		Assert.Equal("job not found", error.Error);

		string json = JsonSerializer.Serialize(error, JsonDefaults.Options);
		Assert.Contains("\"error\":\"job not found\"", json);

		Assert.Equal("job not found", RoundTrip(error).Error);
	}

	[Fact]
	public void Event_RoundTrips()
	{
		var evt = new Event("e-1", "job-1", "task.finished", Ts,
			new Dictionary<string, object?> { ["attempt"] = 1 });

		Assert.Equal("e-1", evt.Id);
		Assert.Equal("job-1", evt.JobId);
		Assert.Equal("task.finished", evt.Type);
		Assert.Equal(Ts, evt.Ts);
		Assert.Equal(1, int.Parse(evt.Payload!["attempt"]!.ToString()!));

		Event parsed = RoundTrip(evt);

		Assert.Equal("e-1", parsed.Id);
		Assert.Equal("job-1", parsed.JobId);
		Assert.Equal(1, int.Parse(parsed.Payload!["attempt"]!.ToString()!));

		var bare = new Event("e-2", null, "job.created", Ts, null);
		Assert.Null(bare.JobId);
		Assert.Null(bare.Payload);
	}

	[Fact]
	public void AgentHello_RoundTrips_WithCapabilitiesAndMeta()
	{
		var hello = new AgentHello(
			"agent-1", "us-east",
			new Dictionary<string, bool> { ["play"] = true, ["trade"] = false },
			new Dictionary<string, string> { ["version"] = "1.0.0" });

		Assert.Equal("agent-1", hello.AgentId);
		Assert.Equal("us-east", hello.Region);
		Assert.True(hello.Capabilities!["play"]);
		Assert.False(hello.Capabilities["trade"]);
		Assert.Equal("1.0.0", hello.Meta!["version"]);

		AgentHello parsed = RoundTrip(hello);

		Assert.Equal("agent-1", parsed.AgentId);
		Assert.Equal("us-east", parsed.Region);
		Assert.True(parsed.Capabilities!["play"]);
		Assert.False(parsed.Capabilities["trade"]);
		Assert.Equal("1.0.0", parsed.Meta!["version"]);
	}

	[Fact]
	public void WSMessage_RoundTrips_EveryFrameVariant()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-12T06:00:00Z");
		var message = new WSMessage(
			"task",
			new AgentHello("agent-1", "us-east", null, null),
			new JobTask("task-1", "job-1", "alice", "ping", null, null, JobTaskStatus.Queued, 1, now, now),
			new TaskResult("task-1", true, null, null, Ts, 1),
			new TaskHeartbeat("task-1", 1, Ts),
			new TaskCancel("task-1", 1, Ts, "agent restart"),
			new Dictionary<string, string> { ["traceparent"] = "00-abc-def-01" });

		Assert.Equal("task", message.Type);
		Assert.Equal("agent-1", message.Hello!.AgentId);
		Assert.Equal("task-1", message.Task!.Id);
		Assert.Equal("task-1", message.TaskResult!.TaskId);
		Assert.Equal("task-1", message.TaskHeartbeat!.TaskId);
		Assert.Equal("agent restart", message.TaskCancel!.Reason);
		Assert.Equal("00-abc-def-01", message.TraceHeaders!["traceparent"]);

		WSMessage parsed = RoundTrip(message);

		Assert.Equal("task", parsed.Type);
		Assert.Equal("agent-1", parsed.Hello!.AgentId);
		Assert.Equal("task-1", parsed.Task!.Id);
		Assert.True(parsed.TaskResult!.Success);
		Assert.Equal(1, parsed.TaskHeartbeat!.Attempt);
		Assert.Equal("agent restart", parsed.TaskCancel!.Reason);
		Assert.Equal("00-abc-def-01", parsed.TraceHeaders!["traceparent"]);
	}

	[Fact]
	public void WSMessage_MinimalFrame_OmitsOptionalMembers()
	{
		var message = new WSMessage("hello", new AgentHello("agent-1", "eu-west", null, null), null, null);

		string json = JsonSerializer.Serialize(message, JsonDefaults.Options);

		Assert.DoesNotContain("taskResult", json);
		Assert.DoesNotContain("taskHeartbeat", json);
		Assert.DoesNotContain("taskCancel", json);
		Assert.DoesNotContain("traceHeaders", json);

		WSMessage parsed = RoundTrip(message);

		Assert.Equal("hello", parsed.Type);
		Assert.Equal("eu-west", parsed.Hello!.Region);
		Assert.Null(parsed.Task);
		Assert.Null(parsed.TaskResult);
		Assert.Null(parsed.TaskHeartbeat);
		Assert.Null(parsed.TaskCancel);
		Assert.Null(parsed.TraceHeaders);
	}

	[Fact]
	public void CreateJobResponse_RoundTrips()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-12T06:00:00Z");
		var response = new CreateJobResponse(
			new Job("job-1", "ping", null, ["alice"], null, JobStatus.Queued, now, now));

		Assert.Equal("job-1", response.Job.Id);

		CreateJobResponse parsed = RoundTrip(response);

		Assert.Equal("job-1", parsed.Job.Id);
		Assert.Equal(JobStatus.Queued, parsed.Job.Status);
	}
}
