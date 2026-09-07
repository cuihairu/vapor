using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Trading;

/// <summary>
/// Result of validating trade assets against an inventory snapshot.
/// </summary>
public sealed record TradeAssetValidation
{
	public bool IsValid { get; init; }

	public IReadOnlyList<string> Errors { get; init; } = [];

	public string CombinedError => Errors.Count == 0
		? string.Empty
		: string.Join("; ", Errors);

	public static TradeAssetValidation Success { get; } = new() { IsValid = true };
}

/// <summary>
/// Validates that assets referenced by an outgoing trade offer are actually
/// owned, tradable, off cooldown, and available in sufficient quantity.
/// The inventory snapshot is expected to be fetched for the matching
/// (appId, contextId) pairs of the assets being validated.
/// </summary>
public static class TradeAssetValidator
{
	/// <param name="assets">Assets the offer intends to give away.</param>
	/// <param name="inventory">Inventory snapshot of the sending account.</param>
	/// <param name="now">Optional clock override for tradability cooldown checks.</param>
	public static TradeAssetValidation ValidateOwnership(
		IReadOnlyList<TradeAsset> assets,
		IReadOnlyList<InventoryItem> inventory,
		DateTimeOffset? now = null)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentNullException.ThrowIfNull(inventory);

		if (assets.Count == 0)
		{
			return TradeAssetValidation.Success;
		}

		DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;
		List<string> errors = [];

		var owned = new Dictionary<(uint AppId, ulong AssetId), InventoryItem>();
		foreach (var item in inventory)
		{
			owned[(item.AppId, item.AssetId)] = item;
		}

		// Aggregate requested quantities per asset to catch duplicates.
		var requestedAmounts = new Dictionary<(uint AppId, ulong AssetId), int>();
		foreach (var asset in assets)
		{
			if (asset.Amount <= 0)
			{
				errors.Add($"Asset {asset.AssetId} has invalid amount {asset.Amount}");
				continue;
			}

			var key = (asset.AppId, asset.AssetId);
			requestedAmounts.TryGetValue(key, out int current);
			requestedAmounts[key] = current + asset.Amount;
		}

		foreach (var ((appId, assetId), requestedAmount) in requestedAmounts)
		{
			if (!owned.TryGetValue((appId, assetId), out var item))
			{
				errors.Add($"Asset {assetId} (app {appId}) was not found in the inventory");
				continue;
			}

			if (!item.Tradable)
			{
				errors.Add($"Asset {assetId} ({item.MarketHashName ?? item.Name ?? "item"}) is not tradable");
				continue;
			}

			if (item.TradabilityDate.HasValue && item.TradabilityDate.Value > currentTime)
			{
				errors.Add($"Asset {assetId} is under trade cooldown until {item.TradabilityDate:O}");
				continue;
			}

			int available = item.Amount > 0 ? item.Amount : 1;
			if (requestedAmount > available)
			{
				errors.Add($"Requested {requestedAmount} of asset {assetId} but only {available} available");
			}
		}

		return errors.Count == 0
			? TradeAssetValidation.Success
			: new TradeAssetValidation { IsValid = false, Errors = errors };
	}
}
