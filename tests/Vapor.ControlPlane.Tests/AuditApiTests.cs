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
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
			});
		}
	}
}
