using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// POST /v1/qr e2e: first-party SVG rendering for console challenge URLs —
/// admin-gated, 400 on blank/oversized payloads, image/svg+xml body.
/// </summary>
public sealed class QrApiTests
{
	private static (HttpClient Client, TestFactory Factory) CreateClient()
	{
		var factory = new TestFactory();
		var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new("Bearer", "admin-token");
		return (client, factory);
	}

	private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

	[Fact]
	public async Task Post_Qr_WithoutAuth_Returns401()
	{
		var (client, factory) = CreateClient();
		client.DefaultRequestHeaders.Authorization = null;

		HttpResponseMessage response = await client.PostAsync("/v1/qr", JsonBody("""{"text":"https://s.team/q/1/xyz"}"""));

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		await factory.DisposeAsync();
	}

	[Fact]
	public async Task Post_Qr_WithValidText_ReturnsSvg()
	{
		var (client, factory) = CreateClient();

		HttpResponseMessage response = await client.PostAsync("/v1/qr", JsonBody("""{"text":"https://s.team/q/1/Ab3dEf9h"}"""));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
		string body = await response.Content.ReadAsStringAsync();
		Assert.StartsWith("<svg ", body, StringComparison.Ordinal);
		Assert.EndsWith("</svg>", body, StringComparison.Ordinal);
		await factory.DisposeAsync();
	}

	[Theory]
	[InlineData("""{"text":""}""")]
	[InlineData("""{"text":"   "}""")]
	[InlineData("{}")]
	public async Task Post_Qr_WithBlankOrMissingText_Returns400(string json)
	{
		var (client, factory) = CreateClient();

		HttpResponseMessage response = await client.PostAsync("/v1/qr", JsonBody(json));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("text is required", body, StringComparison.Ordinal);
		await factory.DisposeAsync();
	}

	[Fact]
	public async Task Post_Qr_WithMalformedJson_Returns400()
	{
		var (client, factory) = CreateClient();

		// The framework rejects the body before the handler runs, so only the
		// status — not our ErrorResponse text — is contractual here.
		HttpResponseMessage response = await client.PostAsync("/v1/qr", JsonBody("{ invalid json"));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		await factory.DisposeAsync();
	}

	[Fact]
	public async Task Post_Qr_WithoutBody_Returns400()
	{
		var (client, factory) = CreateClient();

		HttpResponseMessage response = await client.PostAsync("/v1/qr", null);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		await factory.DisposeAsync();
	}

	[Fact]
	public async Task Post_Qr_WithOversizeText_Returns400()
	{
		var (client, factory) = CreateClient();

		string json = $"{{\"text\":\"{new string('a', QrEncoder.MaxByteLength + 1)}\"}}";
		HttpResponseMessage response = await client.PostAsync("/v1/qr", JsonBody(json));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("capacity", body, StringComparison.Ordinal);
		await factory.DisposeAsync();
	}

	[Fact]
	public async Task Post_Qr_WithNonAsciiText_ReturnsSvg()
	{
		var (client, factory) = CreateClient();

		HttpResponseMessage response = await client.PostAsync("/v1/qr", JsonBody("""{"text":"蒸汽平台扫码登录"}"""));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
		await factory.DisposeAsync();
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
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
