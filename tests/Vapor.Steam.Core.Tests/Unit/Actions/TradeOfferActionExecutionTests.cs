using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>
/// Execution-path tests for the four trade offer actions (send/accept/decline/cancel),
/// driving the full success and failure branches through a fake
/// <see cref="ISteamTradeClient"/> injected via the internal constructor.
/// </summary>
public sealed partial class TradeOfferActionExecutionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;

	private const ulong PartnerSteamId = 76561197972611406UL; // account id 12345678

	private const string PartnerParam = "76561197972611406";

	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);

	private readonly List<BotSession> _sessions = [];

	private BotSession CreateSession(bool withWebHandler)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession(
			"test_account",
			credentials,
			registry.Object,
			_sessionLoggerMock.Object,
			null,
			withWebHandler ? CreateWebHandler() : null,
			null);
		_sessions.Add(session);
		return session;
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	/// <summary>
	/// A limiter whose single window slot and concurrency slot are both held by an
	/// unreleased lease, so any action acquire times out and returns null.
	/// </summary>
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

	/// <summary>Fake trade client whose every operation is scripted via delegates.</summary>
	private sealed class FakeTradeClient : ISteamTradeClient
	{
		public Func<ulong?> OwnSteamIdProvider { get; set; } = () => OwnSteamId;

		public Func<ulong, uint, ulong, ulong?, InventoryResponse> InventoryHandler { get; set; } =
			(_, _, _, _) => new InventoryResponse { Success = true };

		public Func<ulong, TradeOfferResult> GetOfferHandler { get; set; } =
			_ => new TradeOfferResult { Success = true };

		public Func<ulong, IReadOnlyList<TradeAsset>, IReadOnlyList<TradeAsset>, string?, string?, TradeOfferResult> SendHandler { get; set; } =
			(_, _, _, _, _) => new TradeOfferResult { Success = true, TradeOfferId = 1 };

		public Func<ulong, ulong, TradeOfferResult> AcceptHandler { get; set; } =
			(_, _) => new TradeOfferResult { Success = true };

		public Func<ulong, TradeOfferResult> DeclineHandler { get; set; } =
			_ => new TradeOfferResult { Success = true };

		public Func<ulong, TradeOfferResult> CancelHandler { get; set; } =
			_ => new TradeOfferResult { Success = true };

		public ulong? GetOwnSteamId() => OwnSteamIdProvider();

		public Task<InventoryResponse> GetInventoryAsync(
			ulong steamId, uint appId = 730, ulong contextId = 2, ulong? startAssetId = null, CancellationToken cancellationToken = default) =>
			Task.FromResult(InventoryHandler(steamId, appId, contextId, startAssetId));

		public Task<TradeOffersResponse> GetTradeOffersAsync(bool activeOnly = true, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOffersResponse { Success = true });

		public Task<TradeOfferResult> GetTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(GetOfferHandler(tradeOfferId));

		public Task<TradeOfferResult> SendTradeOfferAsync(
			ulong partnerSteamId,
			IReadOnlyList<TradeAsset> itemsToGive,
			IReadOnlyList<TradeAsset> itemsToReceive,
			string? token = null,
			string? message = null,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(SendHandler(partnerSteamId, itemsToGive, itemsToReceive, token, message));

		public Task<TradeOfferResult> AcceptTradeOfferAsync(ulong tradeOfferId, ulong partnerSteamId, CancellationToken cancellationToken = default) =>
			Task.FromResult(AcceptHandler(tradeOfferId, partnerSteamId));

		public Task<TradeOfferResult> DeclineTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(DeclineHandler(tradeOfferId));

		public Task<TradeOfferResult> CancelTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(CancelHandler(tradeOfferId));
	}

}
