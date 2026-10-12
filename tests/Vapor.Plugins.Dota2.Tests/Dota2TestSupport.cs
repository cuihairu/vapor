using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Dota2.Tests;

/// <summary>
/// Shared canned-transport fake for the Dota2 plugin tests: canned inventory
/// pages for the shared paginator, canned offer/send responses, and full call
/// recording so tests can assert exactly what (and whether) hit the transport.
/// </summary>
internal sealed class FakeTradeClient : ISteamTradeClient
{
	public const ulong TestSteamId = 76561198000000001UL;

	private readonly Queue<InventoryResponse> _pages;

	public FakeTradeClient(params InventoryResponse[] pages)
	{
		_pages = new Queue<InventoryResponse>(pages);
	}

	public bool ThrowOnInventory { get; init; }

	public bool ThrowOceOnInventory { get; init; }

	public bool ThrowOnSend { get; init; }

	public ulong? OwnSteamId { get; init; } = TestSteamId;

	public TradeOffersResponse OffersResponse { get; set; } = new() { Success = true };

	public TradeOfferResult SendResult { get; set; } = new() { Success = true, TradeOfferId = 4242 };

	public List<ulong> RequestedSteamIds { get; } = [];

	public List<uint> RequestedAppIds { get; } = [];

	public List<ulong> RequestedContextIds { get; } = [];

	public List<ulong?> RequestedStartAssetIds { get; } = [];

	public List<bool> OffersCalls { get; } = [];

	public List<(ulong Partner, IReadOnlyList<TradeAsset> Give, IReadOnlyList<TradeAsset> Receive, string? Token, string? Message)> SentOffers { get; } = [];

	public int SendCalls => SentOffers.Count;

	public ulong? GetOwnSteamId() => OwnSteamId;

	public Task<InventoryResponse> GetInventoryAsync(
		ulong steamId,
		uint appId = 730,
		ulong contextId = 2,
		ulong? startAssetId = null,
		CancellationToken cancellationToken = default)
	{
		RequestedSteamIds.Add(steamId);
		RequestedAppIds.Add(appId);
		RequestedContextIds.Add(contextId);
		RequestedStartAssetIds.Add(startAssetId);

		if (ThrowOnInventory)
		{
			throw new InvalidOperationException("boom");
		}

		if (ThrowOceOnInventory)
		{
			throw new OperationCanceledException();
		}

		return Task.FromResult(_pages.Dequeue());
	}

	public Task<TradeOffersResponse> GetTradeOffersAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
	{
		if (ThrowOnInventory)
		{
			throw new InvalidOperationException("boom");
		}

		if (ThrowOceOnInventory)
		{
			throw new OperationCanceledException();
		}

		OffersCalls.Add(activeOnly);
		return Task.FromResult(OffersResponse);
	}

	public Task<TradeOfferResult> GetTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("not used by the dota2 actions");

	public Task<TradeOfferResult> SendTradeOfferAsync(
		ulong partnerSteamId,
		IReadOnlyList<TradeAsset> itemsToGive,
		IReadOnlyList<TradeAsset> itemsToReceive,
		string? token = null,
		string? message = null,
		CancellationToken cancellationToken = default)
	{
		if (ThrowOnSend)
		{
			throw new InvalidOperationException("send boom");
		}

		SentOffers.Add((partnerSteamId, itemsToGive, itemsToReceive, token, message));
		return Task.FromResult(SendResult);
	}

	public Task<TradeOfferResult> AcceptTradeOfferAsync(ulong tradeOfferId, ulong partnerSteamId, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("not used by the dota2 actions");

	public Task<TradeOfferResult> DeclineTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("not used by the dota2 actions");

	public Task<TradeOfferResult> CancelTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("not used by the dota2 actions");
}

internal static class Pages
{
	public static InventoryResponse Of(params InventoryItem[] items) => new()
	{
		Success = true,
		Items = [.. items],
		HasMore = false
	};

	public static InventoryResponse Failed(string error) => new()
	{
		Success = false,
		Error = error
	};

	public static InventoryItem Dota(ulong assetId, bool tradable = true, DateTimeOffset? tradabilityDate = null, int amount = 1, string name = "Arcana") => new()
	{
		AssetId = assetId,
		AppId = Dota2Plugin.AppId,
		Tradable = tradable,
		TradabilityDate = tradabilityDate,
		Amount = amount,
		Name = name,
		MarketName = name,
		MarketHashName = name
	};

	public static TradeOffer Offer(
		ulong offerId,
		ulong partner,
		bool isOurOffer,
		IReadOnlyList<TradeAsset>? give = null,
		IReadOnlyList<TradeAsset>? receive = null,
		TradeOfferState state = TradeOfferState.Active) => new()
		{
			TradeOfferId = offerId,
			AccountIdOther = partner,
			IsOurOffer = isOurOffer,
			State = state,
			TimeCreated = DateTimeOffset.UnixEpoch,
			ItemsToGive = give ?? [],
			ItemsToReceive = receive ?? []
		};

	public static TradeAsset Asset(ulong assetId, uint appId = Dota2Plugin.AppId, int amount = 1) => new()
	{
		AppId = appId,
		ContextId = Dota2Plugin.InventoryContextId,
		AssetId = assetId,
		Amount = amount
	};
}

/// <summary>Plugin context stub mirroring the Csgo test harness (no Moq here).</summary>
internal sealed class TestPluginContext(
	IReadOnlyDictionary<string, string> configuration,
	IServiceProvider? services = null) : IPluginContext
{
	public PluginInfo Info { get; } = new(
		"vapor.dota2", "Vapor Dota 2 (test)", new Version(1, 0, 0), PluginApi.Current);

	public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

	public IPluginHostServices Host { get; } =
		new DefaultPluginHostServices(NullLoggerFactory.Instance, services ?? new NullServiceProvider());
}

internal sealed class NullServiceProvider : IServiceProvider
{
	public object? GetService(Type serviceType) => null;
}

internal sealed class FixedServiceProvider(object service) : IServiceProvider
{
	public object? GetService(Type serviceType) =>
		serviceType.IsInstanceOfType(service) ? service : null;
}

/// <summary>Session/web-handler builders with dispose tracking, as in the Csgo tests.</summary>
public abstract class Dota2TestBase : IDisposable
{
	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (BotSession session in _sessions)
		{
			session.Dispose();
		}
	}

	protected BotSession CreateSession(SteamWebHandler? webHandler)
	{
		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password"),
			null!,
			NullLogger<BotSession>.Instance,
			null,
			webHandler,
			null);
		_sessions.Add(session);
		return session;
	}

	protected static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	protected static SteamWebHandler CreateWebHandlerWithCookies() =>
		WithCookies(CreateWebHandler());

	protected static SteamWebHandler WithCookies(SteamWebHandler webHandler)
	{
		webHandler.SetSessionCookies("session-1", $"{FakeTradeClient.TestSteamId}%7C%7Ctoken");
		return webHandler;
	}

	protected static Dictionary<string, object?> Payload(params (string Key, object? Value)[] values) =>
		values.ToDictionary(entry => entry.Key, entry => entry.Value);
}
