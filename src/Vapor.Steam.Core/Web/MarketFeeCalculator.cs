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

	// The breakdown is int-typed end to end. Capping inputs at MaxValue/15
	// keeps every fee multiply and the proceeds+fees sum inside int range —
	// unchecked overflow at multi-million-cent prices would silently wrap
	// the buyer price negative (~$1.43M, far above any real listing).
	public const int MaxSellerProceedsCents = int.MaxValue / 15;

	public static MarketPriceBreakdown FromSellerProceeds(int sellerProceedsCents)
	{
		if (sellerProceedsCents is < 1 or > MaxSellerProceedsCents)
		{
			throw new ArgumentOutOfRangeException(nameof(sellerProceedsCents), sellerProceedsCents,
				$"seller proceeds must be between 1 and {MaxSellerProceedsCents} cents");
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
	/// always reachable — the walk starts three above the exact-15%-fees
	/// estimate <c>floor(target / 1.15)</c> and steps down. The +3 head is a
	/// proven upper bound on how far real fees can undercut the estimate:
	/// each floored fee component loses its fraction and the one-cent
	/// minimum can leave up to two further cents on the table, so any seller
	/// beyond start+3 already pays more than the target. Null when even one
	/// cent of proceeds would cost the buyer more than the target (buyer
	/// targets below the 3-cent minimum listing).
	/// </summary>
	public static MarketPriceBreakdown? FromBuyerPrice(int buyerPriceCents)
	{
		if (buyerPriceCents is < 1 or > MaxSellerProceedsCents)
		{
			throw new ArgumentOutOfRangeException(nameof(buyerPriceCents), buyerPriceCents,
				$"buyer price must be between 1 and {MaxSellerProceedsCents} cents");
		}

		for (int seller = (int)Math.Min((long)buyerPriceCents * 100 / 115 + 3, MaxSellerProceedsCents); seller >= 1; seller--)
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
