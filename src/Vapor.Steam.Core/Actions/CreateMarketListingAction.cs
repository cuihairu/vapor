using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to put one inventory item up for sale on the community market — the
/// ToS-gray-zone core of the market loop, deliberately gated three ways:
/// <c>send</c> defaults to false and then only the fee-aware pricing plan is
/// reported (dry run, zero requests); a real listing additionally requires the
/// per-account switch (control-plane account spec) and this agent's explicit
/// opt-in via <c>AGENT_MARKET_LISTINGS_ENABLED=true</c> (aligned with the
/// AGENT_2FA_AUTO_SUBMIT precedent), so a direct dispatch bypassing REST is
/// still guarded. Prices come from the caller only — no auto-repricing — and
/// are accepted on either side: as seller proceeds, or as a buyer price that
/// is walked down to the largest seller amount Steam's fee model allows under
/// the target. A mobile/email confirmation requirement is reported back
/// as-is; confirming it is the existing market-type confirmations loop's job
/// (confirm_all_confirmations with type=market), not this action's.
/// </summary>
public sealed class CreateMarketListingAction : IAction
{
	private readonly ILogger<CreateMarketListingAction> _logger;
	private readonly bool _marketListingsEnabled;
	private readonly Func<SteamWebHandler, SteamMarketClient> _marketClientFactory;

	public CreateMarketListingAction(ILogger<CreateMarketListingAction> logger, bool marketListingsEnabled = false)
	{
		_logger = logger;
		_marketListingsEnabled = marketListingsEnabled;
		_marketClientFactory = static webHandler => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance);
	}

	// Constructor for testing with a custom factory
	internal CreateMarketListingAction(
		ILogger<CreateMarketListingAction> logger,
		bool marketListingsEnabled,
		Func<SteamWebHandler, SteamMarketClient> marketClientFactory)
	{
		_logger = logger;
		_marketListingsEnabled = marketListingsEnabled;
		_marketClientFactory = marketClientFactory;
	}

	public string Name => "create_market_listing";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Create one market listing (dry run by default reporting the fee-aware pricing plan; a real listing needs send=true plus the account and agent switches)",
		RequiresLogin: true,
		TimeoutSeconds: 120
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

		int appIdRaw = PayloadReader.GetInt32(payload, "app_id") ?? 0;
		if (appIdRaw <= 0)
		{
			return new ActionResult(false, "app_id is required and must be positive", null);
		}

		string? contextId = PayloadReader.GetString(payload, "context_id")?.Trim();
		if (string.IsNullOrEmpty(contextId))
		{
			return new ActionResult(false, "context_id is required (e.g. 6 for the community inventory)", null);
		}

		string? assetId = PayloadReader.GetString(payload, "asset_id")?.Trim();
		if (string.IsNullOrEmpty(assetId))
		{
			return new ActionResult(false, "asset_id is required (take it from the inventory or own-listings output)", null);
		}

		int amount = PayloadReader.GetInt32(payload, "amount") ?? 1;
		if (amount < 1)
		{
			return new ActionResult(false, "amount must be at least 1", null);
		}

		int? sellerCents = PayloadReader.GetInt32(payload, "seller_proceeds_cents");
		int? buyerCents = PayloadReader.GetInt32(payload, "buyer_price_cents");
		if (sellerCents is not null && buyerCents is not null)
		{
			return new ActionResult(false, "pass exactly one of seller_proceeds_cents and buyer_price_cents", null);
		}

		if (sellerCents is < 1 || buyerCents is < 1)
		{
			return new ActionResult(false, "seller_proceeds_cents and buyer_price_cents must be at least 1", null);
		}

		if (sellerCents is null && buyerCents is null)
		{
			return new ActionResult(false, "a price is required: pass seller_proceeds_cents or buyer_price_cents", null);
		}

		bool send = PayloadReader.GetBool(payload, "send") ?? false;
		if (send && !_marketListingsEnabled)
		{
			// The control plane guards this too, but the action must not rely on
			// that: a direct dispatch with send=true is refused as well.
			return new ActionResult(false, "market listing creation is disabled on this agent (set AGENT_MARKET_LISTINGS_ENABLED=true to opt in)", null);
		}

		int sellerProceeds;
		MarketPriceBreakdown pricing;
		if (sellerCents is not null)
		{
			sellerProceeds = sellerCents.Value;
			pricing = MarketFeeCalculator.FromSellerProceeds(sellerProceeds);
		}
		else
		{
			MarketPriceBreakdown? fromBuyer = MarketFeeCalculator.FromBuyerPrice(buyerCents!.Value);
			if (fromBuyer is null)
			{
				return new ActionResult(false, "buyer_price_cents is below the minimum listing price (a 3-cent buyer price is the lowest reachable)", null);
			}

			sellerProceeds = fromBuyer.SellerProceedsCents;
			pricing = fromBuyer;
		}

		var asset = new Dictionary<string, object?>
		{
			["app_id"] = appIdRaw,
			["context_id"] = contextId,
			["asset_id"] = assetId,
			["amount"] = amount
		};
		var pricingOutput = new Dictionary<string, object?>
		{
			["seller_proceeds_cents"] = pricing.SellerProceedsCents,
			["steam_fee_cents"] = pricing.SteamFeeCents,
			["publisher_fee_cents"] = pricing.PublisherFeeCents,
			["buyer_price_cents"] = pricing.BuyerPriceCents
		};

		if (!send)
		{
			// Dry run: report the plan only — no request goes out.
			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["dry_run"] = true,
				["would_list"] = true,
				["asset"] = asset,
				["pricing"] = pricingOutput
			});
		}

		try
		{
			var marketClient = _marketClientFactory(webHandler);
			CreateListingResult? result = await marketClient.CreateListingAsync(
				(uint)appIdRaw, contextId, assetId, amount, sellerProceeds, cancellationToken).ConfigureAwait(false);
			if (result is null)
			{
				return new ActionResult(false, "Failed to create market listing (is the session logged on?)", null);
			}

			var output = new Dictionary<string, object?>
			{
				["dry_run"] = false,
				["asset"] = asset,
				["pricing"] = pricingOutput,
				["success"] = result.Success,
				["requires_confirmation"] = result.RequiresConfirmation,
				["needs_mobile_confirmation"] = result.NeedsMobileConfirmation,
				["needs_email_confirmation"] = result.NeedsEmailConfirmation
			};
			if (result.Message is not null)
			{
				output["message"] = result.Message;
			}

			if (result.EmailDomain is not null)
			{
				output["email_domain"] = result.EmailDomain;
			}

			if (!result.Success)
			{
				// Steam rejected the listing (rate limits included — no dedicated
				// error code, just the message). Surface it as the task error while
				// keeping the full reply in the output.
				return new ActionResult(false, result.Message ?? "market listing rejected by Steam", output);
			}

			_logger.LogInformation(
				"Market listing created for {AccountName}: asset {AssetId} at {Buyer} buyer / {Seller} seller proceeds",
				session.AccountName, assetId, pricing.BuyerPriceCents, pricing.SellerProceedsCents);
			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to create market listing");
			return new ActionResult(false, ex.Message, null);
		}
	}
}
