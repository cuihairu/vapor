using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class SteamWebHandlerResilienceTests
{
	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses = new();

		public int RequestsSent { get; private set; }

		public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestsSent++;
			return Task.FromResult(_responses.Count > 0
				? _responses.Dequeue()
				: new HttpResponseMessage(HttpStatusCode.OK));
		}
	}

	private static HttpResponseMessage Response(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null)
	{
		var response = new HttpResponseMessage(statusCode);
		if (body != null)
		{
			response.Content = new StringContent(body);
		}

		if (headers != null)
		{
			foreach (var header in headers)
			{
				response.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		return response;
	}

	private static SteamWebHandler CreateHandler(
		FakeHttpMessageHandler fake,
		Func<SteamWebHandlerConfig, SteamWebHandlerConfig>? configure = null)
	{
		var config = new SteamWebHandlerConfig
		{
			MaxRetries = 3,
			RetryDelayMs = 1,
			MaxRetryDelayMs = 8,
			RateLimitIntervalMs = 0,
			EnableCircuitBreaker = false
		};
		return new SteamWebHandler(configure?.Invoke(config) ?? config, NullLogger<SteamWebHandler>.Instance, fake);
	}

	[Fact]
	public async Task GetAsync_With429ThenSuccess_RetriesAndSucceeds()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response((HttpStatusCode)429, headers: new Dictionary<string, string> { ["Retry-After"] = "0" }));
		fake.Enqueue(Response(HttpStatusCode.OK, "ok"));
		using var handler = CreateHandler(fake);

		var response = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.True(response.IsSuccess);
		Assert.Equal("ok", response.Body);
		Assert.Equal(2, fake.RequestsSent);
		Assert.Equal(1, handler.Metrics.RateLimited429);
		Assert.Equal(1, handler.Metrics.Retries);
		Assert.Equal(1, handler.Metrics.Successes);
	}

	[Fact]
	public async Task GetAsync_With429RetryAfterHeader_BoundedByConfig()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response((HttpStatusCode)429, headers: new Dictionary<string, string> { ["Retry-After"] = "3600" }));
		fake.Enqueue(Response(HttpStatusCode.OK, "ok"));
		using var handler = CreateHandler(fake, c => c with { MaxRetryAfterSeconds = 0 });

		var response = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.True(response.IsSuccess);
		Assert.Equal(1, handler.Metrics.RateLimited429);
	}

	[Fact]
	public async Task GetAsync_With5xxErrors_RetriesWithExponentialBackoff()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response(HttpStatusCode.InternalServerError));
		fake.Enqueue(Response(HttpStatusCode.BadGateway));
		fake.Enqueue(Response(HttpStatusCode.OK, "recovered"));
		using var handler = CreateHandler(fake);

		var response = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.True(response.IsSuccess);
		Assert.Equal("recovered", response.Body);
		Assert.Equal(3, fake.RequestsSent);
		Assert.Equal(2, handler.Metrics.ServerErrors5xx);
		Assert.Equal(2, handler.Metrics.Retries);
	}

	[Fact]
	public async Task GetAsync_WithPersistent5xx_ReturnsLastErrorResponse()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response(HttpStatusCode.ServiceUnavailable));
		fake.Enqueue(Response(HttpStatusCode.ServiceUnavailable));
		fake.Enqueue(Response(HttpStatusCode.ServiceUnavailable));
		using var handler = CreateHandler(fake);

		var response = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.False(response.IsSuccess);
		Assert.Equal(503, response.StatusCodeNumber);
		Assert.Equal(3, fake.RequestsSent);
		Assert.Equal(3, handler.Metrics.ServerErrors5xx);
	}

	[Fact]
	public async Task GetAsync_With404_DoesNotRetry()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response(HttpStatusCode.NotFound));
		using var handler = CreateHandler(fake);

		var response = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.Equal(404, response.StatusCodeNumber);
		Assert.Equal(1, fake.RequestsSent);
		Assert.Equal(0, handler.Metrics.Retries);
		Assert.Equal(1, handler.Metrics.ClientErrors4xx);
	}

	[Fact]
	public async Task GetAsync_WhenCircuitBreakerOpens_RejectsWithoutHittingNetwork()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response((HttpStatusCode)429));
		fake.Enqueue(Response((HttpStatusCode)429));
		// Third request should never reach the network.
		using var handler = CreateHandler(fake, c => c with
		{
			MaxRetries = 1,
			EnableCircuitBreaker = true,
			CircuitBreakerFailureThreshold = 2
		});

		var first = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));
		var second = await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.Equal(429, first.StatusCodeNumber);
		Assert.Equal(429, second.StatusCodeNumber);
		Assert.Equal(CircuitBreakerState.Open, handler.CircuitState);

		await Assert.ThrowsAsync<CircuitBreakerOpenException>(
			() => handler.GetAsync(new Uri("https://store.steampowered.com/api/x")));

		Assert.Equal(2, fake.RequestsSent);
		Assert.Equal(1, handler.Metrics.CircuitBreakerRejections);
	}

	[Fact]
	public async Task GetAsync_After429ThenSuccess_BreakerStaysClosed()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response((HttpStatusCode)429, headers: new Dictionary<string, string> { ["Retry-After"] = "0" }));
		fake.Enqueue(Response(HttpStatusCode.OK));
		using var handler = CreateHandler(fake, c => c with
		{
			EnableCircuitBreaker = true,
			CircuitBreakerFailureThreshold = 2
		});

		await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		Assert.Equal(CircuitBreakerState.Closed, handler.CircuitState);
	}

	[Fact]
	public async Task Metrics_SnapshotReflectsCounters()
	{
		var fake = new FakeHttpMessageHandler();
		fake.Enqueue(Response(HttpStatusCode.OK));
		using var handler = CreateHandler(fake);

		await handler.GetAsync(new Uri("https://store.steampowered.com/api/x"));

		WebRequestMetricsSnapshot snapshot = handler.Metrics.Snapshot();

		Assert.Equal(1, snapshot.TotalRequests);
		Assert.Equal(1, snapshot.Successes);
		Assert.Equal(0, snapshot.FailureRate);
	}
}
