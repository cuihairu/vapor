using System.Collections.Concurrent;
using Vapor.Protocol;

namespace Vapor.Agent;

/// <summary>
/// Tracks task_cancel frames that arrive before their task reaches the agent's
/// serial execution loop (ARCH-07). Cancellation is fenced by taskId + attempt:
/// a stale attempt cannot suppress a newer retry.
/// </summary>
public sealed class QueuedTaskCanceller
{
	private readonly ConcurrentDictionary<(string TaskId, int Attempt), TaskCancel> _pending = new();

	public int Count => _pending.Count;

	/// <summary>Adds a cancel request. A duplicate with the same fence is idempotent.</summary>
	public void Add(TaskCancel cancel) => _pending[(cancel.TaskId, cancel.Attempt)] = cancel;

	/// <summary>
	/// Removes and returns a matching queued cancel. Non-matching task ids or
	/// attempt numbers do not match, so stale cancels cannot affect a new attempt.
	/// </summary>
	public bool TryRemove(JobTask task, out TaskCancel cancel)
	{
		cancel = null!;
		return _pending.TryRemove((task.Id, task.Attempt), out cancel!);
	}

	/// <summary>Removes stale cancels so a closed connection cannot leak memory.</summary>
	public void Clear() => _pending.Clear();
}
