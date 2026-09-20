namespace Vapor.Protocol;

/// <summary>
/// Desired operational state for a managed farm account.
/// </summary>
public enum AccountDesiredState
{
	/// <summary>No session should be running; the orchestrator keeps no agent assigned.</summary>
	Offline = 0,

	/// <summary>The account should hold a live, logged-on session.</summary>
	Online = 1,

	/// <summary>The account should be logged on and idling the configured apps.</summary>
	Idle = 2,

	/// <summary>
	/// Smart card farming: the orchestrator polls the account's remaining card
	/// drops (community badges page), idles games that still have drops and
	/// rotates to the next one as they run out. In this state
	/// <see cref="AccountSpec.IdleApps"/> is interpreted as an exclusion list
	/// (apps that must never be farmed) instead of the Idle whitelist.
	/// </summary>
	Farm = 3,

	/// <summary>
	/// Playtime boosting: the orchestrator polls the account's total playtime
	/// per game (profile games tab), idles apps that have not reached their
	/// target hours and stops idling once every target is met. In this state
	/// <see cref="AccountSpec.BoostTargets"/> drives the schedule.
	/// </summary>
	Boost = 4
}

/// <summary>
/// One app's playtime-boost target: keep the app idling until its total
/// lifetime playtime reaches <paramref name="TargetHours"/> (measured via the
/// profile games tab, so the count includes playtime earned outside Vapor).
/// </summary>
public sealed record BoostTarget(uint AppId, double TargetHours);

/// <summary>
/// How the farm loop orders the games with remaining card drops. The badges
/// page report is already ordered by drops remaining descending, so
/// <see cref="CardsDescending"/> preserves the natural most-productive-first
/// order; the alternatives trade throughput for tail-cleanup or stable
/// ordering.
/// </summary>
public enum FarmPriorityOrder
{
	/// <summary>Highest remaining card count first (the report's native order).</summary>
	CardsDescending = 0,

	/// <summary>Lowest remaining card count first — clear out nearly-done games.</summary>
	CardsAscending = 1,

	/// <summary>Stable app-id ascending order, independent of report counts.</summary>
	AppIdAscending = 2
}

/// <summary>
/// ASF-style tuning knobs for the farm loop. Null (or a policy that normalizes
/// to nothing active) keeps the default behaviour: the natural report order and
/// no per-game time budget.
/// </summary>
/// <param name="PerGameHourBudget">
/// Optional fuse against dead farming: an app the loop has been idling for at
/// least this many hours (wall clock, per session — restarting the control
/// plane restarts its clock) is marked budget-exhausted, skipped and never
/// re-queued even if the next report still shows drops for it.
/// </param>
/// <param name="PriorityOrder">Queue ordering applied after the priority apps.</param>
/// <param name="PriorityApps">
/// Explicitly prioritized app ids, in declaration order — they head the queue
/// (in list order) ahead of everything the ordering policy places.
/// </param>
public sealed record FarmPolicy(
	double? PerGameHourBudget = null,
	FarmPriorityOrder PriorityOrder = FarmPriorityOrder.CardsDescending,
	IReadOnlyList<uint>? PriorityApps = null
);

/// <summary>
/// Conservative auto-accept policy for incoming trade offers (todo §32).
/// Deliberately gifts-only: offers where the partner gives anything back are
/// never auto-accepted — value equivalence is not judged automatically (the
/// same trade-off ASF makes with AcceptGifts). Off by default and inert while
/// <paramref name="PartnerWhitelist"/> is empty: enabling the flag with an
/// empty whitelist is rejected at declaration time (double interlock).
/// </summary>
public sealed record TradePolicy(
	bool AutoAcceptGifts = false,
	IReadOnlyList<ulong>? PartnerWhitelist = null
);

/// <summary>
/// Desired-state specification for a farm account. This is metadata and intent only —
/// credentials never live in the control plane; per-agent credential stores
/// (e.g. FileCredentialStore) and the existing <c>/v1/config/account</c> settings remain
/// the sources of truth for secrets and per-account tuning.
/// </summary>
public sealed record AccountSpec(
	string AccountName,
	bool Enabled,
	AccountDesiredState DesiredState,
	IReadOnlyList<string>? IdleApps = null,
	string? Region = null,
	string? AgentId = null,
	string? Note = null,
	ConfigVersion? Version = null,
	bool MarketListingsEnabled = false,
	IReadOnlyList<BoostTarget>? BoostTargets = null,
	TradePolicy? TradePolicy = null,
	FarmPolicy? FarmPolicy = null
);
