using Vapor.Plugins.CaseOpening;
using Vapor.Steam.Core.Models;

using Xunit;

namespace Vapor.Plugins.CaseOpening.Tests;

public class CaseInventoryClassifierTests
{
	private static readonly CaseCatalog Catalog = new(
	[
		new CaseDefinition("custom.case", "Custom Case",
		[
			new CaseItem("x1", "Pistol | X", CaseRarity.MilSpec),
			new CaseItem("x2", "SMG | X", CaseRarity.Restricted),
			new CaseItem("x3", "Rifle | X", CaseRarity.Classified),
			new CaseItem("x4", "Rifle | Y", CaseRarity.Covert),
			new CaseItem("x5", "Knife | X", CaseRarity.RareSpecial)
		]),
		new CaseDefinition("other.case", "Other Case",
		[
			new CaseItem("y1", "Pistol | Y", CaseRarity.MilSpec),
			new CaseItem("y2", "SMG | Y", CaseRarity.Restricted),
			new CaseItem("y3", "Rifle | Z", CaseRarity.Classified),
			new CaseItem("y4", "Rifle | W", CaseRarity.Covert),
			new CaseItem("y5", "Knife | Y", CaseRarity.RareSpecial)
		])
	]);

	private static InventoryItem Item(string marketHashName, int amount = 1, string? name = null, uint assetId = 1) =>
		new() { AssetId = assetId, Name = name ?? marketHashName, MarketHashName = marketHashName, Amount = amount };

	[Fact]
	public void Classify_EmptyInventory_ReportsZeroes()
	{
		CaseInventorySummary summary = CaseInventoryClassifier.Classify([], Catalog);

		Assert.Equal(0, summary.TotalCount);
		Assert.Empty(summary.Cases);
		Assert.Equal(0, summary.KeyCount);
		Assert.Equal(0, summary.OtherCount);
	}

	[Fact]
	public void Classify_GroupsCasesByName_SumsStacks_CountsKeysAndOthers()
	{
		List<InventoryItem> items =
		[
			Item("Custom Case", amount: 1),
			Item("Custom Case", amount: 2, assetId: 2),
			Item("Other Case", amount: 4, assetId: 3),
			// "Case Key" ends with "Key" — it is a key, never a case.
			Item("Custom Case Key", amount: 3, assetId: 4),
			Item("Gloves | Black", amount: 5, assetId: 5),
			// An amount of 0 still counts the item once.
			Item("Pistol | Z", amount: 0, assetId: 6)
		];

		CaseInventorySummary summary = CaseInventoryClassifier.Classify(items, Catalog);

		Assert.Equal(6, summary.TotalCount);
		Assert.Equal(2, summary.Cases.Count);
		Assert.Equal("Custom Case", summary.Cases[0].Name);
		Assert.Equal(3, summary.Cases[0].Count);
		Assert.Equal("Other Case", summary.Cases[1].Name);
		Assert.Equal(4, summary.Cases[1].Count);
		Assert.Equal(3, summary.KeyCount);
		Assert.Equal(6, summary.OtherCount); // 5 gloves + 1 zero-amount pistol
	}

	[Fact]
	public void Classify_MapsCatalogCaseIds_ByCaseName()
	{
		List<InventoryItem> items =
		[
			Item("Custom Case"),
			Item("Unknown Case", assetId: 2)
		];

		CaseInventorySummary summary = CaseInventoryClassifier.Classify(items, Catalog);

		Assert.Equal("custom.case", summary.Cases[0].CaseId);
		Assert.Null(summary.Cases[1].CaseId);
	}

	[Fact]
	public void Classify_MapsCatalogCaseId_CaseInsensitively()
	{
		CaseInventorySummary summary = CaseInventoryClassifier.Classify([Item("custom CASE")], Catalog);

		Assert.Equal("custom.case", summary.Cases.Single().CaseId);
		Assert.Equal("custom CASE", summary.Cases.Single().Name);
	}

	[Fact]
	public void Classify_PrefersMarketHashName_OverDisplayName()
	{
		CaseInventorySummary summary = CaseInventoryClassifier.Classify(
			[Item("Real Case", name: "Whatever")], Catalog);

		// The market hash name wins; the display name never leaks into the groups.
		CaseInventoryGroup group = summary.Cases.Single();
		Assert.Equal("Real Case", group.Name);
		Assert.DoesNotContain(summary.Cases, candidate => candidate.Name == "Whatever");
		Assert.Equal(0, summary.OtherCount);
	}

	[Fact]
	public void Classify_FallsBackToDisplayName_WhenMarketHashNameBlank()
	{
		List<InventoryItem> items =
		[
			new InventoryItem { Name = "Fallback Key", MarketHashName = null },
			new InventoryItem { Name = "Fallback Case", MarketHashName = "   ", AssetId = 2 }
		];

		CaseInventorySummary summary = CaseInventoryClassifier.Classify(items, Catalog);

		Assert.Equal(1, summary.KeyCount);
		CaseInventoryGroup group = summary.Cases.Single();
		Assert.Equal("Fallback Case", group.Name);
	}

	[Fact]
	public void Classify_OrdersGroupsByOrdinalName()
	{
		List<InventoryItem> items =
		[
			Item("Zebra Case", assetId: 1),
			Item("Alpha Case", assetId: 2),
			Item("Beta Case", assetId: 3)
		];

		CaseInventorySummary summary = CaseInventoryClassifier.Classify(items, Catalog);

		Assert.Equal(["Alpha Case", "Beta Case", "Zebra Case"], summary.Cases.Select(group => group.Name).ToArray());
	}

	[Fact]
	public void Classify_RejectsNullArguments()
	{
		Assert.Throws<ArgumentNullException>(() => CaseInventoryClassifier.Classify(null!, Catalog));
		Assert.Throws<ArgumentNullException>(() => CaseInventoryClassifier.Classify([], null!));
	}

	[Fact]
	public void Classify_BlankName_IsOther()
	{
		CaseInventorySummary summary = CaseInventoryClassifier.Classify(
			[new InventoryItem { Name = null, MarketHashName = null }], Catalog);

		Assert.Equal(1, summary.OtherCount);
		Assert.Empty(summary.Cases);
	}
}
