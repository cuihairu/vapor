using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// Full-chain restart resilience (roadmap §40.8-⑩): killing the control plane
/// mid-flight must lose no work — the restarted process rehydrates the same
/// database, the fault requeues the in-flight task and the chain finishes it
/// under a fresh attempt. Faults are in-memory only, so the restart also
/// disarms the injector; audit history survives the restart.
/// </summary>
[Collection("E2E")]
public sealed class RestartResilienceE2ETests
{
	private readonly E2EStack _stack;

	public RestartResilienceE2ETests(E2EStack stack)
	{
		_stack = stack;
	}

	[Fact]
	public async Task ControlPlaneRestart_MidFlightJob_RequeuesOrphanAndFinishes()
	{
		// 0. A dispatch-plane delay fault gives the scenario a deterministic
		//    in-flight window: echo would otherwise finish faster than any
		//    observer can see it. The fault requeues the claimed task with a
		//    pushed next-attempt time instead of blocking, so the job sits
		//    claimed (attempt 1) until well after the restart below.
		await ArmFaultAsync(new
		{
			kind = "task-dispatch",
			mode = "delay",
			action = "echo",
			delayMs = 15000,
			budget = 1,
		});

		// 1. An echo job goes in flight: claimed once, then parked by the fault.
		JsonElement created = await _stack.CreateJobAsync(new
		{
			action = "echo",
			region = E2EStack.AgentRegion,
			targets = new[] { "restart-target" },
			payload = new Dictionary<string, object?>
			{
				["refreshToken"] = "dummy-e2e-token",
				["message"] = "survive-the-restart",
			},
		});
		string jobId = created.GetProperty("job").GetProperty("id").GetString()
			?? throw new InvalidOperationException("job id missing from create response");

		await WaitForTaskAttemptAsync(jobId, minAttempt: 1);

		// 2. Kill the whole chain mid-flight and boot it again on the same databases.
		await _stack.RestartControlPlaneAsync();

		// 3. The fault injector is in-memory only: the restarted control plane
		//    must come up disarmed so the parked task can proceed.
		using (HttpRequestMessage faults = NewAdminRequest(HttpMethod.Get, "/v1/faults"))
		using (HttpResponseMessage faultsResponse = await _stack.Http.SendAsync(faults))
		{
			faultsResponse.EnsureSuccessStatusCode();
			Assert.Equal("[]", (await faultsResponse.Content.ReadAsStringAsync()).Trim());
		}

		// 4. The restarted control plane rehydrates from disk: the parked task is
		//    dispatched again once its delay elapses (the attempt counter proves
		//    the pre-kill claim persisted) and the echo output arrives anyway.
		var (status, task) = await _stack.WaitForJobCompletionAsync(
			jobId, terminalStatuses: ["Finished"], timeout: TimeSpan.FromSeconds(120));
		Assert.Equal("finished", status, ignoreCase: true);
		Assert.True(task.GetProperty("attempt").GetInt32() >= 2,
			"the mid-flight task must be re-dispatched after the restart (attempt >= 2)");
		Assert.Equal("survive-the-restart",
			task.GetProperty("output").GetProperty("echo").GetProperty("message").GetString());

		// 5. Audit history written before the restart is still readable afterwards.
		JsonElement logs = await _stack.GetAuditLogsAsync("job.created");
		bool found = logs.GetProperty("logs").EnumerateArray().Any(entry =>
			entry.TryGetProperty("jobId", out var entryJobId) &&
			entryJobId.GetString() == jobId);
		Assert.True(found, "audit history must survive a full-chain restart");
	}

	private async Task ArmFaultAsync(object fault)
	{
		using HttpRequestMessage request = NewAdminRequest(HttpMethod.Post, "/v1/faults");
		request.Content = JsonContent.Create(fault);
		using HttpResponseMessage response = await _stack.Http.SendAsync(request);

		if (!response.IsSuccessStatusCode)
		{
			string body = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"POST /v1/faults failed ({response.StatusCode}): {body}");
		}
	}

	private HttpRequestMessage NewAdminRequest(HttpMethod method, string path)
	{
		var request = new HttpRequestMessage(method, path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2EStack.AdminApiKey);
		return request;
	}

	private async Task WaitForTaskAttemptAsync(string jobId, int minAttempt)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
		int attempt = 0;
		while (DateTimeOffset.UtcNow < deadline)
		{
			var (_, _, _, taskAttempt) = await _stack.GetJobStateAsync(jobId);
			attempt = taskAttempt;
			if (taskAttempt >= minAttempt)
			{
				return;
			}
			await Task.Delay(250);
		}

		throw new TimeoutException($"job {jobId} never reached task attempt >= {minAttempt} (last attempt: {attempt})");
	}
}
