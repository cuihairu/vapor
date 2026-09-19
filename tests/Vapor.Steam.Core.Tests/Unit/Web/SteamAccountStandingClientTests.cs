using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class SteamAccountStandingClientTests
{
	private const ulong SteamId = 76561197960265728UL;

	private const string KeyPage = """
		<html><body><p>Key: 0123456789ABCDEF0123456789ABCDEF</p></body></html>
		""";

	private const string BansBody = """
		{"players":[{"SteamId":"76561197960265728","CommunityBanned":false,"VACBanned":true,
		"NumberOfVACBans":1,"DaysSinceLastBan":42,"NumberOfGameBans":0,"EconomyBan":"probation"}]}
		""";

	private const string LevelBody = """{"response":{"player_level":13}}""";

	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public Func<Uri, HttpResponseMessage> Responder { get; set; } =
			_ => new HttpResponseMessage(HttpStatusCode.OK);

		public List<Uri> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.RequestUri!);
			return Task.FromResult(Responder(request.RequestUri!));
		}
	}

	private static (SteamAccountStandingClient Client, FakeHttpMessageHandler Fake) Create(
		string? keyPage = KeyPage,
		string bansBody = BansBody,
		string levelBody = LevelBody,
		HttpStatusCode? keyStatus = null,
		HttpStatusCode? banStatus = null,
		HttpStatusCode? levelStatus = null)
	{
		var fake = new FakeHttpMessageHandler
		{
			Responder = uri => uri.Host switch
			{
				"steamcommunity.com" => keyStatus is { } ks ? Status(ks) : Json(keyPage ?? string.Empty),
				_ when uri.AbsolutePath.Contains("GetPlayerBans") => banStatus is { } bs ? Status(bs) : Json(bansBody),
				_ when uri.AbsolutePath.Contains("GetSteamLevel") => levelStatus is { } ls ? Status(ls) : Json(levelBody),
				_ => throw new InvalidOperationException($"unexpected request {uri}"),
			}
		};

		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
		return (new SteamAccountStandingClient(webHandler, NullLogger<SteamAccountStandingClient>.Instance), fake);
	}

	[Fact]
	public async Task GetStandingAsync_ParsesBansAndLevel()
	{
		var (client, _) = Create();

		var standing = await client.GetStandingAsync(SteamId);

		Assert.True(standing.VacBanned);
		Assert.Equal(1, standing.NumberOfVacBans);
		Assert.Equal(0, standing.NumberOfGameBans);
		Assert.Equal(42, standing.DaysSinceLastBan);
		Assert.False(standing.CommunityBanned);
		Assert.Equal("probation", standing.EconomyBan);
		Assert.False(standing.Limited);
		Assert.Equal(13, standing.SteamLevel);
	}

	[Fact]
	public async Task GetStandingAsync_CleanAccount_LevelZeroMeansLimited()
	{
		var (client, _) = Create(bansBody: """
			{"players":[{"SteamId":"76561197960265728","CommunityBanned":false,"VACBanned":false,
			"NumberOfVACBans":0,"DaysSinceLastBan":0,"NumberOfGameBans":0,"EconomyBan":"none"}]}
			""", levelBody: """{"response":{"player_level":0}}""");

		var standing = await client.GetStandingAsync(SteamId);

		Assert.False(standing.VacBanned);
		Assert.True(standing.Limited);
		Assert.Equal(0, standing.SteamLevel);
	}

	[Fact]
	public async Task GetStandingAsync_LevelEndpointFails_BansStillAuthoritative()
	{
		var (client, _) = Create(levelStatus: HttpStatusCode.Forbidden);

		var standing = await client.GetStandingAsync(SteamId);

		Assert.True(standing.VacBanned);
		Assert.Null(standing.Limited);
		Assert.Null(standing.SteamLevel);
	}

	[Fact]
	public async Task GetStandingAsync_LevelBodyMissingPlayerLevel_LimitedUnknown()
	{
		var (client, _) = Create(levelBody: """{"response":{}}""");

		var standing = await client.GetStandingAsync(SteamId);

		Assert.Null(standing.Limited);
		Assert.Null(standing.SteamLevel);
	}

	[Fact]
	public async Task GetStandingAsync_LevelBodyMalformedJson_LimitedUnknown()
	{
		var (client, _) = Create(levelBody: """{"response": [broken""");

		var standing = await client.GetStandingAsync(SteamId);

		// Bans stay authoritative; only the degraded limited marker is unknown.
		Assert.True(standing.VacBanned);
		Assert.Null(standing.Limited);
		Assert.Null(standing.SteamLevel);
	}

	[Fact]
	public async Task GetStandingAsync_KeyPageNotSuccess_Throws()
	{
		var (client, _) = Create(keyStatus: HttpStatusCode.Forbidden);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStandingAsync(SteamId));

		Assert.Contains("Web API key", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetStandingAsync_MissingKey_Throws()
	{
		var (client, _) = Create(keyPage: "<html><body>no key here</body></html>");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStandingAsync(SteamId));

		Assert.Contains("Web API key", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetStandingAsync_EmptyPlayers_Throws()
	{
		var (client, _) = Create(bansBody: """{"players":[]}""");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStandingAsync(SteamId));

		Assert.Contains("no player entry", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetStandingAsync_BanEndpointFails_ThrowsWithStatus()
	{
		var (client, _) = Create(banStatus: HttpStatusCode.Unauthorized);

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetStandingAsync(SteamId));

		Assert.Contains("GetPlayerBans failed", ex.Message, StringComparison.Ordinal);
		Assert.Contains("401", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GetStandingAsync_RequestsCarryKeyAndSteamId()
	{
		var (client, fake) = Create();

		await client.GetStandingAsync(SteamId);

		// Key page, then both API calls authenticated with the fetched key.
		Assert.Equal(3, fake.Requests.Count);
		Assert.Contains("key=0123456789ABCDEF0123456789ABCDEF", fake.Requests[1].Query, StringComparison.Ordinal);
		Assert.Contains($"steamids={SteamId}", fake.Requests[1].Query, StringComparison.Ordinal);
		Assert.Contains("IPlayerService/GetSteamLevel", fake.Requests[2].AbsolutePath, StringComparison.Ordinal);
	}

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
	};

	private static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}
