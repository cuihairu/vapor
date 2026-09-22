using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Request-construction and lifecycle coverage for <see cref="SteamWebHandler"/>:
/// cookie/header plumbing per host, post/retry/dispose paths that the resilience
/// suite does not exercise.
/// </summary>
public sealed class SteamWebHandlerRequestTests
{
	private sealed class CapturingHandler : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = [];
		public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (Requests)
			{
				Requests.Add(request);
			}

			return Task.FromResult(Responder(request));
		}

		/// <summary>Gets a request header from the most recent request, joined across values.</summary>
		public string? Header(string name)
		{
			lock (Requests)
			{
				return Requests[^1].Headers.TryGetValues(name, out var values)
					? string.Join(", ", values)
					: null;
			}
		}

		/// <summary>Gets a request header from the request at <paramref name="index"/>.</summary>
		public string? Header(int index, string name)
		{
			lock (Requests)
			{
				return Requests[index].Headers.TryGetValues(name, out var values)
					? string.Join(", ", values)
					: null;
			}
		}
	}

	private static (SteamWebHandler Handler, CapturingHandler Fake) Create(Func<SteamWebHandlerConfig, SteamWebHandlerConfig>? overrideConfig = null)
	{
		var config = new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 3, RetryDelayMs = 1, EnableCircuitBreaker = false };
		if (overrideConfig != null)
		{
			config = overrideConfig(config);
		}

		var fake = new CapturingHandler();
		var handler = new SteamWebHandler(config, NullLogger<SteamWebHandler>.Instance, fake);
		return (handler, fake);
	}

	[Fact]
	public async Task PostAsync_CarriesCookiesContentAndCustomHeaders()
	{
		var (handler, fake) = Create();
		handler.SetSessionCookies("session-id", "76561198000000042%7C%7Ctoken");

		var response = await handler.PostAsync(
			new Uri("https://steamcommunity.com/gateway/do"),
			content: new FormUrlEncodedContent([new KeyValuePair<string, string>("a", "b")]),
			headers: new Dictionary<string, string> { ["X-Custom"] = "yes" });

		Assert.True(response.IsSuccess);
		Assert.Contains("sessionid=session-id", fake.Header("Cookie"), StringComparison.Ordinal);
		Assert.Contains("steamLoginSecure=76561198000000042%7C%7Ctoken", fake.Header("Cookie"), StringComparison.Ordinal);
		Assert.Equal("yes", fake.Header("X-Custom"));
		Assert.Equal("application/x-www-form-urlencoded", fake.Requests[^1].Content!.Headers.ContentType!.MediaType);
	}

	[Fact]
	public async Task GetAsync_AddsRefererAndOriginPerHost()
	{
		var (handler, fake) = Create();
		await handler.GetAsync(new Uri("https://steamcommunity.com/market/overview"));
		await handler.GetAsync(new Uri("https://store.steampowered.com/api/appdetails"));

		Assert.Equal("https://steamcommunity.com/", fake.Header(0, "Referer"));
		Assert.Equal("https://steamcommunity.com/", fake.Header(0, "Origin"));
		Assert.Equal("https://store.steampowered.com/", fake.Header(1, "Referer"));
		Assert.Equal("https://store.steampowered.com/", fake.Header(1, "Origin"));
	}

	[Fact]
	public async Task GetAsync_SetsUserAgentAndDefaultHeaders()
	{
		var (handler, fake) = Create();

		await handler.GetAsync(new Uri("https://steamcommunity.com/x"));

		Assert.Equal("Vapor/1.0", fake.Header("User-Agent"));
		Assert.NotNull(fake.Header("Accept"));
		Assert.NotNull(fake.Header("Accept-Language"));
	}

	[Fact]
	public async Task ClearCookies_RemovesSessionCookies()
	{
		var (handler, fake) = Create();
		handler.SetSessionCookies("sid", "token");
		Assert.NotEmpty(handler.GetAllCookies());

		handler.ClearCookies();

		Assert.Empty(handler.GetAllCookies());
		await handler.GetAsync(new Uri("https://steamcommunity.com/x"));
		Assert.Null(fake.Header("Cookie"));
	}

	[Fact]
	public async Task AfterDispose_RequestsThrow()
	{
		var (handler, _) = Create();
		handler.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() =>
			handler.GetAsync(new Uri("https://steamcommunity.com/x")));

		handler.Dispose(); // idempotent
	}

	[Fact]
	public async Task GetAsync_WithNetworkErrorsThenSuccess_Retries()
	{
		var (handler, fake) = Create();
		int calls = 0;
		fake.Responder = _ =>
		{
			if (Interlocked.Increment(ref calls) < 3)
			{
				throw new HttpRequestException("connection reset");
			}

			return new HttpResponseMessage(HttpStatusCode.OK);
		};

		var response = await handler.GetAsync(new Uri("https://steamcommunity.com/x"));

		Assert.True(response.IsSuccess);
		Assert.Equal(3, fake.Requests.Count);
	}

	[Fact]
	public async Task GetAsync_WithNetworkErrorOnFinalAttempt_Throws()
	{
		var (handler, fake) = Create(c => c with { MaxRetries = 2 });
		fake.Responder = _ => throw new HttpRequestException("down");

		await Assert.ThrowsAsync<HttpRequestException>(() =>
			handler.GetAsync(new Uri("https://steamcommunity.com/x")));
		Assert.Equal(2, fake.Requests.Count);
	}

	[Fact]
	public async Task GetAsync_With429OnFinalAttempt_ReturnsLastResponse()
	{
		var (handler, fake) = Create(c => c with { MaxRetries = 2 });
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

		var response = await handler.GetAsync(new Uri("https://steamcommunity.com/x"));

		Assert.Equal(429, response.StatusCodeNumber);
		Assert.Equal(2, fake.Requests.Count);
	}

	[Fact]
	public async Task GetAsync_429WithoutParsableRetryAfter_UsesFallbackDelay()
	{
		// Without a Retry-After header the conservative fallback delay applies and
		// the request still completes with the final 429.
		var (handler, fake) = Create(c => c with { MaxRetries = 2 });
		fake.Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		var response = await handler.GetAsync(new Uri("https://steamcommunity.com/x"));
		Assert.Equal(429, response.StatusCodeNumber);
		Assert.Equal(2, fake.Requests.Count);

		// An HTTP-date Retry-After (valid per spec, unparseable as seconds) takes
		// the same fallback path.
		var (handler2, fake2) = Create(c => c with { MaxRetries = 2 });
		fake2.Responder = _ =>
		{
			var message = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
			message.Headers.TryAddWithoutValidation("Retry-After", "Wed, 21 Oct 2015 07:28:00 GMT");
			return message;
		};
		var second = await handler2.GetAsync(new Uri("https://steamcommunity.com/x"));
		Assert.Equal(429, second.StatusCodeNumber);
		Assert.Equal(2, fake2.Requests.Count);
	}

	[Fact]
	public void Config_CarriesDefaults()
	{
		var config = new SteamWebHandlerConfig();

		Assert.Equal("Vapor/1.0", config.UserAgent);
		Assert.Equal(5, config.MaxRetries);
		Assert.Equal(1000, config.RetryDelayMs);
		Assert.True(config.EnableCircuitBreaker);
		Assert.Equal(10, config.CircuitBreakerFailureThreshold);
	}

	[Fact]
	public async Task GetAndPost_WithNullUrl_Throw()
	{
		var (handler, _) = Create();

		await Assert.ThrowsAsync<ArgumentNullException>(() => handler.GetAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => handler.GetAsync(null!));
		await Assert.ThrowsAsync<ArgumentNullException>(() => handler.PostAsync(null!, content: null));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void SetSessionCookies_InvalidArguments_Throw(string? value)
	{
		var (handler, _) = Create();

		Assert.Throws<ArgumentNullException>(() => handler.SetSessionCookies(value!, "token"));
		Assert.Throws<ArgumentNullException>(() => handler.SetSessionCookies("sid", value!));
	}

	[Fact]
	public async Task GetAsync_WithZeroRetries_FailsWithInvalidOperationException()
	{
		var (handler, fake) = Create(c => c with { MaxRetries = 0 });
		fake.Responder = _ => throw new HttpRequestException("down");

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			handler.GetAsync(new Uri("https://steamcommunity.com/x")));

		Assert.Contains("all retry attempts", exception.Message);
		Assert.Empty(fake.Requests);
	}

	[Fact]
	public async Task GetAsync_SecondRequestInsideRateLimitWindow_IsDelayed()
	{
		var (handler, _) = Create(c => c with { RateLimitIntervalMs = 120 });

		var started = DateTimeOffset.UtcNow;
		await handler.GetAsync(new Uri("https://steamcommunity.com/a"));
		await handler.GetAsync(new Uri("https://steamcommunity.com/b"));

		Assert.True(
			DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(100),
			"second request should have been throttled by the rate limiter");
	}

	[Fact]
	public void HttpClient_ExposesUnderlyingClient()
	{
		var (handler, _) = Create();

		Assert.NotNull(handler.HttpClient);
	}

	[Fact]
	public void SteamWebResponse_HelperProperties_ClassifyStatusCodes()
	{
		SteamWebResponse Make(int code) => new(
			(System.Net.HttpStatusCode)code,
			code,
			new Dictionary<string, string> { ["Retry-After"] = "5" });

		Assert.True(Make(200).IsSuccess);
		Assert.False(Make(100).IsSuccess); // informational 1xx are not success
		Assert.True(Make(302).IsRedirect);
		Assert.True(Make(403).IsClientError);
		Assert.True(Make(503).IsServerError);

		var response = Make(201);
		Assert.False(response.IsRedirect);
		Assert.False(response.IsClientError);
		Assert.False(response.IsServerError);
		Assert.Equal("5", response.Headers["Retry-After"]);
		Assert.Equal(201, response.StatusCodeNumber);
	}
}
