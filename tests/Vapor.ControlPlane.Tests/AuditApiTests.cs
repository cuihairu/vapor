using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class AuditApiTests
{
	[Fact]
	public async Task AuditLogs_RequiresAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/audit/logs");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task JobCreation_IsPersistedToAuditLogsWithRedactedPayload()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new
		{
			action = "ping",
			targets = new[] { "acct-1" },
			payload = new Dictionary<string, object?>
			{
				["message"] = "hello",
				["password"] = "super-secret",
				["authCode"] = "987654"
			}
		});

		Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

		using HttpResponseMessage auditResponse = await client.GetAsync("/v1/audit/logs?action=job.created");
		Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);

		string body = await auditResponse.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.True(doc.RootElement.GetProperty("total").GetInt32() >= 1);
		string payloadJson = doc.RootElement.GetProperty("logs")[0].GetProperty("details").GetProperty("payload").GetRawText();

		Assert.Contains("hello", payloadJson, StringComparison.Ordinal);
		Assert.DoesNotContain("super-secret", payloadJson, StringComparison.Ordinal);
		Assert.DoesNotContain("987654", payloadJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AuthCodeSubmission_IsPersistedWithRedactedCode()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage submit = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new
		{
			code = "4321ab",
			type = "2fa"
		});

		Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

		using HttpResponseMessage auditResponse = await client.GetAsync("/v1/audit/logs?action=auth.code.submitted&account=alice");
		Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);

		string body = await auditResponse.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
		string detailsJson = doc.RootElement.GetProperty("logs")[0].GetProperty("details").GetRawText();

		Assert.DoesNotContain("4321ab", detailsJson, StringComparison.Ordinal);
	}

	[Fact]
	public async Task LoginSessionEvent_IsPersistedAsDedicatedAuditEntry()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");

		using HttpResponseMessage post = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "state_changed",
			state = "LoggedOn",
			message = "logged in"
		});

		Assert.Equal(HttpStatusCode.OK, post.StatusCode);

		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		using HttpResponseMessage auditResponse = await client.GetAsync("/v1/audit/logs?action=session.login&account=alice");

		Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);

		string body = await auditResponse.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal("LoggedOn", doc.RootElement.GetProperty("logs")[0].GetProperty("details").GetProperty("state").GetString());
	}

	[Fact]
	public async Task AuditLogs_RejectsInvalidTimeRange()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/audit/logs?fromMs=2000&toMs=1000");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task AuditLogs_SupportsPaginationMetadata()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/audit/logs?limit=5&offset=10");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);

		string body = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(5, doc.RootElement.GetProperty("limit").GetInt32());
		Assert.Equal(10, doc.RootElement.GetProperty("offset").GetInt32());
	}

	[Fact]
	public async Task AuditLogs_FiltersByAgentIdAndTaskId()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		IAuditStore store = factory.Services.GetRequiredService<IAuditStore>();
		await store.RecordAsync(AuditStoreExtensions.CreateEntry(
			"task.dispatched", "scheduler", jobId: "job-1", taskId: "task-1", agentId: "agent-a"), CancellationToken.None);
		await store.RecordAsync(AuditStoreExtensions.CreateEntry(
			"task.dispatched", "scheduler", jobId: "job-1", taskId: "task-2", agentId: "agent-b"), CancellationToken.None);
		await store.RecordAsync(AuditStoreExtensions.CreateEntry(
			"task.result", "scheduler", jobId: "job-1", taskId: "task-2", agentId: "agent-a"), CancellationToken.None);

		using HttpResponseMessage byAgent = await client.GetAsync("/v1/audit/logs?agentId=agent-a");
		using HttpResponseMessage byTask = await client.GetAsync("/v1/audit/logs?taskId=task-2");
		using HttpResponseMessage byBoth = await client.GetAsync("/v1/audit/logs?agentId=agent-b&taskId=task-2");

		Assert.Equal(HttpStatusCode.OK, byAgent.StatusCode);
		string agentBody = await byAgent.Content.ReadAsStringAsync();
		using var agentDoc = JsonDocument.Parse(agentBody);
		Assert.Equal(2, agentDoc.RootElement.GetProperty("total").GetInt32());

		string taskBody = await byTask.Content.ReadAsStringAsync();
		using var taskDoc = JsonDocument.Parse(taskBody);
		Assert.Equal(2, taskDoc.RootElement.GetProperty("total").GetInt32());

		string bothBody = await byBoth.Content.ReadAsStringAsync();
		using var bothDoc = JsonDocument.Parse(bothBody);
		Assert.Equal(1, bothDoc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal("task.dispatched", bothDoc.RootElement.GetProperty("logs")[0].GetProperty("action").GetString());
	}

	[Fact]
	public async Task AuditLogs_RecordsTraceIdFromTraceparentHeader()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		client.DefaultRequestHeaders.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new
		{
			action = "ping",
			targets = new[] { "acct-1" }
		});
		Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

		using HttpResponseMessage auditResponse = await client.GetAsync("/v1/audit/logs?action=job.created");
		string body = await auditResponse.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		JsonElement log = doc.RootElement.GetProperty("logs")[0];
		Assert.Equal("0af7651916cd43dd8448eb211c80319c", log.GetProperty("traceId").GetString());
	}

	[Fact]
	public async Task AuditLogs_MalformedTraceparent_OmitsTraceId()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		client.DefaultRequestHeaders.Add("traceparent", "not-a-traceparent");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new
		{
			action = "ping",
			targets = new[] { "acct-1" }
		});
		Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

		using HttpResponseMessage auditResponse = await client.GetAsync("/v1/audit/logs?action=job.created");
		string body = await auditResponse.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		// Null fields are omitted by the protocol serializer (WhenWritingNull).
		JsonElement log = doc.RootElement.GetProperty("logs")[0];
		Assert.False(log.TryGetProperty("traceId", out _));
	}

	private static TestFactory CreateFactory()
	{
		return new TestFactory();
	}

	private sealed class TestFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
			});
		}
	}
}
