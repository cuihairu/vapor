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
	Idle = 2
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
