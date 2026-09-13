using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class LootInventoryActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;
	private const ulong PartnerSteamId = 76561197972611406UL; // account id 12345678

	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new LootInventoryAction(NullLogger<LootInventoryAction>.Instance);
		Assert.Equal("loot_inventory", action.Name);
	}

	[Fact]
	public void Metadata_RequiresLogin()
	{
		var action = new LootInventoryAction(NullLogger<LootInventoryAction>.Instance);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(120, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutPartner_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Either partner_steam_id or trade_url is required", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_InvalidTradeUrl_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_url"] = "https://example.com/nope" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid trade URL format", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_NonNumericPartnerSteamId_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = "not-a-number" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid partner_steam_id parameter", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_ZeroPartnerSteamId_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = "0" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid partner_steam_id parameter", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_EmptyAppIdsList_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		// A provided but unusable app_ids list leaves nothing to scan — refused
		// instead of silently falling back to the 753 default.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = new object[] { 0, true }
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("app_ids must contain at least one app id when provided", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_TooManyApps_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = new object[] { 753u, 730u, 440u, 570u, 252490u, 232090u }
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("limited to 5 apps", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(withWebHandler: false);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_OwnSteamIdUnresolvable_Fails()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => null;
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Unable to resolve own SteamID from the web session", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_InventoryFailure_Fails()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		client.InventoryHandler = (_, _, _, _) => new InventoryResponse { Success = false, Error = "private inventory" };
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to load inventory for app 753", result.Error, StringComparison.Ordinal);
		Assert.Contains("private inventory", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoTradableItems_Fails()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		client.InventoryHandler = (_, _, _, _) => new InventoryResponse
		{
			Success = true,
			Items = [new InventoryItem { AssetId = 1, Tradable = false }]
		};
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("no tradable items to loot in the scanned apps", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RateLimited_Fails()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		client.InventoryHandler = (_, _, _, _) => new InventoryResponse
		{
			Success = true,
			Items = [new InventoryItem { AssetId = 1, Tradable = true }]
		};
		var session = CreateSession(CreateWebHandler());
		var actionWithLimiter = new LootInventoryAction(
			NullLogger<LootInventoryAction>.Instance,
			_ => client,
			CreateExhaustedRateLimiter());

		var result = await actionWithLimiter.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_SendFails_ReturnsClientError()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		client.InventoryHandler = (_, _, _, _) => new InventoryResponse
		{
			Success = true,
			Items = [new InventoryItem { AssetId = 1, Tradable = true }]
		};
		client.SendHandler = (_, _, _, _, _) => new TradeOfferResult { Success = false, Error = "offer already in progress" };
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("offer already in progress", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_TradeUrl_SendsOfferWithContextRulesAndToken()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		var scanned = new List<(uint AppId, ulong ContextId)>();
		client.InventoryHandler = (_, app, context, _) =>
		{
			scanned.Add((app, context));
			return new InventoryResponse
			{
				Success = true,
				Items = app == 753
					?
					[
						// tradable card with a zero amount, plus a future-dated item (excluded)
						new InventoryItem { AssetId = 1, AppId = 753, Tradable = true, Amount = 0 },
						new InventoryItem { AssetId = 2, AppId = 753, Tradable = true, Amount = 2, TradabilityDate = DateTimeOffset.UtcNow.AddDays(7) }
					]
					:
					[
						// app 730: tradable + untradable copy
						new InventoryItem { AssetId = 3, AppId = 730, Tradable = true, Amount = 1 },
						new InventoryItem { AssetId = 4, AppId = 730, Tradable = false, Amount = 1 }
					]
			};
		};
		TradeOfferResult? sent = null;
		client.SendHandler = (partner, give, receive, token, message) =>
		{
			sent = new TradeOfferResult { Success = true, TradeOfferId = 987654, RequiresMobileConfirmation = true };
			sentArgs = (partner, give.ToArray(), receive.ToArray(), token, message);
			return sent;
		};
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["trade_url"] = $"https://steamcommunity.com/tradeoffer/new/?partner=12345678&token=t0k3n",
				["app_ids"] = new object[] { 753u, 730u },
				["message"] = "loot!"
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("987654", result.Output!["trade_offer_id"]);
		Assert.Equal(PartnerSteamId.ToString(), result.Output["partner_steam_id"]);
		Assert.Equal(2, result.Output["item_count"]);
		Assert.Equal(true, result.Output["requires_mobile_confirmation"]);

		// Context rules: 753 → 6 (community items), 730 → 2 (game inventory).
		Assert.Equal([(753u, 6UL), (730u, 2UL)], scanned);

		var (partner, give, receive, token, message) = sentArgs!.Value;
		Assert.Equal(PartnerSteamId, partner);
		Assert.Equal("t0k3n", token);
		Assert.Equal("loot!", message);
		Assert.Empty(receive);
		// Asset 2 is future-dated and asset 4 untradable — both excluded;
		// asset 1's zero amount becomes 1.
		Assert.Equal(
			[new[] { 753u, 6UL, 1UL }, new[] { 730u, 2UL, 3UL }],
			give.Select(a => new[] { a.AppId, a.ContextId, a.AssetId }).ToArray());
		var zeroAmountAsset = Assert.Single(give, a => a.AssetId == 1UL);
		Assert.Equal(1, zeroAmountAsset.Amount);
	}

	[Fact]
	public async Task ExecuteAsync_PaginatesUntilNoMorePages()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		int call = 0;
		var requestedStartIds = new List<ulong?>();
		client.InventoryHandler = (_, _, _, startAssetId) =>
		{
			requestedStartIds.Add(startAssetId);
			call++;
			return call == 1
				? new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 1, Tradable = true }], HasMore = true, LastAssetId = 1 }
				: new InventoryResponse { Success = true, Items = [new InventoryItem { AssetId = 2, Tradable = true }] };
		};
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal([null, 1UL], requestedStartIds);
		Assert.Equal(2, result.Output!["item_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_InventoryThrows_ReturnsError()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		client.InventoryHandler = (_, _, _, _) => throw new InvalidOperationException("connection reset");
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("connection reset", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_DefaultsToApp753_WhenAppIdsMissing()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		var scannedApps = new List<uint>();
		client.InventoryHandler = (steamId, app, context, _) =>
		{
			scannedApps.Add(app);
			Assert.Equal(OwnSteamId, steamId);
			Assert.Equal(6UL, context);
			return new InventoryResponse { Success = true, Items = [] };
		};
		var session = CreateSession(CreateWebHandler());

		// No tradable items — the assertion is on which app got scanned.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerSteamId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal([753u], scannedApps);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsValueShapes_AreAllUnderstood()
	{
		var (action, client) = CreateActionWithMock();
		client.OwnSteamIdProvider = () => OwnSteamId;
		var scannedApps = new List<uint>();
		client.InventoryHandler = (_, app, _, _) =>
		{
			scannedApps.Add(app);
			return new InventoryResponse { Success = true, Items = [] };
		};
		var session = CreateSession(CreateWebHandler());

		var viaJsonArray = await action.ExecuteAsync(
			session,
			System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
				"""{"partner_steam_id":"76561197972611406","app_ids":[753, "730", 440, true, 0]}""")!,
			CancellationToken.None);
		var viaList = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = new List<object?> { 753L, 570.0, "252490", null }
			},
			CancellationToken.None);
		var viaSingleValue = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerSteamId.ToString(),
				["app_ids"] = 730u
			},
			CancellationToken.None);

		Assert.All(new[] { viaJsonArray, viaList, viaSingleValue }, r => Assert.False(r.Success)); // empty inventories
		Assert.Equal(new[] { 753u, 730u, 440u }, scannedApps.Take(3).ToArray());
		Assert.Equal(new[] { 753u, 570u, 252490u }, scannedApps.Skip(3).Take(3).ToArray());
		Assert.Equal(new[] { 730u }, scannedApps.Skip(6).ToArray());
	}

	private (ulong Partner, TradeAsset[] Give, TradeAsset[] Receive, string? Token, string? Message)? sentArgs;

	private (LootInventoryAction Action, FakeTradeClient Client) CreateActionWithMock()
	{
		var client = new FakeTradeClient();
		var action = new LootInventoryAction(
			NullLogger<LootInventoryAction>.Instance,
			_ => client);
		return (action, client);
	}

	private BotSession CreateSession(SteamWebHandler? webHandler)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private BotSession CreateSession(bool withWebHandler) =>
		CreateSession(withWebHandler ? CreateWebHandler() : null);

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	private static TradeRateLimiter CreateExhaustedRateLimiter()
	{
		var limiter = new TradeRateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(5),
			MaxConcurrentOperations = 1,
			AcquireTimeout = TimeSpan.FromMilliseconds(100)
		});
		_ = limiter.AcquireAsync("test_account").GetAwaiter().GetResult();
		return limiter;
	}

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
		public Func<ulong?> OwnSteamIdProvider { get; set; } = () => OwnSteamId;

		public Func<ulong, uint, ulong, ulong?, InventoryResponse> InventoryHandler { get; set; } =
			(_, _, _, _) => new InventoryResponse { Success = true };

		public Func<ulong, IReadOnlyList<TradeAsset>, IReadOnlyList<TradeAsset>, string?, string?, TradeOfferResult> SendHandler { get; set; } =
			(_, _, _, _, _) => new TradeOfferResult { Success = true, TradeOfferId = 1 };

		public ulong? GetOwnSteamId() => OwnSteamIdProvider();

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
			Task.FromResult(SendHandler(partnerSteamId, itemsToGive, itemsToReceive, token, message));

		public Task<TradeOfferResult> AcceptTradeOfferAsync(ulong tradeOfferId, ulong partnerSteamId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });

		public Task<TradeOfferResult> DeclineTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });

		public Task<TradeOfferResult> CancelTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOfferResult { Success = true });
	}
}
