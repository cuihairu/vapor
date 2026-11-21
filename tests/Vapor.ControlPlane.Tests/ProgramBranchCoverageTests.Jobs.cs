using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Job lifecycle branch tests: create/get/cancel validation, cancel rejections, agents.</summary>
public sealed partial class ProgramBranchCoverageTests
{
	// ── jobs endpoints ───────────────────────────────────────────────────────
	[Fact]
	public async Task CreateJob_MissingFields_Return400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage noAction = await client.PostAsJsonAsync("/v1/jobs", new { targets = new[] { "alice" } });
		using HttpResponseMessage noTargets = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping" });

		Assert.Equal(HttpStatusCode.BadRequest, noAction.StatusCode);
		Assert.Contains("action is required", await noAction.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.BadRequest, noTargets.StatusCode);
		Assert.Contains("targets is required", await noTargets.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task GetJob_UnknownId_Returns404()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/jobs/does-not-exist");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		Assert.Contains("job not found", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task CancelJob_UnknownId_Returns404()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsync("/v1/jobs/does-not-exist/cancel", content: null);

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task CancelJob_FullAgentQueue_PublishesCancelRejection()
	{
		var store = new SqliteJobStore(":memory:");
		JobWithTasks created = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null),
			CancellationToken.None);
		JobTask claimed = (await store.ClaimNextQueuedTask("local", CancellationToken.None))!;
		Assert.Equal(JobTaskStatus.Running, claimed.Status);

		var registry = new AgentRegistry();
		var agent = new ConnectedAgent(
			new AgentHello("agent-9", "local", new Dictionary<string, bool> { ["ping"] = true }, null),
			new CancelNoopWebSocket(),
			sendQueueCapacity: 1);
		Assert.True(agent.EnqueueTask(new JobTask("retained", created.Job.Id, "acct-1", "ping", "local", null, JobTaskStatus.Queued, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
		InsertConnectedAgent(registry, agent);

		await using BranchFactory factory = new() { JobStore = store, AgentRegistry = registry };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsync($"/v1/jobs/{created.Job.Id}/cancel", content: null);

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.Equal(1, agent.SendQueueRejected);
		Event rejection = Assert.Single(factory.Events.Published, evt => string.Equals(evt.Type, "job.cancel_rejected", StringComparison.Ordinal));
		Assert.Equal(created.Job.Id, rejection.JobId);
		Assert.Equal(1, Convert.ToInt32(rejection.Payload!["rejected"]));
	}

	[Fact]
	public async Task CancelJob_WithQueuedTaskAndNoAgent_ReturnsOk()
	{
		var store = new SqliteJobStore(":memory:");
		await using BranchFactory factory = new() { JobStore = store };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", targets = new[] { "alice" } });
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using HttpResponseMessage cancel = await client.PostAsync($"/v1/jobs/{jobId}/cancel", content: null);

		Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		Assert.All(job.Tasks, t => Assert.Equal(JobTaskStatus.Canceled, t.Status));
	}

	// ── remaining endpoint branches ──────────────────────────────────────────
	[Fact]
	public async Task GetAgents_ReturnsRegisteredAgents()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/agents");

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.Contains("agents", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task GetJob_ReturnsJobWithTasks()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", region = "us-east", targets = new[] { "alice" } });
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using HttpResponseMessage resp = await client.GetAsync($"/v1/jobs/{jobId}");

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.Contains(jobId, await resp.Content.ReadAsStringAsync());
	}

	private static void InsertConnectedAgent(AgentRegistry registry, ConnectedAgent agent)
	{
		var agents = (System.Collections.Concurrent.ConcurrentDictionary<string, ConnectedAgent>)typeof(AgentRegistry)
			.GetField("_agents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(registry)!;
		agents[agent.Hello.AgentId] = agent;
	}

}
