using System.Text.Json;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// §39 P-b storage-collect orchestration: fans one <c>loot_inventory</c> task
/// per farm source into a single job (so the existing per-job SSE stream is the
/// progress channel), bounded-waits the run, then chains the sender-side mobile
/// confirmations the same way the single-account loot endpoint does — an
/// unconfirmed offer never reaches the storage partner. Reuses the loot+partner
/// semantics deliberately (the rejected alternatives: a dedicated warehouse
/// table / a separate transfer channel). Pacing is rate-limit aware by
/// construction: the agent-side <c>TradeRateLimiter</c> throttles offer
/// creation per sender, which is the binding Steam constraint — cross-sender
/// concurrency is intentional (each farm account has its own session and its
/// own budget).
/// </summary>
internal static class StorageCollectRunner
{
	/// <summary>Job meta origin marking jobs that belong to collect runs (loot and chained confirmations) — the snapshot scans for it.</summary>
	internal const string CollectOrigin = "storage-collect";

	/// <summary>How deep the snapshot scans the job list (newest first) for collect jobs.</summary>
	internal const int SnapshotJobScan = 200;

	/// <summary>At most this many loot + confirm jobs get their tasks fetched when building a snapshot.</summary>
	internal const int SnapshotJobDetail = 10;

	// internal static so tests can shrink the real-clock windows (same pattern
	// as AccountTaskRunner.WaitWindow).
	internal static TimeSpan WaitWindow = TimeSpan.FromSeconds(150);
	internal static TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// The collect source set: enabled farm accounts that are meant to hold a
	/// live session. An Offline-declared account is deliberately logged out —
	/// there is nothing to loot from it.
	/// </summary>
	internal static IReadOnlyList<AccountSpec> SelectFarmSources(IReadOnlyList<AccountSpec> accounts) =>
		accounts
			.Where(a => a.Role == AccountRole.Farm && a.Enabled && a.DesiredState != AccountDesiredState.Offline)
			.ToList();

	/// <summary>
	/// Creates the collect job (one loot task per farm source, partner = the
	/// storage account's SteamId), waits for every task to settle and chains
	/// mobile confirmations. <see cref="CollectRun.Completed"/> false means the
	/// window closed with tasks still in flight — the caller answers 202 and
	/// the job id keeps the run observable; unfinished loot re-runs on the next
	/// collect because unaccepted offers expire and the items stay in place.
	/// </summary>
	internal static async Task<CollectRun> DispatchCollectAsync(
		IJobStore store,
		AccountSpec storage,
		IReadOnlyList<AccountSpec> farms,
		string? message,
		int[]? appIds,
		CancellationToken cancellationToken)
	{
		string[] targets = farms.Select(f => f.AccountName).ToArray();
		var payload = new Dictionary<string, object?> { ["partner_steam_id"] = storage.SteamId };
		if (!string.IsNullOrWhiteSpace(message))
		{
			payload["message"] = message.Trim();
		}

		if (appIds is { Length: > 0 })
		{
			payload["app_ids"] = appIds;
		}

		JobWithTasks created = await store.CreateJob(new CreateJobRequest(
			AccountTaskRunner.LootInventoryAction,
			Region: null,
			Targets: targets,
			Payload: payload,
			Meta: new Dictionary<string, string> { ["origin"] = CollectOrigin, ["storage"] = storage.AccountName }
		), cancellationToken).ConfigureAwait(false);

		var lootWatches = targets.Select(t => (created.Job.Id, t)).ToList();
		Dictionary<string, JobTask>? lootState = await WaitTerminalAsync(store, lootWatches, cancellationToken).ConfigureAwait(false);
		if (lootState is null)
		{
			return new CollectRun(created.Job.Id, Completed: false, []);
		}

		// Sender-side confirmation chaining: identical interlock to the loot
		// endpoint (an offer that Steam wants confirmed and nobody confirms is
		// an offer the storage account never receives).
		var confirmWatches = new List<(string JobId, string Target)>();
		var confirmJobIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (JobTask task in lootState.Values)
		{
			if (task.Status != JobTaskStatus.Finished)
			{
				continue;
			}

			string? offerId = OutputString(task.Output, "trade_offer_id");
			if (!OutputFlag(task.Output, "requires_mobile_confirmation") || string.IsNullOrEmpty(offerId))
			{
				continue;
			}

			JobWithTasks confirm = await store.CreateJob(new CreateJobRequest(
				AccountTaskRunner.ConfirmTradeOfferAction,
				Region: null,
				Targets: [task.Target],
				Payload: new Dictionary<string, object?> { ["trade_offer_id"] = offerId },
				Meta: new Dictionary<string, string>
				{
					["origin"] = CollectOrigin,
					["storage"] = storage.AccountName,
					["parent_job"] = created.Job.Id
				}
			), cancellationToken).ConfigureAwait(false);

			confirmWatches.Add((confirm.Job.Id, task.Target));
			confirmJobIds[task.Target] = confirm.Job.Id;
		}

		Dictionary<string, JobTask>? confirmState = confirmWatches.Count == 0
			? []
			: await WaitTerminalAsync(store, confirmWatches, cancellationToken).ConfigureAwait(false);

		var results = new List<CollectTaskResult>(targets.Length);
		foreach (string target in targets)
		{
			JobTask task = lootState[target];
			bool? confirmed = null;
			string? confirmJobId = null;
			string? confirmError = null;
			if (task.Status == JobTaskStatus.Finished && confirmState is not null && confirmJobIds.TryGetValue(target, out confirmJobId!))
			{
				JobTask confirmTask = confirmState[target];
				confirmed = confirmTask.Status == JobTaskStatus.Finished && OutputFlag(confirmTask.Output, "confirmed");
				if (confirmTask.Status != JobTaskStatus.Finished)
				{
					confirmError = confirmTask.Error ?? $"confirmation ended as {confirmTask.Status}";
				}
			}

			results.Add(new CollectTaskResult(
				target,
				task.Status,
				task.Error,
				OutputInt(task.Output, "item_count"),
				OutputString(task.Output, "trade_offer_id"),
				confirmed,
				confirmJobId,
				confirmError));
		}

		return new CollectRun(created.Job.Id, Completed: true, results);
	}

	/// <summary>
	/// Polls the watched (job, target) pairs until every task reports a terminal
	/// status. Null return = the window closed with tasks still in flight.
	/// </summary>
	// internal for tests (the ghost-target and window-close arms are not
	// reachable through DispatchCollectAsync), see Vapor.ControlPlane.Tests.
	internal static async Task<Dictionary<string, JobTask>?> WaitTerminalAsync(
		IJobStore store,
		IReadOnlyList<(string JobId, string Target)> watches,
		CancellationToken cancellationToken)
	{
		var state = new Dictionary<string, JobTask>(StringComparer.OrdinalIgnoreCase);
		DateTimeOffset deadline = DateTimeOffset.UtcNow + WaitWindow;
		while (true)
		{
			await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
			state.Clear();
			bool allTerminal = true;
			foreach ((string jobId, string target) in watches)
			{
				JobWithTasks job = await store.GetJob(jobId, cancellationToken).ConfigureAwait(false);
				JobTask? task = job.Tasks.FirstOrDefault(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase));
				if (task is null)
				{
					allTerminal = false;
					continue;
				}

				state[target] = task;
				if (task.Status is not (JobTaskStatus.Finished or JobTaskStatus.Failed or JobTaskStatus.Canceled))
				{
					allTerminal = false;
				}
			}

			if (allTerminal)
			{
				return state;
			}

			if (DateTimeOffset.UtcNow >= deadline)
			{
				return null;
			}
		}
	}

	/// <summary>
	/// Per-account last-collect statistics, rebuilt from recent collect jobs
	/// (no dedicated warehouse table — the jobs are the record). Most recent
	/// loot task per source account wins; the matching confirmation job (same
	/// target + offer id) contributes the confirmed flag.
	/// </summary>
	internal static async Task<IReadOnlyList<CollectSnapshotEntry>> BuildSnapshotAsync(
		IJobStore store,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<Job> jobs = await store.ListJobs(SnapshotJobScan, null, cancellationToken).ConfigureAwait(false);

		var lootJobs = new List<Job>();
		var confirmJobs = new List<Job>();
		foreach (Job job in jobs)
		{
			if (job.Meta is null || !job.Meta.TryGetValue("origin", out string? origin) || origin != CollectOrigin)
			{
				continue;
			}

			if (job.Action == AccountTaskRunner.LootInventoryAction && lootJobs.Count < SnapshotJobDetail)
			{
				lootJobs.Add(job);
			}
			else if (job.Action == AccountTaskRunner.ConfirmTradeOfferAction && confirmJobs.Count < SnapshotJobDetail)
			{
				confirmJobs.Add(job);
			}
		}

		// (job id -> tasks) for every scanned confirmation job: the offer id in
		// its payload links it back to the loot task that requested it.
		var confirmTasks = new List<(string JobId, JobTask Task)>(confirmJobs.Count);
		foreach (Job confirm in confirmJobs)
		{
			JobWithTasks detail = await store.GetJob(confirm.Id, cancellationToken).ConfigureAwait(false);
			foreach (JobTask task in detail.Tasks)
			{
				confirmTasks.Add((detail.Job.Id, task));
			}
		}

		var entries = new List<CollectSnapshotEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Job loot in lootJobs)
		{
			JobWithTasks detail = await store.GetJob(loot.Id, cancellationToken).ConfigureAwait(false);
			string? storageName = loot.Meta!.TryGetValue("storage", out string? storage) ? storage : null;
			foreach (JobTask task in detail.Tasks)
			{
				// ListJobs is newest first, so the first sighting of an account
				// is its most recent collect.
				if (!seen.Add(task.Target))
				{
					continue;
				}

				string? offerId = OutputString(task.Output, "trade_offer_id");
				string? confirmJobId = null;
				string? confirmError = null;
				bool? confirmed = null;
				if (offerId is not null)
				{
					foreach ((string confirmJob, JobTask candidate) in confirmTasks)
					{
						if (!string.Equals(candidate.Target, task.Target, StringComparison.OrdinalIgnoreCase)
							|| PayloadString(candidate.Payload, "trade_offer_id") != offerId)
						{
							continue;
						}

						confirmJobId = confirmJob;
						confirmed = candidate.Status == JobTaskStatus.Finished && OutputFlag(candidate.Output, "confirmed");
						if (candidate.Status != JobTaskStatus.Finished)
						{
							confirmError = candidate.Error ?? $"confirmation ended as {candidate.Status}";
						}

						break;
					}
				}

				entries.Add(new CollectSnapshotEntry(
					task.Target,
					storageName,
					loot.Id,
					task.UpdatedAt,
					task.Status.ToString().ToLowerInvariant(),
					task.Error,
					OutputInt(task.Output, "item_count"),
					offerId,
					confirmed,
					confirmJobId,
					confirmError));
			}
		}

		return entries;
	}

	// The three readers are internal for tests: the store round-trip always
	// hands back JsonElement values, so the in-memory primitive arms (bool,
	// int, long, string) are only reachable by direct invocation.
	// see Vapor.ControlPlane.Tests.
	internal static bool OutputFlag(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
		{
			return false;
		}

		return raw switch
		{
			bool b => b,
			JsonElement { ValueKind: JsonValueKind.True } => true,
			JsonElement { ValueKind: JsonValueKind.String } s => bool.TryParse(s.GetString(), out bool parsed) && parsed,
			_ => false
		};
	}

	internal static string? OutputString(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
		{
			return null;
		}

		return raw switch
		{
			string s => s,
			JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
			_ => raw.ToString()
		};
	}

	private static string? PayloadString(IReadOnlyDictionary<string, object?>? payload, string key) => OutputString(payload, key);

	internal static int? OutputInt(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
		{
			return null;
		}

		return raw switch
		{
			int i => i,
			long l => l <= int.MaxValue ? (int)l : null,
			JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt32(out int parsed) ? parsed : null,
			_ => null
		};
	}
}

/// <summary>Outcome of one bounded collect dispatch. <see cref="Completed"/> false means "still in flight when the window closed".</summary>
internal sealed record CollectRun(
	string JobId,
	bool Completed,
	IReadOnlyList<CollectTaskResult> Tasks);

/// <summary>One farm source's slice of a collect run: loot outcome plus its chained confirmation.</summary>
internal sealed record CollectTaskResult(
	string Account,
	JobTaskStatus Status,
	string? Error,
	int? ItemCount,
	string? TradeOfferId,
	bool? Confirmed,
	string? ConfirmJobId,
	string? ConfirmError);

/// <summary>One source account's most recent collect, as reported by the snapshot endpoint.</summary>
internal sealed record CollectSnapshotEntry(
	string Account,
	string? Storage,
	string JobId,
	DateTimeOffset At,
	string Status,
	string? Error,
	int? ItemCount,
	string? TradeOfferId,
	bool? Confirmed,
	string? ConfirmJobId,
	string? ConfirmError);
