using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

/// <summary>
/// Property-based tests over the outgoing-offer ownership validation. The
/// properties pin the guard chain order (invalid amount → not owned → not
/// tradable → cooldown → insufficient quantity; each earlier failure masks
/// the later checks for that asset), the aggregation of duplicate entries
/// (requested quantities sum), the cooldown boundary (TradabilityDate equal
/// to now passes — the check is strict "later than now"), the empty-offer
/// short circuit, and the Success/error invariants (success returns the
/// shared singleton; IsValid exactly mirrors whether any errors exist).
/// Every property uses an injected clock for determinism.
/// </summary>
public sealed class TradeAssetValidatorPropertyTests
{
	private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

	private static InventoryItem Item(uint appId, ulong assetId, int amount, bool tradable, DateTimeOffset? tradableFrom) =>
		new() { AppId = appId, AssetId = assetId, Amount = amount, Tradable = tradable, TradabilityDate = tradableFrom };

	private static TradeAsset Asset(uint appId, ulong assetId, int amount) => new()
	{
		AppId = appId,
		ContextId = 2,
		AssetId = assetId,
		Amount = amount,
	};

	[Property]
	public void EmptyOffer_AlwaysSucceeds(uint appId, ulong assetId, int amount, bool tradable, PositiveInt daysFromNow)
	{
		var inventory = new[] { Item(appId, assetId, amount, tradable, Now.AddDays(daysFromNow.Get)) };

		var result = TradeAssetValidator.ValidateOwnership([], inventory, Now);

		Assert.True(result.IsValid);
		Assert.Same(TradeAssetValidation.Success, result);
	}

	[Property]
	public void SingleOwnedTradableAsset_WithinAmount_AlwaysSucceeds(uint appId, ulong assetId, PositiveInt available, PositiveInt requested)
	{
		if (requested.Get > available.Get)
		{
			return;
		}

		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(appId, assetId, requested.Get)],
			[Item(appId, assetId, available.Get, tradable: true, tradableFrom: null)],
			Now);

		Assert.True(result.IsValid);
		Assert.Same(TradeAssetValidation.Success, result);
		Assert.Equal(string.Empty, result.CombinedError);
	}

	[Property]
	public void UnknownAsset_IsRejectedAsNotFound_BeforeOtherChecks(uint appId, ulong assetId, PositiveInt amount)
	{
		// A fully unusable inventory entry under a different id: the asset is
		// still reported as not found, not as untradable.
		var inventory = new[] { Item(appId, assetId + 1, amount.Get, tradable: false, tradableFrom: Now.AddDays(9)) };

		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(appId, assetId, amount.Get)], inventory, Now);

		Assert.False(result.IsValid);
		var error = Assert.Single(result.Errors);
		Assert.Contains("was not found in the inventory", error);
	}

	[Property]
	public void UntradableEntry_ReportsNotTradable_NotCooldown(uint appId, ulong assetId, PositiveInt amount, PositiveInt cooldownDays)
	{
		// Both gates fail; the tradability check short-circuits before the
		// cooldown check.
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(appId, assetId, amount.Get)],
			[Item(appId, assetId, amount.Get, tradable: false, tradableFrom: Now.AddDays(cooldownDays.Get))],
			Now);

		Assert.False(result.IsValid);
		var error = Assert.Single(result.Errors);
		Assert.Contains("is not tradable", error);
		Assert.DoesNotContain("cooldown", error);
	}

	[Property]
	public void CooldownBoundary_NowItself_Passes_OnlyLaterFails(uint appId, ulong assetId, PositiveInt amount, int secondOffset)
	{
		// TradabilityDate == now passes (strict >); one tick later fails.
		var cooldown = Now.AddSeconds(Math.Abs(secondOffset) + 1);

		var passing = TradeAssetValidator.ValidateOwnership(
			[Asset(appId, assetId, amount.Get)],
			[Item(appId, assetId, amount.Get, tradable: true, tradableFrom: Now)],
			Now);
		Assert.True(passing.IsValid);

		var failing = TradeAssetValidator.ValidateOwnership(
			[Asset(appId, assetId, amount.Get)],
			[Item(appId, assetId, amount.Get, tradable: true, tradableFrom: cooldown)],
			Now);
		Assert.False(failing.IsValid);
		Assert.Contains("cooldown", Assert.Single(failing.Errors));
	}

	[Property]
	public void DuplicateEntries_RequestSummedAmount(PositiveInt perEntry, PositiveInt duplicates, PositiveInt available, uint appId, ulong assetId)
	{
		int requested = perEntry.Get * duplicates.Get;

		var result = TradeAssetValidator.ValidateOwnership(
			Enumerable.Range(0, duplicates.Get).Select(_ => Asset(appId, assetId, perEntry.Get)).ToList(),
			[Item(appId, assetId, available.Get, tradable: true, tradableFrom: null)],
			Now);

		Assert.Equal(requested > available.Get, !result.IsValid);
		if (requested > available.Get)
		{
			// Duplicates collapse into a single aggregated shortage error.
			var error = Assert.Single(result.Errors);
			Assert.Contains($"Requested {requested} of asset {assetId}", error);
			Assert.Contains($"only {available.Get} available", error);
		}
	}

	[Property]
	public void InvalidAmount_IsRejected_Alone_AndExcludedFromAggregation(uint appId, ulong assetId, PositiveInt validAmount)
	{
		// A zero/negative amount entry is its own error and never merges into
		// the requested total of the valid duplicate.
		var result = TradeAssetValidator.ValidateOwnership(
			[Asset(appId, assetId, 0), Asset(appId, assetId, validAmount.Get)],
			[Item(appId, assetId, validAmount.Get, tradable: true, tradableFrom: null)],
			Now);

		Assert.False(result.IsValid);
		var error = Assert.Single(result.Errors);
		Assert.Contains("has invalid amount 0", error);
	}
}
