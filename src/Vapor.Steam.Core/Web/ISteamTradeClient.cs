using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Trade-related Steam Web API surface used by actions and validation flows.
/// </summary>
public interface ISteamTradeClient
{
	ulong? GetOwnSteamId();

	Task<InventoryResponse> GetInventoryAsync(
		ulong steamId,
		uint appId = 730,
		ulong contextId = 2,
		ulong? startAssetId = null,
		CancellationToken cancellationToken = default);

	Task<TradeOffersResponse> GetTradeOffersAsync(
		bool activeOnly = true,
		CancellationToken cancellationToken = default);

	Task<TradeOfferResult> GetTradeOfferAsync(
		ulong tradeOfferId,
		CancellationToken cancellationToken = default);

	Task<TradeOfferResult> SendTradeOfferAsync(
		ulong partnerSteamId,
		IReadOnlyList<TradeAsset> itemsToGive,
		IReadOnlyList<TradeAsset> itemsToReceive,
		string? token = null,
		string? message = null,
		CancellationToken cancellationToken = default);

	Task<TradeOfferResult> AcceptTradeOfferAsync(
		ulong tradeOfferId,
		ulong partnerSteamId,
		CancellationToken cancellationToken = default);

	Task<TradeOfferResult> DeclineTradeOfferAsync(
		ulong tradeOfferId,
		CancellationToken cancellationToken = default);

	Task<TradeOfferResult> CancelTradeOfferAsync(
		ulong tradeOfferId,
		CancellationToken cancellationToken = default);
}
