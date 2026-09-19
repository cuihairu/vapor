using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

/// <summary>
/// Property-based tests over the duplicate detection / 1:1 swap matcher.
/// Raw generated ids are shrunk into a small identity pool so duplicates
/// actually collide (full-range ids would almost never overlap and the
/// properties would pass vacuously); asset ids stay full-range. Items are
/// built with TradabilityDate = null — a future tradability date is equivalent
/// to Tradable=false in <c>IsTradableNow</c> and only one mechanism is exercised.
/// </summary>
public sealed class CardSwapMatcherPropertyTests
{
	private static readonly uint[] AppPool = [753, 730, 440, 570];

	private static InventoryItem Item(uint appIdRaw, ulong classIdRaw, ulong instanceIdRaw, ulong assetId, bool tradable) =>
		new()
		{
			AppId = AppPool[appIdRaw % (uint)AppPool.Length],
			ClassId = classIdRaw % 5,
			InstanceId = instanceIdRaw % 2,
			AssetId = assetId,
			Tradable = tradable,
			MarketHashName = $"card-{classIdRaw % 5}-{instanceIdRaw % 2}"
		};

	private static (uint AppId, ulong ClassId, ulong InstanceId) Key(InventoryItem item) =>
		(item.AppId, item.ClassId, item.InstanceId);

	/// <summary>DuplicateGroup is a record whose list field compares by reference, so
	/// group equality must be checked field by field (§36 lesson).</summary>
	private static bool SameGroups(List<DuplicateGroup> a, List<DuplicateGroup> b) =>
		a.Count == b.Count &&
		a.Zip(b).All(p =>
			p.First.AppId == p.Second.AppId &&
			p.First.ClassId == p.Second.ClassId &&
			p.First.InstanceId == p.Second.InstanceId &&
			p.First.Name == p.Second.Name &&
			p.First.TotalTradable == p.Second.TotalTradable &&
			p.First.ExcessItems.SequenceEqual(p.Second.ExcessItems));

	private static bool SameMatches(List<SwapMatch> a, List<SwapMatch> b) =>
		a.Count == b.Count &&
		a.Zip(b).All(p =>
			p.First.GiveItem == p.Second.GiveItem &&
			p.First.ReceiveItem == p.Second.ReceiveItem);

	// --- FindDuplicates ---

	[Property]
	public void FindDuplicates_GroupInvariants_Hold(
		uint[] appIds, ulong[] classIds, ulong[] instanceIds, ulong[] assetIds, bool[] tradables, PositiveInt keepRaw)
	{
		int n = Math.Min(Math.Min(appIds.Length, classIds.Length), Math.Min(Math.Min(instanceIds.Length, assetIds.Length), tradables.Length));
		if (n < 2)
		{
			return;
		}

		int keep = (int)(keepRaw.Get % 4);
		var items = Enumerable.Range(0, n)
			.Select(i => Item(appIds[i], classIds[i], instanceIds[i], assetIds[i], tradables[i]))
			.ToList();

		var groups = CardSwapMatcher.FindDuplicates(items, keep);
		var tradableKeys = items.Where(i => i.Tradable).Select(Key).ToHashSet();

		for (int g = 0; g < groups.Count; g++)
		{
			DuplicateGroup group = groups[g];

			// Size bookkeeping: excess is exactly the count above the kept amount.
			Assert.True(group.TotalTradable > keep);
			Assert.Equal(group.TotalTradable - keep, group.ExcessItems.Count);

			// Only tradable identities appear, and every excess copy is a
			// tradable input item (by asset id).
			Assert.Subset(tradableKeys, (HashSet<(uint, ulong, ulong)>)[Key(group.ExcessItems[0])]);
			Assert.All(group.ExcessItems, item => Assert.True(item.Tradable));

			// Deterministic ordering: groups sorted by identity.
			if (g > 0)
			{
				DuplicateGroup prev = groups[g - 1];
				Assert.True((prev.AppId, prev.ClassId, prev.InstanceId)
					.CompareTo((group.AppId, group.ClassId, group.InstanceId)) < 0);
			}
		}
	}

	[Property]
	public void FindDuplicates_IsDeterministic(
		uint[] appIds, ulong[] classIds, ulong[] instanceIds, ulong[] assetIds, bool[] tradables, PositiveInt keepRaw)
	{
		int n = Math.Min(Math.Min(appIds.Length, classIds.Length), Math.Min(Math.Min(instanceIds.Length, assetIds.Length), tradables.Length));
		if (n < 2)
		{
			return;
		}

		int keep = (int)(keepRaw.Get % 4);
		var items = Enumerable.Range(0, n)
			.Select(i => Item(appIds[i], classIds[i], instanceIds[i], assetIds[i], tradables[i]))
			.ToList();

		Assert.True(SameGroups(CardSwapMatcher.FindDuplicates(items, keep), CardSwapMatcher.FindDuplicates(items, keep)));
	}

	// --- MatchSwaps ---

	[Property]
	public void MatchSwaps_StrictComplement_ContextRule_AndExcessOnly(
		uint[] ownApps, ulong[] ownClasses, ulong[] ownInstances, ulong[] ownAssets, bool[] ownTradables,
		uint[] partnerApps, ulong[] partnerClasses, ulong[] partnerInstances, ulong[] partnerAssets, bool[] partnerTradables,
		PositiveInt keepRaw, PositiveInt maxSwapsRaw)
	{
		int ownN = Math.Min(Math.Min(ownApps.Length, ownClasses.Length), Math.Min(Math.Min(ownInstances.Length, ownAssets.Length), ownTradables.Length));
		int partnerN = Math.Min(Math.Min(partnerApps.Length, partnerClasses.Length), Math.Min(Math.Min(partnerInstances.Length, partnerAssets.Length), partnerTradables.Length));
		if (ownN < 2 || partnerN < 2)
		{
			return;
		}

		int keep = (int)(keepRaw.Get % 4);
		int maxSwaps = (int)(maxSwapsRaw.Get % 20);

		var ownItems = Enumerable.Range(0, ownN)
			.Select(i => Item(ownApps[i], ownClasses[i], ownInstances[i], ownAssets[i], ownTradables[i]))
			.ToList();
		var partnerItems = Enumerable.Range(0, partnerN)
			.Select(i => Item(partnerApps[i], partnerClasses[i], partnerInstances[i], partnerAssets[i], partnerTradables[i]))
			.ToList();

		var matches = CardSwapMatcher.MatchSwaps(ownItems, partnerItems, keep, maxSwaps);

		Assert.True(matches.Count <= maxSwaps);

		var ownTradableKeys = ownItems.Where(i => i.Tradable).Select(Key).ToHashSet();
		var partnerTradableKeys = partnerItems.Where(i => i.Tradable).Select(Key).ToHashSet();
		var ownExcessAssets = CardSwapMatcher.FindDuplicates(ownItems, keep)
			.SelectMany(g => g.ExcessItems).Select(i => i.AssetId).ToHashSet();
		var partnerExcessAssets = CardSwapMatcher.FindDuplicates(partnerItems, keep)
			.SelectMany(g => g.ExcessItems).Select(i => i.AssetId).ToHashSet();

		foreach (SwapMatch match in matches)
		{
			// Strict complement: neither side owns any copy of the card it receives.
			Assert.DoesNotContain(Key(match.ReceiveItem), ownTradableKeys);
			Assert.DoesNotContain(Key(match.GiveItem), partnerTradableKeys);

			// Both sides of the pair come from a duplicate excess pool.
			Assert.Subset(ownExcessAssets, (HashSet<ulong>)[match.GiveItem.AssetId]);
			Assert.Subset(partnerExcessAssets, (HashSet<ulong>)[match.ReceiveItem.AssetId]);

			// Shared context rule with the loot flow.
			Assert.Equal(CardSwapMatcher.ContextIdFor(match.Give.AppId), match.Give.ContextId);
			Assert.Equal(CardSwapMatcher.ContextIdFor(match.Receive.AppId), match.Receive.ContextId);
		}
	}

	[Property]
	public void MatchSwaps_IsDeterministic(
		uint[] ownApps, ulong[] ownClasses, ulong[] ownInstances, ulong[] ownAssets, bool[] ownTradables,
		uint[] partnerApps, ulong[] partnerClasses, ulong[] partnerInstances, ulong[] partnerAssets, bool[] partnerTradables,
		PositiveInt keepRaw, PositiveInt maxSwapsRaw)
	{
		int ownN = Math.Min(Math.Min(ownApps.Length, ownClasses.Length), Math.Min(Math.Min(ownInstances.Length, ownAssets.Length), ownTradables.Length));
		int partnerN = Math.Min(Math.Min(partnerApps.Length, partnerClasses.Length), Math.Min(Math.Min(partnerInstances.Length, partnerAssets.Length), partnerTradables.Length));
		if (ownN < 2 || partnerN < 2)
		{
			return;
		}

		int keep = (int)(keepRaw.Get % 4);
		int maxSwaps = (int)(maxSwapsRaw.Get % 20);

		var ownItems = Enumerable.Range(0, ownN)
			.Select(i => Item(ownApps[i], ownClasses[i], ownInstances[i], ownAssets[i], ownTradables[i]))
			.ToList();
		var partnerItems = Enumerable.Range(0, partnerN)
			.Select(i => Item(partnerApps[i], partnerClasses[i], partnerInstances[i], partnerAssets[i], partnerTradables[i]))
			.ToList();

		Assert.True(SameMatches(
			CardSwapMatcher.MatchSwaps(ownItems, partnerItems, keep, maxSwaps),
			CardSwapMatcher.MatchSwaps(ownItems, partnerItems, keep, maxSwaps)));
	}

	// --- ContextIdFor ---

	[Property]
	public void ContextIdFor_CommunityApp6_EverythingElse2(uint appId)
	{
		Assert.Equal(appId == 753 ? 6UL : 2UL, CardSwapMatcher.ContextIdFor(appId));
	}
}
