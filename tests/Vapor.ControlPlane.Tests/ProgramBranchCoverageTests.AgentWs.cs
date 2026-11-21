using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Agent WebSocket lifecycle branch tests: handshake policy, full lifecycle, teardown.</summary>
public sealed partial class ProgramBranchCoverageTests
{
	// ── agent WebSocket lifecycle ─────────────────────────────────────────────
	[Fact]
	public async Task AgentWs_PlainGetWithoutUpgrade_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/agent/ws?agentId=agent-9&region=us-east");

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("websocket required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AgentWs_MissingQueryParameters_Returns400()
	{
		await using BranchFactory factory = new();
		_ = factory.CreateClient();

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?authorization=agent-token");

		await Assert.ThrowsAnyAsync<Exception>(() => wsClient.ConnectAsync(uri, CancellationToken.None));
	}

	[Fact]
	public async Task AgentWs_RejectsAgentIdMismatchingBoundKey()
	{
		await using BranchFactory factory = new();
		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-10&region=us-east&authorization=agent-token");

		InvalidOperationException error = await Assert.ThrowsAnyAsync<InvalidOperationException>(
			() => wsClient.ConnectAsync(uri, CancellationToken.None));
		Assert.Contains("403", error.Message);
	}

	[Fact]
	public async Task AgentWs_NonHelloFirstFrame_ClosesWithPolicyViolation()
	{
		await using BranchFactory factory = new();
		_ = factory.CreateClient();

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-9&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		// First frame is not a hello: the server closes with PolicyViolation.
		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat("t-1", 0, DateTimeOffset.UtcNow)), CancellationToken.None);
		byte[] buffer = new byte[1024];
		var segment = new ArraySegment<byte>(buffer);
		System.Net.WebSockets.WebSocketReceiveResult result = await ws.ReceiveAsync(segment, CancellationToken.None);

		Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Close, result.MessageType);
		Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, result.CloseStatus);

		// Complete the close handshake so the server handler runs to completion.
		await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
	}

	[Fact]
	public async Task AgentWs_FullLifecycle_RegistersAcceptsResultsAndUnregisters()
	{
		var store = new SqliteJobStore(":memory:");
		await using BranchFactory factory = new() { JobStore = store };
		using var adminClient = factory.CreateClient();
		adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-9&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		await WebSocketJson.Send(ws, new WSMessage("hello", new AgentHello("agent-9", "us-east", null, null), null, null), CancellationToken.None);

		// Give the server handler a moment to process the hello and register the agent.
		await Task.Delay(200);

		// The connection shows up in the registry…
		using HttpResponseMessage status = await adminClient.GetAsync("/v1/agents/status");
		string statusBody = await status.Content.ReadAsStringAsync();
		Assert.Contains("agent-9", statusBody);

		// …heartbeat for an unknown task is swallowed…
		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat("nope", 0, DateTimeOffset.UtcNow)), CancellationToken.None);

		// …a claimed task accepts heartbeats and results over the tunnel…
		using HttpResponseMessage created = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		JobTask claimed = (await store.ClaimNextQueuedTask("us-east", CancellationToken.None))!;
		Assert.Equal(JobTaskStatus.Running, claimed.Status);

		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat(claimed.Id, claimed.Attempt, DateTimeOffset.UtcNow)), CancellationToken.None);
		await WebSocketJson.Send(ws, new WSMessage("task_result", null, null,
			new TaskResult(claimed.Id, Success: true, Error: null, Output: new Dictionary<string, object?> { ["ok"] = true }, DateTimeOffset.UtcNow, claimed.Attempt)), CancellationToken.None);

		// …a sensitive action's result is audit-logged…
		// (the sensitive-action predicate matches CamelCase action names; dispatching
		// one verbatim via POST /v1/jobs drives the audited result path).
		using HttpResponseMessage sensitiveJob = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "GetInventory", region = "us-east", targets = new[] { "alice" } });
		string sensitiveJobId;
		using (JsonDocument doc = JsonDocument.Parse(await sensitiveJob.Content.ReadAsStringAsync()))
		{
			sensitiveJobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		JobTask sensitiveClaimed = (await store.ClaimNextQueuedTask("us-east", CancellationToken.None))!;
		await WebSocketJson.Send(ws, new WSMessage("task_result", null, null,
			new TaskResult(sensitiveClaimed.Id, Success: true, Error: null, Output: new Dictionary<string, object?> { ["items"] = 0 }, DateTimeOffset.UtcNow, sensitiveClaimed.Attempt)), CancellationToken.None);

		// …a result for an unknown task is swallowed…
		await WebSocketJson.Send(ws, new WSMessage("task_result", null, null,
			new TaskResult("nope", Success: true, Error: null, Output: null, DateTimeOffset.UtcNow)), CancellationToken.None);

		// …a claimed (running) task is visible via the jobs endpoint and its cancel
		// is pushed to the connected agent…
		using HttpResponseMessage runningJob = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string runningJobId;
		using (JsonDocument doc = JsonDocument.Parse(await runningJob.Content.ReadAsStringAsync()))
		{
			runningJobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		JobTask runningClaimed = (await store.ClaimNextQueuedTask("us-east", CancellationToken.None))!;
		Assert.Equal(JobTaskStatus.Running, runningClaimed.Status);

		using HttpResponseMessage jobView = await adminClient.GetAsync($"/v1/jobs/{runningJobId}");
		Assert.Equal(HttpStatusCode.OK, jobView.StatusCode);

		using HttpResponseMessage cancelRunning = await adminClient.PostAsync($"/v1/jobs/{runningJobId}/cancel", content: null);
		Assert.Equal(HttpStatusCode.OK, cancelRunning.StatusCode);

		// …canceling a queued job enqueues the cancel on the connected agent…
		using HttpResponseMessage queuedJob = await adminClient.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string queuedJobId;
		using (JsonDocument doc = JsonDocument.Parse(await queuedJob.Content.ReadAsStringAsync()))
		{
			queuedJobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using HttpResponseMessage cancel = await adminClient.PostAsync($"/v1/jobs/{queuedJobId}/cancel", content: null);
		Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

		// Give the read loop a moment to process the pipeline above, then check outcomes.
		JobWithTasks finished = await store.GetJob(jobId, CancellationToken.None);
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (finished.Tasks.Single(t => t.Id == claimed.Id).Status != JobTaskStatus.Finished && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(25);
			finished = await store.GetJob(jobId, CancellationToken.None);
		}

		Assert.Equal(JobTaskStatus.Finished, finished.Tasks.Single(t => t.Id == claimed.Id).Status);

		// The sensitive action's result was audit-logged with the job id and target
		// (written right after the result lands, so poll briefly).
		AuditEntry? sensitiveEntry = null;
		deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			IReadOnlyList<AuditEntry> sensitiveAudits = await factory.AuditStore!.QueryAsync(new AuditQuery(Action: "task.result.reported"), CancellationToken.None);
			if (sensitiveAudits.Count > 0)
			{
				sensitiveEntry = Assert.Single(sensitiveAudits);
				break;
			}

			await Task.Delay(25);
		}

		Assert.NotNull(sensitiveEntry);
		Assert.Equal("alice", sensitiveEntry.AccountName);
		Assert.Equal(sensitiveJobId, sensitiveEntry.JobId);

		// Closing the socket unregisters the agent. (The server's read loop treats a
		// close frame as an IOException and tears the handler down without completing
		// the close handshake, so abort the client side instead of CloseAsync.)
		ws.Abort();
		deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage final = await adminClient.GetAsync("/v1/agents/status");
			string body = await final.Content.ReadAsStringAsync();
			if (!body.Contains("agent-9"))
			{
				break;
			}

			await Task.Delay(25);
		}

		using HttpResponseMessage afterClose = await adminClient.GetAsync("/v1/agents/status");
		Assert.DoesNotContain("agent-9", await afterClose.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AgentWs_ClientCloseFrame_TearsDownHandlerAndUnregisters()
	{
		// A close frame surfaces as an IOException from WebSocketJson.Receive: the
		// handler must still run its exception cleanup (unregister + disconnected
		// event) even though the server never completes the close handshake.
		await using BranchFactory factory = new();
		using var adminClient = factory.CreateClient();
		adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-9&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		await WebSocketJson.Send(ws, new WSMessage("hello", new AgentHello("agent-9", "us-east", null, null), null, null), CancellationToken.None);
		await Task.Delay(200);

		// CloseOutputAsync sends the close frame without waiting for the server's
		// half of the handshake (which the server intentionally never completes).
		await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage status = await adminClient.GetAsync("/v1/agents/status");
			if (!(await status.Content.ReadAsStringAsync()).Contains("agent-9"))
			{
				break;
			}

			await Task.Delay(25);
		}

		using HttpResponseMessage final = await adminClient.GetAsync("/v1/agents/status");
		Assert.DoesNotContain("agent-9", await final.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AgentWs_RequestAbortedDuringHeartbeat_UnregistersThroughLoopExit()
	{
		// The read loop's own condition (RequestAborted cancelled) is the graceful
		// exit: the heartbeat deliberately outlives the abort and returns normally
		// (SlowHeartbeatStore ignores the token), so the condition re-evaluates to
		// false and teardown runs through the regular loop end — not the
		// read-exception path. The abort is injected by swapping the endpoint's
		// RequestAborted for a test-controlled linked token: TestServer's
		// client-side Abort races the socket teardown and can surface as a read
		// exception instead of a condition exit.
		SlowHeartbeatStore slowStore = new(new SqliteJobStore(":memory:"));
		await using BranchFactory factory = new() { JobStore = slowStore };
		using var adminClient = factory.CreateClient();
		adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		var wsClient = factory.Server.CreateWebSocketClient();
		Uri uri = new(factory.Server.BaseAddress, "v1/agent/ws?agentId=agent-9&region=us-east&authorization=agent-token");
		using System.Net.WebSockets.WebSocket ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

		await WebSocketJson.Send(ws, new WSMessage("hello", new AgentHello("agent-9", "us-east", null, null), null, null), CancellationToken.None);
		await Task.Delay(200);
		await WebSocketJson.Send(ws, new WSMessage("task_heartbeat", null, null, null, new TaskHeartbeat("t-1", 0, DateTimeOffset.UtcNow)), CancellationToken.None);

		// Cancel only after the loop has consumed the heartbeat message and is
		// inside the (deliberately slow, token-ignoring) heartbeat: cancelling
		// earlier could still land inside Receive, whose OCE would take the
		// exception path. From the signal on, the graceful exit is the only
		// possible path — the heartbeat returns normally and the healthy socket
		// means Receive is never entered again.
		await slowStore.HeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.NotNull(factory.RequestAbortControl);
		factory.RequestAbortControl.Cancel();

		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage status = await adminClient.GetAsync("/v1/agents/status");
			if (!(await status.Content.ReadAsStringAsync()).Contains("agent-9"))
			{
				break;
			}

			await Task.Delay(25);
		}

		using HttpResponseMessage final = await adminClient.GetAsync("/v1/agents/status");
		Assert.DoesNotContain("agent-9", await final.Content.ReadAsStringAsync());
	}

}

/// <summary>
/// Factory over the real composition root with store/broker/audit overrides and
/// hosted services removed (background dispatchers would race the in-test agent
/// fakes). Exposes the overrides for assertions.
/// </summary>
/// <summary>
/// Wraps a store so heartbeat handling takes long enough for a test to abort the
/// connection mid-message, forcing the agent tunnel's read loop to re-evaluate its
/// condition after the disconnect instead of dying on a receive exception.
/// </summary>
internal sealed class SlowHeartbeatStore : IJobStore
{
	private readonly IJobStore _inner;

	/// <summary>
	/// Completed when HeartbeatTask is entered, so a test can cancel the request
	/// only after the read loop has consumed the heartbeat message — cancelling
	/// earlier could still land inside Receive and take the exception path.
	/// </summary>
	public TaskCompletionSource HeartbeatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public SlowHeartbeatStore(IJobStore inner)
	{
		_inner = inner;
	}

	public async Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken)
	{
		// Both the delay and the inner call must ignore the token: the whole point
		// is to return normally after the abort so the read loop re-evaluates its
		// condition (RequestAborted) and exits through the regular loop end — the
		// graceful teardown path, not the exception path.
		HeartbeatEntered.TrySetResult();
		await Task.Delay(600).ConfigureAwait(false);
		return await _inner.HeartbeatTask(taskId, attempt, CancellationToken.None).ConfigureAwait(false);
	}

	public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => _inner.CreateJob(request, cancellationToken);
	public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => _inner.GetJob(jobId, cancellationToken);
	public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => _inner.ListJobs(limit, account, cancellationToken);
	public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => _inner.ListRecentTasksForTarget(target, limit, cancellationToken);
	public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => _inner.GetTaskStatusCounts(cancellationToken);
	public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => _inner.ClaimNextQueuedTask(region, cancellationToken);
	public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => _inner.ListDueScheduledJobs(now, limit, cancellationToken);
	public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => _inner.HasActiveChildJob(templateJobId, cancellationToken);
	public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => _inner.TriggerScheduledJob(templateJobId, nextRunAt, extraMeta, cancellationToken);
	public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => _inner.AdvanceSchedule(templateJobId, nextRunAt, cancellationToken);
	public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => _inner.RequeueTask(taskId, retryDelay, cancellationToken);
	public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => _inner.RequeueStaleRunningTasks(taskLease, cancellationToken);
	public Task MarkTaskClaimedBy(string taskId, int attempt, string agentId, CancellationToken cancellationToken) => _inner.MarkTaskClaimedBy(taskId, attempt, agentId, cancellationToken);
	public Task<Dictionary<string, int>> CountRunningTasksByAgent(CancellationToken cancellationToken) => _inner.CountRunningTasksByAgent(cancellationToken);
	public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => _inner.SetTaskResult(result, cancellationToken);
	public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => _inner.FailRunningTask(taskId, error, cancellationToken);
	public Task<(int Jobs, int Tasks)> PurgeSecretPayloadsAsync(bool dryRun, string? backupPath, CancellationToken cancellationToken) => _inner.PurgeSecretPayloadsAsync(dryRun, backupPath, cancellationToken);
	public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => _inner.CancelJob(jobId, cancellationToken);
}
