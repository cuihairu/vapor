using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

public sealed class CardSwapMatcherTests
{
	// A card identity: same app + class + instance across copies.
	private const uint CardsApp = 753;

	// Fixed anchor for tradability-date arms (past dates are tradable, future dates locked).
	private static readonly DateTimeOffset _now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	private static InventoryItem Card(
		ulong assetId,
		ulong classId,
		string name = "Card",
		bool tradable = true,
		DateTimeOffset? tradabilityDate = null,
		uint appId = CardsApp,
		int amount = 0) =>
		new()
		{
			AssetId = assetId,
			AppId = appId,
			ClassId = classId,
			InstanceId = classId + 50,
			Tradable = tradable,
			TradabilityDate = tradabilityDate,
			MarketHashName = name,
			Amount = amount
		};

	// --- FindDuplicates ---

	[Fact]
	public void FindDuplicates_GroupsByIdentity_AndReportsExcess()
	{
		var items = new[]
		{
			Card(1, 100, "Card A"),
			Card(2, 100, "Card A"),
			Card(3, 100, "Card A"),
			Card(4, 200, "Card B")
		};

		var groups = CardSwapMatcher.FindDuplicates(items, keep: 1);

		var group = Assert.Single(groups);
		Assert.Equal(100UL, group.ClassId);
		Assert.Equal("Card A", group.Name);
		Assert.Equal(3, group.TotalTradable);
		// Keep 1 of 3 copies: assets 2 and 3 are the excess.
		Assert.Equal([2UL, 3UL], group.ExcessItems.Select(static i => i.AssetId));
	}

	[Fact]
	public void FindDuplicates_LargerKeep_LeavesMoreCopies()
	{
		var items = new[] { Card(1, 100), Card(2, 100), Card(3, 100) };

		var groups = CardSwapMatcher.FindDuplicates(items, keep: 2);

		Assert.Single(Assert.Single(groups).ExcessItems);
	}

	[Fact]
	public void FindDuplicates_IgnoresUntradableAndTradeLockedCopies()
	{
		var items = new[]
		{
			Card(1, 100),
			Card(2, 100, tradable: false),
			Card(3, 100, tradabilityDate: DateTimeOffset.UtcNow.AddDays(7))
		};

		var groups = CardSwapMatcher.FindDuplicates(items, keep: 1);

		// Only one tradable-now copy: nothing beyond keep.
		Assert.Empty(groups);
	}

	[Fact]
	public void FindDuplicates_GroupsAreSortedForDeterministicOutput()
	{
		var items = new[]
		{
			Card(1, 300),
			Card(2, 300),
			Card(3, 100),
			Card(4, 100),
			Card(5, 200),
			Card(6, 200)
		};

		var groups = CardSwapMatcher.FindDuplicates(items, keep: 1);

		Assert.Equal([100UL, 200UL, 300UL], groups.Select(static g => g.ClassId));
	}

	// --- MatchSwaps ---

	/// <summary>
	/// Own inventory: two spare copies of A plus a lone C. Partner: two spare copies of
	/// B plus a lone D — each side lacks the other's duplicate entirely. Result: 2 A<->B pairs.
	/// </summary>
	[Fact]
	public void MatchSwaps_PairsOnlyComplementaryDuplicates()
	{
		var own = new[]
		{
			Card(1, 100, "A"),
			Card(2, 100, "A"),
			Card(3, 100, "A"),
			Card(5, 300, "C")
		};
		var partner = new[]
		{
			Card(10, 200, "B"),
			Card(11, 200, "B"),
			Card(12, 200, "B"),
			Card(14, 400, "D")
		};

		var matches = CardSwapMatcher.MatchSwaps(own, partner, keep: 1, maxSwaps: 25);

		Assert.Equal(2, matches.Count);
		Assert.All(matches, m =>
		{
			Assert.Equal(100UL, m.Give.ClassId);
			Assert.Equal(200UL, m.Receive.ClassId);
		});
		// Give side rides the community inventory context; the assets carry asset ids.
		Assert.Equal(753u, matches[0].Give.AppId);
		Assert.Equal(6UL, matches[0].Give.ContextId);
		Assert.Equal(2UL, matches[0].Give.AssetId);
		Assert.Equal(11UL, matches[0].Receive.AssetId);
	}

	[Fact]
	public void MatchSwaps_SingleSidedDuplicate_DoesNotPair()
	{
		// I hold duplicates of A; the partner holds duplicates of B — but the partner
		// also owns A, so giving A away matches nothing on their side.
		var own = new[] { Card(1, 100, "A"), Card(2, 100, "A") };
		var partner = new[]
		{
			Card(10, 200, "B"),
			Card(11, 200, "B"),
			Card(12, 100, "A")
		};

		var matches = CardSwapMatcher.MatchSwaps(own, partner, keep: 1, maxSwaps: 25);

		Assert.Empty(matches);
	}

	[Fact]
	public void MatchSwaps_MaxSwapsCapsThePairCount()
	{
		var own = new[]
		{
			Card(1, 100, "A"),
			Card(2, 100, "A"),
			Card(3, 100, "A"),
			Card(4, 100, "A")
		};
		var partner = new[]
		{
			Card(10, 200, "B"),
			Card(11, 200, "B"),
			Card(12, 200, "B"),
			Card(13, 200, "B")
		};

		var matches = CardSwapMatcher.MatchSwaps(own, partner, keep: 1, maxSwaps: 2);

		Assert.Equal(2, matches.Count);
	}

	[Fact]
	public void MatchSwaps_GameAppAssets_RideContext2()
	{
		var own = new[] { Card(1, 100, "A", appId: 730), Card(2, 100, "A", appId: 730) };
		var partner = new[] { Card(10, 200, "B", appId: 730), Card(11, 200, "B", appId: 730) };

		var matches = CardSwapMatcher.MatchSwaps(own, partner, keep: 1, maxSwaps: 25);

		SwapMatch match = Assert.Single(matches);
		Assert.Equal(2UL, match.Give.ContextId);
		Assert.Equal(2UL, match.Receive.ContextId);
	}

	[Fact]
	public void MatchSwaps_PositiveStackAmounts_CarryIntoTheTradeAssets()
	{
		// Stackable items keep their real amount; the >0 ternary only substitutes
		// 1 for amount-less entries, so a stack of 5 must trade as 5.
		var own = new[] { Card(1, 100, "A", amount: 5), Card(2, 100, "A", amount: 5) };
		var partner = new[] { Card(10, 200, "B", amount: 5), Card(11, 200, "B", amount: 5) };

		var matches = CardSwapMatcher.MatchSwaps(own, partner, keep: 1, maxSwaps: 25);

		SwapMatch match = Assert.Single(matches);
		Assert.Equal(5, match.Give.Amount);
		Assert.Equal(5, match.Receive.Amount);
	}

	[Fact]
	public void MatchSwaps_UntradableCopies_NeverPaired()
	{
		var own = new[]
		{
			Card(1, 100, "A"),
			Card(2, 100, "A", tradable: false)
		};
		var partner = new[]
		{
			Card(10, 200, "B"),
			Card(11, 200, "B"),
			Card(12, 200, "B")
		};

		// One tradable-now copy of A (== keep): no excess to give.
		Assert.Empty(CardSwapMatcher.MatchSwaps(own, partner, keep: 1, maxSwaps: 25));

		// keep=0 turns that copy into excess and the pair materializes.
		SwapMatch match = Assert.Single(CardSwapMatcher.MatchSwaps(own, partner, keep: 0, maxSwaps: 25));
		Assert.Equal(1UL, match.Give.AssetId);
	}

	[Fact]
	public void FindDuplicates_NameFallback_MarketHashNameToNameToEmpty()
	{
		// The group label is Items[0].MarketHashName ?? Items[0].Name ?? "": the
		// middle arm names the group via Name when MarketHashName is absent, and
		// the final arm degrades to the empty string when both are absent.
		DateTimeOffset? noDate = null;
		var items = new[]
		{
			new InventoryItem { AssetId = 1, AppId = CardsApp, ClassId = 100, InstanceId = 150, Tradable = true, TradabilityDate = noDate, Name = "Fallback Name" },
			new InventoryItem { AssetId = 2, AppId = CardsApp, ClassId = 100, InstanceId = 150, Tradable = true, TradabilityDate = noDate },
			new InventoryItem { AssetId = 3, AppId = CardsApp, ClassId = 200, InstanceId = 250, Tradable = true, TradabilityDate = noDate },
			new InventoryItem { AssetId = 4, AppId = CardsApp, ClassId = 200, InstanceId = 250, Tradable = true, TradabilityDate = noDate }
		};

		var groups = CardSwapMatcher.FindDuplicates(items, keep: 1);

		Assert.Equal(2, groups.Count);
		Assert.Equal("Fallback Name", groups.Single(g => g.ClassId == 100UL).Name);
		Assert.Equal(string.Empty, groups.Single(g => g.ClassId == 200UL).Name);
	}

	[Fact]
	public void FindDuplicates_PastTradabilityDate_IsTradableNow()
	{
		// Tradable with an already-lapsed tradability date counts as tradable now
		// (the date arm passes), unlike a future date which is trade-locked.
		var items = new[]
		{
			Card(1, 100, "A", tradabilityDate: _now.AddMinutes(-5)),
			Card(2, 100, "A", tradabilityDate: _now.AddMinutes(-5))
		};

		DuplicateGroup group = Assert.Single(CardSwapMatcher.FindDuplicates(items, keep: 1));

		Assert.Equal(2, group.TotalTradable);
		Assert.Single(group.ExcessItems);
	}
}
