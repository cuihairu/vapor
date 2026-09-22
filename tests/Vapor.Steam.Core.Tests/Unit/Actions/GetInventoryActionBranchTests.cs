using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>
/// Branch coverage on top of <see cref="GetInventoryActionTests"/>: invalid
/// steam ids, unresolvable session cookies, the classic app_id/context_id
/// overrides, pagination with app-id dedup, and per-value-shape app_ids parsing.
/// </summary>
public sealed class GetInventoryActionBranchTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;

	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public async Task ExecuteAsync_InvalidSteamId_Fails()
	{
		var (action, session) = CreateAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = "not-a-number" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid steam_id parameter", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WebHandlerWithoutLoginCookies_Fails()
	{
		// A fresh handler has no steamLoginSecure cookie, so the own-SteamID
		// fallback cannot resolve.
		var (action, session) = CreateAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("session cookies do not carry a SteamID", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_AppIds_StringZero_IsSkippedByGuard()
	{
		// The string "0" parses as a number but fails the > 0 guard — skipped,
		// leaving the valid sibling as the only scanned app.
		var client = new FakeTradeClient();
		var requested = new List<uint>();
		client.InventoryHandler = (_, app, _, _) =>
		{
			requested.Add(app);
			return new InventoryResponse { Success = true, Items = [] };
		};
		var (action, session) = CreateAction(client);

		Dictionary<string, object?> payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{ "steam_id": "76561198000000042", "app_ids": ["0", "753"] }""")!;
		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal([753u], requested);
	}

	[Fact]
	public async Task ExecuteAsync_ClassicPath_DefaultsAndOverrides()
	{
		var client = new FakeTradeClient();
		var requested = new List<(uint AppId, ulong ContextId)>();
		client.InventoryHandler = (_, app, context, _) =>
		{
			requested.Add((app, context));
			return new InventoryResponse { Success = true, Items = [] };
		};
		var (action, session) = CreateAction(client);

		var defaults = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);
		var overrides = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_id"] = "753",
				["context_id"] = "6"
			},
			CancellationToken.None);

		Assert.True(defaults.Success);
		Assert.Equal(730u, defaults.Output!["app_id"]);
		Assert.Equal(2UL, defaults.Output["context_id"]);
		Assert.True(overrides.Success);
		Assert.Equal(753u, overrides.Output!["app_id"]);
		Assert.Equal(6UL, overrides.Output["context_id"]);
		Assert.Equal([(730u, 2UL), (753u, 6UL)], requested);
	}

	[Fact]
	public async Task ExecuteAsync_PaginatesAndDeduplicatesAppIds()
	{
		var client = new FakeTradeClient();
		int calls753 = 0;
		client.InventoryHandler = (_, app, _, _) =>
		{
			if (app == 753)
			{
				calls753++;
				if (calls753 == 1)
				{
					return new InventoryResponse
					{
						Success = true,
						Items = [new InventoryItem { AssetId = 1 }],
						HasMore = true,
						LastAssetId = 1
					};
				}

				return new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 2 }] };
			}

			return new InventoryResponse { Success = true, Items = [] };
		};
		var (action, session) = CreateAction(client);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_ids"] = new object[] { 753u, "753" } // duplicate across shapes
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, calls753); // one paginated scan, not two
		Assert.Equal(2, result.Output!["total_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_SingleAppReportsFailure_ReturnsError()
	{
		var client = new FakeTradeClient();
		client.InventoryHandler = (_, _, _, _) => new InventoryResponse { Success = false, Error = "inventory is private" };
		var (action, session) = CreateAction(client);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("inventory is private", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_InventoryBeyondSafetyLimit_StopsPaginating()
	{
		var client = new FakeTradeClient();
		int calls = 0;
		client.InventoryHandler = (_, _, _, _) =>
		{
			calls++;
			return new InventoryResponse
			{
				Success = true,
				Items = Enumerable.Range(0, 30_000)
					.Select(i => new InventoryItem { AssetId = (ulong)(calls * 100_000 + i) })
					.ToList(),
				HasMore = true,
				LastAssetId = (ulong)(calls * 100_000)
			};
		};
		var (action, session) = CreateAction(client);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		// Two pages reach 60000 items > the 50000 safety limit: stop and report
		// what was collected instead of looping forever.
		Assert.True(result.Success);
		Assert.Equal(2, calls);
		Assert.Equal(60000, result.Output!["total_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_SingleAppInventoryThrows_ReturnsError()
	{
		var client = new FakeTradeClient();
		client.InventoryHandler = (_, _, _, _) => throw new InvalidOperationException("connection reset");
		var (action, session) = CreateAction(client);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("connection reset", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_MultiAppScanThrows_ReturnsError()
	{
		var client = new FakeTradeClient();
		client.InventoryHandler = (_, app, _, _) =>
			app == 753
				? new InventoryResponse { Success = true, Items = [] }
				: throw new InvalidOperationException("scan blew up");
		var (action, session) = CreateAction(client);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_ids"] = new object[] { 753u, 730u }
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("scan blew up", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsValueShapes_AreAllUnderstood()
	{
		var client = new FakeTradeClient();
		var scannedApps = new List<uint>();
		client.InventoryHandler = (_, app, _, _) =>
		{
			scannedApps.Add(app);
			return new InventoryResponse { Success = true, Items = [] };
		};
		var (action, session) = CreateAction(client);

		var viaJsonArray = await action.ExecuteAsync(
			session,
			JsonSerializer.Deserialize<Dictionary<string, object?>>(
				"""{"steam_id":"76561198000000042","app_ids":[753, "730", 440, true, 0]}""")!,
			CancellationToken.None);
		var viaList = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["steam_id"] = OwnSteamId.ToString(),
				["app_ids"] = new List<object?> { 730, 753L, 570.0, " 252490 ", null }
			},
			CancellationToken.None);
		var viaSingleValue = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["steam_id"] = OwnSteamId.ToString(), ["app_ids"] = 730u },
			CancellationToken.None);
		var viaNullValue = await action.ExecuteAsync(
			session,
			JsonSerializer.Deserialize<Dictionary<string, object?>>(
				"""{"steam_id":"76561198000000042","app_ids":null}""")!,
			CancellationToken.None);

		Assert.All(new[] { viaJsonArray, viaList, viaSingleValue, viaNullValue }, r => Assert.True(r.Success));
		Assert.Equal(new[] { 753u, 730u, 440u }, scannedApps.Take(3).ToArray());
		Assert.Equal(new[] { 730u, 753u, 570u, 252490u }, scannedApps.Skip(3).Take(4).ToArray());
		// The single-value dispatch and the app_ids:null fallback each scan the
		// classic default (730/2).
		Assert.Equal(new[] { 730u, 730u }, scannedApps.Skip(7).ToArray());
		Assert.Equal(730u, viaNullValue.Output!["app_id"]);
	}

	private (GetInventoryAction Action, BotSession Session) CreateAction(FakeTradeClient tradeClient)
	{
		var action = new GetInventoryAction(NullLogger<GetInventoryAction>.Instance, _ => tradeClient);
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession(
			"test_account",
			credentials,
			registry.Object,
			_sessionLoggerMock.Object,
			null,
			CreateWebHandler(),
			null);
		_sessions.Add(session);
		return (action, session);
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	/// <summary>Fake trade client whose operations are scripted via delegates.</summary>
	private sealed class FakeTradeClient : ISteamTradeClient
	{
		public Func<ulong, uint, ulong, ulong?, InventoryResponse> InventoryHandler { get; set; } =
			(_, _, _, _) => new InventoryResponse { Success = true };

		public ulong? GetOwnSteamId() => OwnSteamId;

		public Task<InventoryResponse> GetInventoryAsync(
			ulong steamId, uint appId = 730, ulong contextId = 2, ulong? startAssetId = null, CancellationToken cancellationToken = default) =>
			Task.FromResult(InventoryHandler(steamId, appId, contextId, startAssetId));

		public Task<TradeOffersResponse> GetTradeOffersAsync(bool activeOnly = true, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOffersResponse { Success = true });

		public Task<TradeOfferResult> GetTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });

		public Task<TradeOfferResult> SendTradeOfferAsync(
			ulong partnerSteamId,
			IReadOnlyList<TradeAsset> itemsToGive,
			IReadOnlyList<TradeAsset> itemsToReceive,
			string? token = null,
			string? message = null,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true, TradeOfferId = 1 });

		public Task<TradeOfferResult> AcceptTradeOfferAsync(ulong tradeOfferId, ulong partnerSteamId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });

		public Task<TradeOfferResult> DeclineTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });

		public Task<TradeOfferResult> CancelTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });
	}
}
