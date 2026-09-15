using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Orchestrates crawl plans: claims due plans from <see cref="SqliteCrawlStore"/>,
/// shards their apps across the enabled account pool (<see cref="CrawlShardPlanner"/>),
/// dispatches one <c>get_game_info_batch</c> job per shard through the regular
/// job queue, and persists every per-app outcome back into the store.
///
/// Guarantees and limits:
/// - One run per plan at a time; a tick that claims an in-flight plan only
///   bumps the overlap counter (no duplicate dispatch, no event spam).
/// - A run hard-stops after <see cref="Config.CrawlRunTimeoutSeconds"/>:
///   outstanding jobs are canceled and their shards recorded as failures.
/// - One-shot plans (no cron, no interval) clear their due cursor on
///   completion; recurring plans advance it through <see cref="ScheduleClock"/>.
/// - Crawl decisions are audited with the actor "orchestrator".
/// - Store fetches stay anonymous (public appdetails endpoint); accounts are
///   only the dispatch identity — payloads never carry credentials.
/// </summary>
public sealed class CrawlRunWorker : BackgroundService
{
	private const string CrawlAction = "get_game_info_batch";

	private sealed record DispatchedJob(string Account, IReadOnlyList<uint> AppIds);

	private sealed class InFlightRun
	{
		public required string RunId { get; init; }
		public required DateTimeOffset Deadline { get; init; }
		public Dictionary<string, DispatchedJob> Jobs { get; } = new(StringComparer.Ordinal);
		/// <summary>Set when the pool resolved empty (run completes as "skipped").</summary>
		public bool EmptyPool { get; set; }
		/// <summary>Set when the run deadline lapsed with jobs still outstanding.</summary>
		public bool TimedOut { get; set; }
	}

	private readonly SqliteCrawlStore _crawl;
	private readonly AccountStore _accounts;
	private readonly IJobStore _jobs;
	private readonly IEventBroker _events;
	private readonly IAuditStore _audit;
	private readonly Config _cfg;
	private readonly ILogger<CrawlRunWorker> _logger;

	private readonly ConcurrentDictionary<string, InFlightRun> _inflight = new(StringComparer.Ordinal);

	private long _runsTriggered;
	private long _runsCompleted;
	private long _runsFailed;
	private long _runsTimedOut;
	private long _tasksDispatched;
	private long _appsSucceeded;
	private long _appsFailed;
	private long _overlapSkips;

	/// <summary>Crawl runs whose jobs were dispatched since startup.</summary>
	public long RunsTriggered => Interlocked.Read(ref _runsTriggered);
	/// <summary>Crawl runs that settled (all jobs terminal, outcome persisted).</summary>
	public long RunsCompleted => Interlocked.Read(ref _runsCompleted);
	/// <summary>Runs aborted while dispatching (job-store failures).</summary>
	public long RunsFailed => Interlocked.Read(ref _runsFailed);
	/// <summary>Runs force-finished past the run timeout.</summary>
	public long RunsTimedOut => Interlocked.Read(ref _runsTimedOut);
	/// <summary>get_game_info_batch jobs dispatched for crawl runs.</summary>
	public long TasksDispatched => Interlocked.Read(ref _tasksDispatched);
	/// <summary>Per-app successes persisted into crawl_results.</summary>
	public long AppsSucceeded => Interlocked.Read(ref _appsSucceeded);
	/// <summary>Per-app failures persisted into crawl_results.</summary>
	public long AppsFailed => Interlocked.Read(ref _appsFailed);
	/// <summary>Ticks skipped because the plan's previous run was still in flight.</summary>
	public long OverlapSkips => Interlocked.Read(ref _overlapSkips);

	public CrawlRunWorker(
		SqliteCrawlStore crawl,
		AccountStore accounts,
		IJobStore jobs,
		IEventBroker events,
		IAuditStore audit,
		Config cfg,
		ILogger<CrawlRunWorker> logger)
	{
		_crawl = crawl;
		_accounts = accounts;
		_jobs = jobs;
		_events = events;
		_audit = audit;
		_cfg = cfg;
		_logger = logger;
	}

	/// <summary>Tick size; tests shrink it to drive the loop without real waits.</summary>
	internal TimeSpan TickInterval => TimeSpan.FromSeconds(Math.Max(_cfg.CrawlWorkerTickSeconds, 1));

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// 0 or below is the documented kill switch.
		if (_cfg.CrawlWorkerTickSeconds <= 0)
		{
			_logger.LogInformation("Crawl run worker disabled (tick <= 0)");
			return;
		}

		using PeriodicTimer timer = new(TickInterval);
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
		{
			try
			{
				await RunTickAsync(stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				// A broken tick must never kill the service.
				_logger.LogError(ex, "Crawl run tick failed");
			}
		}
	}

	internal async Task RunTickAsync(CancellationToken cancellationToken)
	{
		await PollInFlightRunsAsync(cancellationToken).ConfigureAwait(false);
		await TryTriggerDuePlanAsync(cancellationToken).ConfigureAwait(false);
	}

	// --- polling: persist outcomes for jobs that reached a terminal state ---

	private async Task PollInFlightRunsAsync(CancellationToken cancellationToken)
	{
		foreach ((string planId, InFlightRun run) in _inflight)
		{
			foreach ((string jobId, DispatchedJob dispatched) in run.Jobs.ToArray())
			{
				JobTask? terminalTask = await ReadTerminalTaskAsync(jobId, cancellationToken).ConfigureAwait(false);
				if (terminalTask != null)
				{
					await PersistShardOutcomeAsync(planId, run.RunId, dispatched, terminalTask, cancellationToken).ConfigureAwait(false);
					run.Jobs.Remove(jobId);
				}
				else if (DateTimeOffset.UtcNow >= run.Deadline)
				{
					run.TimedOut = true;
					Interlocked.Increment(ref _runsTimedOut);
					await CancelAndRecordTimeoutAsync(planId, run.RunId, jobId, dispatched, cancellationToken).ConfigureAwait(false);
					run.Jobs.Remove(jobId);
				}
			}

			if (run.Jobs.Count == 0)
			{
				_inflight.TryRemove(planId, out _);
				await CompleteRunAsync(planId, run, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Returns the single task when the job reached a terminal state, null while queued/running (or unreadable).</summary>
	private async Task<JobTask?> ReadTerminalTaskAsync(string jobId, CancellationToken cancellationToken)
	{
		JobWithTasks jobWithTasks;
		try
		{
			jobWithTasks = await _jobs.GetJob(jobId, cancellationToken).ConfigureAwait(false);
		}
		catch (NotFoundException)
		{
			// The job vanished (store reset) — treat as a terminal failure below.
			return MakeFailedTask(jobId, "job not found");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to read crawl job {JobId}", jobId);
			return null;
		}

		if (jobWithTasks.Job.Status is not (JobStatus.Finished or JobStatus.Failed or JobStatus.Canceled))
		{
			return null;
		}

		return jobWithTasks.Tasks.Count > 0
			? jobWithTasks.Tasks[0]
			: MakeFailedTask(jobId, "job has no tasks");
	}

	private static JobTask MakeFailedTask(string jobId, string error) => new(
		Id: jobId,
		JobId: jobId,
		Target: "",
		Action: CrawlAction,
		Region: null,
		Payload: null,
		Status: JobTaskStatus.Failed,
		Attempt: 1,
		CreatedAt: DateTimeOffset.UtcNow,
		UpdatedAt: DateTimeOffset.UtcNow,
		Error: error);

	private async Task PersistShardOutcomeAsync(
		string planId,
		string runId,
		DispatchedJob dispatched,
		JobTask task,
		CancellationToken cancellationToken)
	{
		long fetchedAt = task.UpdatedAt.ToUnixTimeMilliseconds();

		if (task.Status is JobTaskStatus.Finished && task.Output != null)
		{
			// Per-app rows from the batch action's output (games / errors), so a
			// partially successful shard still records every app individually.
			foreach (JsonElement game in EnumerateItems(task.Output, "games"))
			{
				uint appId = GetUInt(game, "app_id");
				if (appId == 0)
				{
					continue;
				}

				await _crawl.AddResultAsync(new CrawlResultRow(
					0, planId, runId, appId, dispatched.Account, task.Id,
					Ok: true, Error: null, Data: game, FetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(fetchedAt)),
					cancellationToken).ConfigureAwait(false);
				Interlocked.Increment(ref _appsSucceeded);
			}

			foreach (JsonElement errorItem in EnumerateItems(task.Output, "errors"))
			{
				uint appId = GetUInt(errorItem, "app_id");
				if (appId == 0)
				{
					continue;
				}

				await _crawl.AddResultAsync(new CrawlResultRow(
					0, planId, runId, appId, dispatched.Account, task.Id,
					Ok: false, Error: GetString(errorItem, "error"), Data: null, FetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(fetchedAt)),
					cancellationToken).ConfigureAwait(false);
				Interlocked.Increment(ref _appsFailed);
			}

			return;
		}

		// Whole shard failed (task error, cancel...) — record every dispatched app as failed.
		string error = task.Error ?? $"crawl task ended with status {task.Status}";
		await RecordShardAppsFailureAsync(planId, runId, dispatched, task.Id, error, fetchedAt, cancellationToken).ConfigureAwait(false);
	}

	private async Task CancelAndRecordTimeoutAsync(
		string planId,
		string runId,
		string jobId,
		DispatchedJob dispatched,
		CancellationToken cancellationToken)
	{
		try
		{
			await _jobs.CancelJob(jobId, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to cancel timed-out crawl job {JobId}", jobId);
		}

		await RecordShardAppsFailureAsync(
			planId, runId, dispatched, jobId, "crawl run timed out",
			DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
	}

	private async Task RecordShardAppsFailureAsync(
		string planId,
		string runId,
		DispatchedJob dispatched,
		string jobId,
		string error,
		long fetchedAtMs,
		CancellationToken cancellationToken)
	{
		foreach (uint appId in dispatched.AppIds)
		{
			await _crawl.AddResultAsync(new CrawlResultRow(
				0, planId, runId, appId, dispatched.Account, jobId,
				Ok: false, Error: error, Data: null, FetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs)),
				cancellationToken).ConfigureAwait(false);
			Interlocked.Increment(ref _appsFailed);
		}
	}

	// --- triggering: claim the next due plan and dispatch its shards ---

	private async Task TryTriggerDuePlanAsync(CancellationToken cancellationToken)
	{
		string runId = Id.New();
		CrawlPlan? plan = await _crawl.ClaimDuePlanAsync(runId, cancellationToken).ConfigureAwait(false);
		if (plan == null)
		{
			return;
		}

		if (_inflight.ContainsKey(plan.Id))
		{
			// Same-plan overlap: bookkeeping only — a periodic plan with a long
			// run would otherwise spam skip events on every tick.
			Interlocked.Increment(ref _overlapSkips);
			_logger.LogDebug("Crawl plan {PlanId} still in flight; tick skipped", plan.Id);
			return;
		}

		Interlocked.Increment(ref _runsTriggered);
		var run = new InFlightRun
		{
			RunId = runId,
			Deadline = DateTimeOffset.UtcNow.AddSeconds(_cfg.CrawlRunTimeoutSeconds)
		};

		try
		{
			var pool = ResolvePool(plan);
			if (pool.Count == 0)
			{
				_logger.LogWarning("Crawl plan {PlanId} has no enabled accounts in its pool; run {RunId} skipped", plan.Id, runId);
				run.EmptyPool = true;
				_inflight[plan.Id] = run; // polled next tick → completes as skipped
				return;
			}

			var shardPlan = CrawlShardPlanner.Build(plan.AppIds, pool, plan.Overrides, Math.Min(plan.ShardSize, _cfg.CrawlMaxAppsPerTask));
			foreach (string warning in shardPlan.Warnings)
			{
				_logger.LogWarning("Crawl plan {PlanId}: {Warning}", plan.Id, warning);
			}

			foreach (CrawlAssignment assignment in shardPlan.Assignments)
			{
				JobWithTasks created = await _jobs.CreateJob(new CreateJobRequest(
					CrawlAction,
					assignment.Region,
					[assignment.Account],
					new Dictionary<string, object?>
					{
						["app_ids"] = string.Join(",", assignment.AppIds),
						["cc"] = plan.Cc,
						["interval_ms"] = plan.IntervalMs
					},
					new Dictionary<string, string>
					{
						["origin"] = "crawl",
						["crawl_plan_id"] = plan.Id,
						["crawl_run_id"] = runId
					}), cancellationToken).ConfigureAwait(false);

				run.Jobs[created.Job.Id] = new DispatchedJob(assignment.Account, assignment.AppIds);
				Interlocked.Increment(ref _tasksDispatched);
			}

			_inflight[plan.Id] = run;
			var payload = new Dictionary<string, object?>
			{
				["plan_id"] = plan.Id,
				["run_id"] = runId,
				["jobs"] = run.Jobs.Count,
				["accounts"] = run.Jobs.Values.Select(j => j.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
				["apps"] = run.Jobs.Values.Sum(j => (long)j.AppIds.Count)
			};
			_events.Publish(null, "crawl.run_triggered", payload);
			await RecordAuditAsync("crawl.run_triggered", plan.Id, runId, payload, cancellationToken).ConfigureAwait(false);
			_logger.LogInformation("Crawl plan {PlanId} run {RunId}: dispatched {Jobs} jobs over {Apps} apps",
				plan.Id, runId, run.Jobs.Count, payload["apps"]);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			if (run.Jobs.Count > 0)
			{
				// Some shards were dispatched before the failure — keep the run
				// in flight so the polling path settles them and completes it.
				_inflight[plan.Id] = run;
				_logger.LogError(ex, "Crawl plan {PlanId}: dispatch failed after {Jobs} jobs; remaining shards settle by polling", plan.Id, run.Jobs.Count);
				return;
			}

			Interlocked.Increment(ref _runsFailed);
			_logger.LogError(ex, "Failed to dispatch crawl plan {PlanId}", plan.Id);
			await RecordAuditAsync("crawl.run_failed", plan.Id, runId, new Dictionary<string, object?>
			{
				["plan_id"] = plan.Id,
				["run_id"] = runId,
				["error"] = ex.Message
			}, cancellationToken).ConfigureAwait(false);
			await SettleCursorAsync(plan, cancellationToken).ConfigureAwait(false);
		}
	}

	private IReadOnlyList<AccountSpec> ResolvePool(CrawlPlan plan)
	{
		if (plan.Accounts is { Count: > 0 })
		{
			return plan.Accounts
				.Select(name => _accounts.Get(name))
				.Where(spec => spec is { Enabled: true })
				.Select(spec => spec!)
				.ToList();
		}

		return _accounts.List().Where(spec => spec.Enabled).ToList();
	}

	// --- completion ---

	private async Task CompleteRunAsync(string planId, InFlightRun run, CancellationToken cancellationToken)
	{
		CrawlPlan? plan = await _crawl.GetPlanAsync(planId, cancellationToken).ConfigureAwait(false);
		int total = await _crawl.CountResultsAsync(new CrawlResultQuery(PlanId: planId, RunId: run.RunId), cancellationToken).ConfigureAwait(false);
		int ok = await _crawl.CountResultsAsync(new CrawlResultQuery(PlanId: planId, RunId: run.RunId, Ok: true), cancellationToken).ConfigureAwait(false);
		int failed = total - ok;

		string status = run.TimedOut ? "timeout" : run.EmptyPool ? "skipped" : ok == 0 && failed > 0 ? "failed" : "completed";
		var payload = new Dictionary<string, object?>
		{
			["plan_id"] = planId,
			["run_id"] = run.RunId,
			["status"] = status,
			["total"] = total,
			["ok"] = ok,
			["failed"] = failed
		};

		string eventType = status == "skipped" ? "crawl.run_skipped" : "crawl.run_completed";
		_events.Publish(null, eventType, payload);
		await RecordAuditAsync(eventType, planId, run.RunId, payload, cancellationToken).ConfigureAwait(false);

		if (plan != null)
		{
			if (!run.EmptyPool)
			{
				await _crawl.PruneRunsAsync(planId, _cfg.CrawlKeepRuns, cancellationToken).ConfigureAwait(false);
			}

			await SettleCursorAsync(plan, cancellationToken).ConfigureAwait(false);
		}

		Interlocked.Increment(ref _runsCompleted);
		_logger.LogInformation("Crawl plan {PlanId} run {RunId} finished: {Status}, {Ok}/{Total} apps ok",
			planId, run.RunId, status, ok, total);
	}

	/// <summary>One-shot plans clear the due cursor; recurring plans advance it from now.</summary>
	private async Task SettleCursorAsync(CrawlPlan plan, CancellationToken cancellationToken)
	{
		bool recurring = !string.IsNullOrWhiteSpace(plan.Cron) || plan.IntervalSeconds > 0;
		DateTimeOffset? next = recurring
			? ScheduleClock.NextRun(new JobSchedule(IntervalSeconds: plan.IntervalSeconds, Cron: plan.Cron), DateTimeOffset.UtcNow)
			: null;
		await _crawl.SetNextRunAsync(plan.Id, next, cancellationToken).ConfigureAwait(false);
	}

	private async Task RecordAuditAsync(string action, string planId, string runId, IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken)
	{
		try
		{
			await _audit.RecordAsync(AuditStoreExtensions.CreateEntry(
				action,
				"orchestrator",
				details: details), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to persist audit entry {Action} for crawl run {RunId}", action, runId);
		}
	}

	// --- output parsing helpers (values arrive as JsonElements after the
	// WS/SQLite round-trip; tests may pass plain in-memory structures) ---

	private static IEnumerable<JsonElement> EnumerateItems(IReadOnlyDictionary<string, object?> output, string key)
	{
		if (!output.TryGetValue(key, out object? value) || value == null)
		{
			yield break;
		}

		if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
		{
			foreach (JsonElement item in array.EnumerateArray())
			{
				yield return item;
			}

			yield break;
		}

		if (value is IEnumerable<object?> list)
		{
			foreach (object? item in list)
			{
				if (item is JsonElement element)
				{
					yield return element;
					continue;
				}

				yield return JsonSerializer.SerializeToElement(item, JsonDefaults.Options);
			}
		}
	}

	private static uint GetUInt(JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(property, out JsonElement value) &&
		value.TryGetUInt32(out uint result)
			? result
			: 0;

	private static string? GetString(JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object &&
		element.TryGetProperty(property, out JsonElement value) &&
		value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
