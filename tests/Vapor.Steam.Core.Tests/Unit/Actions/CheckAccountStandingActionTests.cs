using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>
/// Example coverage for CheckAccountStandingAction over the injected fetch
/// seam: SteamID resolution (payload override wins over session cookies),
/// the clean/restricted/banned aggregation, informative failure mapping, and
/// the full output contract.
/// </summary>
public sealed class CheckAccountStandingActionTests : IDisposable
{
	private const ulong SteamId = 76561198000000000UL;

	private const string SteamIdText = "76561198000000000";

	private readonly List<BotSession> _sessions = [];
	private readonly List<SteamWebHandler> _webHandlers = [];

	private static Dictionary<string, object?> Payload() => new()
	{
		["steam_id"] = SteamIdText
	};

	[Fact]
	public async Task ExecuteAsync_CleanStanding_ReportsCleanWithDetails()
	{
		var action = CreateAction(new AccountStanding(SteamId, VacBanned: false, 0, 0, 0, CommunityBanned: false, "none", Limited: false, 5));
		var session = CreateSession();

		var result = await action.ExecuteAsync(session, Payload(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("clean", result.Output!["standing"]);
		Assert.Equal(false, result.Output["vacBanned"]);
		Assert.Equal("none", result.Output["economyBan"]);
		Assert.Equal(false, result.Output["limited"]);
		Assert.Equal(5, result.Output["steamLevel"]);
	}

	[Fact]
	public async Task ExecuteAsync_PayloadSteamId_OverridesSessionIdentity()
	{
		ulong? fetched = null;
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance)
		{
			FetchOverride = (steamId, _) =>
			{
				fetched = steamId;
				return Task.FromResult(new AccountStanding(steamId, false, 0, 0, 0, false, "none", null, null));
			}
		};
		var session = CreateSession();

		await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = SteamId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
			CancellationToken.None);

		Assert.Equal(SteamId, fetched);
	}

	[Fact]
	public async Task ExecuteAsync_MalformedPayloadSteamId_DoesNotResolveToZero()
	{
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance);
		var session = CreateSession(); // no web handler → no session identity fallback either

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = "garbage" },
			CancellationToken.None);

		// "garbage" must not silently parse to SteamId 0 — it yields no identity at all.
		Assert.False(result.Success);
		Assert.Contains("SteamID unknown", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoIdentityAvailable_Fails()
	{
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance);
		var session = CreateSession(); // no web handler cookies → no session identity

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("SteamID unknown", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_ProbationEconomy_ReportsRestricted()
	{
		var action = CreateAction(new AccountStanding(SteamId, false, 0, 0, 0, false, "probation", null, null));
		var session = CreateSession();

		var result = await action.ExecuteAsync(session, Payload(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("restricted", result.Output!["standing"]);
	}

	[Theory]
	[InlineData(true, 0, 0, "none", false, "banned")] // VAC ban
	[InlineData(false, 0, 0, "banned", false, "banned")] // economy ban
	[InlineData(false, 0, 2, "none", false, "banned")] // game bans
	[InlineData(false, 0, 0, "none", true, "banned")] // community ban
	[InlineData(false, 0, 0, "probation", false, "restricted")]
	[InlineData(false, 0, 0, "none", false, "clean")]
	public async Task ExecuteAsync_ClassificationMatrix_MatchesFlagCombination(
		bool vac, int vacCount, int gameBans, string economy, bool community, string expected)
	{
		var action = CreateAction(new AccountStanding(SteamId, vac, vacCount, gameBans, 0, community, economy, null, null));
		var session = CreateSession();

		var result = await action.ExecuteAsync(session, Payload(), CancellationToken.None);

		Assert.Equal(expected, result.Output!["standing"]);
	}

	[Fact]
	public async Task ExecuteAsync_FetchFailure_MapsToInformativeError()
	{
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance)
		{
			FetchOverride = (_, _) => throw new InvalidOperationException("Web API key unavailable")
		};
		var session = CreateSession();

		var result = await action.ExecuteAsync(session, Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Web API key unavailable", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public void Metadata_DescribesStandingProbe()
	{
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance);

		Assert.Equal("check_account_standing", action.Name);
		Assert.Equal("check_account_standing", action.Metadata.Name);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_NoWebHandlerAndNoOverride_Fails()
	{
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance);
		var session = new BotSession(
			"standing_account",
			new AccountCredentials("standing_account", "password"),
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);
		_sessions.Add(session);

		var result = await action.ExecuteAsync(session, Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_ClientFactorySeam_FetchesThroughRealClient()
	{
		// The factory seam routes through a real SteamAccountStandingClient over
		// the fake handler — the production path without the fetch override.
		// The bare fake answers 200 with an empty body, so the key page read
		// degrades to "no key" and the action maps the client's complaint.
		var action = new CheckAccountStandingAction(
			NullLogger<CheckAccountStandingAction>.Instance,
			webHandler => new SteamAccountStandingClient(webHandler, NullLogger<SteamAccountStandingClient>.Instance));
		var session = CreateSession();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = "76561197960265728" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Web API key", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_RealClientHappyPath_ReportsStanding()
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			new KeyedFakeHandler());
		_webHandlers.Add(webHandler);
		var session = new BotSession(
			"standing_account",
			new AccountCredentials("standing_account", "password"),
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: webHandler,
			eventCallback: null);
		_sessions.Add(session);

		var action = new CheckAccountStandingAction(
			NullLogger<CheckAccountStandingAction>.Instance,
			webHandler => new SteamAccountStandingClient(webHandler, NullLogger<SteamAccountStandingClient>.Instance));

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = "76561197960265728" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("banned", result.Output!["standing"]);
		Assert.Equal("probation", result.Output["economyBan"]);
		Assert.Equal(13, result.Output["steamLevel"]);
	}

	/// <summary>Serves the standing fixtures: key page, GetPlayerBans, GetSteamLevel.</summary>
	private sealed class KeyedFakeHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string body = request.RequestUri!.Host switch
			{
				"steamcommunity.com" => """<html><body><p>Key: 0123456789ABCDEF0123456789ABCDEF</p></body></html>""",
				_ when request.RequestUri!.AbsolutePath.Contains("GetPlayerBans") =>
					"""{"players":[{"SteamId":"76561197960265728","CommunityBanned":false,"VACBanned":true,"NumberOfVACBans":1,"DaysSinceLastBan":42,"NumberOfGameBans":0,"EconomyBan":"probation"}]}""",
				_ => """{"response":{"player_level":13}}""",
			};
			return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
			});
		}
	}

	[Fact]
	public async Task ExecuteAsync_CancelledFetch_RethrowsOperationCanceled()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		var action = new CheckAccountStandingAction(NullLogger<CheckAccountStandingAction>.Instance)
		{
			FetchOverride = (_, ct) => Task.FromCanceled<AccountStanding>(ct)
		};
		var session = CreateSession();

		// Task.FromCanceled surfaces as TaskCanceledException (an OCE subclass).
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => action.ExecuteAsync(session, Payload(), cts.Token));
	}

	private static CheckAccountStandingAction CreateAction(AccountStanding standing) =>
		new(NullLogger<CheckAccountStandingAction>.Instance)
		{
			FetchOverride = (_, _) => Task.FromResult(standing)
		};

	/// <summary>Session with a live (cookie-less) web handler, mirroring a logged-in bot.</summary>
	private BotSession CreateSession()
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			new FakeHttpMessageHandler());
		_webHandlers.Add(webHandler);
		var session = new BotSession(
			"standing_account",
			new AccountCredentials("standing_account", "password"),
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: webHandler,
			eventCallback: null);
		_sessions.Add(session);
		return session;
	}

	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}

		foreach (var webHandler in _webHandlers)
		{
			webHandler.Dispose();
		}
	}
}
