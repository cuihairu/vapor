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
	Farm = 3
}

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
	ConfigVersion? Version = null
);
