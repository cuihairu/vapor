using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Trading;

/// <summary>
/// One group of duplicate items sharing the same (app, class, instance) identity —
/// e.g. several copies of the same Steam trading card. <see cref="ExcessItems"/> holds
/// only the tradable copies beyond the kept amount, i.e. what a 1:1 swap can give away.
/// </summary>
public sealed record DuplicateGroup(
	uint AppId,
	ulong ClassId,
	ulong InstanceId,
	string Name,
	int TotalTradable,
	IReadOnlyList<InventoryItem> ExcessItems);

/// <summary>
/// One complementary 1:1 swap: one of my duplicate items for one of the partner's
/// duplicate items. The trade assets carry the context the offer needs (community
/// app 753 → context 6, everything else → 2) while the raw items are kept for display.
/// </summary>
public sealed record SwapMatch(
	InventoryItem GiveItem,
	InventoryItem ReceiveItem,
	TradeAsset Give,
	TradeAsset Receive);

/// <summary>
/// Duplicate detection and complementary 1:1 swap matching (the local equivalent of
/// what SteamTradeMatcher does server-side): both sides keep the copies they need and
/// trade away duplicates of cards the other side lacks entirely, one for one.
/// </summary>
public static class CardSwapMatcher
{
	/// <summary>
	/// Groups tradable items by identity and reports the copies beyond <paramref name="keep"/>
	/// as excess. Untradable or trade-locked items never count — they cannot be given away.
	/// Groups come back sorted by (app, class, instance) for deterministic output.
	/// </summary>
	public static List<DuplicateGroup> FindDuplicates(IEnumerable<InventoryItem> items, int keep)
	{
		var duplicates = items
			.Where(IsTradableNow)
			.GroupBy(ItemKey)
			.Select(g => new
			{
				Key = g.Key,
				Items = g.OrderBy(static i => i.AssetId).ToList()
			})
			.Where(g => g.Items.Count > keep)
			.OrderBy(static g => g.Key)
			.Select(g => new DuplicateGroup(
				g.Key.AppId,
				g.Key.ClassId,
				g.Key.InstanceId,
				g.Items[0].MarketHashName ?? g.Items[0].Name ?? string.Empty,
				g.Items.Count,
				g.Items.Skip(keep).ToList()))
			.ToList();

		return duplicates;
	}

	/// <summary>
	/// Matches complementary duplicates between two inventories: a swap pairs my excess
	/// copy of card A with the partner's excess copy of card B only when I own no copy of
	/// B and the partner owns no copy of A — so both sides strictly fill gaps. At most
	/// <paramref name="maxSwaps"/> pairs are returned.
	/// </summary>
	public static List<SwapMatch> MatchSwaps(
		IReadOnlyList<InventoryItem> ownItems,
		IReadOnlyList<InventoryItem> partnerItems,
		int keep,
		int maxSwaps)
	{
		List<InventoryItem> ownTradable = [.. ownItems.Where(IsTradableNow)];
		List<InventoryItem> partnerTradable = [.. partnerItems.Where(IsTradableNow)];

		HashSet<(uint AppId, ulong ClassId, ulong InstanceId)> ownKeys =
			[.. ownTradable.Select(ItemKey)];
		HashSet<(uint AppId, ulong ClassId, ulong InstanceId)> partnerKeys =
			[.. partnerTradable.Select(ItemKey)];

		var matches = new List<SwapMatch>();
		foreach (var ownGroup in FindDuplicates(ownTradable, keep))
		{
			foreach (var partnerGroup in FindDuplicates(partnerTradable, keep))
			{
				// Strict complement: I must lack the partner's duplicate entirely and vice versa.
				if (ownKeys.Contains((partnerGroup.AppId, partnerGroup.ClassId, partnerGroup.InstanceId))
					|| partnerKeys.Contains((ownGroup.AppId, ownGroup.ClassId, ownGroup.InstanceId)))
				{
					continue;
				}

				int pairs = Math.Min(ownGroup.ExcessItems.Count, partnerGroup.ExcessItems.Count);
				for (int i = 0; i < pairs && matches.Count < maxSwaps; i++)
				{
					matches.Add(new SwapMatch(
						ownGroup.ExcessItems[i],
						partnerGroup.ExcessItems[i],
						ToAsset(ownGroup.ExcessItems[i]),
						ToAsset(partnerGroup.ExcessItems[i])));
				}

				if (matches.Count >= maxSwaps)
				{
					return matches;
				}
			}
		}

		return matches;
	}

	/// <summary>Shared context rule with the loot flow: community items live in context 6.</summary>
	public static ulong ContextIdFor(uint appId) => appId == 753 ? 6UL : 2UL;

	private static (uint AppId, ulong ClassId, ulong InstanceId) ItemKey(InventoryItem item) =>
		(item.AppId, item.ClassId, item.InstanceId);

	private static TradeAsset ToAsset(InventoryItem item) => new()
	{
		AppId = item.AppId,
		ContextId = ContextIdFor(item.AppId),
		AssetId = item.AssetId,
		ClassId = item.ClassId,
		InstanceId = item.InstanceId,
		Amount = item.Amount > 0 ? item.Amount : 1,
		IsCurrency = false
	};

	private static bool IsTradableNow(InventoryItem item)
	{
		if (!item.Tradable)
		{
			return false;
		}

		return item.TradabilityDate is null || item.TradabilityDate <= DateTimeOffset.UtcNow;
	}
}
