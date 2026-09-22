using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Client-level behavior of <see cref="SteamAchievementsClient"/> over a fake
/// HTTP transport: the fetch-failure contract (throw, never silently parse an
/// empty page). Page-parsing branches live in SteamAchievementsPageContractTests.
/// </summary>
public sealed class SteamAchievementsClientTests
{
	private const ulong SteamId = 76561198000000000UL;
	private const uint AppId = 400;

	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<HttpResponseMessage> Responder { get; set; } =
			() => new HttpResponseMessage(HttpStatusCode.OK);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(Responder());
		}
	}

	private static SteamAchievementsClient Create(HttpStatusCode status, string body)
	{
		var fake = new FakeHttpMessageHandler
		{
			Responder = () => new HttpResponseMessage(status)
			{
				Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html")
			}
		};
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return new SteamAchievementsClient(webHandler, NullLogger<SteamAchievementsClient>.Instance);
	}

	[Fact]
	public async Task GetAchievementsAsync_SuccessWithEmptyBody_ThrowsFetchFailure()
	{
		// A 200 with an empty body enters the guard through the || right arm and
		// takes the "empty body" arm of the warning ternary: success alone must
		// never masquerade as an empty-but-valid achievements page.
		var client = Create(HttpStatusCode.OK, string.Empty);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => client.GetAchievementsAsync(SteamId, AppId));

		Assert.Contains("Failed to fetch achievements page", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetAchievementsAsync_NonSuccessStatus_ThrowsFetchFailure()
	{
		// The non-success arm of the same guard: the thrown message must carry
		// the failure rather than the page parse returning an empty result.
		var client = Create(HttpStatusCode.Forbidden, "<html>login required</html>");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => client.GetAchievementsAsync(SteamId, AppId));

		Assert.Contains("Failed to fetch achievements page", ex.Message, StringComparison.Ordinal);
	}
}
