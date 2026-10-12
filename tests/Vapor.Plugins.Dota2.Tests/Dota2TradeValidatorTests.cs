using Vapor.Steam.Core.Models;

using Xunit;

namespace Vapor.Plugins.Dota2.Tests;

public class Dota2TradeValidatorTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Validate_EmptyAssetList_IsSuccess()
	{
		Dota2TradeValidation result = Dota2TradeValidator.Validate([], [Pages.Dota(1)], Now);

		Assert.True(result.IsValid);
		Assert.Equal(string.Empty, result.CombinedError);
	}

	[Fact]
	public void Validate_OwnedTradableAsset_IsSuccess()
	{
		var assets = new[] { Pages.Asset(77) };
		var inventory = new[] { Pages.Dota(77) };

		Dota2TradeValidation result = Dota2TradeValidator.Validate(assets, inventory, Now);

		Assert.True(result.IsValid);
		Assert.Empty(result.Errors);
	}

	[Fact]
	public void Validate_NotOwned_ReportsMissingFromTheDotaInventory()
	{
		var result = Dota2TradeValidator.Validate([Pages.Asset(999)], [Pages.Dota(1)], Now);

		Assert.False(result.IsValid);
		Assert.Contains("999", result.CombinedError);
		Assert.Contains("not found in the Dota 2 inventory", result.CombinedError);
	}

	[Fact]
	public void Validate_ForeignAppId_IsRejectedWithTheScopedMessage()
	{
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77, appId: 730)],
			[new InventoryItem { AssetId = 77, AppId = 730, Tradable = true }],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains("not a Dota 2 item (app 730)", result.CombinedError);
	}

	[Fact]
	public void Validate_FutureTradabilityDate_NamesTheSevenDayCooldown_AndTheUnlockTime()
	{
		var unlock = Now.AddDays(6);
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77)],
			[Pages.Dota(77, tradable: false, tradabilityDate: unlock)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains("7-day market trade cooldown", result.CombinedError);
		Assert.Contains(unlock.ToString("O"), result.CombinedError);
	}

	[Fact]
	public void Validate_PastTradabilityDate_WithFlagSet_Trades()
	{
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77)],
			[Pages.Dota(77, tradable: true, tradabilityDate: Now.AddDays(-1))],
			Now);

		Assert.True(result.IsValid);
	}

	[Fact]
	public void Validate_NotTradable_WithoutUnlockTime_CarriesTheSevenDayGuidance()
	{
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77)],
			[Pages.Dota(77, tradable: false)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains("77", result.CombinedError);
		Assert.Contains("not tradable", result.CombinedError);
		Assert.Contains("up to 7 days", result.CombinedError);
		Assert.Contains("dota2_inventory", result.CombinedError);
	}

	[Fact]
	public void Validate_InventoryItemWithoutACount_TreatsTheAvailabilityAsOne()
	{
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77, amount: 1)],
			[Pages.Dota(77) with { Amount = 0 }],
			Now);

		Assert.True(result.IsValid);
	}

	[Fact]
	public void Validate_InsufficientAmount_ReportsTheAvailableCount()
	{
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77, amount: 5)],
			[Pages.Dota(77, amount: 2)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains("Requested 5 of asset 77 but only 2 available", result.CombinedError);
	}

	[Fact]
	public void Validate_DuplicateEntries_AggregateBeforeTheAmountCheck()
	{
		var result = Dota2TradeValidator.Validate(
			[Pages.Asset(77, amount: 2), Pages.Asset(77, amount: 3)],
			[Pages.Dota(77, amount: 4)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains("Requested 5 of asset 77 but only 4 available", result.CombinedError);
	}

	[Fact]
	public void Validate_NonPositiveAmount_IsRejected()
	{
		var result = Dota2TradeValidator.Validate([Pages.Asset(77, amount: 0)], [Pages.Dota(77)], Now);

		Assert.False(result.IsValid);
		Assert.Contains("invalid amount 0", result.CombinedError);
	}

	[Fact]
	public void Validate_NameFallback_WalksMarketHashNameMarketNameAndName()
	{
		var item = Pages.Dota(77, tradable: false) with
		{
			MarketHashName = null,
			MarketName = null,
			Name = "Fallback Name"
		};

		var result = Dota2TradeValidator.Validate([Pages.Asset(77)], [item], Now);

		Assert.Contains("Fallback Name", result.CombinedError);
	}

	[Fact]
	public void Validate_ItemWithNoNamesAtAll_FallsBackToTheWordItem()
	{
		var item = Pages.Dota(77, tradable: false) with
		{
			MarketHashName = null,
			MarketName = null,
			Name = null
		};

		var result = Dota2TradeValidator.Validate([Pages.Asset(77)], [item], Now);

		Assert.Contains("(item)", result.CombinedError);
	}

	[Fact]
	public void Validate_NullArguments_Throw()
	{
		Assert.Throws<ArgumentNullException>(() => Dota2TradeValidator.Validate(null!, [], Now));
		Assert.Throws<ArgumentNullException>(() => Dota2TradeValidator.Validate([], null!, Now));
	}
}
