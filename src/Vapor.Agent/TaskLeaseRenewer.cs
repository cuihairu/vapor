using Vapor.Protocol;

namespace Vapor.Agent;

/// <summary>
/// Sends periodic <c>task_heartbeat</c> frames so the control plane keeps a
/// dispatched-but-not-yet-started task's lease alive while it sits in the agent's
/// bounded buffer (ARCH-06). Without this, a task queued behind a long-running
/// task would be re-claimed by <see cref="Vapor.ControlPlane.TaskSchedulerService"/>
/// once the lease expired, risking duplicate execution.
/// </summary>
public sealed class TaskLeaseRenewer
{
	private readonly Func<WSMessage, Task> _send;
	private readonly TimeSpan _interval;

	/// <param name="send">Sends a frame (typically the agent's websocket send).</param>
	/// <param name="interval">Heartbeat interval; defaults to 5s.</param>
	public TaskLeaseRenewer(Func<WSMessage, Task> send, TimeSpan? interval = null)
	{
		_send = send;
		_interval = interval ?? TimeSpan.FromSeconds(5);
	}

	/// <summary>
	/// Runs until cancellation. Sends an immediate heartbeat, then one per interval.
	/// Send failures and cancellation both end the loop quietly.
	/// </summary>
	public async Task RunAsync(JobTask task, CancellationToken cancellationToken)
	{
		try
		{
			await SendHeartbeat(task, cancellationToken).ConfigureAwait(false);
			using PeriodicTimer timer = new(_interval);
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				await SendHeartbeat(task, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch
		{
			// Best-effort: a failed send must not break the task itself.
		}
	}

	private async Task SendHeartbeat(JobTask task, CancellationToken cancellationToken)
	{
		var hb = new TaskHeartbeat(TaskId: task.Id, Attempt: task.Attempt, Ts: DateTimeOffset.UtcNow);
		await _send(new WSMessage(Type: "task_heartbeat", Hello: null, Task: null, TaskResult: null, TaskHeartbeat: hb)).ConfigureAwait(false);
	}
}
