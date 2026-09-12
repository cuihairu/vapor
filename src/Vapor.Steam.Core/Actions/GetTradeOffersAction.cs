using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to list incoming and outgoing trade offers via the IEconService API.
/// This is the read side of the trade loop — the control plane's
/// <c>GET /v1/accounts/{name}/trade-offers</c> endpoint dispatches this action
/// and surfaces the parsed offers as REST.
/// </summary>
public sealed class GetTradeOffersAction : IAction
{
	private readonly ILogger<GetTradeOffersAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public GetTradeOffersAction(ILogger<GetTradeOffersAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	// Constructor for testing with a custom factory
	internal GetTradeOffersAction(
		ILogger<GetTradeOffersAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "get_trade_offers";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"List incoming and outgoing trade offers",
		RequiresLogin: true,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		bool activeOnly = PayloadReader.GetBool(payload, "active_only") ?? true;

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			var response = await tradeClient.GetTradeOffersAsync(activeOnly, cancellationToken).ConfigureAwait(false);

			if (!response.Success)
			{
				return new ActionResult(false, response.Error ?? "Failed to get trade offers", null);
			}

			_logger.LogInformation(
				"Got {Sent} sent and {Received} received trade offers (active_only={ActiveOnly})",
				response.SentOffers.Count, response.ReceivedOffers.Count, activeOnly);

			var output = new Dictionary<string, object?>
			{
				["active_only"] = activeOnly,
				["sent_count"] = response.SentOffers.Count,
				["received_count"] = response.ReceivedOffers.Count,
				["sent_offers"] = response.SentOffers.Select(OfferToDictionary).ToList(),
				["received_offers"] = response.ReceivedOffers.Select(OfferToDictionary).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get trade offers");
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static Dictionary<string, object?> OfferToDictionary(TradeOffer offer)
	{
		var dict = new Dictionary<string, object?>
		{
			["trade_offer_id"] = offer.TradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["partner_steam_id"] = offer.AccountIdOther.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["is_our_offer"] = offer.IsOurOffer,
			["state"] = offer.State.ToString(),
			["items_to_give_count"] = offer.ItemsToGiveCount,
			["items_to_receive_count"] = offer.ItemsToReceiveCount,
			["time_created"] = offer.TimeCreated.ToString("O"),
			["items_to_give"] = offer.ItemsToGive.Select(AssetToDictionary).ToList(),
			["items_to_receive"] = offer.ItemsToReceive.Select(AssetToDictionary).ToList()
		};

		if (!string.IsNullOrEmpty(offer.Message))
		{
			dict["message"] = offer.Message;
		}

		if (offer.TimeExpires.HasValue)
		{
			dict["time_expires"] = offer.TimeExpires.Value.ToString("O");
		}

		return dict;
	}

	private static Dictionary<string, object?> AssetToDictionary(TradeAsset asset) => new()
	{
		["app_id"] = asset.AppId,
		["context_id"] = asset.ContextId,
		["asset_id"] = asset.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
		["class_id"] = asset.ClassId.ToString(System.Globalization.CultureInfo.InvariantCulture),
		["instance_id"] = asset.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
		["amount"] = asset.Amount,
		["is_currency"] = asset.IsCurrency
	};
}
