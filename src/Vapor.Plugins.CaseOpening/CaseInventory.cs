using Vapor.Steam.Core.Models;

namespace Vapor.Plugins.CaseOpening;

/// <summary>One classified line of the backpack: how many of one market-hash name.</summary>
public sealed record CaseInventoryGroup(string Name, string? CaseId, int Count);

/// <summary>
/// The classified CS2 inventory: every case container found (carrying the
/// catalog case id when the container name matches a known case), the key
/// count, and everything else. A read-only projection — classification never
/// mutates state and never writes to the ledger.
/// </summary>
public sealed record CaseInventorySummary(
	int TotalCount,
	IReadOnlyList<CaseInventoryGroup> Cases,
	int KeyCount,
	int OtherCount);

/// <summary>
/// Pure classifier over an inventory item list, so the heuristic is pinned by
/// tests without a trade client: a container whose market hash name ends with
/// "Case" is a case; one ending with "Key" is a key (a "Case Key" is a key —
/// keys win); everything else is other. When a container's name also matches a
/// catalog case name, that group carries the catalog's case id, which is how a
/// live read is reconciled against the local ledger.
/// Rules are ordinal and deterministic, and groups are ordered by name.
/// </summary>
public static class CaseInventoryClassifier
{
	public static CaseInventorySummary Classify(IReadOnlyList<InventoryItem> items, CaseCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(catalog);

		var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		int keys = 0;
		int others = 0;

		foreach (InventoryItem item in items)
		{
			string name = MarketName(item) ?? string.Empty;
			if (name.EndsWith("Key", StringComparison.OrdinalIgnoreCase))
			{
				keys += StackCount(item);
			}
			else if (name.EndsWith("Case", StringComparison.OrdinalIgnoreCase))
			{
				byName[name] = byName.GetValueOrDefault(name) + StackCount(item);
			}
			else
			{
				others += StackCount(item);
			}
		}

		var cases = byName
			.OrderBy(entry => entry.Key, StringComparer.Ordinal)
			.Select(entry => new CaseInventoryGroup(entry.Key, FindCaseId(catalog, entry.Key), entry.Value))
			.ToArray();

		return new CaseInventorySummary(items.Count, cases, keys, others);
	}

	/// <summary>The name used for classification: market hash name, falling back to the display name.</summary>
	private static string? MarketName(InventoryItem item) =>
		!string.IsNullOrWhiteSpace(item.MarketHashName) ? item.MarketHashName : item.Name;

	/// <summary>Stack size (an item with amount 0 still counts once).</summary>
	private static int StackCount(InventoryItem item) => item.Amount > 0 ? item.Amount : 1;

	private static string? FindCaseId(CaseCatalog catalog, string name)
	{
		foreach (CaseDefinition definition in catalog.Cases)
		{
			if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				return definition.Id;
			}
		}

		return null;
	}
}
