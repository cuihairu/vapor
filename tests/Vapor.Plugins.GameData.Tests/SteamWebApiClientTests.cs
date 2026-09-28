using System.Net;
using System.Text;
using Xunit;

namespace Vapor.Plugins.GameData.Tests;

/// <summary>
/// The keyed Web API client: URL composition (key + escaped query), the
/// injected-handler test seam, and the two failure shapes (HTTP status,
/// malformed JSON) that actions translate into failed results.
/// </summary>
public sealed class SteamWebApiClientTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Constructor_RejectsBlankKeys(string? apiKey)
	{
		// null throws ArgumentNullException (an ArgumentException subclass).
		Assert.ThrowsAny<ArgumentException>(() => new SteamWebApiClient(apiKey!));
	}

	[Fact]
	public async Task GetJsonAsync_BuildsKeyedUrl_WithEscapedQuery()
	{
		FakeSteamHandler handler = new();
		handler.Queue(HttpStatusCode.OK, """{"result":{"status":1}}""");
		using var client = new SteamWebApiClient("test-key", handler);

		using System.Text.Json.JsonDocument document = await client.GetJsonAsync(
			"IDOTA2Match_570/GetMatchHistory/v1/",
			[("hero_id", "14"), ("note", "a b&c")],
			CancellationToken.None);

		Assert.Single(handler.Requests);
		Assert.Equal(
			"https://api.steampowered.com/IDOTA2Match_570/GetMatchHistory/v1/?key=test-key&hero_id=14&note=a%20b%26c",
			handler.Requests[0].AbsoluteUri);
		Assert.Equal(1, document.RootElement.GetProperty("result").GetProperty("status").GetInt32());
	}

	[Fact]
	public async Task GetJsonAsync_ThrowsHttpRequestException_OnNonSuccessStatus()
	{
		FakeSteamHandler handler = new();
		handler.Queue(HttpStatusCode.Forbidden, "");
		using var client = new SteamWebApiClient("test-key", handler);

		HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
			client.GetJsonAsync("IEconDOTA2_570/GetHeroes/v1/", [], CancellationToken.None));

		Assert.Contains("403 Forbidden", error.Message);
		Assert.Contains("IEconDOTA2_570/GetHeroes/v1/", error.Message);
	}

	[Fact]
	public async Task GetJsonAsync_ThrowsJsonException_OnMalformedBody()
	{
		FakeSteamHandler handler = new();
		handler.Queue(HttpStatusCode.OK, "not json at all");
		using var client = new SteamWebApiClient("test-key", handler);

		await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
			client.GetJsonAsync("IEconDOTA2_570/GetGameItems/v1/", [], CancellationToken.None));
	}

	[Fact]
	public void PublicConstructor_BuildsLiveClient_AndDisposeIsSafe()
	{
		using var client = new SteamWebApiClient("live-key");
		Assert.Equal("https://api.steampowered.com/", SteamWebApiClient.BaseUrl);
	}

	[Fact]
	public async Task Client_HonoursCancelledTokens()
	{
		FakeSteamHandler handler = new();
		handler.Queue(HttpStatusCode.OK, "{}");
		using var client = new SteamWebApiClient("test-key", handler);
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			client.GetJsonAsync("IEconDOTA2_570/GetHeroes/v1/", [], cancelled.Token));
	}

	/// <summary>Scripted transport: records request URIs, replays queued responses.</summary>
	internal sealed class FakeSteamHandler : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses = new();

		public List<Uri> Requests { get; } = [];

		public void Queue(HttpStatusCode status, string body) =>
			_responses.Enqueue(new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			});

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.RequestUri!);
			return cancellationToken.IsCancellationRequested
				? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
				: Task.FromResult(_responses.Dequeue());
		}
	}
}
