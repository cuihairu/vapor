using FsCheck;
using FsCheck.Xunit;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Property-based invariants (FsCheck) over the system-status pure helpers —
/// the session-state vocabulary, desired-vs-actual consistency and the
/// overall-verdict derivation. The example tests in SystemStatusApiTests pin
/// known words and each verdict arm; these sweep the free-text domain agent
/// reports actually arrive from (arbitrary casing, whitespace, unknown words).
/// </summary>
public sealed class SystemStatusPropertyTests
{
	private static bool Disconnected(string s) => SystemStatusService.IsDisconnectedState(s);

	private static bool Live(string s) => SystemStatusService.IsLiveSessionState(s);

	private static bool Consistent(AccountDesiredState desired, string? s) =>
		SystemStatusService.IsSessionConsistent(desired, s);

	private static OverallHealth Overall(bool dbAvailable, bool lastPassFailed, int connectedAgents, int totalAccounts, int pendingChallenges) =>
		SystemStatusService.DeriveOverall(dbAvailable, lastPassFailed, connectedAgents, totalAccounts, pendingChallenges);

	// ---------- session-state vocabulary ----------

	[Property]
	public Property DisconnectedState_IsCaseInvariant(NonNull<string> raw)
	{
		string s = raw.Get;
		return (Disconnected(s) == Disconnected(s.ToUpperInvariant())
			&& Disconnected(s) == Disconnected(s.ToLowerInvariant())).ToProperty();
	}

	[Property]
	public Property LiveState_IsCaseInvariant(NonNull<string> raw)
	{
		string s = raw.Get;
		return (Live(s) == Live(s.ToUpperInvariant())
			&& Live(s) == Live(s.ToLowerInvariant())).ToProperty();
	}

	[Property]
	public Property UnknownState_IsNeverEvidenceInAnyCasing(NonNull<string> raw)
	{
		string s = raw.Get;
		if (!string.Equals(s, "unknown", StringComparison.OrdinalIgnoreCase))
		{
			return true.ToProperty();
		}

		// The agent-side default is evidence neither of a live session nor of a
		// disconnected one, in every casing the free-text field can arrive in.
		return (!Live(s) && !Disconnected(s)).ToProperty();
	}

	[Property]
	public Property Trichotomy_OverNonEmptyStates_IsExclusiveAndExhaustive(NonNull<string> raw)
	{
		string s = raw.Get;
		bool disconnected = Disconnected(s);
		bool live = Live(s);

		// Mutually exclusive evidence…
		bool exclusive = !disconnected || !live;
		// …and exhaustive over the complement: a state that is neither live nor
		// disconnected evidence must be blank or the "unknown" default — the
		// vocabulary treats every other word as positive live evidence.
		bool exhaustive = disconnected || live || string.IsNullOrWhiteSpace(s)
			|| string.Equals(s, "unknown", StringComparison.OrdinalIgnoreCase);

		return (exclusive && exhaustive).ToProperty();
	}

	// ---------- desired-vs-actual consistency ----------

	[Property]
	public Property Consistency_NonOfflineDesiredStates_NeverDisagree(NonNull<string> raw, AccountDesiredState desired)
	{
		if (desired == AccountDesiredState.Offline)
		{
			return true.ToProperty();
		}

		// Online and Idle share one rule: consistency needs live-session
		// evidence, regardless of which non-offline state is desired.
		return (Consistent(desired, raw.Get) == Consistent(AccountDesiredState.Online, raw.Get)).ToProperty();
	}

	[Property]
	public Property Consistency_OfflineAndNonOffline_AreComplementary(string? sessionState)
	{
		bool offline = Consistent(AccountDesiredState.Offline, sessionState);
		bool online = Consistent(AccountDesiredState.Online, sessionState);
		string present = sessionState ?? string.Empty;

		// Never both: offline consistency demands absent/disconnected evidence,
		// non-offline demands live evidence, and no state carries both.
		bool exclusive = !offline || !online;
		// The verdicts are decided exactly by the evidence triad — the
		// unresolved middle (present-but-blank, "unknown") is consistent with
		// neither desired state.
		bool hasEvidence = sessionState is null || Disconnected(present) || Live(present);
		bool decidedByEvidence = (offline || online) == hasEvidence;

		return (exclusive && decidedByEvidence).ToProperty();
	}

	// ---------- overall verdict ----------

	[Property]
	public Property Verdict_IsClosedDomainWithAgreeingReasons(bool dbAvailable, bool lastPassFailed, int connectedAgents, int totalAccounts, int pendingChallenges)
	{
		OverallHealth verdict = Overall(dbAvailable, lastPassFailed, connectedAgents, totalAccounts, pendingChallenges);

		bool closed = verdict.Status is "healthy" or "degraded" or "unhealthy";
		bool reasonsAgree = (verdict.Status == "healthy") == (verdict.Reasons.Count == 0);
		bool dbAgrees = (verdict.Status == "unhealthy") != dbAvailable;

		return (closed && reasonsAgree && dbAgrees).ToProperty();
	}

	[Property]
	public Property Reasons_FireExactlyOnTheirTrigger(bool dbAvailable, bool lastPassFailed, int connectedAgents, int totalAccounts, int pendingChallenges)
	{
		OverallHealth verdict = Overall(dbAvailable, lastPassFailed, connectedAgents, totalAccounts, pendingChallenges);
		bool Has(string fragment) => verdict.Reasons.Any(r => r.Contains(fragment, StringComparison.Ordinal));

		bool dbExact = Has("任务库") == !dbAvailable;
		bool reconcilerExact = Has("收敛器") == lastPassFailed;
		bool agentExact = Has("agent") == (dbAvailable && totalAccounts > 0 && connectedAgents == 0);
		bool challengeExact = Has("待处理登录挑战") == (pendingChallenges > 0);

		return (dbExact && reconcilerExact && agentExact && challengeExact).ToProperty();
	}

	[Property]
	public Property ChallengeReason_CarriesExactPendingCount(NonNegativeInt pendingChallenges, bool dbAvailable)
	{
		OverallHealth verdict = Overall(dbAvailable, lastPassFailed: dbAvailable, connectedAgents: 1, totalAccounts: 1, pendingChallenges.Get);

		// The challenge reason interpolates the exact pending count, and there
		// is at most one such reason regardless of how large the count grows.
		List<string> challengeReasons = verdict.Reasons
			.Where(r => r.Contains("待处理登录挑战", StringComparison.Ordinal))
			.ToList();
		bool exactCount = challengeReasons.Count == (pendingChallenges.Get > 0 ? 1 : 0);
		bool carriesCount = challengeReasons.All(r => r.Contains($"{pendingChallenges.Get} 个账号", StringComparison.Ordinal));

		return (exactCount && carriesCount).ToProperty();
	}
}
