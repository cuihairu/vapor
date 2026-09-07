using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

public sealed class TradeAssetValidatorTests
{
	private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private static InventoryItem Item(
		ulong assetId,
		bool tradable = true,
		int amount = 1,
		DateTimeOffset? tradabilityDate = null,
		uint appId = 730) =>
		new()
		{
			AssetId = assetId,
			ClassId = assetId + 1000,
			AppId = appId,
			Amount = amount,
			Tradable = tradable,
			TradabilityDate = tradabilityDate,
			MarketHashName = $"Item {assetId}"
		};

	private static TradeAsset Asset(ulong assetId, int amount = 1, uint appId = 730) =>
		new() { AppId = appId, ContextId = 2, AssetId = assetId, Amount = amount };

	[Fact]
	public void ValidateOwnership_WithNoAssets_Succeeds()
	{
		var result = TradeAssetValidator.ValidateOwnership([], [Item(1)], Now);

		Assert.True(result.IsValid);
		Assert.Empty(result.Errors);
	}

	[Fact]
	public void ValidateOwnership_WithOwnedTradableAssets_Succeeds()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1), Asset(2)],
			[Item(1), Item(2)],
			Now);

		Assert.True(result.IsValid);
	}

	[Fact]
	public void ValidateOwnership_WithUnknownAsset_ReportsNotFound()
	{
		var result = TradeAssetValidator.ValidateOwnership([Asset(42)], [Item(1)], Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("was not found", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOwnership_WithAssetFromDifferentApp_ReportsNotFound()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1, appId: 570)],
			[Item(1, appId: 730)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("was not found", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOwnership_WithUntradableAsset_ReportsNotTradable()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1)],
			[Item(1, tradable: false)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("not tradable", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOwnership_WithAssetUnderCooldown_ReportsCooldown()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1)],
			[Item(1, tradabilityDate: Now.AddHours(7))],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("cooldown", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOwnership_WithCooldownInPast_Succeeds()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1)],
			[Item(1, tradabilityDate: Now.AddHours(-1))],
			Now);

		Assert.True(result.IsValid);
	}

	[Fact]
	public void ValidateOwnership_WithInsufficientAmount_ReportsShortage()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1, amount: 3)],
			[Item(1, amount: 2)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("only 2 available", StringComparison.Ordinal));
	}

	[Fact]
	public void ValidateOwnership_WithStackableAmount_Succeeds()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1, amount: 5)],
			[Item(1, amount: 10)],
			Now);

		Assert.True(result.IsValid);
	}

	[Fact]
	public void ValidateOwnership_WithDuplicateReferences_AggregatesAmounts()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1, amount: 2), Asset(1, amount: 2)],
			[Item(1, amount: 3)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("Requested 4", StringComparison.Ordinal));
	}

	[Fact]
	public void ValidateOwnership_WithNonPositiveAmount_ReportsInvalidAmount()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1, amount: 0)],
			[Item(1)],
			Now);

		Assert.False(result.IsValid);
		Assert.Contains(result.Errors, e => e.Contains("invalid amount", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ValidateOwnership_CollectsAllErrors()
	{
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(1), Asset(999)],
			[Item(1, tradable: false)],
			Now);

		Assert.False(result.IsValid);
		Assert.Equal(2, result.Errors.Count);
	}

	[Fact]
	public void CombinedError_JoinsAllMessages()
	{
		var validation = new TradeAssetValidation
		{
			IsValid = false,
			Errors = ["first", "second"]
		};

		Assert.Equal("first; second", validation.CombinedError);
	}
}
