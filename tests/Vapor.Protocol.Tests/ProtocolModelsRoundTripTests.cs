using System.Text.Json;
using Xunit;

namespace Vapor.Protocol.Tests;

/// <summary>
/// Round-trip coverage for the protocol model records. Field-by-field assertions
/// (record equality would compare collection members by reference) cover the
/// shapes every REST route and tunnel frame rides on.
/// </summary>
public sealed class ProtocolModelsRoundTripTests
{
	private static T RoundTrip<T>(T value)
	{
		string json = JsonSerializer.Serialize(value, JsonDefaults.Options);
		return JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)!;
	}

	[Fact]
	public void JobSchedule_RoundTrips_WithPolicies()
	{
		var schedule = new JobSchedule(IntervalSeconds: 300, Cron: null, Missed: ScheduleMissedPolicy.RunOnce, Overlap: ScheduleOverlapPolicy.Allow);

		JobSchedule parsed = RoundTrip(schedule);

		Assert.Equal(300, parsed.IntervalSeconds);
		Assert.Null(parsed.Cron);
		Assert.Equal(ScheduleMissedPolicy.RunOnce, parsed.Missed);
		Assert.Equal(ScheduleOverlapPolicy.Allow, parsed.Overlap);
	}

	[Fact]
	public void JobSchedule_SerializesCronAndPolicyNamesAsCamelCase()
	{
		string json = JsonSerializer.Serialize(
			new JobSchedule(0, "0 9 * * 1-5", ScheduleMissedPolicy.RunOnce, ScheduleOverlapPolicy.Skip),
			JsonDefaults.Options);

		Assert.Contains("\"intervalSeconds\":0", json);
		Assert.Contains("\"cron\":\"0 9 * * 1-5\"", json);
		Assert.Contains("\"missed\":\"runOnce\"", json);
		Assert.Contains("\"overlap\":\"skip\"", json);
	}

	[Fact]
	public void Job_RoundTrips_WithScheduleAndNextRun()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-12T06:00:00.500Z");
		var job = new Job(
			"job-1", "ping", "us-east", ["alice", "bob"],
			new Dictionary<string, string> { ["scheduledFrom"] = "tpl-1" },
			JobStatus.Scheduled, now, now,
			new JobSchedule(IntervalSeconds: 60), now.AddSeconds(60));

		Job parsed = RoundTrip(job);

		Assert.Equal("job-1", parsed.Id);
		Assert.Equal("ping", parsed.Action);
		Assert.Equal("us-east", parsed.Region);
		Assert.Equal(new[] { "alice", "bob" }, parsed.Targets.ToArray());
		Assert.Equal("tpl-1", parsed.Meta!["scheduledFrom"]);
		Assert.Equal(JobStatus.Scheduled, parsed.Status);
		Assert.Equal(now, parsed.CreatedAt);
		Assert.Equal(now, parsed.UpdatedAt);
		Assert.Equal(60, parsed.Schedule!.IntervalSeconds);
		Assert.Equal(now.AddSeconds(60), parsed.NextRunAt);
	}

	[Fact]
	public void Job_OmitsOptionalMembers_WhenNull()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-12T06:00:00.500Z");
		var job = new Job("job-2", "echo", null, ["alice"], null, JobStatus.Queued, now, now);

		string json = JsonSerializer.Serialize(job, JsonDefaults.Options);

		Assert.DoesNotContain("region", json);
		Assert.DoesNotContain("meta", json);
		Assert.DoesNotContain("schedule", json);
		Assert.DoesNotContain("nextRunAt", json);
	}

	[Fact]
	public void JobTask_RoundTrips_WithPayloadAndOutput()
	{
		DateTimeOffset now = DateTimeOffset.Parse("2026-09-12T06:00:00.500Z");
		var task = new JobTask(
			"task-1", "job-1", "alice", "play_games", "eu-west",
			new Dictionary<string, object?> { ["appIds"] = new[] { 730, 570 } },
			JobTaskStatus.Finished, 2, now, now,
			Error: "first attempt failed",
			Output: new Dictionary<string, object?> { ["played"] = 2 });

		JobTask parsed = RoundTrip(task);

		Assert.Equal("task-1", parsed.Id);
		Assert.Equal("job-1", parsed.JobId);
		Assert.Equal("alice", parsed.Target);
		Assert.Equal("play_games", parsed.Action);
		Assert.Equal("eu-west", parsed.Region);
		Assert.Equal(JobTaskStatus.Finished, parsed.Status);
		Assert.Equal(2, parsed.Attempt);
		Assert.Equal(now, parsed.CreatedAt);
		Assert.Equal(now, parsed.UpdatedAt);
		Assert.Equal("first attempt failed", parsed.Error);
		Assert.Equal("730", ((JsonElement)parsed.Payload!["appIds"]!)[0].ToString());
		Assert.Equal("570", ((JsonElement)parsed.Payload["appIds"]!)[1].ToString());
		Assert.Equal(2, int.Parse(parsed.Output!["played"]!.ToString()!));
	}

	[Fact]
	public void CreateJobRequest_RoundTrips_WithSchedule()
	{
		var request = new CreateJobRequest(
			"ping", "us-east", ["alice", "bob"],
			new Dictionary<string, object?> { ["minutes"] = 30 },
			new Dictionary<string, string> { ["origin"] = "test" },
			new JobSchedule(Cron: "*/5 * * * *"));

		CreateJobRequest parsed = RoundTrip(request);

		Assert.Equal("ping", parsed.Action);
		Assert.Equal("us-east", parsed.Region);
		Assert.Equal(new[] { "alice", "bob" }, parsed.Targets.ToArray());
		Assert.Equal(30, int.Parse(parsed.Payload!["minutes"]!.ToString()!));
		Assert.Equal("test", parsed.Meta!["origin"]);
		Assert.Equal("*/5 * * * *", parsed.Schedule!.Cron);
	}

	[Fact]
	public void AccountSpec_RoundTrips_WithDesiredState()
	{
		var spec = new AccountSpec(
			"alice", Enabled: true, AccountDesiredState.Idle,
			IdleApps: new[] { "730", "570" }, Region: "us-east", AgentId: "agent-1",
			Note: "farm account", Version: new ConfigVersion(3, DateTimeOffset.Parse("2026-09-12T06:00:00Z"), "admin"));

		AccountSpec parsed = RoundTrip(spec);

		Assert.Equal("alice", parsed.AccountName);
		Assert.True(parsed.Enabled);
		Assert.Equal(AccountDesiredState.Idle, parsed.DesiredState);
		Assert.Equal(new[] { "730", "570" }, parsed.IdleApps!.ToArray());
		Assert.Equal("us-east", parsed.Region);
		Assert.Equal("agent-1", parsed.AgentId);
		Assert.Equal("farm account", parsed.Note);
		Assert.Equal(3, parsed.Version!.Version);
		Assert.Equal("admin", parsed.Version.UpdatedBy);
	}

	[Fact]
	public void AccountSpec_RoundTrips_FarmStateWithExclusionApps()
	{
		var spec = new AccountSpec(
			"alice", Enabled: true, AccountDesiredState.Farm,
			IdleApps: new[] { "730" }, Region: "us-east", AgentId: null,
			Note: "smart farming", Version: new ConfigVersion(1, DateTimeOffset.Parse("2026-09-12T06:00:00Z"), "admin"));

		AccountSpec parsed = RoundTrip(spec);

		Assert.Equal(AccountDesiredState.Farm, parsed.DesiredState);
		Assert.Equal(new[] { "730" }, parsed.IdleApps!.ToArray());
	}

	[Fact]
	public void AccountDesiredState_Farm_SerializesAsCamelCaseName()
	{
		string json = JsonSerializer.Serialize(
			new AccountSpec("alice", Enabled: true, AccountDesiredState.Farm),
			JsonDefaults.Options);

		Assert.Contains("\"desiredState\":\"farm\"", json);
	}

	[Fact]
	public void TradePolicy_RoundTrips_WithWhitelist()
	{
		var spec = new AccountSpec(
			"alice", Enabled: true, AccountDesiredState.Online,
			TradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [76561197960265728ul, 76561198000000000ul]));

		AccountSpec parsed = RoundTrip(spec);

		Assert.NotNull(parsed.TradePolicy);
		Assert.True(parsed.TradePolicy!.AutoAcceptGifts);
		Assert.Equal(new ulong[] { 76561197960265728, 76561198000000000 }, parsed.TradePolicy.PartnerWhitelist!.ToArray());
	}

	[Fact]
	public void TradePolicy_DisabledFlag_SerializesExplicitly()
	{
		// The flag must stay visible in JSON (not dropped by a default-value
		// omit): an operator reading the API response must be able to tell an
		// explicit opt-out from an absent policy.
		string json = JsonSerializer.Serialize(
			new TradePolicy(AutoAcceptGifts: false),
			JsonDefaults.Options);

		Assert.Contains("\"autoAcceptGifts\":false", json);
	}

	[Fact]
	public void AccountConfig_RoundTrips_WithPasswordFormat()
	{
		var config = new AccountConfig(
			"bob", Enabled: false, Password: "s3cret", EPasswordFormat.AES,
			Region: "eu-west", Labels: ["vip"],
			Settings: new Dictionary<string, object?> { ["max_retries"] = 5 },
			Version: new ConfigVersion(1, DateTimeOffset.Parse("2026-09-12T06:00:00Z")));

		AccountConfig parsed = RoundTrip(config);

		Assert.Equal("bob", parsed.AccountName);
		Assert.False(parsed.Enabled);
		Assert.Equal("s3cret", parsed.Password);
		Assert.Equal(EPasswordFormat.AES, parsed.PasswordFormat);
		Assert.Equal("eu-west", parsed.Region);
		Assert.Equal(new[] { "vip" }, parsed.Labels!.ToArray());
		Assert.Equal(5, int.Parse(parsed.Settings!["max_retries"]!.ToString()!));
		Assert.Equal(1, parsed.Version!.Version);
	}

	[Fact]
	public void GlobalConfig_RoundTrips()
	{
		var config = new GlobalConfig(
			new ConfigVersion(7, DateTimeOffset.Parse("2026-09-12T06:00:00Z"), "ops"),
			EncryptionKey: "key-material",
			Settings: new Dictionary<string, object?> { ["feature_x"] = true });

		GlobalConfig parsed = RoundTrip(config);

		Assert.Equal(7, parsed.Version.Version);
		Assert.Equal("ops", parsed.Version.UpdatedBy);
		Assert.Equal("key-material", parsed.EncryptionKey);
		Assert.True(((JsonElement)parsed.Settings!["feature_x"]!).GetBoolean());
	}

	[Fact]
	public void SessionEvent_RoundTrips()
	{
		var evt = new SessionEvent("e-1", "alice", "StateChanged", "Online", "logged on", DateTimeOffset.Parse("2026-09-12T06:00:00.250Z"));

		SessionEvent parsed = RoundTrip(evt);

		Assert.Equal("e-1", parsed.Id);
		Assert.Equal("alice", parsed.AccountName);
		Assert.Equal("StateChanged", parsed.EventType);
		Assert.Equal("Online", parsed.State);
		Assert.Equal("logged on", parsed.Message);
		Assert.Equal(evt.Timestamp, parsed.Timestamp);
	}

	[Fact]
	public void AuthChallengeEvent_RoundTrips_WithoutCode()
	{
		// Challenge frames published to non-agent subscribers carry no code;
		// omission on the wire is the redaction contract.
		var evt = new AuthChallengeEvent("e-2", "alice", "auth_code_required", "enter code", null, DateTimeOffset.Parse("2026-09-12T06:00:00.250Z"), "job-1");

		string json = JsonSerializer.Serialize(evt, JsonDefaults.Options);
		AuthChallengeEvent parsed = JsonSerializer.Deserialize<AuthChallengeEvent>(json, JsonDefaults.Options)!;

		Assert.DoesNotContain("\"code\"", json);
		Assert.Equal("auth_code_required", parsed.ChallengeType);
		Assert.Equal("job-1", parsed.JobId);
	}

	[Fact]
	public void CommandRequestAndResult_RoundTrip()
	{
		var request = new CommandRequest("status", ["alice"], new Dictionary<string, object?> { ["verbose"] = true });
		var result = new CommandResult(Success: true, Error: null, Output: new Dictionary<string, object?> { ["state"] = "online" });

		CommandRequest parsedRequest = RoundTrip(request);
		CommandResult parsedResult = RoundTrip(result);

		Assert.Equal("status", parsedRequest.Command);
		Assert.Equal(new[] { "alice" }, parsedRequest.Targets!.ToArray());
		Assert.True(((JsonElement)parsedRequest.Args!["verbose"]!).GetBoolean());
		Assert.True(parsedResult.Success);
		Assert.Null(parsedResult.Error);
		Assert.Equal("online", parsedResult.Output!["state"]!.ToString());
	}

	[Fact]
	public void JobAndTaskEvent_RoundTrip()
	{
		var jobEvent = new JobEvent("e-3", "job-1", "job.scheduled_triggered", DateTimeOffset.Parse("2026-09-12T06:00:00Z"),
			new Dictionary<string, object?> { ["missedCount"] = 0 });
		var taskEvent = new TaskEvent("e-4", "task-1", "job-1", "task.finished", DateTimeOffset.Parse("2026-09-12T06:00:01Z"), null);

		JobEvent parsedJobEvent = RoundTrip(jobEvent);
		TaskEvent parsedTaskEvent = RoundTrip(taskEvent);

		Assert.Equal("job.scheduled_triggered", parsedJobEvent.Type);
		Assert.Equal("job-1", parsedJobEvent.JobId);
		Assert.Equal(0, int.Parse(parsedJobEvent.Payload!["missedCount"]!.ToString()!));
		Assert.Equal("task-1", parsedTaskEvent.TaskId);
		Assert.Null(parsedTaskEvent.Payload);
	}
}
