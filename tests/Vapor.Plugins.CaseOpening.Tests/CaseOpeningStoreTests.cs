using System.Text.Json;
using Xunit;

namespace Vapor.Plugins.CaseOpening.Tests;

/// <summary>
/// Result recorder behavior: retention window, durable JSONL archive with
/// restart rehydration, malformed-line tolerance, swallowed I/O failures and
/// stats aggregation.
/// </summary>
public sealed class CaseOpeningStoreTests
{
	private static OpenResult Result(string caseId = "c1", CaseRarity rarity = CaseRarity.MilSpec, int seed = 0) => new(
		CaseId: caseId,
		ItemId: "item",
		ItemName: "Rifle | Test",
		Rarity: rarity,
		StatTrak: false,
		FloatValue: 0.2,
		Wear: WearTier.FieldTested,
		PaintSeed: seed)
	{
		Timestamp = DateTimeOffset.UnixEpoch
	};

	[Fact]
	public void Record_KeepsInsertionOrder_InMemoryOnlyByDefault()
	{
		var store = new CaseOpeningStore();
		store.Record(Result(seed: 1));
		store.Record(Result(seed: 2));

		Assert.Equal(2, store.Count);
		Assert.Equal([1, 2], store.RecentResults().Select(result => result.PaintSeed));
	}

	[Fact]
	public void Record_RejectsNullResults()
	{
		Assert.Throws<ArgumentNullException>(() => new CaseOpeningStore().Record(null!));
	}

	[Fact]
	public void Record_EvictsOldest_BeyondCapacity()
	{
		var store = new CaseOpeningStore(filePath: null, capacity: 3);
		foreach (int seed in Enumerable.Range(1, 5))
		{
			store.Record(Result(seed: seed));
		}

		Assert.Equal(3, store.Count);
		Assert.Equal([3, 4, 5], store.RecentResults().Select(result => result.PaintSeed));
	}

	[Fact]
	public void Constructor_RejectsNonPositiveCapacity()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new CaseOpeningStore(null, capacity: 0));
	}

	[Fact]
	public void Record_AppendsToArchive_AndRestartRehydrates()
	{
		string path = Path.Combine(Path.GetTempPath(), $"vapor-case-results-{Guid.NewGuid():N}.jsonl");
		try
		{
			var store = new CaseOpeningStore(path);
			store.Record(Result(seed: 1));
			store.Record(Result(caseId: "c2", rarity: CaseRarity.RareSpecial, seed: 2));

			string[] lines = File.ReadAllLines(path);
			Assert.Equal(2, lines.Length);

			OpenResultDto dto = JsonSerializer.Deserialize<OpenResultDto>(lines[1])!;
			Assert.Equal("c2", dto.CaseId);
			Assert.Equal("rare_special", dto.Rarity);
			Assert.Equal(DateTimeOffset.UnixEpoch, dto.Timestamp);

			// A fresh store over the same file feeds its window from the archive.
			var rehydrated = new CaseOpeningStore(path);
			Assert.Equal(2, rehydrated.Count);
			Assert.Equal([1, 2], rehydrated.RecentResults().Select(result => result.PaintSeed));
			Assert.Equal(CaseRarity.RareSpecial, rehydrated.RecentResults()[1].Rarity);

			// Rehydration respects capacity: only the newest lines survive.
			var bounded = new CaseOpeningStore(path, capacity: 1);
			Assert.Equal(1, bounded.Count);
			Assert.Equal(2, bounded.RecentResults()[0].PaintSeed);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Constructor_SkipsMalformedArchiveLines()
	{
		string path = Path.Combine(Path.GetTempPath(), $"vapor-case-results-{Guid.NewGuid():N}.jsonl");
		try
		{
			string good = JsonSerializer.Serialize(CaseOpeningStore.ToDto(Result(seed: 7)));
			File.WriteAllLines(path, [good, "not json", "", "   ", "{}"]);

			var store = new CaseOpeningStore(path);

			Assert.Equal(1, store.Count);
			Assert.Equal(7, store.RecentResults()[0].PaintSeed);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void Constructor_ToleratesUnreadableArchive()
	{
		// A directory "exists" but cannot be read line-by-line: the store starts
		// empty instead of refusing to run.
		string directory = Path.Combine(Path.GetTempPath(), $"vapor-case-results-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);
		try
		{
			var store = new CaseOpeningStore(directory);

			Assert.Equal(0, store.Count);
		}
		finally
		{
			Directory.Delete(directory);
		}
	}

	[Fact]
	public void Record_ToleratesUnwritableArchivePath()
	{
		// The archive path runs through an existing FILE, so directory creation
		// throws on every record — swallowed by design, memory keeps recording.
		string blockingFile = Path.Combine(Path.GetTempPath(), $"vapor-case-block-{Guid.NewGuid():N}");
		File.WriteAllText(blockingFile, "not a directory");
		string path = Path.Combine(blockingFile, "results", "results.jsonl");
		try
		{
			var store = new CaseOpeningStore(path);
			store.Record(Result(seed: 3));

			Assert.Equal(1, store.Count);
			Assert.Equal(3, store.RecentResults()[0].PaintSeed);
		}
		finally
		{
			File.Delete(blockingFile);
		}
	}

	[Fact]
	public void ComputeStats_EmptyStoreListsNoCases()
	{
		CaseOpeningStats stats = new CaseOpeningStore().ComputeStats();

		Assert.Equal(0, stats.TotalOpens);
		Assert.Empty(stats.Cases);
	}

	[Fact]
	public void ComputeStats_AggregatesPerCaseAndTier_WithObservedRates()
	{
		var store = new CaseOpeningStore();
		// c1: 5 milspec + 1 covert out of 6; c2: 1 gold out of 1.
		foreach (int seed in Enumerable.Range(1, 5))
		{
			store.Record(Result(caseId: "c1", rarity: CaseRarity.MilSpec, seed: seed));
		}

		store.Record(Result(caseId: "c1", rarity: CaseRarity.Covert, seed: 6));
		store.Record(Result(caseId: "c2", rarity: CaseRarity.RareSpecial, seed: 7));

		CaseOpeningStats stats = store.ComputeStats();

		Assert.Equal(7, stats.TotalOpens);
		CaseStat first = stats.Cases[0];
		Assert.Equal("c1", first.CaseId);
		Assert.Equal(6, first.Opens);
		Assert.Equal(5, first.Rarities.Single(stat => stat.Rarity == "milspec").Count);
		Assert.Equal(5 / 6.0, first.Rarities.Single(stat => stat.Rarity == "milspec").Rate, precision: 6);
		Assert.Equal(1 / 6.0, first.Rarities.Single(stat => stat.Rarity == "covert").Rate, precision: 6);
		// All five published tiers are always listed, zero-filled when unobserved.
		Assert.Equal(5, first.Rarities.Count);
		Assert.Equal(0, first.Rarities.Single(stat => stat.Rarity == "rare_special").Count);
		Assert.Equal(0, first.Rarities.Single(stat => stat.Rarity == "rare_special").Rate);

		CaseStat second = stats.Cases[1];
		Assert.Equal("c2", second.CaseId);
		Assert.Equal(1, second.Opens);
		Assert.Equal(1.0, second.Rarities.Single(stat => stat.Rarity == "rare_special").Rate);
	}

	[Fact]
	public void ToDto_MapsEveryField_WithCanonicalNames()
	{
		OpenResultDto dto = CaseOpeningStore.ToDto(Result(caseId: "case-x", rarity: CaseRarity.Classified, seed: 9));

		Assert.Equal("case-x", dto.CaseId);
		Assert.Equal("item", dto.ItemId);
		Assert.Equal("Rifle | Test", dto.ItemName);
		Assert.Equal("classified", dto.Rarity);
		Assert.False(dto.StatTrak);
		Assert.Equal(0.2, dto.FloatValue);
		Assert.Equal(WearTier.FieldTested, dto.Wear);
		Assert.Equal(9, dto.PaintSeed);
		Assert.Equal(DateTimeOffset.UnixEpoch, dto.Timestamp);

		Assert.Throws<ArgumentNullException>(() => CaseOpeningStore.ToDto(null!));
	}

	[Fact]
	public void ArchiveRoundTrip_PreservesEveryResultField()
	{
		string path = Path.Combine(Path.GetTempPath(), $"vapor-case-results-{Guid.NewGuid():N}.jsonl");
		try
		{
			var original = new OpenResult(
				"round.case", "item-9", "Knife | Round", CaseRarity.RareSpecial,
				StatTrak: true, FloatValue: 0.123456789, Wear: WearTier.MinimalWear, PaintSeed: 999)
			{
				Timestamp = DateTimeOffset.Parse("2026-09-27T08:30:00+00:00")
			};
			var store = new CaseOpeningStore(path);
			store.Record(original);

			OpenResult restored = new CaseOpeningStore(path).RecentResults().Single();

			Assert.Equal(original, restored);
		}
		finally
		{
			File.Delete(path);
		}
	}
}
