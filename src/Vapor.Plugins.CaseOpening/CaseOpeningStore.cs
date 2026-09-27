using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapor.Plugins.CaseOpening;

/// <summary>Wire/archive shape of an <see cref="OpenResult"/> (stable JSON names).</summary>
public sealed record OpenResultDto(
	[property: JsonPropertyName("caseId")] string CaseId,
	[property: JsonPropertyName("itemId")] string ItemId,
	[property: JsonPropertyName("itemName")] string ItemName,
	[property: JsonPropertyName("rarity")] string Rarity,
	[property: JsonPropertyName("stattrak")] bool StatTrak,
	[property: JsonPropertyName("float")] double FloatValue,
	[property: JsonPropertyName("wear")] string Wear,
	[property: JsonPropertyName("paintSeed")] int PaintSeed,
	[property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);

/// <summary>Aggregate over the retained result window: opens and per-tier rates.</summary>
public sealed record RarityStat(string Rarity, int Count, double Rate);

/// <summary>Per-case aggregate.</summary>
public sealed record CaseStat(string CaseId, int Opens, IReadOnlyList<RarityStat> Rarities);

/// <summary>Catalog-wide aggregate over all recorded (retained) results.</summary>
public sealed record CaseOpeningStats(int TotalOpens, IReadOnlyList<CaseStat> Cases);

/// <summary>
/// Result recorder: appends every open as one JSON line to the configured
/// results file (durable archive) and keeps the most recent
/// <c>capacity</c> results in memory for reporting. When no path is configured
/// the archive is disabled and only the in-memory ring is kept. Recording
/// never blocks or fails an open: archive I/O errors are swallowed on purpose
/// (the durable audit trail for orchestration lives in the control plane; a
/// simulator's result file must not become an operational dependency).
/// </summary>
public sealed class CaseOpeningStore
{
	/// <summary>Default in-memory retention.</summary>
	public const int DefaultCapacity = 1000;

	private readonly object _gate = new();
	private readonly Queue<OpenResult> _recent;
	private readonly int _capacity;
	private readonly string? _filePath;

	/// <summary>Creates an in-memory-only store.</summary>
	public CaseOpeningStore()
		: this(filePath: null)
	{
	}

	/// <summary>
	/// Creates a store appending to <paramref name="filePath"/>. An existing
	/// file is re-read on startup (well-formed lines feed the retention window,
	/// malformed ones are skipped) so stats survive restarts within capacity.
	/// </summary>
	public CaseOpeningStore(string? filePath, int capacity = DefaultCapacity)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
		_capacity = capacity;
		_recent = new Queue<OpenResult>(capacity);
		_filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;

		// Path.Exists (not File.Exists) so an archive path that is actually a
		// directory also enters the read attempt below and lands in the same
		// tolerated-failure catch as any other unreadable archive.
		if (_filePath is null || !Path.Exists(_filePath))
		{
			return;
		}

		try
		{
			foreach (OpenResult result in ReadArchive(_filePath))
			{
				Retain(result);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Unreadable archive: start empty rather than refuse to open —
			// recording continues (append may still work) and history is lost
			// only for reporting, never for the sim itself.
		}
	}

	/// <summary>Records one open (memory ring + archive append when configured).</summary>
	public void Record(OpenResult result)
	{
		ArgumentNullException.ThrowIfNull(result);

		lock (_gate)
		{
			Retain(result);
		}

		if (_filePath is not null)
		{
			AppendToArchive(_filePath, result);
		}
	}

	/// <summary>Snapshot of the retained results, oldest first.</summary>
	public IReadOnlyList<OpenResult> RecentResults()
	{
		lock (_gate)
		{
			return _recent.ToArray();
		}
	}

	/// <summary>Retained count (bounded by capacity).</summary>
	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _recent.Count;
			}
		}
	}

	/// <summary>
	/// Aggregates the retained results: total opens plus per-case, per-tier
	/// counts and observed rates (rate is 0 for a case with no opens). All five
	/// published tiers are always listed so consumers never special-case a
	/// missing row.
	/// </summary>
	public CaseOpeningStats ComputeStats()
	{
		lock (_gate)
		{
			Dictionary<string, Dictionary<CaseRarity, int>> byCase = [];
			foreach (OpenResult result in _recent)
			{
				if (!byCase.TryGetValue(result.CaseId, out Dictionary<CaseRarity, int>? tiers))
				{
					tiers = [];
					byCase[result.CaseId] = tiers;
				}

				tiers[result.Rarity] = tiers.TryGetValue(result.Rarity, out int current) ? current + 1 : 1;
			}

			var cases = new List<CaseStat>(byCase.Count);
			foreach ((string caseId, Dictionary<CaseRarity, int> tiers) in byCase.OrderBy(entry => entry.Key, StringComparer.Ordinal))
			{
				int opens = tiers.Values.Sum();
				// Entries only exist for cases with at least one recorded open, so
				// opens >= 1 and the missing-tier lookup falls back to 0 below.
				var rarities = Enum.GetValues<CaseRarity>()
					.Select(tier =>
					{
						tiers.TryGetValue(tier, out int count);
						return new RarityStat(
							CaseDefinition.RarityName(tier),
							count,
							Math.Round(count / (double)opens, 6));
					})
					.ToArray();
				cases.Add(new CaseStat(caseId, opens, rarities));
			}

			return new CaseOpeningStats(_recent.Count, cases);
		}
	}

	private void Retain(OpenResult result)
	{
		if (_recent.Count == _capacity)
		{
			_recent.Dequeue();
		}

		_recent.Enqueue(result);
	}

	private static IEnumerable<OpenResult> ReadArchive(string filePath)
	{
		foreach (string line in File.ReadLines(filePath))
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			OpenResult? result = TryParseArchiveLine(line);
			if (result is not null)
			{
				yield return result;
			}
		}
	}

	private static OpenResult? TryParseArchiveLine(string line)
	{
		try
		{
			OpenResultDto? dto = JsonSerializer.Deserialize<OpenResultDto>(line);
			if (dto is null
				|| string.IsNullOrWhiteSpace(dto.CaseId)
				|| string.IsNullOrWhiteSpace(dto.ItemId)
				|| !CaseDefinition.TryParseRarity(dto.Rarity, out CaseRarity rarity))
			{
				return null;
			}

			return new OpenResult(dto.CaseId, dto.ItemId, dto.ItemName, rarity, dto.StatTrak, dto.FloatValue, dto.Wear, dto.PaintSeed)
			{
				Timestamp = dto.Timestamp
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void AppendToArchive(string filePath, OpenResult result)
	{
		try
		{
			string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			OpenResultDto dto = ToDto(result);
			File.AppendAllText(
				filePath,
				JsonSerializer.Serialize(dto) + Environment.NewLine,
				Encoding.UTF8);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Swallowed by design — see the class doc.
		}
	}

	/// <summary>Maps a result onto its archive/wire shape.</summary>
	public static OpenResultDto ToDto(OpenResult result)
	{
		ArgumentNullException.ThrowIfNull(result);

		return new OpenResultDto(
			result.CaseId,
			result.ItemId,
			result.ItemName,
			CaseDefinition.RarityName(result.Rarity),
			result.StatTrak,
			result.FloatValue,
			result.Wear,
			result.PaintSeed,
			result.Timestamp);
	}
}
