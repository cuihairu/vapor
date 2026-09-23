using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// A crawl plan: which apps to fetch, which account pool to spread them over,
/// and how often to re-run. Runtime progress (run count, last/next run) lives
/// on the same row — the cursor the worker advances after every completed run.
/// </summary>
public sealed record CrawlPlan(
	string Id,
	string Name,
	IReadOnlyList<uint> AppIds,
	IReadOnlyList<string>? Accounts,
	IReadOnlyDictionary<uint, string>? Overrides,
	int ShardSize,
	int IntervalMs,
	string Cc,
	string? Cron,
	int IntervalSeconds,
	bool Enabled,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	int RunCount = 0,
	string? LastRunId = null,
	DateTimeOffset? LastRunAt = null,
	DateTimeOffset? NextRunAt = null
);

/// <summary>One per-app crawl outcome persisted from a finished task.</summary>
public sealed record CrawlResultRow(
	long Id,
	string PlanId,
	string RunId,
	uint AppId,
	string Account,
	string? JobId,
	bool Ok,
	string? Error,
	JsonElement? Data,
	DateTimeOffset FetchedAt
);

/// <summary>Filter for <see cref="SqliteCrawlStore.QueryResultsAsync"/>; null fields are skipped.</summary>
public sealed record CrawlResultQuery(
	string? PlanId = null,
	string? RunId = null,
	uint? AppId = null,
	string? Account = null,
	bool? Ok = null,
	int Limit = 100,
	int Offset = 0
);

/// <summary>Aggregated per-run view (GROUP BY run over the results table).</summary>
public sealed record CrawlRunSummary(
	string RunId,
	string PlanId,
	DateTimeOffset StartedAt,
	DateTimeOffset? FinishedAt,
	int Total,
	int Ok,
	int Failed
);

/// <summary>
/// SQLite persistence for the crawl feature: plans in <c>crawl_plans</c>,
/// per-app outcomes in <c>crawl_results</c>. Deleting a plan keeps its
/// results (they remain queryable history); pruning keeps only the newest N
/// runs per plan.
/// </summary>
public sealed class SqliteCrawlStore : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteCrawlStore(string dbPath)
	{
		if (string.IsNullOrWhiteSpace(dbPath))
		{
			throw new ArgumentException("DB path is required", nameof(dbPath));
		}

		if (!string.Equals(dbPath, ":memory:", StringComparison.Ordinal))
		{
			string? dir = Path.GetDirectoryName(dbPath);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}

		_connection = new SqliteConnection($"Data Source={dbPath}");
		_connection.Open();

		Migrate();
	}

	public void Dispose()
	{
		_connection.Dispose();
		_mutex.Dispose();
	}

	// --- plans ---

	public async Task UpsertPlanAsync(CrawlPlan plan, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO crawl_plans (id, name, app_ids_json, accounts_json, overrides_json, shard_size, interval_ms, cc,
					cron, interval_seconds, enabled, created_at_ms, updated_at_ms, run_count, last_run_id, last_run_at_ms, next_run_at_ms)
				VALUES ($id, $name, $appIds, $accounts, $overrides, $shardSize, $intervalMs, $cc,
					$cron, $intervalSeconds, $enabled, $created, $updated, $runCount, $lastRunId, $lastRunAt, $nextRun)
				ON CONFLICT(id) DO UPDATE SET
					name=$name, app_ids_json=$appIds, accounts_json=$accounts, overrides_json=$overrides,
					shard_size=$shardSize, interval_ms=$intervalMs, cc=$cc, cron=$cron, interval_seconds=$intervalSeconds,
					enabled=$enabled, updated_at_ms=$updated, next_run_at_ms=$nextRun;
				""";
			BindPlan(cmd, plan);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<CrawlPlan?> GetPlanAsync(string planId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(planId))
		{
			return null;
		}

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT * FROM crawl_plans WHERE id = $id;";
			cmd.Parameters.AddWithValue("$id", planId);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPlanRow(reader) : null;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyList<CrawlPlan>> ListPlansAsync(CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT * FROM crawl_plans ORDER BY created_at_ms DESC, id;";
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			var plans = new List<CrawlPlan>();
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				plans.Add(ReadPlanRow(reader));
			}

			return plans;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Deletes the plan row only — persisted crawl results stay queryable.</summary>
	public async Task<bool> DeletePlanAsync(string planId, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM crawl_plans WHERE id = $id;";
			cmd.Parameters.AddWithValue("$id", planId);
			return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task SetPlanEnabledAsync(string planId, bool enabled, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "UPDATE crawl_plans SET enabled = $enabled, updated_at_ms = $now WHERE id = $id;";
			cmd.Parameters.AddWithValue("$id", planId);
			cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
			cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>
	/// Atomically claims the oldest due enabled plan: the CAS on
	/// <c>next_run_at_ms</c> makes concurrent workers pick disjoint plans, and
	/// the same UPDATE advances run bookkeeping (count, last run id/at). The
	/// due cursor itself stays untouched until the run completes — the worker
	/// sets the next value (or null for one-shots) via <see cref="SetNextRunAsync"/>.
	/// </summary>
	internal async Task<CrawlPlan?> ClaimDuePlanAsync(string runId, CancellationToken cancellationToken = default)
	{
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			CrawlPlan? plan;
			long expectedDue;
			using (var select = _connection.CreateCommand())
			{
				// The queued row IS the plan row: reading the full record here (instead
				// of re-reading by id afterwards) leaves no window for the plan to
				// vanish mid-claim — the CAS below is the only concurrency gate needed.
				select.CommandText = """
					SELECT * FROM crawl_plans
					WHERE enabled = 1 AND next_run_at_ms IS NOT NULL AND next_run_at_ms <= $now
					ORDER BY next_run_at_ms, id LIMIT 1;
					""";
				select.Parameters.AddWithValue("$now", nowMs);
				using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
				if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				{
					return null;
				}

				plan = ReadPlanRow(reader);
				expectedDue = reader.GetInt64(reader.GetOrdinal("next_run_at_ms"));
			}

			using var claim = _connection.CreateCommand();
			claim.CommandText = """
				UPDATE crawl_plans SET run_count = run_count + 1, last_run_id = $runId, last_run_at_ms = $now
				WHERE id = $id AND enabled = 1 AND next_run_at_ms = $expected;
				""";
			claim.Parameters.AddWithValue("$id", plan.Id);
			claim.Parameters.AddWithValue("$runId", runId);
			claim.Parameters.AddWithValue("$now", nowMs);
			claim.Parameters.AddWithValue("$expected", expectedDue);
			int affected = await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			return affected > 0 ? plan with { RunCount = plan.RunCount + 1, LastRunId = runId, LastRunAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMs) } : null;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Advances (or clears, for one-shots) the due cursor after a run settles.</summary>
	public async Task SetNextRunAsync(string planId, DateTimeOffset? nextRun, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "UPDATE crawl_plans SET next_run_at_ms = $nextRun WHERE id = $id;";
			cmd.Parameters.AddWithValue("$id", planId);
			cmd.Parameters.AddWithValue("$nextRun", nextRun?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	// --- results ---

	public async Task AddResultAsync(CrawlResultRow row, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(row);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO crawl_results (plan_id, run_id, app_id, account, job_id, ok, error, data_json, fetched_at_ms)
				VALUES ($planId, $runId, $appId, $account, $jobId, $ok, $error, $data, $fetchedAt);
				""";
			cmd.Parameters.AddWithValue("$planId", row.PlanId);
			cmd.Parameters.AddWithValue("$runId", row.RunId);
			cmd.Parameters.AddWithValue("$appId", row.AppId);
			cmd.Parameters.AddWithValue("$account", row.Account);
			cmd.Parameters.AddWithValue("$jobId", (object?)row.JobId ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$ok", row.Ok ? 1 : 0);
			cmd.Parameters.AddWithValue("$error", (object?)row.Error ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$data", row.Data is { } data ? data.GetRawText() : (object)DBNull.Value);
			cmd.Parameters.AddWithValue("$fetchedAt", row.FetchedAt.ToUnixTimeMilliseconds());
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<IReadOnlyList<CrawlResultRow>> QueryResultsAsync(CrawlResultQuery query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			BuildQueryCommand(cmd, query, withPaging: true);
			var rows = new List<CrawlResultRow>();
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				rows.Add(ReadResultRow(reader));
			}

			return rows;
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<int> CountResultsAsync(CrawlResultQuery query, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			BuildQueryCommand(cmd, query, withPaging: false);
			object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			return (int)(long)result!; // the command is always SELECT COUNT(*), which SQLite returns as a boxed long (never NULL) — the is-long fallback would be an unreachable probe
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Newest runs for a plan, newest first, capped at <paramref name="limit"/> (1..100).</summary>
	public async Task<IReadOnlyList<CrawlRunSummary>> ListRunsAsync(string planId, int limit = 20, CancellationToken cancellationToken = default)
	{
		int capped = Math.Clamp(limit, 1, 100);
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				SELECT run_id, plan_id, MIN(fetched_at_ms) AS started_ms, MAX(fetched_at_ms) AS finished_ms,
					COUNT(*) AS total, SUM(ok) AS ok_count
				FROM crawl_results WHERE plan_id = $planId
				GROUP BY run_id ORDER BY started_ms DESC LIMIT $limit;
				""";
			cmd.Parameters.AddWithValue("$planId", planId);
			cmd.Parameters.AddWithValue("$limit", capped);
			var runs = new List<CrawlRunSummary>();
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				int total = reader.GetInt32(4);
				int ok = reader.GetInt32(5);
				runs.Add(new CrawlRunSummary(
					reader.GetString(0),
					reader.GetString(1),
					DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
					DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
					total,
					ok,
					total - ok));
			}

			return runs;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Keeps only the newest <paramref name="keepRuns"/> runs for the plan; older rows are deleted.</summary>
	public async Task<int> PruneRunsAsync(string planId, int keepRuns, CancellationToken cancellationToken = default)
	{
		if (keepRuns <= 0)
		{
			return 0;
		}

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				DELETE FROM crawl_results WHERE plan_id = $planId AND run_id NOT IN (
					SELECT run_id FROM crawl_results WHERE plan_id = $planId
					GROUP BY run_id ORDER BY MIN(fetched_at_ms) DESC LIMIT $keep);
				""";
			cmd.Parameters.AddWithValue("$planId", planId);
			cmd.Parameters.AddWithValue("$keep", keepRuns);
			return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	// --- internals ---

	private static void BindPlan(SqliteCommand cmd, CrawlPlan plan)
	{
		cmd.Parameters.AddWithValue("$id", plan.Id);
		cmd.Parameters.AddWithValue("$name", plan.Name);
		cmd.Parameters.AddWithValue("$appIds", JsonSerializer.Serialize(plan.AppIds, JsonDefaults.Options));
		cmd.Parameters.AddWithValue("$accounts", plan.Accounts is { Count: > 0 } accounts
			? JsonSerializer.Serialize(accounts, JsonDefaults.Options)
			: DBNull.Value);
		cmd.Parameters.AddWithValue("$overrides", plan.Overrides is { Count: > 0 } overrides
			? JsonSerializer.Serialize(overrides, JsonDefaults.Options)
			: DBNull.Value);
		cmd.Parameters.AddWithValue("$shardSize", plan.ShardSize);
		cmd.Parameters.AddWithValue("$intervalMs", plan.IntervalMs);
		cmd.Parameters.AddWithValue("$cc", plan.Cc);
		cmd.Parameters.AddWithValue("$cron", (object?)plan.Cron ?? DBNull.Value);
		cmd.Parameters.AddWithValue("$intervalSeconds", plan.IntervalSeconds);
		cmd.Parameters.AddWithValue("$enabled", plan.Enabled ? 1 : 0);
		cmd.Parameters.AddWithValue("$created", plan.CreatedAt.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$updated", plan.UpdatedAt.ToUnixTimeMilliseconds());
		cmd.Parameters.AddWithValue("$runCount", plan.RunCount);
		cmd.Parameters.AddWithValue("$lastRunId", (object?)plan.LastRunId ?? DBNull.Value);
		cmd.Parameters.AddWithValue("$lastRunAt", plan.LastRunAt?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
		cmd.Parameters.AddWithValue("$nextRun", plan.NextRunAt?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
	}

	private CrawlPlan ReadPlanRow(SqliteDataReader reader)
	{
		IReadOnlyList<uint> appIds = [];
		if (reader.IsDBNull(reader.GetOrdinal("app_ids_json")) == false)
		{
			var parsed = JsonSerializer.Deserialize<List<uint>>(reader.GetString(reader.GetOrdinal("app_ids_json")), JsonDefaults.Options);
			if (parsed is { Count: > 0 })
			{
				appIds = parsed;
			}
		}

		IReadOnlyList<string>? accounts = null;
		int accountsOrdinal = reader.GetOrdinal("accounts_json");
		if (!reader.IsDBNull(accountsOrdinal))
		{
			accounts = JsonSerializer.Deserialize<List<string>>(reader.GetString(accountsOrdinal), JsonDefaults.Options);
		}

		Dictionary<uint, string>? overrides = null;
		int overridesOrdinal = reader.GetOrdinal("overrides_json");
		if (!reader.IsDBNull(overridesOrdinal))
		{
			overrides = new Dictionary<uint, string>();
			using var doc = JsonDocument.Parse(reader.GetString(overridesOrdinal));
			foreach (var prop in doc.RootElement.EnumerateObject())
			{
				if (uint.TryParse(prop.Name, out uint appId))
				{
					overrides[appId] = prop.Value.GetString() ?? "";
				}
			}
		}

		int cronOrdinal = reader.GetOrdinal("cron");
		int lastRunIdOrdinal = reader.GetOrdinal("last_run_id");
		int lastRunAtOrdinal = reader.GetOrdinal("last_run_at_ms");
		int nextRunOrdinal = reader.GetOrdinal("next_run_at_ms");

		return new CrawlPlan(
			Id: reader.GetString(reader.GetOrdinal("id")),
			Name: reader.GetString(reader.GetOrdinal("name")),
			AppIds: appIds,
			Accounts: accounts,
			Overrides: overrides,
			ShardSize: reader.GetInt32(reader.GetOrdinal("shard_size")),
			IntervalMs: reader.GetInt32(reader.GetOrdinal("interval_ms")),
			Cc: reader.GetString(reader.GetOrdinal("cc")),
			Cron: reader.IsDBNull(cronOrdinal) ? null : reader.GetString(cronOrdinal),
			IntervalSeconds: reader.GetInt32(reader.GetOrdinal("interval_seconds")),
			Enabled: reader.GetInt64(reader.GetOrdinal("enabled")) != 0,
			CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at_ms"))),
			UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("updated_at_ms"))),
			RunCount: reader.GetInt32(reader.GetOrdinal("run_count")),
			LastRunId: reader.IsDBNull(lastRunIdOrdinal) ? null : reader.GetString(lastRunIdOrdinal),
			LastRunAt: reader.IsDBNull(lastRunAtOrdinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(lastRunAtOrdinal)),
			NextRunAt: reader.IsDBNull(nextRunOrdinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(nextRunOrdinal)));
	}

	private static CrawlResultRow ReadResultRow(SqliteDataReader reader)
	{
		int jobIdOrdinal = reader.GetOrdinal("job_id");
		int errorOrdinal = reader.GetOrdinal("error");
		int dataOrdinal = reader.GetOrdinal("data_json");

		JsonElement? data = null;
		if (!reader.IsDBNull(dataOrdinal))
		{
			using var doc = JsonDocument.Parse(reader.GetString(dataOrdinal));
			data = doc.RootElement.Clone();
		}

		return new CrawlResultRow(
			Id: reader.GetInt64(reader.GetOrdinal("id")),
			PlanId: reader.GetString(reader.GetOrdinal("plan_id")),
			RunId: reader.GetString(reader.GetOrdinal("run_id")),
			AppId: (uint)reader.GetInt64(reader.GetOrdinal("app_id")),
			Account: reader.GetString(reader.GetOrdinal("account")),
			JobId: reader.IsDBNull(jobIdOrdinal) ? null : reader.GetString(jobIdOrdinal),
			Ok: reader.GetInt64(reader.GetOrdinal("ok")) != 0,
			Error: reader.IsDBNull(errorOrdinal) ? null : reader.GetString(errorOrdinal),
			Data: data,
			FetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("fetched_at_ms"))));
	}

	private static void BuildQueryCommand(SqliteCommand cmd, CrawlResultQuery query, bool withPaging)
	{
		var where = new List<string>();

		if (!string.IsNullOrWhiteSpace(query.PlanId))
		{
			where.Add("plan_id = $planId");
			cmd.Parameters.AddWithValue("$planId", query.PlanId.Trim());
		}

		if (!string.IsNullOrWhiteSpace(query.RunId))
		{
			where.Add("run_id = $runId");
			cmd.Parameters.AddWithValue("$runId", query.RunId.Trim());
		}

		if (query.AppId is { } appId)
		{
			where.Add("app_id = $appId");
			cmd.Parameters.AddWithValue("$appId", appId);
		}

		if (!string.IsNullOrWhiteSpace(query.Account))
		{
			where.Add("account = $account");
			cmd.Parameters.AddWithValue("$account", query.Account.Trim());
		}

		if (query.Ok is { } ok)
		{
			where.Add("ok = $ok");
			cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
		}

		string whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";

		// CA2100 suppressed: every interpolated fragment (whereClause) is a
		// compile-time constant built above; all user input rides as $parameters.
#pragma warning disable CA2100
		if (withPaging)
		{
			int limit = query.Limit <= 0 ? 100 : Math.Min(query.Limit, 500);
			int offset = Math.Max(query.Offset, 0);
			cmd.CommandText = $"""
				SELECT id, plan_id, run_id, app_id, account, job_id, ok, error, data_json, fetched_at_ms
				FROM crawl_results {whereClause}
				ORDER BY fetched_at_ms DESC, id DESC
				LIMIT $limit OFFSET $offset;
				""";
			cmd.Parameters.AddWithValue("$limit", limit);
			cmd.Parameters.AddWithValue("$offset", offset);
		}
		else
		{
			cmd.CommandText = $"SELECT COUNT(*) FROM crawl_results {whereClause};";
#pragma warning restore CA2100
		}
	}

	private void Migrate()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			PRAGMA journal_mode = WAL;
			PRAGMA synchronous = NORMAL;

			CREATE TABLE IF NOT EXISTS crawl_plans (
				id TEXT PRIMARY KEY,
				name TEXT NOT NULL,
				app_ids_json TEXT NOT NULL,
				accounts_json TEXT,
				overrides_json TEXT,
				shard_size INTEGER NOT NULL DEFAULT 50,
				interval_ms INTEGER NOT NULL DEFAULT 500,
				cc TEXT NOT NULL DEFAULT 'us',
				cron TEXT,
				interval_seconds INTEGER NOT NULL DEFAULT 0,
				enabled INTEGER NOT NULL DEFAULT 1,
				created_at_ms INTEGER NOT NULL,
				updated_at_ms INTEGER NOT NULL,
				run_count INTEGER NOT NULL DEFAULT 0,
				last_run_id TEXT,
				last_run_at_ms INTEGER,
				next_run_at_ms INTEGER
			);

			CREATE TABLE IF NOT EXISTS crawl_results (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				plan_id TEXT NOT NULL,
				run_id TEXT NOT NULL,
				app_id INTEGER NOT NULL,
				account TEXT NOT NULL,
				job_id TEXT,
				ok INTEGER NOT NULL,
				error TEXT,
				data_json TEXT,
				fetched_at_ms INTEGER NOT NULL
			);

			CREATE INDEX IF NOT EXISTS idx_crawl_plans_due ON crawl_plans(enabled, next_run_at_ms);
			CREATE INDEX IF NOT EXISTS idx_crawl_results_run ON crawl_results(run_id);
			CREATE INDEX IF NOT EXISTS idx_crawl_results_plan_app ON crawl_results(plan_id, app_id, fetched_at_ms);
			CREATE INDEX IF NOT EXISTS idx_crawl_results_account ON crawl_results(account);
			""";
		cmd.ExecuteNonQuery();
	}
}
