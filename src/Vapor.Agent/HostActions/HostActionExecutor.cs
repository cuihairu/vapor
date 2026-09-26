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

		// Mirror BotSession's per-action timeout semantics on the host path: the
		// declared TimeoutSeconds is enforced with a linked CTS so a stuck host
		// action surfaces as a structured "action timeout" result instead of
		// holding the dispatch lease forever. A caller cancel still wins.
		CancellationToken effectiveToken = cancellationToken;

		// CA2000 suppressed: timeoutCts is disposed in this method's finally block.
#pragma warning disable CA2000
		CancellationTokenSource? timeoutCts = null;
		if (action.Metadata.TimeoutSeconds is > 0)
		{
			timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveToken);
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(action.Metadata.TimeoutSeconds.Value));
			effectiveToken = timeoutCts.Token;
		}
#pragma warning restore CA2000

		try
		{
			var result = await action.ExecuteAsync(task.Payload ?? new Dictionary<string, object?>(), effectiveToken).ConfigureAwait(false);
			return (result.Success, result.Error, result.Output);
		}
		catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
		{
			// timeoutCts is linked to the caller token, so a caller cancel cancels
			// it too; only report a timeout when the caller itself is still running.
			return (false, "action timeout", null);
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
		finally
		{
			timeoutCts?.Dispose();
		}
	}
}
