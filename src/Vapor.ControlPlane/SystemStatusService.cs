using System.Diagnostics;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Aggregated internal status report served by <c>GET /v1/system/status</c>.
/// Combines control-plane self health, proxy probe results, connected agents
/// and account-level desired-vs-actual state into one read-only view.
/// Credentials never appear here: proxy endpoints reuse the agent-side masked
/// form, auth challenges contribute counters only (never challenge codes).
/// </summary>
public sealed record SystemStatusReport(
	OverallHealth Overall,
	ControlPlaneStatus ControlPlane,
	ProxyStatus Proxies,
	AgentsStatus Agents,
	AccountsStatus Accounts);

/// <summary>Top-level verdict: healthy / degraded / unhealthy, with one reason line per finding.</summary>
public sealed record OverallHealth(string Status, IReadOnlyList<string> Reasons);

public sealed record ControlPlaneStatus(
	DbStatus Db,
	JobQueueStatus Jobs,
	SchedulerStatus Scheduler,
	ReconcilerStatus Reconciler,
	RecurringJobsStatus RecurringJobs,
	PluginsStatus Plugins);

public sealed record DbStatus(bool Available, long LatencyMs, string? Error);

public sealed record JobQueueStatus(int Queued, int Running, int Finished, int Failed, int Canceled);

public sealed record SchedulerStatus(
	DateTimeOffset? LastTickAt,
	long DispatchNoCapableAgent,
	long DispatchEnqueueFailed,
	long DispatchAttemptsExhausted);

public sealed record ReconcilerStatus(
	DateTimeOffset? LastPassAt,
	long? LastPassDurationMs,
	bool LastPassFailed,
	long LoginsDispatched,
	long PlaysDispatched,
	long CardDropsDispatched,
	long PlaytimesDispatched,
	long TradeAcceptsDispatched,
	long Rebalances,
	long Unassignments,
	long ThrottledSkips,
	long NoAgentSkips,
	long DryRunDeviations);

public sealed record RecurringJobsStatus(
	long Triggered,
	long OverlapSkipped,
	long MissedDropped,
	long MissedCatchUps);

public sealed record PluginsStatus(int AgentsReporting, int Entries, IReadOnlyDictionary<string, int> ByTrust);

/// <summary>Aggregated view of historical check_proxy task outputs. There is no proxy pool registry in the control plane — proxies are per-account agent-side configuration, so only probe history is summarized here.</summary>
public sealed record ProxyStatus(int ProbesOk, int ProbesFailed, int ProxyDisabled, IReadOnlyList<ProxyProbeSummary> Recent);

/// <summary>One recent proxy probe outcome. The proxy endpoint, when present, is already masked by the agent before the task output is stored.</summary>
public sealed record ProxyProbeSummary(
	string Account,
	bool Success,
	string? Proxy,
	string? ExitIp,
	double? LatencyMs,
	string? Error,
	DateTimeOffset CheckedAt);

public sealed record AgentsStatus(int Connected, IReadOnlyList<string> Regions, IReadOnlyList<AgentStatusEntry> Entries);

public sealed record AgentStatusEntry(
	string Id,
	string Region,
	DateTimeOffset ConnectedAt,
	IReadOnlyDictionary<string, bool>? Capabilities);

public sealed record AccountsStatus(
	int Total,
	IReadOnlyDictionary<string, int> ByDesiredState,
	IReadOnlyList<AccountMismatch> Mismatches,
	int SessionsTracked,
	int PendingChallenges,
	IReadOnlyDictionary<string, int> ChallengeTypes);

/// <summary>An account whose desired state and latest session snapshot disagree.</summary>
public sealed record AccountMismatch(string Account, string DesiredState, string? ActualSessionState, string? AssignedAgent);

/// <summary>
/// Builds <see cref="SystemStatusReport"/> from the live services the
/// composition root already hosts. Read-only over existing stores and
/// counters — no new persistence, no direct DB access beyond the job store
/// interface.
/// </summary>
public sealed class SystemStatusService(
	IJobStore store,
	AgentRegistry agents,
	AccountStore accounts,
	SessionTracker sessions,
	AuthChallengeTracker challenges,
	DesiredStateReconciler reconciler,
	TaskSchedulerService scheduler,
	RecurringJobScheduler recurringJobs,
	PluginInventory plugins)
{
	/// <summary>How many recent jobs to scan for check_proxy task outputs. Bounded so the report stays O(small).</summary>
	internal const int ProbeJobScanLimit = 100;

	/// <summary>How many recent probe summaries to include per report.</summary>
	internal const int RecentProbeLimit = 20;

	public async Task<SystemStatusReport> BuildAsync(CancellationToken cancellationToken)
	{
		(DbStatus db, JobQueueStatus jobs) = await ProbeDbAsync(cancellationToken).ConfigureAwait(false);
		ProxyStatus proxies = await BuildProxyStatusAsync(cancellationToken).ConfigureAwait(false);
		AgentsStatus agentStatus = BuildAgentStatus();
		AccountsStatus accountStatus = BuildAccountStatus();
		ControlPlaneStatus controlPlane = new(
			Db: db,
			Jobs: jobs,
			Scheduler: BuildSchedulerStatus(),
			Reconciler: BuildReconcilerStatus(),
			RecurringJobs: new RecurringJobsStatus(
				Triggered: recurringJobs.TriggeredRuns,
				OverlapSkipped: recurringJobs.SkippedOverlaps,
				MissedDropped: recurringJobs.MissedDropped,
				MissedCatchUps: recurringJobs.MissedCatchUps),
			Plugins: BuildPluginsStatus());
		OverallHealth overall = DeriveOverall(
			dbAvailable: db.Available,
			lastPassFailed: controlPlane.Reconciler.LastPassFailed,
			connectedAgents: agentStatus.Connected,
			totalAccounts: accountStatus.Total,
			pendingChallenges: accountStatus.PendingChallenges);
		return new SystemStatusReport(overall, controlPlane, proxies, agentStatus, accountStatus);
	}

	/// <summary>One GetTaskStatusCounts call doubles as the DB liveness probe and the queue-depth snapshot; its wall time is the reported latency.</summary>
	private async Task<(DbStatus Db, JobQueueStatus Jobs)> ProbeDbAsync(CancellationToken cancellationToken)
	{
		long started = Stopwatch.GetTimestamp();
		IReadOnlyDictionary<JobTaskStatus, int> counts;
		string? error = null;
		try
		{
			counts = await store.GetTaskStatusCounts(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) // includes cancellation: a store that dies mid-cancel is still a store failure
		{
			counts = new Dictionary<JobTaskStatus, int>();
			error = ex.Message;
		}
		long latencyMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		int Count(JobTaskStatus s) => counts.TryGetValue(s, out int n) ? n : 0;
		return (
			new DbStatus(Available: error is null, LatencyMs: latencyMs, Error: error),
			new JobQueueStatus(
				Queued: Count(JobTaskStatus.Queued),
				Running: Count(JobTaskStatus.Running),
				Finished: Count(JobTaskStatus.Finished),
				Failed: Count(JobTaskStatus.Failed),
				Canceled: Count(JobTaskStatus.Canceled)));
	}

	private async Task<ProxyStatus> BuildProxyStatusAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<Job> recentJobs = await store.ListJobs(ProbeJobScanLimit, null, cancellationToken).ConfigureAwait(false);
		int ok = 0, failed = 0, disabled = 0;
		List<ProxyProbeSummary> recent = [];
		foreach (Job job in recentJobs)
		{
			if (!string.Equals(job.Action, "check_proxy", StringComparison.Ordinal))
			{
				continue;
			}
			JobWithTasks withTasks = await store.GetJob(job.Id, cancellationToken).ConfigureAwait(false);
			foreach (JobTask task in withTasks.Tasks)
			{
				if (task.Status == JobTaskStatus.Queued || task.Status == JobTaskStatus.Running)
				{
					continue; // outcome not yet known — not a probe result
				}
				IReadOnlyDictionary<string, object?> output = task.Output ?? new Dictionary<string, object?>();
				bool proxyEnabled = OutputBool(output, "proxyEnabled") ?? false;
				if (!proxyEnabled)
				{
					disabled++;
					continue;
				}
				bool success = OutputBool(output, "steamReachable") == true
					&& output.TryGetValue("exitIp", out object? exitIpValue)
					&& exitIpValue is string exitIpText
					&& exitIpText.Length > 0;
				if (success)
				{
					ok++;
				}
				else
				{
					failed++;
				}
				if (recent.Count < RecentProbeLimit)
				{
					recent.Add(new ProxyProbeSummary(
						Account: OutputString(output, "account") ?? task.Target,
						Success: success,
						Proxy: OutputString(output, "proxy"),
						ExitIp: OutputString(output, "exitIp"),
						LatencyMs: OutputDouble(output, "latencyMs"),
						Error: OutputString(output, "error"),
						CheckedAt: task.UpdatedAt));
				}
			}
		}
		return new ProxyStatus(ProbesOk: ok, ProbesFailed: failed, ProxyDisabled: disabled, Recent: recent);
	}

	private AgentsStatus BuildAgentStatus()
	{
		IReadOnlyList<ConnectedAgent> connected = agents.ListConnected();
		List<AgentStatusEntry> entries = connected
			.Select(a => new AgentStatusEntry(
				Id: a.Hello.AgentId,
				Region: a.Hello.Region,
				ConnectedAt: a.ConnectedAt,
				Capabilities: a.Hello.Capabilities))
			.ToList();
		return new AgentsStatus(Connected: entries.Count, Regions: agents.Regions(), Entries: entries);
	}

	private AccountsStatus BuildAccountStatus()
	{
		IReadOnlyList<AccountSpec> specs = accounts.List();
		Dictionary<string, string> sessionStateByAccount = sessions.List().ToDictionary(
			s => s.AccountName, s => s.State, StringComparer.Ordinal);
		Dictionary<string, int> byDesired = [];
		List<AccountMismatch> mismatches = [];
		foreach (AccountSpec spec in specs)
		{
			string desired = spec.DesiredState.ToString();
			byDesired[desired] = byDesired.TryGetValue(desired, out int n) ? n + 1 : 1;
			// Desired-vs-actual: compare the spec against the latest session
			// snapshot; missing snapshot counts as "no session running".
			sessionStateByAccount.TryGetValue(spec.AccountName, out string? actual);
			if (!IsSessionConsistent(spec.DesiredState, actual))
			{
				mismatches.Add(new AccountMismatch(
					Account: spec.AccountName,
					DesiredState: desired,
					ActualSessionState: actual,
					AssignedAgent: spec.AgentId));
			}
		}
		IReadOnlyList<AuthChallengeEvent> pending = challenges.List();
		Dictionary<string, int> challengeTypes = [];
		foreach (AuthChallengeEvent challenge in pending)
		{
			// Counters only — the challenge Code field never enters this report.
			challengeTypes[challenge.ChallengeType] = challengeTypes.TryGetValue(challenge.ChallengeType, out int n) ? n + 1 : 1;
		}
		return new AccountsStatus(
			Total: specs.Count,
			ByDesiredState: byDesired,
			Mismatches: mismatches,
			SessionsTracked: sessionStateByAccount.Count,
			PendingChallenges: pending.Count,
			ChallengeTypes: challengeTypes);
	}

	private SchedulerStatus BuildSchedulerStatus() => new(
		LastTickAt: scheduler.LastTickAt,
		DispatchNoCapableAgent: scheduler.DispatchNoCapableAgent,
		DispatchEnqueueFailed: scheduler.DispatchEnqueueFailed,
		DispatchAttemptsExhausted: scheduler.DispatchAttemptsExhausted);

	private ReconcilerStatus BuildReconcilerStatus() => new(
		LastPassAt: reconciler.LastPassAt,
		LastPassDurationMs: reconciler.LastPassDurationMs,
		LastPassFailed: reconciler.LastPassFailed,
		LoginsDispatched: reconciler.LoginsDispatched,
		PlaysDispatched: reconciler.PlaysDispatched,
		CardDropsDispatched: reconciler.CardDropsDispatched,
		PlaytimesDispatched: reconciler.PlaytimesDispatched,
		TradeAcceptsDispatched: reconciler.TradeAcceptsDispatched,
		Rebalances: reconciler.Rebalances,
		Unassignments: reconciler.Unassignments,
		ThrottledSkips: reconciler.ThrottledSkips,
		NoAgentSkips: reconciler.NoAgentSkips,
		DryRunDeviations: reconciler.DryRunDeviations);

	private PluginsStatus BuildPluginsStatus()
	{
		IReadOnlyDictionary<string, AgentPlugins> snapshot = plugins.Snapshot();
		Dictionary<string, int> byTrust = [];
		int entries = 0;
		foreach (AgentPlugins agentPlugins in snapshot.Values)
		{
			foreach (PluginInventoryEntry entry in agentPlugins.Plugins)
			{
				entries++;
				string trust = entry.Trust ?? "unknown";
				byTrust[trust] = byTrust.TryGetValue(trust, out int n) ? n + 1 : 1;
			}
		}
		return new PluginsStatus(AgentsReporting: snapshot.Count, Entries: entries, ByTrust: byTrust);
	}

	/// <summary>Desired-vs-actual consistency for one account (internal static for exhaustive tests).</summary>
	internal static bool IsSessionConsistent(AccountDesiredState desired, string? sessionState)
	{
		if (desired == AccountDesiredState.Offline)
		{
			return sessionState is null || IsDisconnectedState(sessionState);
		}
		return sessionState is not null && IsLiveSessionState(sessionState);
	}

	/// <summary>True when a reported session state clearly means "no live session". Agent-reported states are free text; this matches the known offline vocabulary.</summary>
	internal static bool IsDisconnectedState(string state) =>
		state.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
		state.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
		state.Contains("logged_out", StringComparison.OrdinalIgnoreCase) ||
		state.Contains("logged-out", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(state, "none", StringComparison.OrdinalIgnoreCase);

	/// <summary>True when a reported session state is positive evidence of a live session. "unknown" (the agent-side default when no state was reported) is not evidence either way, so it never counts as live.</summary>
	internal static bool IsLiveSessionState(string state) =>
		!string.IsNullOrWhiteSpace(state) &&
		!string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase) &&
		!IsDisconnectedState(state);

	/// <summary>Top-level verdict. DB unavailable means unhealthy; anything else that needs attention is degraded; otherwise healthy.</summary>
	internal static OverallHealth DeriveOverall(
		bool dbAvailable,
		bool lastPassFailed,
		int connectedAgents,
		int totalAccounts,
		int pendingChallenges)
	{
		List<string> reasons = [];
		if (!dbAvailable)
		{
			reasons.Add("任务库不可用，控制面无法读写作业状态");
		}
		if (lastPassFailed)
		{
			reasons.Add("期望状态收敛器最近一轮执行失败");
		}
		if (dbAvailable && totalAccounts > 0 && connectedAgents == 0)
		{
			reasons.Add("没有已连接的 agent，账号无法调度");
		}
		if (pendingChallenges > 0)
		{
			reasons.Add($"有 {pendingChallenges} 个账号待处理登录挑战（如 Steam Guard）");
		}
		string status = !dbAvailable ? "unhealthy" : reasons.Count > 0 ? "degraded" : "healthy";
		return new OverallHealth(status, reasons);
	}

	private static bool? OutputBool(IReadOnlyDictionary<string, object?> output, string key) =>
		output.TryGetValue(key, out object? value) && value is bool b ? b : null;

	private static string? OutputString(IReadOnlyDictionary<string, object?> output, string key) =>
		output.TryGetValue(key, out object? value) && value is string s ? s : null;

	private static double? OutputDouble(IReadOnlyDictionary<string, object?> output, string key) =>
		output.TryGetValue(key, out object? value) && value is double d ? d : null;
}
