using System.Text.Json;

using Vapor.Plugins.Csgo;

using Xunit;

namespace Vapor.Plugins.Csgo.Tests;

public class CaseLedgerTests : IDisposable
{
	private readonly string _root;

	public CaseLedgerTests()
	{
		_root = Path.Combine(Path.GetTempPath(), $"vapor-case-ledger-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private string WriteArchive(params string[] lines)
	{
		string path = Path.Combine(_root, $"archive-{Guid.NewGuid():N}.jsonl");
		File.WriteAllLines(path, lines);
		return path;
	}

	private static ConsumeEntry Entry(string caseId = "vapor.cases.starter", int cases = 2, int keys = 3, params string[] awards) =>
		new(caseId, cases, keys, awards.Length == 0 ? null : awards);

	[Fact]
	public void Record_Then_Summarize_AggregatesPerCase()
	{
		var ledger = new CaseLedger();
		ledger.Record(Entry(cases: 2, keys: 2));
		ledger.Record(Entry("vapor.cases.golden", cases: 1, keys: 1));
		ledger.Record(Entry(cases: 3, keys: 4, awards: ["AK-47 | Redline"]));

		CaseLedgerSummary summary = ledger.Summarize();

		Assert.Equal(3, summary.TotalEntries);
		Assert.Equal(6, summary.TotalCases);
		Assert.Equal(7, summary.TotalKeys);
		Assert.Equal(2, summary.Cases.Count);

		// Per-case totals are ordered by case id.
		CaseLedgerCaseTotal golden = summary.Cases[0];
		Assert.Equal("vapor.cases.golden", golden.CaseId);
		Assert.Equal(1, golden.Cases);
		Assert.Equal(1, golden.Keys);

		CaseLedgerCaseTotal starter = summary.Cases[1];
		Assert.Equal("vapor.cases.starter", starter.CaseId);
		Assert.Equal(5, starter.Cases);
		Assert.Equal(6, starter.Keys);
	}

	[Fact]
	public void Summarize_EmptyLedger_ReportsZeroes()
	{
		CaseLedgerSummary summary = new CaseLedger().Summarize();

		Assert.Equal(0, summary.TotalEntries);
		Assert.Equal(0, summary.TotalCases);
		Assert.Equal(0, summary.TotalKeys);
		Assert.Empty(summary.Cases);
	}

	[Fact]
	public void RecentEntries_KeepsOldestFirst_WithinCapacity()
	{
		var ledger = new CaseLedger(capacity: 3);
		ledger.Record(Entry(cases: 1));
		ledger.Record(Entry(cases: 2));
		ledger.Record(Entry(cases: 3));
		ledger.Record(Entry(cases: 4)); // evicts the first

		IReadOnlyList<ConsumeEntry> recent = ledger.RecentEntries();

		Assert.Equal(3, recent.Count);
		Assert.Equal(2, recent[0].Cases);
		Assert.Equal(3, recent[1].Cases);
		Assert.Equal(4, recent[2].Cases);
	}

	[Fact]
	public void Constructor_RejectsNonPositiveCapacity()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new CaseLedger(capacity: 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new CaseLedger(capacity: -1));
	}

	[Fact]
	public void Ledger_PersistsEntriesAcrossRestart()
	{
		string path = Path.Combine(_root, "ledger.jsonl");
		var first = new CaseLedger(path);
		first.Record(Entry(cases: 2, keys: 2));
		first.Record(Entry("vapor.cases.golden", cases: 1, keys: 1));

		var second = new CaseLedger(path);
		CaseLedgerSummary summary = second.Summarize();

		Assert.Equal(2, summary.TotalEntries);
		Assert.Equal(3, summary.TotalCases);
		Assert.Equal(3, summary.TotalKeys);
		Assert.Equal(2, File.ReadLines(path).Count());
	}

	[Fact]
	public void Constructor_SkipsMalformedArchiveLines()
	{
		string path = WriteArchive(
			"not json",
			"{}",
			"""{"caseId":"vapor.cases.starter","cases":0,"keys":1,"awards":[],"timestamp":"2026-01-01T00:00:00+00:00"}""",
			"""{"caseId":"vapor.cases.starter","cases":1,"keys":0,"awards":[],"timestamp":"2026-01-01T12:00:00+00:00"}""",
			"""{"caseId":"vapor.cases.starter","cases":1,"keys":1,"awards":["AK"],"timestamp":"2026-01-02T00:00:00+00:00"}""",
			"   ");

		var ledger = new CaseLedger(path);
		CaseLedgerSummary summary = ledger.Summarize();

		// Only the well-formed, valid line survives
		Assert.Equal(1, summary.TotalEntries);
		Assert.Equal(1, summary.TotalCases);
		Assert.Equal(1, summary.TotalKeys);
		Assert.Equal(["AK"], ledger.RecentEntries().Single().Awards);
	}

	[Fact]
	public void Constructor_ToleratesUnreadableArchive()
	{
		// A directory "exists" but cannot be read line-by-line: the ledger
		// starts empty instead of refusing to record.
		string directory = Path.Combine(Path.GetTempPath(), $"vapor-case-ledger-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);

		var ledger = new CaseLedger(directory);
		ledger.Record(Entry());

		Assert.Single(ledger.RecentEntries());
		Assert.Equal(1, ledger.Summarize().TotalEntries);
	}

	[Fact]
	public void Constructor_MissingOrBlankPath_MeansInMemoryOnly()
	{
		Assert.Empty(new CaseLedger(null!).RecentEntries());
		Assert.Empty(new CaseLedger("").RecentEntries());
		Assert.Empty(new CaseLedger("   ").RecentEntries());
		Assert.Empty(new CaseLedger(Path.Combine(_root, "missing.jsonl")).RecentEntries());
	}

	[Fact]
	public void Record_AppendsOneJsonLine_PerEntry()
	{
		string path = Path.Combine(_root, "appends.jsonl");
		var ledger = new CaseLedger(path);
		ledger.Record(Entry(cases: 2, keys: 2, awards: ["AK-47 | Redline"]));

		string[] lines = File.ReadLines(path).ToArray();
		Assert.Single(lines);
		ConsumeEntryDto dto = JsonSerializer.Deserialize<ConsumeEntryDto>(lines[0])!;
		Assert.Equal("vapor.cases.starter", dto.CaseId);
		Assert.Equal(2, dto.Cases);
		Assert.Equal(2, dto.Keys);
		Assert.Equal(["AK-47 | Redline"], dto.Awards);
		Assert.True(dto.Timestamp > default(DateTimeOffset));
	}

	[Fact]
	public void Record_ToleratesUnwritableArchive()
	{
		// A directory path cannot be appended: the entry is still retained in memory.
		string directory = Path.Combine(Path.GetTempPath(), $"vapor-case-ledger-{Guid.NewGuid():N}");
		Directory.CreateDirectory(directory);

		var ledger = new CaseLedger(directory);
		ledger.Record(Entry());

		Assert.Single(ledger.RecentEntries());
	}

	[Fact]
	public void Record_RejectsNull()
	{
		Assert.Throws<ArgumentNullException>(() => new CaseLedger().Record(null!));
	}

	[Fact]
	public void ConsumeEntry_ValidatesInputs()
	{
		Assert.Throws<ArgumentException>(() => new ConsumeEntry("", 1, 1));
		Assert.Throws<ArgumentNullException>(() => new ConsumeEntry(null!, 1, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => new ConsumeEntry("c", 0, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => new ConsumeEntry("c", 1, 0));
	}

	[Fact]
	public void ConsumeEntry_AwardsDefaultEmpty_AndCopy()
	{
		ConsumeEntry noAwards = new("c", 1, 1);
		Assert.Empty(noAwards.Awards);

		string[] awards = ["One", "Two"];
		ConsumeEntry withAwards = new("c", 1, 1, awards);
		Assert.Equal(["One", "Two"], withAwards.Awards);

		awards[0] = "mutated";
		Assert.Equal("One", withAwards.Awards[0]);
	}

	[Fact]
	public void ConsumeEntry_TimestampDefaultsToUtcNow()
	{
		DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
		ConsumeEntry entry = new("c", 1, 1);
		DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(1);

		Assert.InRange(entry.Timestamp, before, after);

		DateTimeOffset explicitTime = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);
		Assert.Equal(explicitTime, new ConsumeEntry("c", 1, 1, timestamp: explicitTime).Timestamp);
	}

	[Fact]
	public void ToDto_MapsAllFields_AndRejectsNull()
	{
		DateTimeOffset time = new(2026, 5, 4, 3, 2, 1, TimeSpan.Zero);
		ConsumeEntry entry = new("c", 7, 8, ["X"], time);

		ConsumeEntryDto dto = CaseLedger.ToDto(entry);
		Assert.Equal("c", dto.CaseId);
		Assert.Equal(7, dto.Cases);
		Assert.Equal(8, dto.Keys);
		Assert.Equal(["X"], dto.Awards);
		Assert.Equal(time, dto.Timestamp);

		Assert.Throws<ArgumentNullException>(() => CaseLedger.ToDto(null!));
	}

	[Fact]
	public void Summarize_OrdersPerCaseTotals_Ordinally()
	{
		var ledger = new CaseLedger();
		ledger.Record(Entry("vapor.cases.golden"));
		ledger.Record(Entry("vapor.cases.starter"));
		ledger.Record(Entry("custom.case"));

		Assert.Equal(
			["custom.case", "vapor.cases.golden", "vapor.cases.starter"],
			ledger.Summarize().Cases.Select(total => total.CaseId).ToArray());
	}
}
