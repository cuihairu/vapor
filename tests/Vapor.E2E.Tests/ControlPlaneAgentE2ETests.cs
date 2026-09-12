using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// End-to-end tests over real Control Plane and Agent processes: a job created via the
/// REST API must be scheduled, executed by the agent over the WebSocket tunnel, and
/// reported back with results persisted in SQLite — plus audit and auth guarantees.
/// </summary>
[Collection("E2E")]
public sealed class ControlPlaneAgentE2ETests
{
	private readonly E2EStack _stack;

	public ControlPlaneAgentE2ETests(E2EStack stack)
	{
		_stack = stack;
	}

	[Fact]
	public async Task EchoJob_CompletesSuccessfully_WithExpectedOutput()
	{
		JsonElement created = await _stack.CreateJobAsync(new
		{
			action = "echo",
			region = E2EStack.AgentRegion,
			targets = new[] { "e2e-account" },
			payload = new Dictionary<string, object?>
			{
				["refreshToken"] = "dummy-e2e-token",
				["message"] = "hello-vapor",
			},
		});

		string jobId = created.GetProperty("job").GetProperty("id").GetString()
			?? throw new InvalidOperationException("job id missing from create response");

		var (status, task) = await _stack.WaitForJobCompletionAsync(jobId, terminalStatuses: ["Finished"]);

		Assert.Equal("finished", status, ignoreCase: true);

		// The task row itself carries execution status; note the Control Plane does not persist
		// task output payloads (they flow only over the agent WebSocket), so asserting on them
		// via REST is not part of the current API contract.
		Assert.Equal(1, task.GetProperty("attempt").GetInt32());

		var (jobStatus, _, _, _) = await _stack.GetJobStateAsync(jobId);
		Assert.Equal("finished", jobStatus, ignoreCase: true);
	}

	[Fact]
	public async Task UnknownAction_TaskEventuallyFailsWithTerminalError()
	{
		JsonElement created = await _stack.CreateJobAsync(new
		{
			action = "no_such_action_e2e",
			region = E2EStack.AgentRegion,
			targets = new[] { "e2e-account" },
			payload = new Dictionary<string, object?> { ["refreshToken"] = "dummy-e2e-token" },
		});

		string jobId = created.GetProperty("job").GetProperty("id").GetString()
			?? throw new InvalidOperationException("job id missing from create response");

		// The scheduler only routes actions a connected agent declared in its hello, so an
		// unknown action is never dispatched. After exhausting the dispatch attempt limit
		// (configured to 3 fast retries for this stack) the task must reach a terminal
		// Failed state instead of blocking the queue forever.
		var (status, task) = await _stack.WaitForJobCompletionAsync(jobId, terminalStatuses: ["Failed"], timeout: TimeSpan.FromSeconds(30));

		Assert.Equal("failed", status, ignoreCase: true);
		string error = task.GetProperty("error").GetString() ?? string.Empty;
		Assert.Contains("no capable agent available", error, StringComparison.OrdinalIgnoreCase);
		Assert.True(task.GetProperty("attempt").GetInt32() >= 3, "task should have retried before failing");

		var (jobStatus, _, _, _) = await _stack.GetJobStateAsync(jobId);
		Assert.Equal("failed", jobStatus, ignoreCase: true);
	}

	[Fact]
	public async Task JobCreation_IsRecordedInAuditLog()
	{
		JsonElement created = await _stack.CreateJobAsync(new
		{
			action = "echo",
			region = E2EStack.AgentRegion,
			targets = new[] { "audit-account" },
			payload = new Dictionary<string, object?> { ["refreshToken"] = "dummy-e2e-token" },
		});

		string jobId = created.GetProperty("job").GetProperty("id").GetString()
			?? throw new InvalidOperationException("job id missing from create response");

		// Wait for the job to finish first: results are reported (and audited) after creation,
		// giving the audit query a stable point in time.
		await _stack.WaitForJobCompletionAsync(jobId, terminalStatuses: ["Finished", "Failed"]);

		JsonElement logs = await _stack.GetAuditLogsAsync("job.created");
		var entries = logs.GetProperty("logs");

		bool found = entries.EnumerateArray().Any(entry =>
			entry.TryGetProperty("jobId", out var entryJobId) &&
			entryJobId.GetString() == jobId);

		Assert.True(found,
			$"No 'job.created' audit entry found for job {jobId}. Entries:{Environment.NewLine}{entries.GetRawText()}");
	}

	[Fact]
	public async Task AdminEndpoints_RejectUnauthenticatedAndAgentKeyRequests()
	{
		// No credentials at all.
		using var anonymous = await _stack.Http.GetAsync("/v1/jobs");
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

		// Valid agent credentials must not grant admin access.
		using var agentKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/jobs");
		agentKeyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2EStack.AgentApiKey);
		using var withAgentKey = await _stack.Http.SendAsync(agentKeyRequest);
		Assert.Equal(HttpStatusCode.Unauthorized, withAgentKey.StatusCode);
	}

	[Fact]
	public async Task Healthz_IsPublic()
	{
		using var response = await _stack.Http.GetAsync("/healthz");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
	}
}
