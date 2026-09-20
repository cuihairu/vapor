using Microsoft.Extensions.Logging;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Utilities;

namespace Vapor.Agent;

/// <summary>
/// Runs host-scoped actions (plugin install/uninstall/list) that arrive as regular
/// tasks but must not touch a bot session. The "agent:{id}" target convention is
/// enforced here as defense in depth: the ControlPlane scheduler routes these tasks
/// to the named agent, and a misrouted delivery fails loudly instead of mutating
/// the wrong machine's plugin directory.
/// </summary>
public static class HostActionExecutor
{
	public const string TargetPrefix = "agent:";

	public static async Task<(bool Success, string? Error, IReadOnlyDictionary<string, object?>? Output)> ExecuteAsync(
		IHostAction action,
		JobTask task,
		string agentId,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		string expected = TargetPrefix + agentId;
		if (!string.Equals(task.Target, expected, StringComparison.Ordinal))
		{
			logger.LogWarning(
				"Host action {ActionName} arrived with target {Target} on agent {AgentId}; refusing",
				action.Name, SensitiveDataRedactor.Redact(task.Target), agentId);
			return (false, $"host action {action.Name} is targeted at '{task.Target}', not this agent ('{expected}')", null);
		}

		try
		{
			var result = await action.ExecuteAsync(task.Payload ?? new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
			return (result.Success, result.Error, result.Output);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Host action {ActionName} failed unexpectedly", action.Name);
			return (false, ex.Message, null);
		}
	}
}
