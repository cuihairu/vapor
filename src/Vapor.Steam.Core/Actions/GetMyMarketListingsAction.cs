using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to list the account's own community market listings, parsed from the
/// login-gated mylistings page (the only source for own-listing ids and the
/// fee split). This is the read side of the market loop — the control plane's
/// <c>GET /v1/accounts/{name}/market/listings</c> endpoint dispatches it, and
/// it feeds the future batch-cancel flow. One dispatch fetches exactly one
/// page (<c>start</c>/<c>count</c> in the payload); callers page by issuing
/// further dispatches until <c>start</c> reaches <c>total_count</c>.
/// </summary>
public sealed class GetMyMarketListingsAction : IAction
{
	private readonly ILogger<GetMyMarketListingsAction> _logger;
	private readonly Func<SteamWebHandler, SteamMarketClient> _marketClientFactory;

	public GetMyMarketListingsAction(ILogger<GetMyMarketListingsAction> logger)
	{
		_logger = logger;
		_marketClientFactory = static webHandler => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance);
	}

	// Constructor for testing with a custom factory
	internal GetMyMarketListingsAction(
		ILogger<GetMyMarketListingsAction> logger,
		Func<SteamWebHandler, SteamMarketClient> marketClientFactory)
	{
		_logger = logger;
		_marketClientFactory = marketClientFactory;
	}

	// Distinct from get_market_listings (the public per-app market search).
	public string Name => "get_my_market_listings";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"List the account's own community market listings (mylistings page)",
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

		int start = Math.Max(PayloadReader.GetInt32(payload, "start") ?? 0, 0);
		int count = PayloadReader.GetInt32(payload, "count") ?? 100;

		try
		{
			var marketClient = _marketClientFactory(webHandler);
			var page = await marketClient.GetMyListingsAsync(start, count, cancellationToken).ConfigureAwait(false);
			if (page is null)
			{
				return new ActionResult(false, "Failed to fetch own market listings (is the session logged on?)", null);
			}

			_logger.LogInformation(
				"Got {Count} of {Total} own market listings (start={Start})",
				page.Listings.Count, page.TotalCount, page.Start);

			var output = new Dictionary<string, object?>
			{
				["start"] = page.Start,
				["count"] = page.PageSize,
				["total_count"] = page.TotalCount,
				["active_count"] = page.ActiveCount,
				["on_hold_count"] = page.OnHoldCount,
				["to_be_confirmed_count"] = page.ToBeConfirmedCount,
				["listings"] = page.Listings.Select(ListingToDictionary).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get own market listings");
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static Dictionary<string, object?> ListingToDictionary(MyMarketListing listing) => new()
	{
		["listing_id"] = listing.ListingId,
		["app_id"] = listing.AppId,
		["context_id"] = listing.ContextId,
		["asset_id"] = listing.AssetId,
		["class_id"] = listing.ClassId,
		["market_hash_name"] = listing.MarketHashName,
		["market_name"] = listing.MarketName,
		["game_name"] = listing.GameName,
		["price_cents"] = listing.PriceCents,
		["fee_cents"] = listing.FeeCents,
		["seller_proceeds_cents"] = listing.SellerProceedsCents,
		["currency_id"] = listing.CurrencyId,
		["icon_url"] = listing.IconUrl,
		["time_created"] = listing.TimeCreated?.ToString("O"),
		["cancel_requested"] = listing.CancelRequested
	};
}
