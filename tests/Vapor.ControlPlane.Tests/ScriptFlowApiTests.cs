using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
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

[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed class ScriptFlowApiTests
{
	[Fact]
	public async Task FlowEndpoints_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		var stepBody = new { name = "x", steps = new[] { new { scriptId = "s", agentId = "a" } } };

		using HttpResponseMessage list = await client.GetAsync("/v1/script-flows");
		using HttpResponseMessage get = await client.GetAsync("/v1/script-flows/abc");
		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/script-flows", stepBody);
		using HttpResponseMessage update = await client.PutAsJsonAsync("/v1/script-flows/abc", stepBody);
		using HttpResponseMessage delete = await client.DeleteAsync("/v1/script-flows/abc");
		using HttpResponseMessage run = await client.PostAsync("/v1/script-flows/abc/run", content: null);
		using HttpResponseMessage runs = await client.GetAsync("/v1/script-flows/abc/runs");
		using HttpResponseMessage runDetail = await client.GetAsync("/v1/script-flows/runs/abc");

		Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, run.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, runs.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, runDetail.StatusCode);
	}

	[Fact]
	public async Task Flow_Create_Get_List_RoundTrip()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/script-flows", new
		{
			name = "日维护",
			description = "清理与巡检",
			steps = new object[]
			{
				new { scriptId = "s-clean", agentId = "agent-a" },
				new { scriptId = "s-check", agentId = "agent-b", onFailure = "continue" }
			}
		});
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		using var createdDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createdDoc.RootElement.GetProperty("id").GetString()!;
		Assert.Equal("日维护", createdDoc.RootElement.GetProperty("name").GetString());
		Assert.Equal("清理与巡检", createdDoc.RootElement.GetProperty("description").GetString());
		var steps = createdDoc.RootElement.GetProperty("steps");
		Assert.Equal(2, steps.GetArrayLength());
		// Omitted onFailure defaults to stop; given values survive untouched.
		Assert.Equal("stop", steps[0].GetProperty("onFailure").GetString());
		Assert.Equal("continue", steps[1].GetProperty("onFailure").GetString());
		Assert.True(createdDoc.RootElement.GetProperty("createdAtMs").GetInt64() > 0);

		using HttpResponseMessage get = await client.GetAsync($"/v1/script-flows/{id}");
		Assert.Equal(HttpStatusCode.OK, get.StatusCode);
		using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.Equal(id, getDoc.RootElement.GetProperty("id").GetString());
		Assert.Equal(2, getDoc.RootElement.GetProperty("steps").GetArrayLength());

		using HttpResponseMessage list = await client.GetAsync("/v1/script-flows");
		Assert.Equal(HttpStatusCode.OK, list.StatusCode);
		using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
		Assert.Single(listDoc.RootElement.GetProperty("flows").EnumerateArray());
		Assert.Equal(id, listDoc.RootElement.GetProperty("flows")[0].GetProperty("id").GetString());
	}

	[Fact]
	public async Task Flow_Create_ValidationArms()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage blankName = await client.PostAsJsonAsync(
			"/v1/script-flows", new { name = "  ", steps = new[] { new { scriptId = "s", agentId = "a" } } });
		Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);
		using var blankNameDoc = JsonDocument.Parse(await blankName.Content.ReadAsStringAsync());
		Assert.Equal("name is required", blankNameDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage noSteps = await client.PostAsJsonAsync("/v1/script-flows", new { name = "f" });
		Assert.Equal(HttpStatusCode.BadRequest, noSteps.StatusCode);
		using var noStepsDoc = JsonDocument.Parse(await noSteps.Content.ReadAsStringAsync());
		Assert.Equal("at least one step is required", noStepsDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage tooMany = await client.PostAsJsonAsync(
			"/v1/script-flows",
			new
			{
				name = "f",
				steps = Enumerable.Range(0, 51).Select(i => new { scriptId = $"s{i}", agentId = "a" })
			});
		Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
		using var tooManyDoc = JsonDocument.Parse(await tooMany.Content.ReadAsStringAsync());
		Assert.Equal("a flow supports at most 50 steps", tooManyDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage blankScript = await client.PostAsJsonAsync(
			"/v1/script-flows", new { name = "f", steps = new[] { new { scriptId = "  ", agentId = "a" } } });
		Assert.Equal(HttpStatusCode.BadRequest, blankScript.StatusCode);
		using var blankScriptDoc = JsonDocument.Parse(await blankScript.Content.ReadAsStringAsync());
		Assert.Equal("every step needs a scriptId", blankScriptDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage blankAgent = await client.PostAsJsonAsync(
			"/v1/script-flows", new { name = "f", steps = new[] { new { scriptId = "s", agentId = "" } } });
		Assert.Equal(HttpStatusCode.BadRequest, blankAgent.StatusCode);
		using var blankAgentDoc = JsonDocument.Parse(await blankAgent.Content.ReadAsStringAsync());
		Assert.Equal("every step needs an agentId", blankAgentDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage badPolicy = await client.PostAsJsonAsync(
			"/v1/script-flows", new { name = "f", steps = new[] { new { scriptId = "s", agentId = "a", onFailure = "retry" } } });
		Assert.Equal(HttpStatusCode.BadRequest, badPolicy.StatusCode);
		using var badPolicyDoc = JsonDocument.Parse(await badPolicy.Content.ReadAsStringAsync());
		Assert.Equal("onFailure must be 'stop' or 'continue'", badPolicyDoc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Flow_Create_NormalizesOnFailure()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/script-flows", new
		{
			name = "f",
			steps = new[] { new { scriptId = "s", agentId = "a", onFailure = "  CONTINUE " } }
		});
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("continue", doc.RootElement.GetProperty("steps")[0].GetProperty("onFailure").GetString());
	}

	[Fact]
	public async Task Flow_Put_UnknownId_Returns404_BeforeValidation()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PutAsJsonAsync("/v1/script-flows/missing", new { name = "x", steps = new[] { new { scriptId = "s", agentId = "a" } } });
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Flow_Put_ReplacesAndPreservesCreatedAt()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync(
			"/v1/script-flows", new { name = "v1", steps = new[] { new { scriptId = "s1", agentId = "a1" } } });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createDoc.RootElement.GetProperty("id").GetString()!;
		long createdAt = createDoc.RootElement.GetProperty("createdAtMs").GetInt64();

		using HttpResponseMessage update = await client.PutAsJsonAsync($"/v1/script-flows/{id}", new
		{
			name = "v2",
			description = "renamed",
			steps = new object[]
			{
				new { scriptId = "s2", agentId = "a2", onFailure = "continue" },
				new { scriptId = "s3", agentId = "a3" }
			}
		});
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);
		using var updateDoc = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
		Assert.Equal("v2", updateDoc.RootElement.GetProperty("name").GetString());
		Assert.Equal("renamed", updateDoc.RootElement.GetProperty("description").GetString());
		Assert.Equal(2, updateDoc.RootElement.GetProperty("steps").GetArrayLength());
		Assert.Equal(createdAt, updateDoc.RootElement.GetProperty("createdAtMs").GetInt64());
		Assert.True(updateDoc.RootElement.GetProperty("updatedAtMs").GetInt64() >= createdAt);
	}

	[Fact]
	public async Task Flow_Put_InvalidBody_Returns400_AndLeavesFlowUntouched()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		string flowId = await CreateFlow(client, "keep-me");

		using HttpResponseMessage update = await client.PutAsJsonAsync(
			$"/v1/script-flows/{flowId}", new { name = "renamed", steps = Array.Empty<object>() });
		Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
		using var errorDoc = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
		Assert.Equal("at least one step is required", errorDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage get = await client.GetAsync($"/v1/script-flows/{flowId}");
		using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.Equal("keep-me", getDoc.RootElement.GetProperty("name").GetString());
	}

	[Fact]
	public async Task Flow_Delete_ThenGet404_AndUnknownDelete404()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		string flowId = await CreateFlow(client, "doomed");

		using HttpResponseMessage delete = await client.DeleteAsync($"/v1/script-flows/{flowId}");
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

		using HttpResponseMessage get = await client.GetAsync($"/v1/script-flows/{flowId}");
		Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

		using HttpResponseMessage deleteAgain = await client.DeleteAsync($"/v1/script-flows/{flowId}");
		Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
	}

	[Fact]
	public async Task Flow_Mutations_AreAudited()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		string flowId = await CreateFlow(client, "audited");
		await client.PutAsJsonAsync($"/v1/script-flows/{flowId}", new { name = "audited", steps = new[] { new { scriptId = "s", agentId = "a" } } });
		await client.DeleteAsync($"/v1/script-flows/{flowId}");

		IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
		IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 100), CancellationToken.None);

		var actions = entries.Select(e => e.Action).ToList();
		Assert.Contains("flow_created", actions);
		Assert.Contains("flow_updated", actions);
		Assert.Contains("flow_deleted", actions);

		AuditEntry created = entries.Single(e => e.Action == "flow_created");
		Assert.False(string.IsNullOrWhiteSpace(created.Actor));
		// Details round-trip through JSON, so scalar values come back as JsonElement.
		Assert.Equal(flowId, ((JsonElement)created.Details!["flowId"]!).GetString());
		Assert.Equal(1, ((JsonElement)created.Details!["steps"]!).GetInt32());
	}

	[Fact]
	public async Task Flow_Run_UnknownFlow_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsync("/v1/script-flows/missing/run", content: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("does not exist", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Flow_Run_PreFlight_MissingScript_Returns404_BeforeAnyDispatch()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/script-flows", new
		{
			name = "ghost-script",
			steps = new[] { new { scriptId = "no-such-script", agentId = "agent-a" } }
		});
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string flowId = createDoc.RootElement.GetProperty("id").GetString()!;

		using HttpResponseMessage response = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("step 0 references script 'no-such-script' which does not exist", doc.RootElement.GetProperty("error").GetString());

		// The run never started: no flow_run_started audit entry.
		IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
		IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 100), CancellationToken.None);
		Assert.DoesNotContain(entries, e => e.Action == "flow_run_started");
	}

	[Fact]
	public async Task Flow_Run_PreFlight_DisconnectedAgent_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);
		string scriptId = await CreateScript(client, "ready");
		string flowId = await CreateFlow(client, "ghost-agent", steps: new[] { new { scriptId, agentId = "ghost" } });

		using HttpResponseMessage response = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("step 0 targets agent 'ghost' which is not connected", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Flow_Run_AllStepsSucceed_ReturnsCompletedRun_AndAuditsEachStep()
	{
		await using var factory = CreateFactory(removeHosted: true);
		RegisterAgent(factory, "agent-exec");
		using var client = CreateAdminClient(factory);
		string scriptA = await CreateScript(client, "step-a");
		string scriptB = await CreateScript(client, "step-b");
		string flowId = await CreateFlow(client, "happy-path", steps: new[]
		{
			new { scriptId = scriptA, agentId = "agent-exec" },
			new { scriptId = scriptB, agentId = "agent-exec" }
		});

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToScriptExecTasksAsync(
			store,
			index => (true, new Dictionary<string, object?> { ["exitCode"] = 0, ["stdout"] = $"out-{index}" }, null),
			cts.Token));

		// The responder resolves each step at the first poll, so the default
		// wait window is never exhausted.
		using HttpResponseMessage response = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
		Assert.True(doc.RootElement.TryGetProperty("finishedAtMs", out _));
		var steps = doc.RootElement.GetProperty("steps");
		Assert.Equal(2, steps.GetArrayLength());
		Assert.Equal("succeeded", steps[0].GetProperty("status").GetString());
		Assert.Equal(0, steps[0].GetProperty("output").GetProperty("exitCode").GetInt32());
		Assert.Equal("out-0", steps[0].GetProperty("output").GetProperty("stdout").GetString());
		Assert.Equal("succeeded", steps[1].GetProperty("status").GetString());
		Assert.False(string.IsNullOrEmpty(steps[1].GetProperty("jobId").GetString()));

		string runId = doc.RootElement.GetProperty("id").GetString()!;

		IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
		IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 50), CancellationToken.None);
		Assert.Contains(entries, e => e.Action == "flow_run_started");
		Assert.Equal(2, entries.Count(e => e.Action == "flow_step_completed"));
		AuditEntry finished = entries.Single(e => e.Action == "flow_run_finished");
		Assert.Equal(runId, ((JsonElement)finished.Details!["runId"]!).GetString());
		Assert.Equal("completed", ((JsonElement)finished.Details!["status"]!).GetString());
		Assert.Equal(2, ((JsonElement)finished.Details!["succeeded"]!).GetInt32());

		// The run is persisted: the detail endpoint returns the same record.
		using HttpResponseMessage detail = await client.GetAsync($"/v1/script-flows/runs/{runId}");
		Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
		using var detailDoc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
		Assert.Equal("completed", detailDoc.RootElement.GetProperty("status").GetString());
		Assert.Equal(flowId, detailDoc.RootElement.GetProperty("flowId").GetString());
	}

	[Fact]
	public async Task Flow_Run_StopPolicy_FailureSkipsRemaining()
	{
		await using var factory = CreateFactory(removeHosted: true);
		RegisterAgent(factory, "agent-exec");
		using var client = CreateAdminClient(factory);
		string scriptA = await CreateScript(client, "fails");
		string flowId = await CreateFlow(client, "stop-on-fail", steps: new object[]
		{
			new { scriptId = scriptA, agentId = "agent-exec", onFailure = "stop" },
			new { scriptId = scriptA, agentId = "agent-exec" },
			new { scriptId = scriptA, agentId = "agent-exec" }
		});

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToScriptExecTasksAsync(
			store,
			index => (false, null, "script exited with code 3"),
			cts.Token));

		HttpResponseMessage response = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
		var steps = doc.RootElement.GetProperty("steps");
		Assert.Equal(3, steps.GetArrayLength());
		Assert.Equal("failed", steps[0].GetProperty("status").GetString());
		Assert.Contains("script exited with code 3", steps[0].GetProperty("error").GetString());
		Assert.Equal("skipped", steps[1].GetProperty("status").GetString());
		Assert.Equal("skipped", steps[2].GetProperty("status").GetString());

		IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
		IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 50), CancellationToken.None);
		Assert.Single(entries, e => e.Action == "flow_step_failed");
		AuditEntry finished = entries.Single(e => e.Action == "flow_run_finished");
		Assert.Equal("failed", ((JsonElement)finished.Details!["status"]!).GetString());
		Assert.Equal(2, ((JsonElement)finished.Details!["skipped"]!).GetInt32());
	}

	[Fact]
	public async Task Flow_Run_ContinuePolicy_CompletesWithFailures()
	{
		await using var factory = CreateFactory(removeHosted: true);
		RegisterAgent(factory, "agent-exec");
		using var client = CreateAdminClient(factory);
		string scriptA = await CreateScript(client, "fails");
		string scriptB = await CreateScript(client, "works");
		string flowId = await CreateFlow(client, "keep-going", steps: new object[]
		{
			new { scriptId = scriptA, agentId = "agent-exec", onFailure = "continue" },
			new { scriptId = scriptB, agentId = "agent-exec" }
		});

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToScriptExecTasksAsync(
			store,
			index => index == 0 ? (false, null, "script exited with code 3") : (true, new Dictionary<string, object?> { ["exitCode"] = 0 }, null),
			cts.Token));

		HttpResponseMessage response = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("completed_with_failures", doc.RootElement.GetProperty("status").GetString());
		var steps = doc.RootElement.GetProperty("steps");
		Assert.Equal("failed", steps[0].GetProperty("status").GetString());
		// The second step really ran despite the earlier failure.
		Assert.Equal("succeeded", steps[1].GetProperty("status").GetString());
	}

	[Fact]
	public async Task Flow_Run_PendingStep_EndsRunPending_AndSkipsRest()
	{
		await using var factory = CreateFactory();
		// The registered agent never claims anything: the step job stays queued
		// past the (shortened) wait window and the run ends unresolved.
		RegisterAgent(factory, "agent-silent");
		using var client = CreateAdminClient(factory);
		string scriptA = await CreateScript(client, "slow");
		string flowId = await CreateFlow(client, "stalls", steps: new[]
		{
			new { scriptId = scriptA, agentId = "agent-silent" },
			new { scriptId = scriptA, agentId = "agent-silent" }
		});

		TimeSpan savedWindow = AccountTaskRunner.WaitWindow;
		TimeSpan savedInterval = AccountTaskRunner.PollInterval;
		try
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(50);

			using HttpResponseMessage response = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
			var steps = doc.RootElement.GetProperty("steps");
			Assert.Equal(2, steps.GetArrayLength());
			Assert.Equal("pending", steps[0].GetProperty("status").GetString());
			Assert.False(string.IsNullOrEmpty(steps[0].GetProperty("jobId").GetString()));
			Assert.Equal("skipped", steps[1].GetProperty("status").GetString());

			IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
			IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 50), CancellationToken.None);
			Assert.Contains(entries, e => e.Action == "flow_step_pending");
			AuditEntry finished = entries.Single(e => e.Action == "flow_run_finished");
			Assert.Equal("pending", ((JsonElement)finished.Details!["status"]!).GetString());
			Assert.Equal(1, ((JsonElement)finished.Details!["pending"]!).GetInt32());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = savedWindow;
			AccountTaskRunner.PollInterval = savedInterval;
		}
	}

	[Fact]
	public async Task Flow_Runs_ListAndHistorySurviveFlowDelete()
	{
		await using var factory = CreateFactory();
		RegisterAgent(factory, "agent-silent");
		using var client = CreateAdminClient(factory);
		string scriptA = await CreateScript(client, "slow");
		string flowId = await CreateFlow(client, "history", steps: new[] { new { scriptId = scriptA, agentId = "agent-silent" } });

		TimeSpan savedWindow = AccountTaskRunner.WaitWindow;
		TimeSpan savedInterval = AccountTaskRunner.PollInterval;
		string runId;
		try
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(50);

			using HttpResponseMessage run = await client.PostAsync($"/v1/script-flows/{flowId}/run", content: null);
			using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
			runId = runDoc.RootElement.GetProperty("id").GetString()!;
		}
		finally
		{
			AccountTaskRunner.WaitWindow = savedWindow;
			AccountTaskRunner.PollInterval = savedInterval;
		}

		using HttpResponseMessage list = await client.GetAsync($"/v1/script-flows/{flowId}/runs");
		Assert.Equal(HttpStatusCode.OK, list.StatusCode);
		using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
		Assert.Single(listDoc.RootElement.GetProperty("runs").EnumerateArray());
		Assert.Equal(runId, listDoc.RootElement.GetProperty("runs")[0].GetProperty("id").GetString());

		// Runs are history: deleting the flow definition keeps them fetchable.
		await client.DeleteAsync($"/v1/script-flows/{flowId}");
		using HttpResponseMessage detail = await client.GetAsync($"/v1/script-flows/runs/{runId}");
		Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

		using HttpResponseMessage unknownRun = await client.GetAsync("/v1/script-flows/runs/missing");
		Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
	}

	/// <summary>Plays the agent side: claims every queued script_exec task in arrival order and reports the scripted outcome.</summary>
	private static async Task RespondToScriptExecTasksAsync(
		IJobStore store,
		Func<int, (bool Success, IReadOnlyDictionary<string, object?>? Output, string? Error)> outcomeForIndex,
		CancellationToken ct)
	{
		int index = 0;
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == "script_exec")
			{
				(bool success, IReadOnlyDictionary<string, object?>? output, string? error) = outcomeForIndex(index++);
				await store.SetTaskResult(
					new TaskResult(claimed.Id, success, success ? null : error, success ? output : null, DateTimeOffset.UtcNow),
					ct);
				continue;
			}

			await Task.Delay(25, ct);
		}
	}

	private static void RegisterAgent(TestFactory factory, string agentId)
	{
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		// The token source intentionally outlives this helper: the registry may
		// read it for the whole test lifetime, so it is never disposed here.
		CancellationTokenSource registrationCts = new();
		registry.Register(
			new AgentHello(agentId, "us-east", new Dictionary<string, bool> { ["script_exec"] = true }, null),
			new FlowNoopWebSocket(),
			registrationCts.Token);
	}

	private static async Task<string> CreateScript(HttpClient client, string name)
	{
		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name, content = "echo" });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		return createDoc.RootElement.GetProperty("id").GetString()!;
	}

	private static async Task<string> CreateFlow(HttpClient client, string name, object? steps = null)
	{
		// Default: one step pointing at placeholder ids. Callers that need
		// specific steps pass an anonymous array.
		object body = steps is null
			? new { name, steps = new[] { new { scriptId = "s", agentId = "a" } } }
			: new { name, steps };
		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/script-flows", body);
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		return createDoc.RootElement.GetProperty("id").GetString()!;
	}

	private static TestFactory CreateFactory(bool removeHosted = false) => new(removeHosted);

	private sealed class TestFactory(bool removeHosted = false) : WebApplicationFactory<Program>
	{
		// Flows and scripts must share one database so a run's pre-flight can
		// find scripts created through the API; ":memory:" gives each store its
		// own private database, so the tests use a temp file instead.
		private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vapor-flows-{Guid.NewGuid():N}.db");

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.RemoveAll<SqliteScriptStore>();
				services.RemoveAll<SqliteFlowStore>();
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:", ScriptDbPath: _dbPath));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.AddSingleton<SqliteScriptStore>(sp => new SqliteScriptStore(_dbPath));
				services.AddSingleton<SqliteFlowStore>(sp => new SqliteFlowStore(_dbPath));
				if (removeHosted)
				{
					// The run endpoint races its own fake agent responder;
					// background dispatchers would claim/fail the tasks first.
					services.RemoveAll<IHostedService>();
					services.RemoveAll<IHostedLifecycleService>();
				}
			});
		}

		public override async ValueTask DisposeAsync()
		{
			await base.DisposeAsync();
			try
			{
				// Best effort: the temp database only matters within the test run.
				File.Delete(_dbPath);
			}
			catch (IOException)
			{
			}
		}
	}

	private sealed class FlowNoopWebSocket : WebSocket
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

	private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
	{
		var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}
}
