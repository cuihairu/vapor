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

	private static TestFactory CreateFactory() => new();

	private sealed class TestFactory : WebApplicationFactory<Program>
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
