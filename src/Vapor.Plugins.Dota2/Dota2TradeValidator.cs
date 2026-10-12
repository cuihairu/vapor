using Vapor.Steam.Core.Models;

namespace Vapor.Plugins.Dota2;

/// <summary>Result of validating Dota 2 trade assets against an inventory snapshot.</summary>
public sealed record Dota2TradeValidation
{
	public bool IsValid { get; init; }

	public IReadOnlyList<string> Errors { get; init; } = [];

	public string CombinedError => Errors.Count == 0
		? string.Empty
		: string.Join("; ", Errors);

	public static Dota2TradeValidation Success { get; } = new() { IsValid = true };
}

/// <summary>
/// Validates the give-side assets of a Dota 2 trade offer against a live
/// inventory snapshot of the sending account. Mirrors the semantics of the
/// core <c>TradeAssetValidator</c> (ownership, tradability, cooldown, amount)
/// with two differences that matter for the curated Dota surface: every error
/// names Steam's 7-day market trade cooldown instead of a bare "not tradable"
/// flag, and a future tradability date is reported ahead of the flag so the
/// unlock time reaches the operator. Runs entirely on the caller's snapshot —
/// the action fetches it before any offer is submitted.
/// </summary>
public static class Dota2TradeValidator
{
	/// <param name="assets">Assets the offer intends to give away (app 570 only).</param>
	/// <param name="inventory">Dota 2 inventory snapshot of the sending account.</param>
	/// <param name="now">Optional clock override for cooldown checks.</param>
	public static Dota2TradeValidation Validate(
		IReadOnlyList<TradeAsset> assets,
		IReadOnlyList<InventoryItem> inventory,
		DateTimeOffset? now = null)
	{
		ArgumentNullException.ThrowIfNull(assets);
		ArgumentNullException.ThrowIfNull(inventory);

		if (assets.Count == 0)
		{
			return Dota2TradeValidation.Success;
		}

		DateTimeOffset currentTime = now ?? DateTimeOffset.UtcNow;
		List<string> errors = [];

		var owned = new Dictionary<(uint AppId, ulong AssetId), InventoryItem>();
		foreach (InventoryItem item in inventory)
		{
			owned[(item.AppId, item.AssetId)] = item;
		}

		// Aggregate requested quantities per asset to catch duplicates, exactly
		// like the core validator.
		var requestedAmounts = new Dictionary<(uint AppId, ulong AssetId), int>();
		foreach (TradeAsset asset in assets)
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
			if (appId != Dota2Plugin.AppId)
			{
				// Defensive: the action pins app 570 at parse time, so reaching
				// this with a foreign app id means a caller bypassed the parser.
				errors.Add($"Asset {assetId} is not a Dota 2 item (app {appId}): this action only moves app {Dota2Plugin.AppId} items");
				continue;
			}

			if (!owned.TryGetValue((appId, assetId), out InventoryItem? item))
			{
				errors.Add($"Asset {assetId} (app {appId}) was not found in the Dota 2 inventory");
				continue;
			}

			string name = item.MarketHashName ?? item.MarketName ?? item.Name ?? "item";

			// A future tradability date wins over the raw flag: Steam reports the
			// unlock time for market-bought items, and that date is what the
			// operator needs. The wording names the 7-day market cooldown.
			if (item.TradabilityDate is DateTimeOffset unlock && unlock > currentTime)
			{
				errors.Add($"Asset {assetId} ({name}) is under Steam's 7-day market trade cooldown until {unlock:O}");
				continue;
			}

			if (!item.Tradable)
			{
				errors.Add(
					$"Asset {assetId} ({name}) is not tradable: Steam's market trade cooldown holds market-bought items for up to 7 days, " +
					"and some Dota 2 items (gifted or event-locked) never trade; dota2_inventory lists unlock times when Steam reports them");
				continue;
			}

			int available = item.Amount > 0 ? item.Amount : 1;
			if (requestedAmount > available)
			{
				errors.Add($"Requested {requestedAmount} of asset {assetId} but only {available} available");
			}
		}

		return errors.Count == 0
			? Dota2TradeValidation.Success
			: new Dota2TradeValidation { IsValid = false, Errors = errors };
	}
}
