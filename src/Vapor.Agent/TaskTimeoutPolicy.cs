namespace Vapor.Agent;

/// <summary>
/// Watchdog policy over any single dispatched task on the agent. Per-action
/// <see cref="Vapor.Steam.Core.ActionMetadata.TimeoutSeconds"/> bounds well-behaved
/// actions, but an action that declares no timeout (or hangs somewhere its token is
/// never observed) would hold the agent's serial task loop forever while its
/// heartbeats keep the control-plane lease alive — no layer below the operator
/// recovers that. This policy is the hard outer bound: the task is cancelled and
/// reported to the control plane as a structured failure, and the loop moves on to
/// the next task. The default sits above the largest in-tree action bound (600 s)
/// so the more precise per-action timeouts fire first. A timeout of
/// <c>&lt;= 0</c> disables the watchdog.
/// </summary>
public sealed class TaskTimeoutPolicy
{
	public const string EnvironmentVariable = "AGENT_TASK_TIMEOUT_SECONDS";
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(900);

	public TaskTimeoutPolicy(TimeSpan timeout)
	{
		Timeout = timeout;
	}

	public TimeSpan Timeout { get; }

	public bool IsEnabled => Timeout > TimeSpan.Zero;

	public string TimeoutError => $"task timeout after {(int)Timeout.TotalSeconds}s";

	/// <summary>
	/// Classifies a cancelled execution: this is a watchdog timeout only when the
	/// execution token fired while neither the agent shutdown nor an explicit
	/// server-side task_cancel was in flight — a shutdown must stay silent (the
	/// result would be lost anyway) and a server cancel must keep its existing
	/// semantics instead of being misreported as a timeout.
	/// </summary>
	public bool IsTaskTimeout(bool executionCancelled, bool globalCancelled, bool cancelledByServer)
	{
		return IsEnabled && executionCancelled && !globalCancelled && !cancelledByServer;
	}

	public static TaskTimeoutPolicy FromEnvironment(Func<string, string?> getEnvironmentVariable)
	{
		ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

		int seconds = ParseSeconds(getEnvironmentVariable(EnvironmentVariable), 900, EnvironmentVariable);
		return new TaskTimeoutPolicy(TimeSpan.FromSeconds(seconds));
	}

	private static int ParseSeconds(string? value, int fallback, string variableName)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		if (!int.TryParse(value, out int parsed))
		{
			throw new InvalidOperationException($"{variableName} must be a valid integer.");
		}

		return parsed;
	}
}
