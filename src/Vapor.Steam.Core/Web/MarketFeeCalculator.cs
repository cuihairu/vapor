namespace Vapor.Steam.Core.Web;

/// <summary>Both sides of one market price plus the fees between them, in cents.</summary>
public sealed record MarketPriceBreakdown(
	int SellerProceedsCents,
	int SteamFeeCents,
	int PublisherFeeCents,
	int BuyerPriceCents);

/// <summary>
/// The Steam Community Market fee math, mirroring the model the market page itself
/// applies (cross-confirmed 2026-09-14 against Steam's own economy_v2.js /
/// market_multisell.js and steam-game-idler's market.rs): fees are computed on the
/// amount the *seller* receives — a 5% Steam fee plus a 10% publisher fee (the
/// ratio most games charge), each floored to whole cents with a minimum of one
/// cent — and the buyer pays seller proceeds + fees. The sellitem endpoint's
/// price field takes the seller amount. A few games deviate from the 10%
/// publisher ratio, so a breakdown is a pricing aid for dry-run output, not an
/// authority: Steam's own sell dialog shows the definitive split.
/// </summary>
public static class MarketFeeCalculator
{
	private const int SteamFeePercent = 5;
	private const int PublisherFeePercent = 10;

	public static MarketPriceBreakdown FromSellerProceeds(int sellerProceedsCents)
	{
		if (sellerProceedsCents < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(sellerProceedsCents), sellerProceedsCents, "seller proceeds must be at least 1 cent");
		}

		// Integer division == floor for non-negative operands; the minimum fee
		// of one cent per component kicks in on very low prices.
		int steamFee = Math.Max(sellerProceedsCents * SteamFeePercent / 100, 1);
		int publisherFee = Math.Max(sellerProceedsCents * PublisherFeePercent / 100, 1);
		return new MarketPriceBreakdown(sellerProceedsCents, steamFee, publisherFee, sellerProceedsCents + steamFee + publisherFee);
	}

	/// <summary>
	/// Largest seller amount whose resulting buyer price does not exceed the
	/// target. Because fees floor to whole cents, the exact target is not
	/// always reachable — the walk starts from the exact-15%-fees estimate
	/// <c>floor(target / 1.15)</c> and steps down. Null when even one cent of
	/// proceeds would cost the buyer more than the target (buyer targets
	/// below the 3-cent minimum listing).
	/// </summary>
	public static MarketPriceBreakdown? FromBuyerPrice(int buyerPriceCents)
	{
		if (buyerPriceCents < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(buyerPriceCents), buyerPriceCents, "buyer price must be at least 1 cent");
		}

		for (int seller = buyerPriceCents * 100 / 115; seller >= 1; seller--)
		{
			MarketPriceBreakdown breakdown = FromSellerProceeds(seller);
			if (breakdown.BuyerPriceCents <= buyerPriceCents)
			{
				return breakdown;
			}
		}

		return null;
	}
}
