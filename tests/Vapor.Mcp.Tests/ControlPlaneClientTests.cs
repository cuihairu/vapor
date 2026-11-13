using Xunit;
using System.Net;
using ModelContextProtocol;

namespace Vapor.Mcp.Tests;

public sealed class ControlPlaneClientTests
{
	private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
		: HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return await responder(request, cancellationToken);
		}
	}

	private static StubHandler Respond(HttpStatusCode status, string body)
		=> new((_, _) => Task.FromResult(ToJsonResponse(status, body)));

	private static HttpResponseMessage ToJsonResponse(HttpStatusCode status, string body)
		=> new(status) { Content = new StringContent(body) };

	private static ControlPlaneClient Client(HttpMessageHandler handler)
		=> new("http://cp.test", "admin-key", new HttpClient(handler));

	[Fact]
	public async Task GetAsync_Success_ReturnsBodyAndSendsBearerHeader()
	{
		StubHandler handler = Respond(HttpStatusCode.OK, """{"agents":[]}""");
		using ControlPlaneClient client = Client(handler);

		string body = await client.GetAsync("/v1/agents");

		Assert.Equal("""{"agents":[]}""", body);
		HttpRequestMessage sent = Assert.Single(handler.Requests);
		Assert.Equal("http://cp.test/v1/agents", sent.RequestUri!.ToString());
		Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
		Assert.Equal("admin-key", sent.Headers.Authorization.Parameter);
	}

	[Fact]
	public async Task PostAsync_Success_ReturnsBody()
	{
		StubHandler handler = Respond(HttpStatusCode.OK, """{"spec":{"accountName":"alice"}}""");
		using ControlPlaneClient client = Client(handler);

		string body = await client.PostAsync("/v1/accounts/alice/enable");

		Assert.Contains("alice", body);
		HttpRequestMessage sent = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Post, sent.Method);
		Assert.Equal("http://cp.test/v1/accounts/alice/enable", sent.RequestUri!.ToString());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("  ")]
	public void Constructor_RejectsMissingBaseUrl(string? baseUrl)
		=> Assert.ThrowsAny<ArgumentException>(() => new ControlPlaneClient(baseUrl!, "key"));

	[Theory]
	[InlineData(null)]
	[InlineData("  ")]
	public void Constructor_RejectsMissingApiKey(string? apiKey)
		=> Assert.ThrowsAny<ArgumentException>(() => new ControlPlaneClient("http://cp.test", apiKey!));

	[Fact]
	public void Constructor_KeepsPresetBaseAddress()
	{
		using var http = new HttpClient { BaseAddress = new Uri("http://preset.test") };
		using ControlPlaneClient client = new("http://cp.test", "key", http);

		Assert.Equal("http://preset.test/", http.BaseAddress!.ToString());
	}

	[Fact]
	public async Task GetAsync_Unauthorized_NamesTheAdminKeyConfiguration()
	{
		StubHandler handler = Respond(HttpStatusCode.Unauthorized, "");
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/v1/agents"));

		Assert.Contains("401", error.Message, StringComparison.Ordinal);
		Assert.Contains("Vapor_MCP_ADMIN_API_KEY", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAsync_ErrorStatus_IncludesTruncatedBody()
	{
		string longBody = new('x', 400);
		longBody += "TRUNCATED-TAIL";
		StubHandler handler = Respond(HttpStatusCode.NotFound, longBody);
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/v1/accounts/ghost"));

		Assert.Contains("404", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("TRUNCATED-TAIL", error.Message, StringComparison.Ordinal);
		Assert.EndsWith("…", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAsync_ErrorStatusWithShortBody_IncludesBodyWithoutEllipsis()
	{
		StubHandler handler = Respond(HttpStatusCode.BadRequest, """unknown desired state 'weird'""");
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/v1/accounts"));

		Assert.Contains("""unknown desired state 'weird'""", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("…", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAsync_ErrorStatusWithEmptyBody_ReportsStatusOnly()
	{
		StubHandler handler = Respond(HttpStatusCode.InternalServerError, "");
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/metrics"));

		Assert.Contains("500 InternalServerError", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAsync_UnreadableErrorBody_ReportsStatusOnly()
	{
		var handler = new StubHandler((_, _) =>
		{
			var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new ThrowingContent(),
			};
			return Task.FromResult(response);
		});
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/v1/accounts"));

		Assert.Contains("400", error.Message, StringComparison.Ordinal);
		Assert.Contains("unreadable", error.Message, StringComparison.Ordinal);
	}

	private sealed class ThrowingContent : HttpContent
	{
		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
			=> throw new InvalidOperationException("simulated read failure");

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return true;
		}
	}

	[Fact]
	public async Task GetAsync_TransportFailure_WrapsWithBaseUrlHint()
	{
		var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/v1/agents"));

		Assert.Contains("http://cp.test", error.Message, StringComparison.Ordinal);
		Assert.Contains("Vapor_MCP_BASE_URL", error.Message, StringComparison.Ordinal);
		Assert.IsType<HttpRequestException>(error.InnerException);
	}

	[Fact]
	public async Task GetAsync_NonCancellationTimeout_WrapsWithBaseUrlHint()
	{
		var handler = new StubHandler((_, _) => throw new TaskCanceledException("timed out"));
		using ControlPlaneClient client = Client(handler);

		McpException error = await Assert.ThrowsAsync<McpException>(() => client.GetAsync("/v1/agents"));

		Assert.Contains("cannot reach the control plane", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAsync_CallerCancellation_Propagates()
	{
		var handler = new StubHandler((_, token) => throw new OperationCanceledException(token));
		using ControlPlaneClient client = Client(handler);

		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();
		// HttpClient surfaces cancellations as TaskCanceledException, a subclass.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => client.GetAsync("/v1/agents", cts.Token));
	}

	[Fact]
	public void WithQuery_AppendsOnlyNotBlankParametersEscaped()
	{
		string path = ControlPlaneClient.WithQuery(
			"/v1/accounts",
			("state", " idle "),
			("region", null),
			("agent", ""),
			("note", "a b&c=d"));

		Assert.Equal("/v1/accounts?state=idle&note=a%20b%26c%3Dd", path);
	}

	[Fact]
	public void WithQuery_AllBlank_ReturnsBarePath()
	{
		Assert.Equal("/v1/accounts", ControlPlaneClient.WithQuery("/v1/accounts", ("state", null), ("region", " ")));
	}
}
