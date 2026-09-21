using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Unit tests for the dev/apikey page scraping: a missing, truncated, or
/// too-short key must read as "no key" (null) — never as a partial secret.
/// </summary>
public sealed class SteamWebApiKeyFetcherTests
{
	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(Responder(request));
		}
	}

	private static (SteamWebHandler WebHandler, FakeHttpMessageHandler Fake) Create()
	{
		var fake = new FakeHttpMessageHandler();
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (webHandler, fake);
	}

	private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html")
	};

	[Fact]
	public async Task FetchAsync_RevealedKey_IsReturned()
	{
		var (webHandler, fake) = Create();
		fake.Responder = _ => Html(
			"<html><body><p>Key: 0123456789ABCDEF0123456789ABCDEF</p></body></html>");

		string? key = await SteamWebApiKeyFetcher.FetchAsync(webHandler, CancellationToken.None);

		Assert.Equal("0123456789ABCDEF0123456789ABCDEF", key);
	}

	[Fact]
	public async Task FetchAsync_TooShortKeyAfterPattern_YieldsNull()
	{
		// A short blob after the marker (placeholder text, "Registering..."
		// status) is not a key; falling through must read as "no key yet".
		var (webHandler, fake) = Create();
		fake.Responder = _ => Html("<html><body><p>Key: n/a</p></body></html>");

		string? key = await SteamWebApiKeyFetcher.FetchAsync(webHandler, CancellationToken.None);

		Assert.Null(key);
	}

	[Fact]
	public async Task FetchAsync_MarkerWithoutClosingParagraph_YieldsNull()
	{
		var (webHandler, fake) = Create();
		fake.Responder = _ => Html(
			"<html><body><p>Key: 0123456789ABCDEF0123456789ABCDEF and the page just ends");

		string? key = await SteamWebApiKeyFetcher.FetchAsync(webHandler, CancellationToken.None);

		Assert.Null(key);
	}

	[Fact]
	public async Task FetchAsync_PageWithoutMarker_YieldsNull()
	{
		var (webHandler, fake) = Create();
		fake.Responder = _ => Html("<html><body>account has no key registered</body></html>");

		string? key = await SteamWebApiKeyFetcher.FetchAsync(webHandler, CancellationToken.None);

		Assert.Null(key);
	}
}
