using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

/// <summary>
/// Property-based tests over the Steam market fee math. Inputs run the full
/// int domain with an explicit cap guard, so silent int-overflow behavior at
/// extreme prices is pinned as a thrown guard rather than wrapped output.
/// </summary>
public sealed class MarketFeeCalculatorPropertyTests
{
	private const int MaxSafe = MarketFeeCalculator.MaxSellerProceedsCents;

	// --- FromSellerProceeds ---

	[Property]
	public void FromSellerProceeds_SplitAddsUp_AndBuyerStaysAboveSeller(int seller)
	{
		if (seller < 1)
		{
			return;
		}

		if (seller > MaxSafe)
		{
			// Above the int-safe cap the guard must refuse, not wrap.
			Assert.Throws<ArgumentOutOfRangeException>(() => MarketFeeCalculator.FromSellerProceeds(seller));
			return;
		}

		var breakdown = MarketFeeCalculator.FromSellerProceeds(seller);

		Assert.Equal(seller + breakdown.SteamFeeCents + breakdown.PublisherFeeCents, breakdown.BuyerPriceCents);
		Assert.True(breakdown.SteamFeeCents >= 1);
		Assert.True(breakdown.PublisherFeeCents >= 1);
		Assert.True(breakdown.BuyerPriceCents > seller);
	}

	[Property]
	public void FromSellerProceeds_FiveAndTenPercentFloor_ExactAboveTwentyCents(int seller)
	{
		// From 20 cents up, 5% and 10% floor to at least one cent, so the
		// raw ratio holds exactly; below that the one-cent minimum wins.
		if (seller is < 20 or > MaxSafe)
		{
			return;
		}

		var breakdown = MarketFeeCalculator.FromSellerProceeds(seller);

		Assert.Equal(seller * 5 / 100, breakdown.SteamFeeCents);
		Assert.Equal(seller * 10 / 100, breakdown.PublisherFeeCents);
	}

	[Property]
	public void FromSellerProceeds_BuyerPrice_IsStrictlyMonotoneInSeller(int a, int b)
	{
		if (a is < 1 or > MaxSafe || b is < 1 or > MaxSafe || a >= b)
		{
			return;
		}

		int buyerA = MarketFeeCalculator.FromSellerProceeds(a).BuyerPriceCents;
		int buyerB = MarketFeeCalculator.FromSellerProceeds(b).BuyerPriceCents;

		Assert.True(buyerA < buyerB);
	}

	// --- FromBuyerPrice ---

	[Property]
	public void FromBuyerPrice_NullExactlyBelowThreeCents_GuardAboveCap(int buyer)
	{
		if (buyer < 1)
		{
			return;
		}

		if (buyer > MaxSafe)
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => MarketFeeCalculator.FromBuyerPrice(buyer));
			return;
		}

		MarketPriceBreakdown? breakdown = MarketFeeCalculator.FromBuyerPrice(buyer);

		// 1 seller cent is the smallest listing (buyer pays 3), so below
		// three cents nothing fits and from three cents up one always does.
		if (buyer < 3)
		{
			Assert.Null(breakdown);
		}
		else
		{
			Assert.NotNull(breakdown);
		}
	}

	[Property]
	public void FromBuyerPrice_RespectsTarget_AndIsMaximal(int buyer)
	{
		if (buyer is < 3 or > MaxSafe)
		{
			return;
		}

		var breakdown = MarketFeeCalculator.FromBuyerPrice(buyer);
		Assert.NotNull(breakdown);

		// Lands on or under the target...
		Assert.True(breakdown!.BuyerPriceCents <= buyer);

		// ...and one more cent of proceeds would overshoot it (maximality),
		// which also pins the result to the same math as FromSellerProceeds.
		var oneMore = MarketFeeCalculator.FromSellerProceeds(breakdown.SellerProceedsCents + 1);
		Assert.True(oneMore.BuyerPriceCents > buyer);
	}
}
