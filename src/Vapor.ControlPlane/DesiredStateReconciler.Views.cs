namespace Vapor.ControlPlane;

/// <summary>Read projections of orchestrator state for the account aggregate and dashboard payloads.</summary>
public sealed partial class DesiredStateReconciler
{
	/// <summary>Exposes orchestration state for the account aggregate view (GET /v1/accounts/{name}).</summary>
	internal AccountOrchestrationView? GetOrchestrationView(string accountName)
	{
		if (!_runtime.TryGetValue(accountName, out AccountRuntime? runtime))
		{
			return null;
		}

		return new AccountOrchestrationView(
			AssignedAgent: runtime.AssignedAgent,
			ActiveJobId: runtime.ActiveJobId,
			ActiveJobAction: runtime.ActiveJobAction,
			LoginAttempts: runtime.LoginAttempts,
			NextAttemptAt: runtime.NextAttemptAt == DateTimeOffset.MinValue ? null : runtime.NextAttemptAt,
			Idling: runtime.Idling,
			FarmingAppId: runtime.FarmingAppId,
			FarmQueue: runtime.FarmQueue?.ToList(),
			FarmQueueCheckedAt: runtime.FarmQueueCheckedAt == DateTimeOffset.MinValue ? null : runtime.FarmQueueCheckedAt,
			BoostUnmetApps: runtime.BoostUnmetApps,
			BoostCheckedAt: runtime.BoostCheckedAt == DateTimeOffset.MinValue ? null : runtime.BoostCheckedAt,
			TradeOffersToAccept: runtime.TradeOffersToAccept?.Select(o => o.OfferId).ToList(),
			TradeOfferCheckedAt: runtime.TradeOfferCheckedAt == DateTimeOffset.MinValue ? null : runtime.TradeOfferCheckedAt,
			LastAction: runtime.LastAction,
			LastActionAt: runtime.LastActionAt == DateTimeOffset.MinValue ? null : runtime.LastActionAt,
			LastDeviation: runtime.LastDeviation,
			Standing: runtime.StandingSummary,
			StandingQuarantined: runtime.StandingQuarantined,
			StandingCheckedAt: runtime.StandingCheckedAt == DateTimeOffset.MinValue ? null : runtime.StandingCheckedAt,
			FarmCompletedApps: runtime.FarmCompletedApps?.ToList(),
			FarmSkippedApps: runtime.FarmSkippedApps?.ToList(),
			FarmAppStartedAt: runtime.FarmAppStartedAt,
			FarmStatsStartedAt: runtime.FarmStatsStartedAt,
			FarmCardsRemaining: runtime.FarmCardsRemaining,
			FarmCardsCollected: runtime.FarmCardsCollected,
			FarmCardsPerHour: CardsPerHour(runtime)
		);
	}

	/// <summary>Collection rate over the stats window (cards per hour); null before the first report.</summary>
	private static double? CardsPerHour(AccountRuntime runtime)
	{
		if (runtime.FarmStatsStartedAt is not { } startedAt || runtime.FarmCardsCollected <= 0)
		{
			return null;
		}

		double hours = (DateTimeOffset.UtcNow - startedAt).TotalHours;
		return hours > 0 ? Math.Round(runtime.FarmCardsCollected / hours, 2) : null;
	}

	/// <summary>
	/// Forces the next reconcile pass to re-run the standing check for one
	/// account (dashboard "run check now"). Honors the single-writer slot:
	/// refused while a job is in flight, when the account is unknown to the
	/// orchestrator, or when standing checks are disabled.
	/// </summary>
	internal bool RequestStandingCheck(string accountName)
	{
		if (_cfg.ReconcileStandingRefreshSeconds <= 0
			|| !_runtime.TryGetValue(accountName, out AccountRuntime? runtime)
			|| runtime.ActiveJobId is not null)
		{
			return false;
		}

		runtime.StandingCheckedAt = DateTimeOffset.MinValue;
		return true;
	}

	/// <summary>
	/// Standing view for one account, or null when the account has never been
	/// reconciled (never checked ⇒ never quarantined). Explicit dispatch paths
	/// (manual loot, storage collect) gate on this — a null or healthy view must
	/// never block an operator.
	/// </summary>
	internal AccountStandingView? GetStandingView(string accountName) =>
		!_runtime.TryGetValue(accountName, out AccountRuntime? runtime)
			? null
			: new AccountStandingView(
				accountName,
				runtime.StandingSummary,
				runtime.StandingQuarantined,
				runtime.StandingCheckedAt == DateTimeOffset.MinValue ? null : runtime.StandingCheckedAt);

	/// <summary>
	/// Test seam: seeds standing state without driving a reconcile cycle
	/// (same precedent as CheckProxyAction.ProbeOverride — the production settle
	/// path keeps its own tests in DesiredStateReconcilerTests). CheckedAt stays
	/// untouched unless given, so default seeds keep the never-checked shape.
	/// </summary>
	internal void SetStandingForTests(string accountName, bool quarantined, string? summary = "banned", DateTimeOffset? checkedAt = null)
	{
		AccountRuntime runtime = _runtime.GetOrAdd(accountName, _ => new AccountRuntime());
		runtime.StandingSummary = summary;
		runtime.StandingQuarantined = quarantined;
		if (checkedAt.HasValue)
		{
			runtime.StandingCheckedAt = checkedAt.Value;
		}
	}

	/// <summary>Standing snapshot for every tracked account (dashboard account list).</summary>
	internal IReadOnlyList<AccountStandingView> GetStandingSummaries() =>
		_runtime.OrderBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv => new AccountStandingView(
				kv.Key,
				kv.Value.StandingSummary,
				kv.Value.StandingQuarantined,
				kv.Value.StandingCheckedAt == DateTimeOffset.MinValue ? null : kv.Value.StandingCheckedAt))
			.ToList();

	/// <summary>Farm-loop snapshot for every tracked account (GET /v1/orchestration/farm).</summary>
	internal IReadOnlyList<FarmAccountView> GetFarmSummaries() =>
		_runtime.OrderBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv => new FarmAccountView(
				AccountName: kv.Key,
				FarmingAppId: kv.Value.FarmingAppId,
				Queue: kv.Value.FarmQueue?.ToList(),
				CardsRemaining: kv.Value.FarmCardsRemaining,
				CardsCollected: kv.Value.FarmCardsCollected,
				CardsPerHour: CardsPerHour(kv.Value),
				CompletedApps: kv.Value.FarmCompletedApps?.ToList(),
				SkippedApps: kv.Value.FarmSkippedApps?.ToList(),
				QueueRefreshedAt: kv.Value.FarmQueueCheckedAt == DateTimeOffset.MinValue ? null : kv.Value.FarmQueueCheckedAt,
				StatsStartedAt: kv.Value.FarmStatsStartedAt))
			.ToList();

}


/// <summary>Read-only orchestration state for one account (aggregate view payload).</summary>
public sealed record AccountOrchestrationView(
	string? AssignedAgent,
	string? ActiveJobId,
	string? ActiveJobAction,
	int LoginAttempts,
	DateTimeOffset? NextAttemptAt,
	bool Idling,
	uint? FarmingAppId = null,
	IReadOnlyList<uint>? FarmQueue = null,
	DateTimeOffset? FarmQueueCheckedAt = null,
	IReadOnlyList<uint>? BoostUnmetApps = null,
	DateTimeOffset? BoostCheckedAt = null,
	IReadOnlyList<ulong>? TradeOffersToAccept = null,
	DateTimeOffset? TradeOfferCheckedAt = null,
	string? LastAction = null,
	DateTimeOffset? LastActionAt = null,
	string? LastDeviation = null,
	string? Standing = null,
	bool StandingQuarantined = false,
	DateTimeOffset? StandingCheckedAt = null,
	IReadOnlyList<uint>? FarmCompletedApps = null,
	IReadOnlyList<uint>? FarmSkippedApps = null,
	DateTimeOffset? FarmAppStartedAt = null,
	DateTimeOffset? FarmStatsStartedAt = null,
	int? FarmCardsRemaining = null,
	int FarmCardsCollected = 0,
	double? FarmCardsPerHour = null
);

/// <summary>Read-only standing snapshot for one account (dashboard list payload).</summary>
public sealed record AccountStandingView(
	string AccountName,
	string? Standing,
	bool Quarantined,
	DateTimeOffset? CheckedAt
);

/// <summary>Read-only farm-loop snapshot for one account (dashboard farm panel payload).</summary>
public sealed record FarmAccountView(
	string AccountName,
	uint? FarmingAppId,
	IReadOnlyList<uint>? Queue,
	int? CardsRemaining,
	int CardsCollected,
	double? CardsPerHour,
	IReadOnlyList<uint>? CompletedApps,
	IReadOnlyList<uint>? SkippedApps,
	DateTimeOffset? QueueRefreshedAt,
	DateTimeOffset? StatsStartedAt
);
