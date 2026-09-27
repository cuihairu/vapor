using Xunit;

namespace Vapor.Plugins.CaseOpening.Tests;

/// <summary>
/// Engine roll semantics: the exact published rarity windows, tier-uniform item
/// picks, the 1:10 StatTrak roll, per-item float mapping, wear thresholds and
/// paint-seed range — scripted-random determinism plus one coarse statistical
/// sanity pass.
/// </summary>
public sealed class CaseOpeningEngineTests
{
	/// <summary>A case with one item per tier (hand-built; bypasses catalog validation).</summary>
	private static CaseDefinition OnePerTierCase() => new(
		"test.case",
		"Test Case",
		[
			new CaseItem("t-blue", "Pistol | Blue", CaseRarity.MilSpec, 0.06, 0.80),
			new CaseItem("t-purple", "SMG | Purple", CaseRarity.Restricted, 0.00, 0.50, StatTrakAllowed: false),
			new CaseItem("t-pink", "Rifle | Pink", CaseRarity.Classified, 0.10, 0.70),
			new CaseItem("t-red", "Rifle | Red", CaseRarity.Covert),
			new CaseItem("t-gold", "Knife | Gold", CaseRarity.RareSpecial, 0.00, 1.00)
		]);

	[Theory]
	[InlineData(0, CaseRarity.MilSpec)]
	[InlineData(624, CaseRarity.MilSpec)]
	[InlineData(625, CaseRarity.Restricted)]
	[InlineData(749, CaseRarity.Restricted)]
	[InlineData(750, CaseRarity.Classified)]
	[InlineData(774, CaseRarity.Classified)]
	[InlineData(775, CaseRarity.Covert)]
	[InlineData(779, CaseRarity.Covert)]
	[InlineData(780, CaseRarity.RareSpecial)]
	[InlineData(781, CaseRarity.RareSpecial)]
	public void RarityFor_MapsExactPublishedWindows(int sample, CaseRarity expected)
	{
		Assert.Equal(expected, CaseOpeningEngine.RarityFor(sample));
	}

	[Fact]
	public void PickItem_ReturnsTierItem_AndWrapsIndex()
	{
		CaseDefinition definition = OnePerTierCase();

		Assert.Equal("t-blue", CaseOpeningEngine.PickItem(definition, CaseRarity.MilSpec, 0).Id);
		Assert.Equal("t-gold", CaseOpeningEngine.PickItem(definition, CaseRarity.RareSpecial, 7).Id);
	}

	[Fact]
	public void PickItem_Throws_WhenTierEmpty()
	{
		// Hand-built definition skips catalog validation, so the engine guard
		// (not the catalog) must catch a missing tier.
		var definition = new CaseDefinition("broken.case", "Broken", [new CaseItem("only", "Only | One", CaseRarity.MilSpec)]);

		var error = Assert.Throws<InvalidOperationException>(
			() => CaseOpeningEngine.PickItem(definition, CaseRarity.RareSpecial, 0));

		Assert.Contains("no items of rarity rare_special", error.Message);
	}

	[Fact]
	public void Open_FollowsDocumentedRollOrder_AndMapsEveryRoll()
	{
		var random = new ScriptedRandom();
		// rarity, item index, stattrak (0 == stattrak), float sample, paint seed
		random.EnqueueInt(780);
		random.EnqueueInt(0);
		random.EnqueueInt(0);
		random.EnqueueDouble(0.5);
		random.EnqueueInt(42);

		DateTimeOffset timestamp = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
		OpenResult result = new CaseOpeningEngine(random).Open(OnePerTierCase(), timestamp);

		Assert.Equal("test.case", result.CaseId);
		Assert.Equal("t-gold", result.ItemId);
		Assert.Equal(CaseRarity.RareSpecial, result.Rarity);
		Assert.True(result.StatTrak);
		Assert.Equal(0.5, result.FloatValue, precision: 12); // knife range [0, 1], sample 0.5
		Assert.Equal(WearTier.BattleScarred, result.Wear);
		Assert.Equal(42, result.PaintSeed);
		Assert.Equal(timestamp, result.Timestamp);
	}

	[Fact]
	public void Open_SkipsStatTrakRoll_WhenItemHasNoStatTrakVariant()
	{
		var random = new ScriptedRandom();
		random.EnqueueInt(625); // restricted tier
		random.EnqueueInt(0); // only item, StatTrakAllowed: false
		random.EnqueueDouble(0.0);
		random.EnqueueInt(1000);

		OpenResult result = new CaseOpeningEngine(random).Open(OnePerTierCase());

		Assert.Equal("t-purple", result.ItemId);
		Assert.False(result.StatTrak);
		Assert.Equal(0.0, result.FloatValue, precision: 12);
		Assert.Equal(WearTier.FactoryNew, result.Wear);
		Assert.Equal(1000, result.PaintSeed);
	}

	[Fact]
	public void Open_StatTrakFalse_WhenRollMissesOneInTen()
	{
		var random = new ScriptedRandom();
		random.EnqueueInt(0); // milspec
		random.EnqueueInt(0);
		random.EnqueueInt(1); // 1..9 = no stattrak
		random.EnqueueDouble(0.44);
		random.EnqueueInt(0);

		OpenResult result = new CaseOpeningEngine(random).Open(OnePerTierCase());

		Assert.Equal("t-blue", result.ItemId);
		Assert.False(result.StatTrak);
		// Sample 0.44 mapped into the item's own [0.06, 0.80] range.
		Assert.Equal(0.06 + (0.44 * (0.80 - 0.06)), result.FloatValue, precision: 12);
		Assert.Equal(WearTier.WellWorn, result.Wear);
	}

	[Fact]
	public void Open_UsesItemOwnFloatRange()
	{
		var random = new ScriptedRandom();
		random.EnqueueInt(750); // classified: [0.10, 0.70]
		random.EnqueueInt(0);
		random.EnqueueInt(9); // no stattrak
		random.EnqueueDouble(1.0); // top of the item's own range
		random.EnqueueInt(500);

		OpenResult result = new CaseOpeningEngine(random).Open(OnePerTierCase());

		Assert.Equal("t-pink", result.ItemId);
		// Same expression as the engine: min + sample * (max - min).
		Assert.Equal(0.10 + (1.0 * (0.70 - 0.10)), result.FloatValue, precision: 15);
	}

	[Theory]
	[InlineData(0.0, WearTier.FactoryNew)]
	[InlineData(0.069, WearTier.FactoryNew)]
	[InlineData(0.07, WearTier.MinimalWear)]
	[InlineData(0.149, WearTier.MinimalWear)]
	[InlineData(0.15, WearTier.FieldTested)]
	[InlineData(0.379, WearTier.FieldTested)]
	[InlineData(0.38, WearTier.WellWorn)]
	[InlineData(0.449, WearTier.WellWorn)]
	[InlineData(0.45, WearTier.BattleScarred)]
	[InlineData(1.0, WearTier.BattleScarred)]
	public void WearTier_ForFloat_UsesFixedThresholds(double floatValue, string expected)
	{
		Assert.Equal(expected, WearTier.ForFloat(floatValue));
	}

	[Fact]
	public void Constructor_RejectsNullRandom()
	{
		Assert.Throws<ArgumentNullException>(() => new CaseOpeningEngine((Random)null!));
	}

	[Fact]
	public void PublicConstructors_ProduceWorkingEngines()
	{
		CaseDefinition definition = OnePerTierCase();

		OpenResult seeded = new CaseOpeningEngine(20260927).Open(definition);
		OpenResult unseeded = new CaseOpeningEngine().Open(definition);

		Assert.Equal("test.case", seeded.CaseId);
		Assert.Equal("test.case", unseeded.CaseId);
		Assert.InRange(seeded.FloatValue, 0, 1);
		Assert.InRange(unseeded.FloatValue, 0, 1);
		Assert.InRange(unseeded.PaintSeed, 0, CaseOpeningEngine.MaxPaintSeed);
	}

	/// <summary>
	/// Coarse statistical sanity over 200k opens: the observed tier rates must
	/// sit within wide bounds around the published table (4σ+ slack; this is a
	/// tripwire for a broken table, not a chi-squared test).
	/// </summary>
	[Fact]
	public void Open_StatisticalSanity_MatchesPublishedRates()
	{
		const int opens = 200_000;
		var engine = new CaseOpeningEngine(20260927);
		CaseDefinition definition = OnePerTierCase();
		var counts = new int[5];

		for (int i = 0; i < opens; i++)
		{
			counts[(int)engine.Open(definition).Rarity]++;
		}

		AssertRate(counts[(int)CaseRarity.MilSpec], opens, 0.79923, 0.78, 0.82);
		AssertRate(counts[(int)CaseRarity.Restricted], opens, 0.15985, 0.15, 0.17);
		AssertRate(counts[(int)CaseRarity.Classified], opens, 0.03197, 0.028, 0.036);
		AssertRate(counts[(int)CaseRarity.Covert], opens, 0.00639, 0.005, 0.008);
		AssertRate(counts[(int)CaseRarity.RareSpecial], opens, 0.00256, 0.0018, 0.0034);
	}

	private static void AssertRate(int count, int opens, double expected, double low, double high)
	{
		double rate = count / (double)opens;
		Assert.True(
			rate >= low && rate <= high,
			$"tier with published rate {expected}: observed {rate:0.0000} outside [{low}, {high}] ({count}/{opens})");
	}

	/// <summary>Random replacement feeding scripted samples in call order.</summary>
	private sealed class ScriptedRandom : Random
	{
		private readonly Queue<int> _ints = new();
		private readonly Queue<double> _doubles = new();

		public void EnqueueInt(int value) => _ints.Enqueue(value);

		public void EnqueueDouble(double value) => _doubles.Enqueue(value);

		public override int Next() => DequeueInt();

		public override int Next(int maxValue) => DequeueInt();

		public override int Next(int minValue, int maxValue) => minValue + DequeueInt() % (maxValue - minValue);

		public override double NextDouble() => _doubles.Count > 0 ? _doubles.Dequeue() : 0.5;

		private int DequeueInt() => _ints.Count > 0 ? _ints.Dequeue() : 0;
	}
}
