using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// One dispatchable crawl unit: a named account (already filtered to enabled
/// accounts by the worker) plus the shard of apps its task should fetch.
/// </summary>
internal sealed record CrawlAssignment(string Account, string? Region, IReadOnlyList<uint> AppIds);

/// <summary>
/// Deterministic output of <see cref="CrawlShardPlanner.Build"/>: the sharded
/// assignments plus warnings for apps dropped by validation (an override naming
/// an account outside the pool, say) — the worker logs them.
/// </summary>
internal sealed record CrawlShardPlan(
	IReadOnlyList<CrawlAssignment> Assignments,
	IReadOnlyList<string> Warnings);

/// <summary>
/// Pure sharding for crawl plans: spreads apps across the account pool
/// round-robin so every account carries a fair share, lets explicit
/// app→account overrides win over the rotation, and splits each account's
/// bucket into task-sized chunks (one <c>get_game_info_batch</c> task each).
/// Deterministic on (appIds order, pool order, overrides) — asserted in tests.
/// </summary>
internal static class CrawlShardPlanner
{
	/// <summary>Matches <see cref="Vapor.Steam.Core.Actions.GetGameInfoBatchAction.MaxAppsPerBatch"/>.</summary>
	internal const int MaxShardApps = 200;

	internal static CrawlShardPlan Build(
		IReadOnlyList<uint> appIds,
		IReadOnlyList<AccountSpec> poolAccounts,
		IReadOnlyDictionary<uint, string>? overrides,
		int shardSize)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(shardSize);
		int effectiveShard = Math.Clamp(shardSize, 1, MaxShardApps);
		var warnings = new List<string>();

		// Pool lookup is case-insensitive, matching AccountStore's key semantics.
		var poolByName = new Dictionary<string, AccountSpec>(StringComparer.OrdinalIgnoreCase);
		foreach (var spec in poolAccounts)
		{
			poolByName[spec.AccountName] = spec;
		}

		// Bucket per pool account, preserving the plan's app order within each.
		var buckets = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

		if (poolAccounts.Count == 0)
		{
			// Overrides without a pool have nowhere to go either.
			if (overrides is { Count: > 0 })
			{
				warnings.Add("no accounts in the crawl pool: all overrides dropped");
			}

			return new CrawlShardPlan([], warnings);
		}

		if (overrides != null)
		{
			foreach (var (appId, account) in overrides)
			{
				if (!poolByName.ContainsKey(account))
				{
					warnings.Add($"override for app {appId} names '{account}', which is not in the crawl pool — app dropped");
				}
			}
		}

		for (int i = 0; i < appIds.Count; i++)
		{
			uint appId = appIds[i];

			// Explicit mapping wins over the rotation; anything pointing outside
			// the pool was already reported above and falls through to rotation
			// rather than losing the app entirely.
			string owner = overrides?.TryGetValue(appId, out string? named) == true && poolByName.ContainsKey(named)
				? named
				: poolAccounts[i % poolAccounts.Count].AccountName;

			if (!buckets.TryGetValue(owner, out var bucket))
			{
				bucket = new List<uint>();
				buckets[owner] = bucket;
			}

			bucket.Add(appId);
		}

		// Stable output: pool order, one or more chunks per account.
		var assignments = new List<CrawlAssignment>();
		foreach (var spec in poolAccounts)
		{
			if (!buckets.TryGetValue(spec.AccountName, out var bucket))
			{
				continue;
			}

			foreach (var chunk in Chunk(bucket, effectiveShard))
			{
				assignments.Add(new CrawlAssignment(spec.AccountName, spec.Region, chunk));
			}
		}

		return new CrawlShardPlan(assignments, warnings);
	}

	/// <summary>Splits a bucket into consecutive task-sized shards.</summary>
	internal static IReadOnlyList<IReadOnlyList<uint>> Chunk(IReadOnlyList<uint> bucket, int shardSize)
	{
		int effectiveShard = Math.Clamp(shardSize, 1, MaxShardApps);
		var chunks = new List<IReadOnlyList<uint>>();
		for (int offset = 0; offset < bucket.Count; offset += effectiveShard)
		{
			chunks.Add(bucket.Skip(offset).Take(effectiveShard).ToList());
		}

		return chunks;
	}
}
