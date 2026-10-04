namespace Vapor.Protocol;

/// <summary>
/// Execution-safety classification of an action: what happens if the same action
/// is executed twice (lease reclaim redelivery, lost-report retry, parallel
/// dispatch). The scheduler uses it to bound redispatch of actions whose repeated
/// execution can double the external side effect — see docs/consistency.md §2
/// (at-least-once ≠ exactly-once) and docs/vnext-convergence-plan.md P0-A.
/// </summary>
public enum ActionSafety
{
	/// <summary>
	/// Unclassified. Treated conservatively as bounded (like
	/// <see cref="NonIdempotent"/>) by the scheduler: no unbounded redelivery.
	/// </summary>
	Unknown = 0,

	/// <summary>Never mutates anything; safe to run any number of times.</summary>
	ReadOnly,

	/// <summary>Mutates, but repeated execution converges to the same state.</summary>
	Idempotent,

	/// <summary>
	/// Writes with real-world consequences; safe to retry only within a bounded
	/// budget (and, eventually, with an explicit external idempotency key).
	/// </summary>
	GuardedWrite,

	/// <summary>
	/// Repeated execution can double the external side effect (e.g. two trade
	/// offers, two listings). Dispatch redelivery is bounded to the minimum.
	/// </summary>
	NonIdempotent
}