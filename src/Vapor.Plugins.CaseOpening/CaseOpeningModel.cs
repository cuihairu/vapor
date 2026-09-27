namespace Vapor.Plugins.CaseOpening;

/// <summary>
/// The five rarity tiers of a key-opened weapon case, in ascending order of
/// scarcity. Odds per tier follow Valve's published disclosure (625:125:25:5:2
/// out of 782 — see docs/plugins.md, "Case opening plugin").
/// </summary>
public enum CaseRarity
{
	MilSpec = 0,
	Restricted = 1,
	Classified = 2,
	Covert = 3,
	RareSpecial = 4
}

/// <summary>One item in a case's content pool.</summary>
/// <param name="Id">Stable item identifier, unique within its case.</param>
/// <param name="Name">Display name (e.g. "Rifle | Legacy").</param>
/// <param name="Rarity">The tier this item belongs to.</param>
/// <param name="MinFloat">
/// Lower bound of the item's wear range. Ranges are per paint kit, not per
/// case (e.g. 0.10–0.70 for a field-tested-only finish; knives span
/// 0.00–1.00). Default 0.06, the common "unless overridden" release range.
/// </param>
/// <param name="MaxFloat">Upper bound of the item's wear range; default 0.80.</param>
/// <param name="StatTrakAllowed">
/// Whether a StatTrak™ variant exists (rolled independently at 1:10).
/// </param>
public sealed record CaseItem(
	string Id,
	string Name,
	CaseRarity Rarity,
	double MinFloat = 0.06,
	double MaxFloat = 0.80,
	bool StatTrakAllowed = true);

/// <summary>A case definition: identity plus the full content pool.</summary>
public sealed record CaseDefinition(
	string Id,
	string Name,
	IReadOnlyList<CaseItem> Items)
{
	/// <summary>
	/// Items grouped by rarity, in tier order. Built once at construction so a
	/// roll never re-filters the pool; empty groups mean the case is invalid
	/// (see <see cref="CaseCatalog"/> validation) and the engine throws if asked
	/// to roll into one.
	/// </summary>
	public IReadOnlyDictionary<CaseRarity, IReadOnlyList<CaseItem>> ItemsByRarity { get; } =
		Enum.GetValues<CaseRarity>().ToDictionary(
			rarity => rarity,
			rarity => (IReadOnlyList<CaseItem>)Items.Where(item => item.Rarity == rarity).ToArray());

	/// <summary>Canonical lowercase JSON name of a rarity tier.</summary>
	public static string RarityName(CaseRarity rarity) => rarity switch
	{
		CaseRarity.MilSpec => "milspec",
		CaseRarity.Restricted => "restricted",
		CaseRarity.Classified => "classified",
		CaseRarity.Covert => "covert",
		_ => "rare_special"
	};

	/// <summary>Parses a rarity name (case-insensitive); false when unknown.</summary>
	public static bool TryParseRarity(string? text, out CaseRarity rarity)
	{
		rarity = default;
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		return text.Trim().ToLowerInvariant() switch
		{
			"milspec" or "mil-spec" or "blue" => Assign(CaseRarity.MilSpec, ref rarity),
			"restricted" or "purple" => Assign(CaseRarity.Restricted, ref rarity),
			"classified" or "pink" => Assign(CaseRarity.Classified, ref rarity),
			"covert" or "red" => Assign(CaseRarity.Covert, ref rarity),
			"rare_special" or "rare-special" or "rarespecial" or "gold" or "knife" => Assign(CaseRarity.RareSpecial, ref rarity),
			_ => false
		};

		static bool Assign(CaseRarity value, ref CaseRarity rarity)
		{
			rarity = value;
			return true;
		}
	}
}

/// <summary>The five wear tiers and their fixed float thresholds.</summary>
public static class WearTier
{
	/// <summary>Factory New: float below 0.07.</summary>
	public const string FactoryNew = "Factory New";

	/// <summary>Minimal Wear: float in [0.07, 0.15).</summary>
	public const string MinimalWear = "Minimal Wear";

	/// <summary>Field-Tested: float in [0.15, 0.38).</summary>
	public const string FieldTested = "Field-Tested";

	/// <summary>Well-Worn: float in [0.38, 0.45).</summary>
	public const string WellWorn = "Well-Worn";

	/// <summary>Battle-Scarred: float of 0.45 or above.</summary>
	public const string BattleScarred = "Battle-Scarred";

	/// <summary>Maps a float value to its wear tier (fixed thresholds from the CSFloat float analysis).</summary>
	public static string ForFloat(double floatValue) => floatValue switch
	{
		< 0.07 => FactoryNew,
		< 0.15 => MinimalWear,
		< 0.38 => FieldTested,
		< 0.45 => WellWorn,
		_ => BattleScarred
	};
}

/// <summary>One recorded open: what was rolled, from which case, with which rolls.</summary>
public sealed record OpenResult(
	string CaseId,
	string ItemId,
	string ItemName,
	CaseRarity Rarity,
	bool StatTrak,
	double FloatValue,
	string Wear,
	int PaintSeed)
{
	/// <summary>UTC instant of the open.</summary>
	public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
