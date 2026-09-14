using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Web;

public sealed class MarketFeeCalculatorTests
{
	[Fact]
	public void FromSellerProceeds_AppliesFiveAndTenPercent()
	{
		var breakdown = MarketFeeCalculator.FromSellerProceeds(100);

		Assert.Equal(100, breakdown.SellerProceedsCents);
		Assert.Equal(5, breakdown.SteamFeeCents);
		Assert.Equal(10, breakdown.PublisherFeeCents);
		Assert.Equal(115, breakdown.BuyerPriceCents);
	}

	[Fact]
	public void FromSellerProceeds_MinimumOneCentPerFee()
	{
		// 1 * 5% and 1 * 10% both floor below one cent — the minimum kicks in.
		var breakdown = MarketFeeCalculator.FromSellerProceeds(1);

		Assert.Equal(1, breakdown.SteamFeeCents);
		Assert.Equal(1, breakdown.PublisherFeeCents);
		Assert.Equal(3, breakdown.BuyerPriceCents);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-5)]
	public void FromSellerProceeds_NonPositive_Throws(int sellerProceedsCents)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => MarketFeeCalculator.FromSellerProceeds(sellerProceedsCents));
	}

	[Fact]
	public void FromBuyerPrice_ExactFifteenPercentHitsOnFirstTry()
	{
		var breakdown = MarketFeeCalculator.FromBuyerPrice(115);

		Assert.NotNull(breakdown);
		Assert.Equal(100, breakdown.SellerProceedsCents);
		Assert.Equal(115, breakdown.BuyerPriceCents);
	}

	[Fact]
	public void FromBuyerPrice_FlooredFees_StayUnderTarget()
	{
		// A 114-cent target is not exactly reachable (floor(99*0.05)=4,
		// floor(99*0.10)=9): the walk lands on 99 seller / 112 buyer.
		var breakdown = MarketFeeCalculator.FromBuyerPrice(114);

		Assert.NotNull(breakdown);
		Assert.Equal(99, breakdown.SellerProceedsCents);
		Assert.Equal(4, breakdown.SteamFeeCents);
		Assert.Equal(9, breakdown.PublisherFeeCents);
		Assert.Equal(112, breakdown.BuyerPriceCents);
		Assert.True(breakdown.BuyerPriceCents <= 114);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void FromBuyerPrice_BelowMinimumListing_ReturnsNull(int buyerPriceCents)
	{
		// Even one cent of proceeds costs the buyer three cents.
		Assert.Null(MarketFeeCalculator.FromBuyerPrice(buyerPriceCents));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(0)]
	public void FromBuyerPrice_NonPositive_Throws(int buyerPriceCents)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => MarketFeeCalculator.FromBuyerPrice(buyerPriceCents));
	}

	[Fact]
	public void RoundTrip_BuyerTargetIsNeverExceeded()
	{
		foreach (int target in new[] { 3, 4, 10, 33, 100, 115, 114, 999, 12345 })
		{
			var breakdown = MarketFeeCalculator.FromBuyerPrice(target);
			if (breakdown is null)
			{
				Assert.True(target < 3, $"only sub-minimum targets may fail, got null for {target}");
				continue;
			}

			Assert.True(breakdown.BuyerPriceCents <= target, $"buyer price {breakdown.BuyerPriceCents} exceeded target {target}");
			// Recomputing from the chosen seller amount reproduces the same buyer price.
			Assert.Equal(breakdown, MarketFeeCalculator.FromSellerProceeds(breakdown.SellerProceedsCents));
		}
	}
}
