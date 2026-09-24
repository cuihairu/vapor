using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// GET /v1/system/status — the aggregated internal status view. Covers the
// auth gate, the overall-health derivation arms (healthy / degraded /
// unhealthy), the desired-vs-actual account comparison and the proxy probe
// aggregation. Credentials and challenge codes must never appear.
public sealed class SystemStatusApiTests
{
	[Fact]
	public async Task SystemStatus_RequiresAuthorization()
	{
		await using TestFactory factory = new();
		using HttpClient client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task SystemStatus_ReportsHealthyOnEmptySystem()
	{
		await using TestFactory factory = new();
		// Seed the plugin mirror so the by-trust aggregation loop runs with
		// known data (two official + community + trust-less entries; the second
		// official entry exercises the cumulative byTrust increment branch).
		PluginInventory pluginInventory = factory.Services.GetRequiredService<PluginInventory>();
		pluginInventory.Update("agent-a", DateTimeOffset.UtcNow, PluginInventoryTests.RoundTripForSeed(new Dictionary<string, object?>
		{
			["plugins"] = new List<object>
			{
				new Dictionary<string, object?> { ["id"] = "vapor.x", ["trust"] = "official" },
				new Dictionary<string, object?> { ["id"] = "vapor.w", ["trust"] = "official" },
				new Dictionary<string, object?> { ["id"] = "vapor.y", ["trust"] = "community" },
				new Dictionary<string, object?> { ["id"] = "vapor.z" }
			}
		}));
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;
		Assert.Equal("healthy", root.GetProperty("overall").GetProperty("status").GetString());
		Assert.Empty(root.GetProperty("overall").GetProperty("reasons").EnumerateArray());
		Assert.True(root.GetProperty("controlPlane").GetProperty("db").GetProperty("available").GetBoolean());
		Assert.Equal(0, root.GetProperty("controlPlane").GetProperty("jobs").GetProperty("queued").GetInt32());
		JsonElement pluginsElement = root.GetProperty("controlPlane").GetProperty("plugins");
		Assert.Equal(1, pluginsElement.GetProperty("agentsReporting").GetInt32());
		Assert.Equal(4, pluginsElement.GetProperty("entries").GetInt32());
		Assert.Equal(2, pluginsElement.GetProperty("byTrust").GetProperty("official").GetInt32());
		Assert.Equal(1, pluginsElement.GetProperty("byTrust").GetProperty("community").GetInt32());
		Assert.Equal(1, pluginsElement.GetProperty("byTrust").GetProperty("unknown").GetInt32());
		Assert.Equal(0, root.GetProperty("agents").GetProperty("connected").GetInt32());
		Assert.Equal(0, root.GetProperty("accounts").GetProperty("total").GetInt32());
		Assert.Equal(0, root.GetProperty("proxies").GetProperty("probesOk").GetInt32());
		// Before the first dispatch tick the scheduler heartbeat is unset; null
		// fields are omitted from responses (WhenWritingNull), so absence and
		// JSON null both count as "not ticking".
		JsonElement schedulerElement = root.GetProperty("controlPlane").GetProperty("scheduler");
		Assert.False(
			schedulerElement.TryGetProperty("lastTickAt", out JsonElement lastTick) && lastTick.ValueKind != JsonValueKind.Null);
	}

	[Fact]
	public async Task SystemStatus_ReportsDegradedWithAccountAndChallenge()
	{
		await using TestFactory factory = new();
		AccountStore accountStore = factory.Services.GetRequiredService<AccountStore>();
		accountStore.Upsert("alice", enabled: true, AccountDesiredState.Online, null, "us-east", null, null);
		AuthChallengeTracker challengeTracker = factory.Services.GetRequiredService<AuthChallengeTracker>();
		challengeTracker.Upsert(new AuthChallengeEvent(
			Id: "ch-1", AccountName: "alice", ChallengeType: "steam_guard", Message: null, Code: "12345",
			Timestamp: DateTimeOffset.UtcNow, JobId: null));
		// A second challenge of the same type exercises the tally increment arm.
		challengeTracker.Upsert(new AuthChallengeEvent(
			Id: "ch-2", AccountName: "hank", ChallengeType: "steam_guard", Message: null, Code: null,
			Timestamp: DateTimeOffset.UtcNow, JobId: null));
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;
		Assert.Equal("degraded", root.GetProperty("overall").GetProperty("status").GetString());
		// Desired Online + no session snapshot => mismatch listed.
		JsonElement mismatches = root.GetProperty("accounts").GetProperty("mismatches");
		JsonElement mismatch = Assert.Single(mismatches.EnumerateArray());
		Assert.Equal("alice", mismatch.GetProperty("account").GetString());
		Assert.Equal("Online", mismatch.GetProperty("desiredState").GetString());
		// Null fields are omitted from responses (WhenWritingNull): absence of
		// actualSessionState means "no session snapshot".
		Assert.False(
			mismatch.TryGetProperty("actualSessionState", out JsonElement actual) && actual.ValueKind != JsonValueKind.Null);
		// Challenge appears as a counter only; its Code must never leak.
		Assert.Equal(2, root.GetProperty("accounts").GetProperty("pendingChallenges").GetInt32());
		Assert.Equal(2, root.GetProperty("accounts").GetProperty("challengeTypes").GetProperty("steam_guard").GetInt32());
		Assert.DoesNotContain("12345", body);
	}

	[Fact]
	public async Task SystemStatus_ReportsNoMismatchWhenSessionMatchesDesired()
	{
		await using TestFactory factory = new();
		AccountStore accountStore = factory.Services.GetRequiredService<AccountStore>();
		accountStore.Upsert("alice", enabled: true, AccountDesiredState.Online, null, "us-east", null, null);
		accountStore.Upsert("bob", enabled: true, AccountDesiredState.Offline, null, "us-east", null, null);
		// A second Online account exercises the increment arm of the
		// desired-state tally (same state, second account).
		accountStore.Upsert("carol", enabled: true, AccountDesiredState.Online, null, "us-east", null, null);
		SessionTracker sessions = factory.Services.GetRequiredService<SessionTracker>();
		sessions.Update("alice", "session.connected", "connected", null);
		sessions.Update("bob", "session.disconnected", "disconnected", null);
		sessions.Update("carol", "session.connected", "connected", null);
		// A connected agent keeps the overall verdict on the healthy arm; this
		// test targets the mismatch comparison, not the no-agent degradation.
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		using CancellationTokenSource registrationCts = new();
		registry.Register(new AgentHello("agent-1", "us-east", null, null), new SystemStatusNoopWebSocket(), registrationCts.Token);
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;
		Assert.Empty(root.GetProperty("accounts").GetProperty("mismatches").EnumerateArray());
		Assert.Equal(3, root.GetProperty("accounts").GetProperty("sessionsTracked").GetInt32());
		Assert.Equal(1, root.GetProperty("agents").GetProperty("connected").GetInt32());
		Assert.Equal("healthy", root.GetProperty("overall").GetProperty("status").GetString());
	}

	[Fact]
	public async Task SystemStatus_ReportsUnhealthyWhenJobStoreFails()
	{
		await using TestFactory factory = new() { JobStore = new StatusProbeStore() };
		((StatusProbeStore)factory.Services.GetRequiredService<IJobStore>()).CountFailure = new InvalidOperationException("db gone");
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;
		Assert.Equal("unhealthy", root.GetProperty("overall").GetProperty("status").GetString());
		JsonElement db = root.GetProperty("controlPlane").GetProperty("db");
		Assert.False(db.GetProperty("available").GetBoolean());
		Assert.Contains("db gone", db.GetProperty("error").GetString());
	}

	[Fact]
	public async Task SystemStatus_AggregatesProxyProbeHistory()
	{
		StatusProbeStore store = new() { Counts = { [JobTaskStatus.Queued] = 1 } };
		DateTimeOffset now = DateTimeOffset.UtcNow;
		store.Jobs.Add(new Job("j1", "check_proxy", null, ["alice"], null, JobStatus.Finished, now, now));
		store.Jobs.Add(new Job("j2", "ping", null, ["carol"], null, JobStatus.Finished, now, now));
		store.TasksByJob["j1"] =
		[
			new JobTask("t1", "j1", "alice", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?>
				{
					["proxyEnabled"] = true,
					["proxy"] = "user:***@proxy.example:1080",
					["scheme"] = "Socks5",
					["account"] = "alice",
					["exitIp"] = "203.0.113.7",
					["steamReachable"] = true,
					["latencyMs"] = 123.0
				}),
			new JobTask("t2", "j1", "bob", "check_proxy", null, null, JobTaskStatus.Failed, 1, now, now,
				Output: new Dictionary<string, object?>
				{
					["proxyEnabled"] = true,
					["proxy"] = "user:***@proxy.example:1080",
					["scheme"] = "Socks5",
					["account"] = "bob",
					["exitIp"] = null,
					["steamReachable"] = false,
					["error"] = "connect timeout"
				}),
			new JobTask("t3", "j1", "dave", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?> { ["proxyEnabled"] = false, ["account"] = "dave" }),
			// Outcome not yet known — must not count as a probe result.
			new JobTask("t4", "j1", "erin", "check_proxy", null, null, JobTaskStatus.Queued, 1, now, now),
			// Empty output: the proxyEnabled lookup misses and defaults to
			// disabled (?? false arm), never reaching the success expression.
			new JobTask("t6", "j1", "frank", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?>()),
			// steamReachable=true with no exitIp key: the success conjunction
			// short-circuits on the missing key.
			new JobTask("t7", "j1", "grace", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?> { ["proxyEnabled"] = true, ["steamReachable"] = true }),
			// Non-string exitIp: the is-string pattern arm fails.
			new JobTask("t8", "j1", "heidi", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?> { ["proxyEnabled"] = true, ["steamReachable"] = true, ["exitIp"] = 12345 }),
			// Non-bool steamReachable: OutputBool yields null, so success is false.
			new JobTask("t9", "j1", "ivan", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?> { ["proxyEnabled"] = true, ["steamReachable"] = "yes", ["exitIp"] = "5.6.7.8" }),
			// Empty exitIp string: the length check arm fails.
			new JobTask("t10", "j1", "judy", "check_proxy", null, null, JobTaskStatus.Failed, 1, now, now,
				Output: new Dictionary<string, object?> { ["proxyEnabled"] = true, ["steamReachable"] = true, ["exitIp"] = "" }),
			// Null output on a finished probe: the ?? fallback arm supplies an
			// empty dictionary and the disabled tally increments.
			new JobTask("t11", "j1", "karl", "check_proxy", null, null, JobTaskStatus.Finished, 1, now, now)
		];
		// Non-proxy job tasks are ignored even with probe-shaped output.
		store.TasksByJob["j2"] =
		[
			new JobTask("t5", "j2", "carol", "ping", null, null, JobTaskStatus.Finished, 1, now, now,
				Output: new Dictionary<string, object?> { ["proxyEnabled"] = true, ["steamReachable"] = true, ["exitIp"] = "9.9.9.9" })
		];
		await using TestFactory factory = new() { JobStore = store };
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;
		JsonElement proxies = root.GetProperty("proxies");
		Assert.Equal(1, proxies.GetProperty("probesOk").GetInt32());
		Assert.Equal(5, proxies.GetProperty("probesFailed").GetInt32());
		Assert.Equal(3, proxies.GetProperty("proxyDisabled").GetInt32());
		JsonElement recent = proxies.GetProperty("recent");
		// Disabled-proxy and not-yet-finished tasks count toward their own
		// tallies but never appear in the recent list; every other outcome does.
		Assert.Equal(6, recent.GetArrayLength());
		JsonElement first = recent[0];
		Assert.Equal("alice", first.GetProperty("account").GetString());
		Assert.True(first.GetProperty("success").GetBoolean());
		Assert.Equal("203.0.113.7", first.GetProperty("exitIp").GetString());
		Assert.Equal(123.0, first.GetProperty("latencyMs").GetDouble());
		// The masked proxy endpoint passes through as stored; the raw
		// credential form never existed in the output to begin with.
		Assert.Contains("***@proxy.example", first.GetProperty("proxy").GetString());
	}

	[Fact]
	public async Task SchedulerService_HeartbeatTicksOnDispatchOnce()
	{
		await using TestFactory factory = new();
		TaskSchedulerService scheduler = factory.Services.GetRequiredService<TaskSchedulerService>();
		Assert.Null(scheduler.LastTickAt);

		await scheduler.DispatchOnce(CancellationToken.None);

		Assert.NotNull(scheduler.LastTickAt);
	}

	[Fact]
	public async Task SystemStatus_IncludesSchedulerHeartbeatAfterTick()
	{
		await using TestFactory factory = new();
		TaskSchedulerService scheduler = factory.Services.GetRequiredService<TaskSchedulerService>();
		await scheduler.DispatchOnce(CancellationToken.None);
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/system/status");

		string body = await response.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.Equal(
			JsonValueKind.String,
			doc.RootElement.GetProperty("controlPlane").GetProperty("scheduler").GetProperty("lastTickAt").ValueKind);
	}

	[Theory]
	[InlineData(true, false, 1, 0, 0, "healthy")]
	[InlineData(true, false, 0, 0, 0, "healthy")]
	[InlineData(true, true, 1, 0, 0, "degraded")]
	[InlineData(true, false, 0, 2, 0, "degraded")]
	[InlineData(true, false, 1, 0, 1, "degraded")]
	[InlineData(false, false, 1, 0, 0, "unhealthy")]
	[InlineData(false, true, 0, 0, 0, "unhealthy")]
	public void DeriveOverall_CoversAllVerdictArms(
		bool dbAvailable,
		bool lastPassFailed,
		int connectedAgents,
		int totalAccounts,
		int pendingChallenges,
		string expectedStatus)
	{
		OverallHealth overall = SystemStatusService.DeriveOverall(
			dbAvailable, lastPassFailed, connectedAgents, totalAccounts, pendingChallenges);

		Assert.Equal(expectedStatus, overall.Status);
		if (expectedStatus == "healthy")
		{
			Assert.Empty(overall.Reasons);
		}
		else
		{
			Assert.NotEmpty(overall.Reasons);
		}
	}

	[Fact]
	public void DeriveOverall_NoAgentReasonRequiresAccounts()
	{
		// Accounts > 0 with zero agents must produce the no-agent reason;
		// with zero accounts it must not (short-circuit arm coverage).
		OverallHealth withAccounts = SystemStatusService.DeriveOverall(dbAvailable: true, lastPassFailed: false, connectedAgents: 0, totalAccounts: 3, pendingChallenges: 0);
		Assert.Contains(withAccounts.Reasons, r => r.Contains("agent"));

		OverallHealth withoutAccounts = SystemStatusService.DeriveOverall(dbAvailable: true, lastPassFailed: false, connectedAgents: 0, totalAccounts: 0, pendingChallenges: 0);
		Assert.DoesNotContain(withoutAccounts.Reasons, r => r.Contains("agent"));
	}

	[Theory]
	[InlineData(AccountDesiredState.Offline, null, true)]
	[InlineData(AccountDesiredState.Offline, "connected", false)]
	[InlineData(AccountDesiredState.Offline, "disconnected", true)]
	[InlineData(AccountDesiredState.Offline, "unknown", false)]
	[InlineData(AccountDesiredState.Online, null, false)]
	[InlineData(AccountDesiredState.Online, "connected", true)]
	[InlineData(AccountDesiredState.Online, "disconnected", false)]
	[InlineData(AccountDesiredState.Idle, "connected", true)]
	[InlineData(AccountDesiredState.Farm, "connected", true)]
	[InlineData(AccountDesiredState.Boost, "connected", true)]
	[InlineData(AccountDesiredState.Online, "unknown", false)]
	[InlineData(AccountDesiredState.Online, "", false)]
	[InlineData(AccountDesiredState.Online, "  ", false)]
	public void IsSessionConsistent_CoversAllDesiredStates(AccountDesiredState desired, string? sessionState, bool expected)
	{
		Assert.Equal(expected, SystemStatusService.IsSessionConsistent(desired, sessionState));
	}

	[Theory]
	[InlineData("connected", false)]
	[InlineData("CONNECTED", false)]
	[InlineData("disconnected", true)]
	[InlineData("session.DISCONNECTED", true)]
	[InlineData("offline", true)]
	[InlineData("went Offline", true)]
	[InlineData("logged_out", true)]
	[InlineData("logged-out", true)]
	[InlineData("none", true)]
	[InlineData("NONE", true)]
	[InlineData("unknown", false)]
	[InlineData("running", false)]
	public void IsDisconnectedState_MatchesOfflineVocabulary(string state, bool expected)
	{
		Assert.Equal(expected, SystemStatusService.IsDisconnectedState(state));
	}

	private sealed class TestFactory : WebApplicationFactory<Program>
	{
		public IJobStore? JobStore { get; set; }

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => JobStore ?? new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}

	/// <summary>Minimal connected-socket stand-in for agent registrations.</summary>
	private sealed class SystemStatusNoopWebSocket : System.Net.WebSockets.WebSocket
	{
		public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
		public override string? SubProtocol => null;

		public override void Abort() { }
		public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose() { }
		public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => new TaskCompletionSource<System.Net.WebSockets.WebSocketReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
		public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	/// <summary>Job store fake with configurable counts failure and probe jobs for the aggregation tests.</summary>
	private sealed class StatusProbeStore : IJobStore
	{
		public Exception? CountFailure { get; set; }
		public Dictionary<JobTaskStatus, int> Counts { get; } = [];
		public List<Job> Jobs { get; } = [];
		public Dictionary<string, IReadOnlyList<JobTask>> TasksByJob { get; } = [];

		public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken)
		{
			if (CountFailure is not null)
			{
				return Task.FromException<IReadOnlyDictionary<JobTaskStatus, int>>(CountFailure);
			}
			return Task.FromResult<IReadOnlyDictionary<JobTaskStatus, int>>(new Dictionary<JobTaskStatus, int>(Counts));
		}

		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<Job>>(Jobs);

		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
			=> Task.FromResult(new JobWithTasks(Jobs.Single(j => j.Id == jobId), TasksByJob[jobId]));

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobTask>>([]);
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => Task.FromResult<Job?>(null);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => Task.FromResult<JobTask?>(null);
		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
