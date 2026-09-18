namespace Vapor.Steam.Core.Steam;

/// <summary>
/// Bitmap math for achievement bits inside a game's stats blob. Achievements
/// are numbered 0..N-1 in schema order; bit <c>i</c> lives in the stats entry
/// <c>stat_id = i &gt;&gt; 5</c> at bit position <c>i &amp; 31</c>. This encoding
/// is community-established reverse engineering of the client protocol, not an
/// official contract — the write path guards it with a schema-count check and a
/// post-store read-back instead of trusting it blind.
/// </summary>
public static class AchievementStatsBitmap
{
	/// <summary>The stats entry id that carries the bit for achievement <paramref name="achievementId"/>.</summary>
	public static uint StatIdFor(uint achievementId) => achievementId >> 5;

	/// <summary>The bit mask for achievement <paramref name="achievementId"/> within its stats entry.</summary>
	public static uint BitFor(uint achievementId) => 1u << (int)(achievementId & 31);

	/// <summary>Whether achievement <paramref name="achievementId"/> is set in the given stats entries.</summary>
	public static bool IsSet(IReadOnlyList<UserStatsEntry> stats, uint achievementId)
	{
		uint statId = StatIdFor(achievementId);
		uint bit = BitFor(achievementId);
		foreach (var entry in stats)
		{
			if (entry.StatId == statId)
			{
				return (entry.StatValue & bit) != 0;
			}
		}

		return false; // absent entries are zero
	}

	/// <summary>
	/// Returns the stats entries with the given achievements' bits set or cleared.
	/// Entries are merged onto the input blob (absent entries count as zero) so
	/// untouched stats values survive the round trip — the store path submits the
	/// merged full blob, not a delta.
	/// </summary>
	public static List<UserStatsEntry> Apply(
		IReadOnlyList<UserStatsEntry> stats,
		IReadOnlyCollection<uint> achievementIds,
		bool unlock)
	{
		var merged = new Dictionary<uint, uint>();
		foreach (var entry in stats)
		{
			merged[entry.StatId] = entry.StatValue;
		}

		foreach (uint achievementId in achievementIds)
		{
			uint statId = StatIdFor(achievementId);
			merged.TryGetValue(statId, out uint value);
			merged[statId] = unlock ? value | BitFor(achievementId) : value & ~BitFor(achievementId);
		}

		return merged
			.Select(kv => new UserStatsEntry(kv.Key, kv.Value))
			.OrderBy(e => e.StatId)
			.ToList();
	}
}
