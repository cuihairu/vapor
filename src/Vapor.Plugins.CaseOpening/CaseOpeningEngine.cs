namespace Vapor.Plugins.CaseOpening;

/// <summary>
/// The case-opening probability engine: Valve's published rarity table, uniform
/// item picks, an independent 1:10 StatTrak™ roll, per-item float mapping and a
/// uniform paint seed. Purely local RNG — no network or Steam access of any
/// kind (docs/plugins.md, "Case opening plugin").
///
/// Roll order per open (fixed; scripted-random tests depend on it):
/// 1. rarity — <see cref="Next(782)"/> against the 625:125:25:5:2 table,
/// 2. item — uniform pick within the rolled tier,
/// 3. StatTrak™ — <see cref="Next(10)"/> == 0 when the item allows it,
/// 4. float — <see cref="NextDouble"/> mapped linearly into the item's range,
/// 5. paint seed — <see cref="Next(1001)"/> (0–1000).
/// </summary>
public sealed class CaseOpeningEngine
{
	/// <summary>Total weight of the rarity table (625 + 125 + 25 + 5 + 2).</summary>
	internal const int RarityWeightTotal = 782;

	internal const int MilSpecWeight = 625;
	internal const int RestrictedWeight = 125;
	internal const int ClassifiedWeight = 25;
	internal const int CovertWeight = 5;
	internal const int RareSpecialWeight = 2;

	/// <summary>StatTrak™ chance: one in ten when the item has a StatTrak variant.</summary>
	internal const int StatTrakOddsDenominator = 10;

	/// <summary>Highest valid paint seed (inclusive); seed 1000 is reachable via trade-ups in-game.</summary>
	internal const int MaxPaintSeed = 1000;

	private readonly Random _random;

	/// <summary>Creates an engine backed by a fresh Random.</summary>
	public CaseOpeningEngine()
		: this(new Random())
	{
	}

	/// <summary>Creates an engine with a seeded Random (deterministic tests).</summary>
	public CaseOpeningEngine(int seed)
		: this(new Random(seed))
	{
	}

	/// <summary>Creates an engine over an injected Random (scripted-random tests).</summary>
	internal CaseOpeningEngine(Random random)
	{
		_random = random ?? throw new ArgumentNullException(nameof(random));
	}

	/// <summary>
	/// Rolls one open against <paramref name="caseDefinition"/>. Each call is an
	/// independent identically distributed draw — there is no pity system.
	/// </summary>
	public OpenResult Open(CaseDefinition caseDefinition)
		=> Open(caseDefinition, DateTimeOffset.UtcNow);

	/// <summary>Rolls one open with an explicit timestamp (tests).</summary>
	internal OpenResult Open(CaseDefinition caseDefinition, DateTimeOffset timestamp)
	{
		ArgumentNullException.ThrowIfNull(caseDefinition);

		CaseRarity rarity = RarityFor(_random.Next(RarityWeightTotal));
		CaseItem item = PickItem(caseDefinition, rarity, _random.Next(caseDefinition.ItemsByRarity[rarity].Count));
		bool stattrak = item.StatTrakAllowed && _random.Next(StatTrakOddsDenominator) == 0;
		double floatValue = item.MinFloat + (_random.NextDouble() * (item.MaxFloat - item.MinFloat));

		return new OpenResult(
			CaseId: caseDefinition.Id,
			ItemId: item.Id,
			ItemName: item.Name,
			Rarity: rarity,
			StatTrak: stattrak,
			FloatValue: floatValue,
			Wear: WearTier.ForFloat(floatValue),
			PaintSeed: _random.Next(MaxPaintSeed + 1))
		{
			Timestamp = timestamp
		};
	}

	/// <summary>Maps a [0, 782) sample onto a rarity tier (exact published weights).</summary>
	internal static CaseRarity RarityFor(int sample) => sample switch
	{
		< MilSpecWeight => CaseRarity.MilSpec,
		< MilSpecWeight + RestrictedWeight => CaseRarity.Restricted,
		< MilSpecWeight + RestrictedWeight + ClassifiedWeight => CaseRarity.Classified,
		< MilSpecWeight + RestrictedWeight + ClassifiedWeight + CovertWeight => CaseRarity.Covert,
		_ => CaseRarity.RareSpecial
	};

	/// <summary>Picks the <paramref name="index"/>-th item of a tier (uniform within the tier).</summary>
	internal static CaseItem PickItem(CaseDefinition caseDefinition, CaseRarity rarity, int index)
	{
		IReadOnlyList<CaseItem> tierItems = caseDefinition.ItemsByRarity[rarity];
		if (tierItems.Count == 0)
		{
			throw new InvalidOperationException(
				$"Case '{caseDefinition.Id}' has no items of rarity {CaseDefinition.RarityName(rarity)}; catalog validation must guarantee one item per tier");
		}

		return tierItems[index % tierItems.Count];
	}
}
