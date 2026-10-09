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

// The execute tests tune the static AccountTaskRunner wait knobs, so they join
// the shared collection that serializes every writer/reader of them.
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed class ScriptApiTests
{
	[Fact]
	public async Task ScriptEndpoints_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage list = await client.GetAsync("/v1/scripts");
		using HttpResponseMessage get = await client.GetAsync("/v1/scripts/abc");
		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name = "x", content = "y" });
		using HttpResponseMessage update = await client.PutAsJsonAsync("/v1/scripts/abc", new { name = "x", content = "y" });
		using HttpResponseMessage delete = await client.DeleteAsync("/v1/scripts/abc");

		Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
	}

	[Fact]
	public async Task Script_Create_Get_List_RoundTrip()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new
		{
			name = "cleanup-temp",
			description = "删除临时下载",
			content = "#!/bin/sh\nrm -rf /tmp/vapor-downloads/*\n"
		});
		Assert.Equal(HttpStatusCode.Created, create.StatusCode);
		using var createdDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createdDoc.RootElement.GetProperty("id").GetString()!;
		Assert.Equal("cleanup-temp", createdDoc.RootElement.GetProperty("name").GetString());
		Assert.Equal("删除临时下载", createdDoc.RootElement.GetProperty("description").GetString());
		Assert.Equal("shell", createdDoc.RootElement.GetProperty("language").GetString());
		Assert.True(createdDoc.RootElement.GetProperty("createdAtMs").GetInt64() > 0);

		using HttpResponseMessage get = await client.GetAsync($"/v1/scripts/{id}");
		Assert.Equal(HttpStatusCode.OK, get.StatusCode);
		using var getDoc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
		Assert.Equal(id, getDoc.RootElement.GetProperty("id").GetString());
		Assert.Equal("cleanup-temp", getDoc.RootElement.GetProperty("name").GetString());

		using HttpResponseMessage list = await client.GetAsync("/v1/scripts");
		Assert.Equal(HttpStatusCode.OK, list.StatusCode);
		using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
		Assert.Single(listDoc.RootElement.GetProperty("scripts").EnumerateArray());
		Assert.Equal(id, listDoc.RootElement.GetProperty("scripts")[0].GetProperty("id").GetString());
	}

	[Fact]
	public async Task Script_Create_ValidationArms()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage blankName = await client.PostAsJsonAsync("/v1/scripts", new { name = "  ", content = "echo hi" });
		Assert.Equal(HttpStatusCode.BadRequest, blankName.StatusCode);
		using var blankNameDoc = JsonDocument.Parse(await blankName.Content.ReadAsStringAsync());
		Assert.Equal("name is required", blankNameDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage blankContent = await client.PostAsJsonAsync("/v1/scripts", new { name = "ok", content = "   " });
		Assert.Equal(HttpStatusCode.BadRequest, blankContent.StatusCode);
		using var blankContentDoc = JsonDocument.Parse(await blankContent.Content.ReadAsStringAsync());
		Assert.Equal("content is required", blankContentDoc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Script_Create_NormalizesLanguage()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/scripts", new { name = "py", language = "  PYTHON ", content = "print(1)" });
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("python", doc.RootElement.GetProperty("language").GetString());
	}

	[Fact]
	public async Task Script_Put_ReplacesAndPreservesCreatedAt()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name = "v1", content = "echo one" });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createDoc.RootElement.GetProperty("id").GetString()!;
		long createdAt = createDoc.RootElement.GetProperty("createdAtMs").GetInt64();

		using HttpResponseMessage update = await client.PutAsJsonAsync($"/v1/scripts/{id}", new { name = "v2", description = "renamed", content = "echo two" });
		Assert.Equal(HttpStatusCode.OK, update.StatusCode);
		using var updateDoc = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
		Assert.Equal("v2", updateDoc.RootElement.GetProperty("name").GetString());
		Assert.Equal("renamed", updateDoc.RootElement.GetProperty("description").GetString());
		Assert.Equal("echo two", updateDoc.RootElement.GetProperty("content").GetString());
		Assert.Equal(createdAt, updateDoc.RootElement.GetProperty("createdAtMs").GetInt64());
		Assert.True(updateDoc.RootElement.GetProperty("updatedAtMs").GetInt64() >= createdAt);
	}

	[Fact]
	public async Task Script_Put_UnknownId_Returns404_BeforeValidation()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PutAsJsonAsync("/v1/scripts/missing", new { name = "x", content = "y" });
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
	}

	[Fact]
	public async Task Script_Put_ExistingWithBlankName_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name = "keep", content = "echo" });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createDoc.RootElement.GetProperty("id").GetString()!;

		using HttpResponseMessage response = await client.PutAsJsonAsync($"/v1/scripts/{id}", new { name = "", content = "echo" });
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Script_Delete_ThenGet404_AndUnknownDelete404()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name = "doomed", content = "echo" });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createDoc.RootElement.GetProperty("id").GetString()!;

		using HttpResponseMessage delete = await client.DeleteAsync($"/v1/scripts/{id}");
		Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

		using HttpResponseMessage get = await client.GetAsync($"/v1/scripts/{id}");
		Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

		using HttpResponseMessage deleteAgain = await client.DeleteAsync($"/v1/scripts/{id}");
		Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
	}

	[Fact]
	public async Task Script_Mutations_AreAudited()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name = "audited", content = "echo" });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string id = createDoc.RootElement.GetProperty("id").GetString()!;

		await client.PutAsJsonAsync($"/v1/scripts/{id}", new { name = "audited", content = "echo v2" });
		await client.DeleteAsync($"/v1/scripts/{id}");

		IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
		IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 100), CancellationToken.None);

		var actions = entries.Select(e => e.Action).ToList();
		Assert.Contains("script_created", actions);
		Assert.Contains("script_updated", actions);
		Assert.Contains("script_deleted", actions);

		AuditEntry created = entries.Single(e => e.Action == "script_created");
		Assert.False(string.IsNullOrWhiteSpace(created.Actor));
		// Details round-trip through JSON, so scalar values come back as JsonElement.
		Assert.Equal(id, ((JsonElement)created.Details!["scriptId"]!).GetString());
		Assert.Equal("audited", ((JsonElement)created.Details!["name"]!).GetString());
	}

	[Fact]
	public async Task Script_Execute_RequiresAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/scripts/abc/execute", new { agentId = "agent-1" });

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Script_Execute_UnknownScript_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);

		using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/scripts/missing/execute", new { agentId = "agent-1" });

		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Contains("does not exist", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Script_Execute_ValidationArms()
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);
		string scriptId = await CreateScript(client, "s");

		using HttpResponseMessage blankAgent = await client.PostAsJsonAsync($"/v1/scripts/{scriptId}/execute", new { agentId = "   " });
		Assert.Equal(HttpStatusCode.BadRequest, blankAgent.StatusCode);
		using var blankAgentDoc = JsonDocument.Parse(await blankAgent.Content.ReadAsStringAsync());
		Assert.Equal("agentId is required (script execution is host-scoped and targets one agent)", blankAgentDoc.RootElement.GetProperty("error").GetString());

		using HttpResponseMessage missingAgent = await client.PostAsJsonAsync($"/v1/scripts/{scriptId}/execute", new { agentId = "ghost" });
		Assert.Equal(HttpStatusCode.NotFound, missingAgent.StatusCode);
		using var missingAgentDoc = JsonDocument.Parse(await missingAgent.Content.ReadAsStringAsync());
		Assert.Contains("is not connected", missingAgentDoc.RootElement.GetProperty("error").GetString());
	}

	[Theory]
	[InlineData(0)]
	[InlineData(301)]
	public async Task Script_Execute_TimeoutOutsideBounds_Returns400(int timeoutSeconds)
	{
		await using var factory = CreateFactory();
		using var client = CreateAdminClient(factory);
		string scriptId = await CreateScript(client, "s");

		using HttpResponseMessage response = await client.PostAsJsonAsync(
			$"/v1/scripts/{scriptId}/execute",
			new { agentId = "agent-1", timeoutSeconds });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal("timeoutSeconds must be between 1 and 300", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Script_Execute_DispatchesHostTaskToNamedAgent_AndAudits()
	{
		await using var factory = CreateFactory();
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		using CancellationTokenSource registrationCts = new();
		registry.Register(
			new AgentHello("agent-exec", "us-east", new Dictionary<string, bool> { ["script_exec"] = true }, null),
			new ScriptExecNoopWebSocket(),
			registrationCts.Token);
		using var client = CreateAdminClient(factory);
		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new
		{
			name = "disk-check",
			description = "df -h",
			language = "shell",
			content = "df -h /"
		});
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		string scriptId = createDoc.RootElement.GetProperty("id").GetString()!;

		TimeSpan savedWindow = AccountTaskRunner.WaitWindow;
		TimeSpan savedInterval = AccountTaskRunner.PollInterval;
		try
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(50);

			// The noop websocket never claims the task, so the route exhausts the
			// (shortened) wait window and answers 202 pending.
			using HttpResponseMessage response = await client.PostAsJsonAsync(
				$"/v1/scripts/{scriptId}/execute",
				new { agentId = "agent-exec", timeoutSeconds = 60 });
			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
			string jobId = doc.RootElement.GetProperty("job_id").GetString()!;

			await using var scope = factory.Services.CreateAsyncScope();
			var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
			JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
			Assert.Equal("script_exec", job.Job.Action);
			JobTask task = Assert.Single(job.Tasks);
			Assert.Equal("agent:agent-exec", task.Target);
			Assert.Equal(scriptId, ((JsonElement)task.Payload!["scriptId"]!).GetString());
			Assert.Equal("shell", ((JsonElement)task.Payload!["language"]!).GetString());
			Assert.Equal("df -h /", ((JsonElement)task.Payload!["content"]!).GetString());
			Assert.Equal(60, ((JsonElement)task.Payload!["timeoutSeconds"]!).GetInt32());

			IAuditStore audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
			IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Limit: 20), CancellationToken.None);
			AuditEntry dispatched = Assert.Single(entries, e => e.Action == "script_exec_dispatched");
			Assert.Equal(jobId, dispatched.JobId);
			Assert.Equal("agent-exec", ((JsonElement)dispatched.Details!["agentId"]!).GetString());
			Assert.Equal("df -h /".Length, ((JsonElement)dispatched.Details!["bytes"]!).GetInt32());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = savedWindow;
			AccountTaskRunner.PollInterval = savedInterval;
		}
	}

	[Fact]
	public async Task Script_Execute_OmitsTimeoutFromPayloadWhenUnset()
	{
		await using var factory = CreateFactory();
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		using CancellationTokenSource registrationCts = new();
		registry.Register(
			new AgentHello("agent-exec", "us-east", new Dictionary<string, bool> { ["script_exec"] = true }, null),
			new ScriptExecNoopWebSocket(),
			registrationCts.Token);
		using var client = CreateAdminClient(factory);
		string scriptId = await CreateScript(client, "s");

		TimeSpan savedWindow = AccountTaskRunner.WaitWindow;
		TimeSpan savedInterval = AccountTaskRunner.PollInterval;
		try
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(50);

			using HttpResponseMessage response = await client.PostAsJsonAsync(
				$"/v1/scripts/{scriptId}/execute",
				new { agentId = "agent-exec" });
			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
			string jobId = doc.RootElement.GetProperty("job_id").GetString()!;

			await using var scope = factory.Services.CreateAsyncScope();
			var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
			JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
			Assert.False(job.Tasks.Single().Payload!.ContainsKey("timeoutSeconds"));
		}
		finally
		{
			AccountTaskRunner.WaitWindow = savedWindow;
			AccountTaskRunner.PollInterval = savedInterval;
		}
	}

	[Fact]
	public async Task Script_Execute_AgentReportsFinished_ReturnsResult()
	{
		await using var factory = CreateFactory(removeHosted: true);
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		using CancellationTokenSource registrationCts = new();
		registry.Register(
			new AgentHello("agent-exec", "us-east", new Dictionary<string, bool> { ["script_exec"] = true }, null),
			new ScriptExecNoopWebSocket(),
			registrationCts.Token);
		using var client = CreateAdminClient(factory);
		string scriptId = await CreateScript(client, "df-check");

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstScriptExecTaskAsync(store, success: true, new Dictionary<string, object?>
		{
			["exitCode"] = 0,
			["timedOut"] = false,
			["stdout"] = "/dev/sda1 90%",
			["stderr"] = ""
		}, null, cts.Token));

		using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/scripts/{scriptId}/execute", new { agentId = "agent-exec" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(scriptId, doc.RootElement.GetProperty("script").GetString());
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
		Assert.False(result.GetProperty("timedOut").GetBoolean());
		Assert.Equal("/dev/sda1 90%", result.GetProperty("stdout").GetString());
	}

	[Fact]
	public async Task Script_Execute_AgentReportsFailure_Returns502()
	{
		await using var factory = CreateFactory(removeHosted: true);
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		using CancellationTokenSource registrationCts = new();
		registry.Register(
			new AgentHello("agent-exec", "us-east", new Dictionary<string, bool> { ["script_exec"] = true }, null),
			new ScriptExecNoopWebSocket(),
			registrationCts.Token);
		using var client = CreateAdminClient(factory);
		string scriptId = await CreateScript(client, "boom");

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstScriptExecTaskAsync(store, success: false, null, "script exited with code 3", cts.Token));

		using HttpResponseMessage response = await client.PostAsJsonAsync($"/v1/scripts/{scriptId}/execute", new { agentId = "agent-exec" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("script exited with code 3", body);
	}

	/// <summary>Plays the agent side: claims the queued script_exec task and reports a result.</summary>
	private static async Task<Dictionary<string, object?>?> RespondToFirstScriptExecTaskAsync(
		IJobStore store,
		bool success,
		Dictionary<string, object?>? output,
		string? error,
		CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == "script_exec")
			{
				await store.SetTaskResult(
					new TaskResult(claimed.Id, success, success ? null : error, success ? output : null, DateTimeOffset.UtcNow),
					ct);
				return claimed.Payload is null ? null : new Dictionary<string, object?>(claimed.Payload);
			}

			await Task.Delay(25, ct);
		}

		return null;
	}

	private static TestFactory CreateFactory(bool removeHosted = false) => new(removeHosted);

	private static async Task<string> CreateScript(HttpClient client, string name)
	{
		using HttpResponseMessage create = await client.PostAsJsonAsync("/v1/scripts", new { name, content = "echo" });
		using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
		return createDoc.RootElement.GetProperty("id").GetString()!;
	}

	private static TestFactory CreateFactory() => new();

	private sealed class ScriptExecNoopWebSocket : WebSocket
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

	private sealed class TestFactory(bool removeHosted = false) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:", ScriptDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				if (removeHosted)
				{
					// The execute endpoint races its own fake agent responder;
					// background dispatchers would claim/fail the task first.
					services.RemoveAll<IHostedService>();
					services.RemoveAll<IHostedLifecycleService>();
				}
			});
		}
	}

	private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
	{
		var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}
}
