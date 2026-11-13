using Xunit;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;

namespace Vapor.Mcp.Tests;

public sealed class AccountWriteToolsTests
{
	private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
		}
	}

	[Fact]
	public async Task EnableAccount_PostsToEnableEndpointAndSanitizesOutput()
	{
		var handler = new StubHandler(HttpStatusCode.OK, """
			{"spec":{"accountName":"alice","enabled":true,"desiredState":"online","password":"never"}}
			""");
		using ControlPlaneClient client = new("http://cp.test", "key", new HttpClient(handler));

		string output = await AccountWriteTools.EnableAccount(client, "alice", CancellationToken.None);

		Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
		Assert.Equal("http://cp.test/v1/accounts/alice/enable", handler.LastRequest.RequestUri!.ToString());
		using JsonDocument doc = JsonDocument.Parse(output);
		JsonElement spec = doc.RootElement.GetProperty("spec");
		Assert.True(spec.GetProperty("enabled").GetBoolean());
		Assert.False(spec.TryGetProperty("password", out _));
	}

	[Fact]
	public async Task DisableAccount_PostsToDisableEndpointAndSanitizesOutput()
	{
		var handler = new StubHandler(HttpStatusCode.OK, """
			{"spec":{"accountName":"bob","enabled":false,"desiredState":"offline"}}
			""");
		using ControlPlaneClient client = new("http://cp.test", "key", new HttpClient(handler));

		string output = await AccountWriteTools.DisableAccount(client, "bob", CancellationToken.None);

		Assert.Equal("http://cp.test/v1/accounts/bob/disable", handler.LastRequest!.RequestUri!.ToString());
		using JsonDocument doc = JsonDocument.Parse(output);
		Assert.False(doc.RootElement.GetProperty("spec").GetProperty("enabled").GetBoolean());
	}

	[Fact]
	public async Task SetEnabled_TrimsAndEscapesAccountName()
	{
		var handler = new StubHandler(HttpStatusCode.OK, """{"spec":{"accountName":"a b/c?"}}""");
		using ControlPlaneClient client = new("http://cp.test", "key", new HttpClient(handler));

		await AccountWriteTools.EnableAccount(client, " a b/c? ", CancellationToken.None);

		// AbsoluteUri keeps the escaped form; ToString() renders %20 as a space.
		Assert.Equal("http://cp.test/v1/accounts/a%20b%2Fc%3F/enable", handler.LastRequest!.RequestUri!.AbsoluteUri);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task SetEnabled_BlankName_ThrowsWithoutCalling(string? accountName)
	{
		var handler = new StubHandler(HttpStatusCode.OK, """{"spec":{}}""");
		using ControlPlaneClient client = new("http://cp.test", "key", new HttpClient(handler));

		if (accountName is null)
		{
			await Assert.ThrowsAsync<McpException>(
				() => AccountWriteTools.EnableAccount(client, accountName!, CancellationToken.None));
		}
		else
		{
			await Assert.ThrowsAsync<McpException>(
				() => AccountWriteTools.DisableAccount(client, accountName, CancellationToken.None));
		}

		Assert.Null(handler.LastRequest);
	}

	[Fact]
	public async Task SetEnabled_UnknownAccount_SurfacesControlPlaneError()
	{
		var handler = new StubHandler(HttpStatusCode.NotFound, """account 'ghost' is not declared""");
		using ControlPlaneClient client = new("http://cp.test", "key", new HttpClient(handler));

		McpException error = await Assert.ThrowsAsync<McpException>(
			() => AccountWriteTools.DisableAccount(client, "ghost", CancellationToken.None));

		Assert.Contains("404", error.Message, StringComparison.Ordinal);
		Assert.Contains("ghost", error.Message, StringComparison.Ordinal);
	}
}
