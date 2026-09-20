using Vapor.Steam.Core;

namespace Vapor.Agent;

/// <summary>
/// An action that runs on the agent host itself rather than inside a bot session:
/// dispatched through the regular task pipeline (so retries, audits and the jobs
/// panel all apply) but executed without touching any account credentials or
/// session. Targets use the "agent:{agentId}" convention so the ControlPlane can
/// route the task to a specific machine instead of any capable agent in the region.
/// </summary>
public interface IHostAction
{
	string Name { get; }

	ActionMetadata Metadata { get; }

	Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken
	);
}
