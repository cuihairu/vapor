using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapor.Plugins.CaseOpening;

/// <summary>
/// A set of loadable case definitions plus catalog lookup. The default catalog
/// ships two illustrative cases with synthetic item names; operators point
/// <c>cases.path</c> at their own catalog JSON (replacing the default) to model
/// real case pools — per-case item data is available from community sources
/// such as jonese1234/Csgo-Case-Data (see docs/plugins.md).
/// </summary>
/// <param name="Cases">The catalog's cases, in file/declaration order.</param>
public sealed record CaseCatalog(IReadOnlyList<CaseDefinition> Cases)
{
	private static readonly JsonSerializerOptions StrictOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Disallow,
		AllowTrailingCommas = false,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
	};

	/// <summary>The built-in illustrative catalog.</summary>
	public static CaseCatalog Default { get; } = BuildDefault();

	/// <summary>Finds a case by id (case-insensitive); null when absent.</summary>
	public CaseDefinition? Find(string caseId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

		return Cases.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, caseId.Trim(), StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Parses a catalog document. Strict by design: unknown members, trailing
	/// commas, missing tiers, inverted float ranges, duplicate ids and blank
	/// names all fail with a descriptive error — a typo must never silently
	/// reshape the odds.
	/// </summary>
	public static CaseCatalog ParseJson(string json)
	{
		ArgumentException.ThrowIfNullOrEmpty(json);

		CatalogDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<CatalogDocument>(json, StrictOptions);
		}
		catch (JsonException ex)
		{
			throw new FormatException($"Invalid case catalog: {ex.Message}", ex);
		}

		if (document?.Cases is null || document.Cases.Count == 0)
		{
			throw new FormatException("Invalid case catalog: expected a non-empty 'cases' array");
		}

		var cases = new List<CaseDefinition>(document.Cases.Count);
		var seenCaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CaseDocument entry in document.Cases)
		{
			CaseDefinition definition = BuildDefinition(entry);
			if (!seenCaseIds.Add(definition.Id))
			{
				throw new FormatException($"Invalid case catalog: duplicate case id '{definition.Id}'");
			}

			cases.Add(definition);
		}

		return new CaseCatalog(cases);
	}

	/// <summary>Loads and parses a catalog file.</summary>
	public static CaseCatalog LoadFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		string json;
		try
		{
			json = File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new FormatException($"Case catalog '{path}' could not be read: {ex.Message}", ex);
		}

		return ParseJson(json);
	}

	private static CaseDefinition BuildDefinition(CaseDocument entry)
	{
		if (string.IsNullOrWhiteSpace(entry.Id))
		{
			throw new FormatException("Invalid case catalog: every case needs a non-empty 'id'");
		}

		if (string.IsNullOrWhiteSpace(entry.Name))
		{
			throw new FormatException($"Invalid case catalog: case '{entry.Id}' needs a non-empty 'name'");
		}

		if (entry.Items is null || entry.Items.Count == 0)
		{
			throw new FormatException($"Invalid case catalog: case '{entry.Id}' has no items");
		}

		var items = new List<CaseItem>(entry.Items.Count);
		var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var presentTiers = new HashSet<CaseRarity>();
		foreach (ItemDocument itemEntry in entry.Items)
		{
			CaseItem item = BuildItem(entry.Id, itemEntry);
			if (!seenItemIds.Add(item.Id))
			{
				throw new FormatException($"Invalid case catalog: case '{entry.Id}' has duplicate item id '{item.Id}'");
			}

			if (item.MinFloat < 0 || item.MaxFloat > 1 || item.MinFloat >= item.MaxFloat)
			{
				throw new FormatException(
					$"Invalid case catalog: item '{item.Id}' in case '{entry.Id}' has an invalid float range [{item.MinFloat}, {item.MaxFloat}] (need 0 <= min < max <= 1)");
			}

			presentTiers.Add(item.Rarity);
			items.Add(item);
		}

		foreach (CaseRarity tier in Enum.GetValues<CaseRarity>())
		{
			if (!presentTiers.Contains(tier))
			{
				throw new FormatException(
					$"Invalid case catalog: case '{entry.Id}' is missing its {CaseDefinition.RarityName(tier)} tier (every tier of the published table must be represented)");
			}
		}

		return new CaseDefinition(entry.Id.Trim(), entry.Name.Trim(), items);
	}

	private static CaseItem BuildItem(string caseId, ItemDocument entry)
	{
		if (string.IsNullOrWhiteSpace(entry.Id))
		{
			throw new FormatException($"Invalid case catalog: case '{caseId}' has an item without an 'id'");
		}

		if (string.IsNullOrWhiteSpace(entry.Name))
		{
			throw new FormatException($"Invalid case catalog: item '{entry.Id}' in case '{caseId}' has no 'name'");
		}

		if (!CaseDefinition.TryParseRarity(entry.Rarity, out CaseRarity rarity))
		{
			throw new FormatException(
				$"Invalid case catalog: item '{entry.Id}' in case '{caseId}' has unknown rarity '{entry.Rarity}' (expected milspec/restricted/classified/covert/rare_special)");
		}

		return new CaseItem(
			Id: entry.Id.Trim(),
			Name: entry.Name.Trim(),
			Rarity: rarity,
			MinFloat: entry.MinFloat ?? 0.06,
			MaxFloat: entry.MaxFloat ?? 0.80,
			StatTrakAllowed: entry.Stattrak ?? true);
	}

	private static CaseCatalog BuildDefault()
	{
		// Illustrative cases with synthetic item names; one item per tier beyond
		// mil-spec keeps the demo light while still exercising every tier of the
		// published table. Real pools load via cases.path.
		CaseItem Item(string id, string name, CaseRarity rarity) => new(id, name, rarity);

		return new CaseCatalog(
		[
			new CaseDefinition("vapor.cases.starter", "Starter Case (illustrative)",
			[
				Item("starter-1", "Pistol | Sandspout", CaseRarity.MilSpec),
				Item("starter-2", "SMG | Dustline", CaseRarity.MilSpec),
				Item("starter-3", "Shotgun | Quarry", CaseRarity.MilSpec),
				Item("starter-4", "Rifle | Terracotta", CaseRarity.MilSpec),
				Item("starter-5", "SMG | Tidepool", CaseRarity.Restricted),
				Item("starter-6", "Rifle | Foxfire", CaseRarity.Restricted),
				Item("starter-7", "Pistol | Copperhead", CaseRarity.Classified),
				Item("starter-8", "Rifle | Marigold", CaseRarity.Covert),
				Item("starter-9", "Knife | Ember", CaseRarity.RareSpecial)
			]),
			new CaseDefinition("vapor.cases.golden", "Golden Case (illustrative)",
			[
				Item("golden-1", "Pistol | Gilded Sand", CaseRarity.MilSpec),
				Item("golden-2", "SMG | Brassline", CaseRarity.MilSpec),
				Item("golden-3", "Shotgun | Nomad", CaseRarity.MilSpec),
				Item("golden-4", "Rifle | Aurum", CaseRarity.MilSpec),
				Item("golden-5", "Pistol | Necropolis", CaseRarity.Restricted),
				Item("golden-6", "SMG | Chrysos", CaseRarity.Restricted),
				Item("golden-7", "Rifle | Palatial", CaseRarity.Classified),
				Item("golden-8", "SMG | Imperial", CaseRarity.Covert),
				Item("golden-9", "Knife | Sovereign", CaseRarity.RareSpecial)
			])
		]);
	}

	private sealed record CatalogDocument(IReadOnlyList<CaseDocument> Cases);

	[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
	private sealed record CaseDocument(
		string? Id,
		string? Name,
		IReadOnlyList<ItemDocument>? Items);

	[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
	private sealed record ItemDocument(
		string? Id,
		string? Name,
		string? Rarity,
		double? MinFloat = null,
		double? MaxFloat = null,
		bool? Stattrak = null);
}
