using System.Globalization;
using Vapor.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Dota2;

/// <summary>
/// "dota2_trades": lists the session account's trade offers and flags which
/// ones involve Dota 2 (app 570) items. Filters to Dota-touching offers by
/// default (the core <c>get_trade_offers</c> already returns everything, so
/// the plugin's value is the curated view); pass include_all=true for the
/// unfiltered list. Read-only — accepting/declining/confirming stays with the
/// core actions, which are game-agnostic already.
/// </summary>
public sealed class Dota2TradesAction : IAction
{
	private readonly ILogger<Dota2TradesAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public Dota2TradesAction(Dota2Plugin plugin)
		: this(plugin, NullLogger<Dota2TradesAction>.Instance)
	{
	}

	// Constructor for testing with a custom trade client factory.
	internal Dota2TradesAction(
		Dota2Plugin plugin,
		ILogger<Dota2TradesAction> logger,
		Func<SteamWebHandler, ISteamTradeClient>? tradeClientFactory = null)
	{
		ArgumentNullException.ThrowIfNull(plugin);
		_logger = logger;
		_tradeClientFactory = tradeClientFactory ?? plugin.TradeClientFactory;
	}

	public string Name => "dota2_trades";

	public ActionMetadata Metadata => new(
		Name,
		"Lists trade offers and flags which ones involve Dota 2 (app 570) items; dota-only by default, include_all for the unfiltered view. Read-only; accept/decline/confirm through the core trade actions.",
		RequiresLogin: true,
		TimeoutSeconds: 30
	)
	{ Safety = ActionSafety.ReadOnly };

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		SteamWebHandler? webHandler = session.SteamWebHandler;
		if (webHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		bool activeOnly = PayloadReader.GetBool(payload, "active_only") ?? true;
		bool includeAll = PayloadReader.GetBool(payload, "include_all") ?? false;

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			TradeOffersResponse response = await tradeClient.GetTradeOffersAsync(activeOnly, cancellationToken).ConfigureAwait(false);
			if (!response.Success)
			{
				return new ActionResult(false, response.Error ?? "Failed to load trade offers", null);
			}

			List<Dictionary<string, object?>> received = DescribeOffers(response.ReceivedOffers, includeAll, out int receivedDota);
			List<Dictionary<string, object?>> sent = DescribeOffers(response.SentOffers, includeAll, out int sentDota);

			_logger.LogInformation(
				"Listed trade offers: {Received} received ({ReceivedDota} dota), {Sent} sent ({SentDota} dota)",
				response.ReceivedOffers.Count, receivedDota, response.SentOffers.Count, sentDota);

			var output = new Dictionary<string, object?>
			{
				["mode"] = "readonly",
				["app_id"] = Dota2Plugin.AppId,
				["dota_only"] = !includeAll,
				["active_only"] = activeOnly,
				["received"] = new Dictionary<string, object?>
				{
					["total"] = response.ReceivedOffers.Count,
					["dota"] = receivedDota,
					["offers"] = received
				},
				["sent"] = new Dictionary<string, object?>
				{
					["total"] = response.SentOffers.Count,
					["dota"] = sentDota,
					["offers"] = sent
				}
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to list trade offers");
			return new ActionResult(false, ex.Message, null);
		}
	}

	private List<Dictionary<string, object?>> DescribeOffers(
		IReadOnlyList<TradeOffer> offers,
		bool includeAll,
		out int dotaCount)
	{
		List<Dictionary<string, object?>> described = [];
		dotaCount = 0;

		foreach (TradeOffer offer in offers)
		{
			bool dotaInvolved =
				offer.ItemsToGive.Any(asset => asset.AppId == Dota2Plugin.AppId) ||
				offer.ItemsToReceive.Any(asset => asset.AppId == Dota2Plugin.AppId);
			if (dotaInvolved)
			{
				dotaCount++;
			}

			if (!dotaInvolved && !includeAll)
			{
				continue;
			}

			described.Add(new Dictionary<string, object?>
			{
				["trade_offer_id"] = offer.TradeOfferId.ToString(CultureInfo.InvariantCulture),
				["partner_steam_id"] = offer.AccountIdOther.ToString(CultureInfo.InvariantCulture),
				["state"] = offer.State.ToString(),
				["is_our_offer"] = offer.IsOurOffer,
				["message"] = offer.Message,
				["time_created"] = offer.TimeCreated.ToString("O", CultureInfo.InvariantCulture),
				["time_expires"] = offer.TimeExpires?.ToString("O", CultureInfo.InvariantCulture),
				["dota_involved"] = dotaInvolved,
				["items_to_give"] = DescribeAssets(offer.ItemsToGive),
				["items_to_receive"] = DescribeAssets(offer.ItemsToReceive)
			});
		}

		return described;
	}

	private static IReadOnlyList<Dictionary<string, object?>> DescribeAssets(IReadOnlyList<TradeAsset> assets) =>
		assets.Select(asset => new Dictionary<string, object?>
		{
			["asset_id"] = asset.AssetId.ToString(CultureInfo.InvariantCulture),
			["app_id"] = asset.AppId,
			["context_id"] = asset.ContextId,
			["amount"] = asset.Amount
		}).ToList();
}
