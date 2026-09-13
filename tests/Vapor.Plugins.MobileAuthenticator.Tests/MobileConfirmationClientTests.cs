using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Plugins.MobileAuthenticator;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

/// <summary>
/// End-to-end coverage for <see cref="MobileConfirmationClient"/> (constructor, URL
/// signing, cookie-based SteamID resolution and HTTP handling) against a stubbed
/// transport. SteamWebHandler only accepts a custom HttpMessageHandler through an
/// internal constructor that is not visible to this test project, so its HttpClient
/// field is swapped via reflection — keeping these tests deterministic and network-free.
/// </summary>
public class MobileConfirmationClientTests
{
	private const string IdentitySecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
	private const ulong SteamId = 76561197960265729UL;
	private const long FixedSteamTime = 1735689600; // 2025-01-01T00:00:00Z

	[Fact]
	public void Constructor_NullWebHandler_Throws()
	{
		Assert.Throws<ArgumentNullException>(
			() => new MobileConfirmationClient(null!, CreateSynchronizer()));
	}

	[Fact]
	public void Constructor_NullTimeSynchronizer_Throws()
	{
		Assert.Throws<ArgumentNullException>(
			() => new MobileConfirmationClient(CreateWebHandler(new StubHttpHandler(), steamLoginSecure: null), null!));
	}

	[Fact]
	public void Constructor_WithDependencies_BuildsClient()
	{
		var webHandler = CreateWebHandler(new StubHttpHandler(), steamLoginSecure: null);
		var synchronizer = CreateSynchronizer();

		var withoutLogger = new MobileConfirmationClient(webHandler, synchronizer);
		var withLogger = new MobileConfirmationClient(webHandler, synchronizer, NullLogger<MobileConfirmationClient>.Instance);

		Assert.IsAssignableFrom<IMobileConfirmationClient>(withoutLogger);
		Assert.IsAssignableFrom<IMobileConfirmationClient>(withLogger);
	}

	[Fact]
	public async Task GetConfirmationsAsync_WithoutSteamIdCookie_ReturnsFailure()
	{
		var http = new StubHttpHandler();
		var client = new MobileConfirmationClient(
			CreateWebHandler(http, steamLoginSecure: null), CreateSynchronizer());

		var result = await client.GetConfirmationsAsync(IdentitySecret, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Unable to determine own SteamID from session cookies", result.Error);
		// The failure is detected before any HTTP request is attempted.
		Assert.Empty(http.RequestedUrls);
	}

	[Fact]
	public async Task RespondAsync_WithoutSteamIdCookie_ReturnsFailure()
	{
		var http = new StubHttpHandler();
		var client = new MobileConfirmationClient(
			CreateWebHandler(http, steamLoginSecure: null), CreateSynchronizer());

		var result = await client.RespondAsync(IdentitySecret, 111UL, 222UL, ConfirmationOperation.Allow, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Unable to determine own SteamID from session cookies", result.Error);
		Assert.Empty(http.RequestedUrls);
	}

	[Theory]
	[InlineData("not-a-steamid%7C%7Ctoken")]
	[InlineData("123%7C%7Ctoken")]
	public async Task GetConfirmationsAsync_WithUnusableSteamIdCookie_ReturnsFailure(string cookieValue)
	{
		var http = new StubHttpHandler();
		var client = new MobileConfirmationClient(CreateWebHandler(http, cookieValue), CreateSynchronizer());

		var result = await client.GetConfirmationsAsync(IdentitySecret, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Unable to determine own SteamID from session cookies", result.Error);
		Assert.Empty(http.RequestedUrls);
	}

	[Fact]
	public async Task GetConfirmationsAsync_WithPipeSeparatedCookie_ResolvesSteamId()
	{
		var http = new StubHttpHandler { Response = JsonResponse("""{ "success": true, "conf": [] }""") };
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}|token"), CreateSynchronizer());

		var result = await client.GetConfirmationsAsync(IdentitySecret, CancellationToken.None);

		Assert.True(result.Success);
		var request = Assert.Single(http.RequestedUrls);
		Assert.Equal("https://steamcommunity.com/mobileconf/getlist", request.GetLeftPart(UriPartial.Path));
	}

	[Fact]
	public async Task GetConfirmationsAsync_Success_BuildsSignedUrlAndParsesList()
	{
		var http = new StubHttpHandler
		{
			Response = JsonResponse("""{ "success": true, "conf": [ { "id": "111", "nonce": "222", "creator_id": "333", "headline": "Trade", "summary": "items", "type": 2 } ] }""")
		};
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.GetConfirmationsAsync(IdentitySecret, CancellationToken.None);

		Assert.True(result.Success);
		var confirmation = Assert.Single(result.Confirmations!);
		Assert.Equal(111UL, confirmation.Id);
		Assert.Equal("trade", confirmation.Type);

		var request = Assert.Single(http.RequestedUrls);
		Assert.Equal("https://steamcommunity.com/mobileconf/getlist", request.GetLeftPart(UriPartial.Path));

		var query = ParseQuery(request.Query);
		Assert.Equal(SteamDeviceId.FromSteamId(SteamId), query["p"]);
		Assert.Equal(SteamId.ToString(), query["a"]);
		Assert.Equal(ConfirmationHashGenerator.Generate(IdentitySecret, FixedSteamTime, "conf"), query["k"]);
		Assert.Equal(FixedSteamTime.ToString(), query["t"]);
		Assert.Equal("react", query["m"]);
		Assert.Equal("conf", query["tag"]);
	}

	[Fact]
	public async Task GetConfirmationsAsync_HttpError_ReturnsFailureWithStatusCode()
	{
		var http = new StubHttpHandler { Response = new HttpResponseMessage(HttpStatusCode.Forbidden) };
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.GetConfirmationsAsync(IdentitySecret, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Failed to fetch confirmations: HTTP 403", result.Error);
	}

	[Fact]
	public async Task GetConfirmationsAsync_EmptyBody_TreatedAsFailure()
	{
		var http = new StubHttpHandler
		{
			Response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") }
		};
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.GetConfirmationsAsync(IdentitySecret, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Failed to fetch confirmations: HTTP 200", result.Error);
	}

	[Fact]
	public async Task RespondAsync_Allow_BuildsOperationUrlAndReturnsSuccess()
	{
		var http = new StubHttpHandler { Response = JsonResponse("""{ "success": true }""") };
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.RespondAsync(IdentitySecret, 111UL, 222UL, ConfirmationOperation.Allow, CancellationToken.None);

		Assert.True(result.Success);

		var request = Assert.Single(http.RequestedUrls);
		Assert.Equal("https://steamcommunity.com/mobileconf/ajaxop", request.GetLeftPart(UriPartial.Path));

		var query = ParseQuery(request.Query);
		Assert.Equal("allow", query["tag"]);
		Assert.Equal(ConfirmationHashGenerator.Generate(IdentitySecret, FixedSteamTime, "allow"), query["k"]);
		// The ajaxop operation parameters are appended after the signed query.
		Assert.Equal("allow", query["op"]);
		Assert.Equal("111", query["cid"]);
		Assert.Equal("222", query["ck"]);
	}

	[Fact]
	public async Task RespondAsync_Cancel_SignsWithCancelTag()
	{
		var http = new StubHttpHandler { Response = JsonResponse("""{ "success": true }""") };
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.RespondAsync(IdentitySecret, 111UL, 222UL, ConfirmationOperation.Cancel, CancellationToken.None);

		Assert.True(result.Success);
		var query = ParseQuery(Assert.Single(http.RequestedUrls).Query);
		Assert.Equal("cancel", query["tag"]);
		Assert.Equal(ConfirmationHashGenerator.Generate(IdentitySecret, FixedSteamTime, "cancel"), query["k"]);
		Assert.Equal("cancel", query["op"]);
	}

	[Fact]
	public async Task RespondAsync_HttpError_ReturnsFailureWithStatusCode()
	{
		var http = new StubHttpHandler { Response = new HttpResponseMessage(HttpStatusCode.NotFound) };
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.RespondAsync(IdentitySecret, 111UL, 222UL, ConfirmationOperation.Allow, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Failed to respond to confirmation: HTTP 404", result.Error);
	}

	[Fact]
	public async Task RespondAsync_SteamRejection_ReturnsSteamMessage()
	{
		var http = new StubHttpHandler { Response = JsonResponse("""{ "success": false, "message": "Invalid authenticator" }""") };
		var client = new MobileConfirmationClient(CreateWebHandler(http, $"{SteamId}%7C%7Ctoken"), CreateSynchronizer());

		var result = await client.RespondAsync(IdentitySecret, 111UL, 222UL, ConfirmationOperation.Allow, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid authenticator", result.Error);
	}

	// --- list-parsing nullability branches ---

	[Fact]
	public void ParseConfirmationsList_NullType_YieldsNullType()
	{
		var result = MobileConfirmationClient.ParseConfirmationsList(
			"{ \"success\": true, \"conf\": [ { \"id\": \"1\", \"nonce\": \"2\", \"type\": null } ] }");

		Assert.True(result.Success);
		Assert.Null(result.Confirmations!.Single().Type);
	}

	[Fact]
	public void ParseConfirmationsList_NonNumericStringIds_AreSkipped()
	{
		var result = MobileConfirmationClient.ParseConfirmationsList(
			"{ \"success\": true, \"conf\": [ { \"id\": \"abc\", \"nonce\": \"2\" }, { \"id\": \"3\", \"nonce\": \"not-a-number\" } ] }");

		Assert.True(result.Success);
		Assert.Empty(result.Confirmations!);
	}

	// --- helpers ---

	private static SteamTimeSynchronizer CreateSynchronizer() =>
		new(_ => Task.FromResult(FixedSteamTime), new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(FixedSteamTime)));

	private static SteamWebHandler CreateWebHandler(StubHttpHandler http, string? steamLoginSecure)
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

		// SteamWebHandler builds its own real HttpClient; swap in one backed by the stub
		// transport so no request ever leaves the process.
		var field = typeof(SteamWebHandler).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field.SetValue(webHandler, new HttpClient(http));

		if (steamLoginSecure is not null)
		{
			webHandler.SetSessionCookies("sessionid", steamLoginSecure);
		}

		return webHandler;
	}

	private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, Encoding.UTF8, "application/json")
	};

	private static Dictionary<string, string> ParseQuery(string query)
	{
		var pairs = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
		return pairs
			.Select(pair => pair.Split('=', 2))
			.ToDictionary(
				parts => Uri.UnescapeDataString(parts[0]),
				parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
	}

	private sealed class FixedTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _now;

		public FixedTimeProvider(DateTimeOffset now) => _now = now;

		public override DateTimeOffset GetUtcNow() => _now;
	}

	private sealed class StubHttpHandler : HttpMessageHandler
	{
		public HttpResponseMessage Response { get; set; } = JsonResponse("{}");

		public List<Uri> RequestedUrls { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestedUrls.Add(request.RequestUri!);
			return Task.FromResult(Response);
		}
	}
}
