using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapor.Plugins.CaseOpening;

/// <summary>Wire/archive shape of a consumption entry (stable JSON names).</summary>
public sealed record ConsumeEntryDto(
	[property: JsonPropertyName("caseId")] string CaseId,
	[property: JsonPropertyName("cases")] int Cases,
	[property: JsonPropertyName("keys")] int Keys,
	[property: JsonPropertyName("awards")] IReadOnlyList<string> Awards,
	[property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

/// <summary>Per-case aggregate over the ledger.</summary>
public sealed record CaseLedgerCaseTotal(string CaseId, int Cases, int Keys);

/// <summary>Aggregate over every retained consumption entry.</summary>
public sealed record CaseLedgerSummary(
	int TotalEntries,
	int TotalCases,
	int TotalKeys,
	IReadOnlyList<CaseLedgerCaseTotal> Cases);

/// <summary>
/// One recorded consumption: how many cases and keys the operator consumed,
/// plus the awards reported received. The physical opening is a CS2 client
/// transaction that Vapor cannot see, so an entry records what the operator
/// declares — the ledger answers "how many keys are left" and "which cases
/// were opened" by pairing with a live <c>case_inventory</c> read.
/// </summary>
public sealed class ConsumeEntry
{
	public ConsumeEntry(string caseId, int cases, int keys, IReadOnlyList<string>? awards = null, DateTimeOffset? timestamp = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
		ArgumentOutOfRangeException.ThrowIfLessThan(cases, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(keys, 1);

		CaseId = caseId;
		Cases = cases;
		Keys = keys;
		Awards = awards is null || awards.Count == 0 ? [] : [.. awards];
		Timestamp = timestamp ?? DateTimeOffset.UtcNow;
	}

	/// <summary>The catalog case id the consumption was recorded against.</summary>
	public string CaseId { get; }

	/// <summary>Cases consumed by this entry.</summary>
	public int Cases { get; }

	/// <summary>Keys consumed by this entry (one per case in practice).</summary>
	public int Keys { get; }

	/// <summary>Awards the operator reports receiving (may be empty).</summary>
	public IReadOnlyList<string> Awards { get; }

	/// <summary>When the entry was recorded.</summary>
	public DateTimeOffset Timestamp { get; }
}

/// <summary>
/// Consumption ledger: appends every consume as one JSON line to the configured
/// file (durable archive) and keeps the most recent <c>capacity</c> entries in
/// memory for reporting, mirroring <see cref="CaseOpeningStore"/>. Recording
/// never blocks or fails: archive I/O errors are swallowed on purpose — the
/// durable audit trail for orchestration lives in the control plane, and a
/// local ledger must not become an operational dependency.
/// </summary>
public sealed class CaseLedger
{
	/// <summary>Default in-memory retention.</summary>
	public const int DefaultCapacity = 1000;

	private readonly object _gate = new();
	private readonly Queue<ConsumeEntry> _recent;
	private readonly int _capacity;
	private readonly string? _filePath;

	/// <summary>
	/// Creates a ledger appending to <paramref name="filePath"/> (in memory only
	/// when null). An existing file is re-read on startup (well-formed lines
	/// feed the retention window, malformed ones are skipped) so totals survive
	/// restarts within capacity.
	/// </summary>
	public CaseLedger(string? filePath = null, int capacity = DefaultCapacity)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
		_capacity = capacity;
		_recent = new Queue<ConsumeEntry>(capacity);
		_filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;

		// Path.Exists (not File.Exists) so a ledger path that is actually a
		// directory also enters the read attempt below and lands in the same
		// tolerated-failure catch as any other unreadable archive.
		if (_filePath is null || !Path.Exists(_filePath))
		{
			return;
		}

		try
		{
			foreach (ConsumeEntry entry in ReadArchive(_filePath))
			{
				Retain(entry);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Unreadable ledger: start empty rather than refuse to start —
			// recording continues (append may still work) and history is lost
			// only for reporting, never for the plugin itself.
		}
	}

	/// <summary>Records one consumption (memory ring + archive append when configured).</summary>
	public void Record(ConsumeEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		lock (_gate)
		{
			Retain(entry);
		}

		if (_filePath is not null)
		{
			AppendToArchive(_filePath, entry);
		}
	}

	/// <summary>Snapshot of the retained entries, oldest first.</summary>
	public IReadOnlyList<ConsumeEntry> RecentEntries()
	{
		lock (_gate)
		{
			return _recent.ToArray();
		}
	}

	/// <summary>
	/// Aggregates the retained entries: total entries plus total cases and keys
	/// and the per-case split (ordered by case id), which is what answers "how
	/// many keys were used" and "which cases were opened".
	/// </summary>
	public CaseLedgerSummary Summarize()
	{
		lock (_gate)
		{
			Dictionary<string, (int Cases, int Keys)> byCase = [];
			foreach (ConsumeEntry entry in _recent)
			{
				if (!byCase.TryGetValue(entry.CaseId, out (int Cases, int Keys) totals))
				{
					totals = (0, 0);
				}

				byCase[entry.CaseId] = (totals.Cases + entry.Cases, totals.Keys + entry.Keys);
			}

			var cases = byCase
				.OrderBy(entry => entry.Key, StringComparer.Ordinal)
				.Select(entry => new CaseLedgerCaseTotal(entry.Key, entry.Value.Cases, entry.Value.Keys))
				.ToArray();

			return new CaseLedgerSummary(
				_recent.Count,
				_recent.Sum(entry => entry.Cases),
				_recent.Sum(entry => entry.Keys),
				cases);
		}
	}

	private void Retain(ConsumeEntry entry)
	{
		if (_recent.Count == _capacity)
		{
			_recent.Dequeue();
		}

		_recent.Enqueue(entry);
	}

	private static IEnumerable<ConsumeEntry> ReadArchive(string filePath)
	{
		foreach (string line in File.ReadLines(filePath))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			ConsumeEntry? entry = TryParseArchiveLine(line);
			if (entry is not null)
			{
				yield return entry;
			}
		}
	}

	private static ConsumeEntry? TryParseArchiveLine(string line)
	{
		try
		{
			ConsumeEntryDto? dto = JsonSerializer.Deserialize<ConsumeEntryDto>(line);
			if (dto is null
				|| string.IsNullOrWhiteSpace(dto.CaseId)
				|| dto.Cases < 1
				|| dto.Keys < 1)
			{
				return null;
			}

			return new ConsumeEntry(dto.CaseId, dto.Cases, dto.Keys, dto.Awards, dto.Timestamp);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void AppendToArchive(string filePath, ConsumeEntry entry)
	{
		try
		{
			string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.AppendAllText(
				filePath,
				JsonSerializer.Serialize(ToDto(entry)) + Environment.NewLine,
				Encoding.UTF8);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Swallowed by design — see the class doc.
		}
	}

	/// <summary>Maps an entry onto its archive/wire shape.</summary>
	public static ConsumeEntryDto ToDto(ConsumeEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		return new ConsumeEntryDto(entry.CaseId, entry.Cases, entry.Keys, entry.Awards, entry.Timestamp);
	}
}
