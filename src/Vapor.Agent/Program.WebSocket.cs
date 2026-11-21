using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Agent;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Utilities;

internal partial class Program
{
	static async Task RunOnce(
		string wsUrlBase,
		string agentId,
		string region,
		string agentApiKey,
		TaskTimeoutPolicy taskTimeoutPolicy,
		IActionRegistry actionRegistry,
		ISessionManager sessionManager,
		IReadOnlyDictionary<string, IHostAction> hostActions,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		Uri uri = AgentWebSocketUri.Build(wsUrlBase, agentId, region);

		using ClientWebSocket ws = new();
		ws.Options.SetRequestHeader("Authorization", $"Bearer {agentApiKey}");

		Console.WriteLine($"connecting: {SensitiveDataRedactor.Redact(uri.ToString())}");
		await ws.ConnectAsync(uri, cancellationToken);

		using SemaphoreSlim sendGate = new(1, 1);
		int taskBufferCapacity = AgentTaskBuffer.ResolveCapacity(
			Environment.GetEnvironmentVariable("AGENT_TASK_BUFFER_CAPACITY"));
		using var tasks = new AgentTaskDispatcher(taskBufferCapacity);

		var executionGate = new object();
		CancellationTokenSource? currentTaskCts = null;
		string? currentTaskId = null;
		int currentAttempt = 0;
		bool currentTaskCancelledByServer = false;
		bool currentTaskCancelledByDisconnect = false;
		var pendingLeaseCts = new ConcurrentDictionary<(string TaskId, int Attempt), CancellationTokenSource>();
		var queuedCancels = new QueuedTaskCanceller();

		var capabilities = actionRegistry.ListNames().Concat(hostActions.Keys)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToDictionary(name => name, _ => true, StringComparer.OrdinalIgnoreCase);
		var hello = new AgentHello(agentId, region, capabilities, null);
		var helloMeta = new Dictionary<string, string>
		{
			["task_buffer_capacity"] = taskBufferCapacity.ToString()
		};
		hello = hello with { Meta = helloMeta };
		await SendLocked(ws, sendGate, new WSMessage("hello", hello, null, null), cancellationToken);

		var receiver = Task.Run(async () =>
		{
			try
			{
				while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
				{
					WSMessage msg = await Receive<WSMessage>(ws, cancellationToken);
					if (string.Equals(msg.Type, "task", StringComparison.Ordinal) && msg.Task != null)
					{
						JobTask queuedTask = msg.Task;
						var leaseKey = (queuedTask.Id, queuedTask.Attempt);
						CancellationTokenSource leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						pendingLeaseCts[leaseKey] = leaseCts;
						try
						{
							// Keep the whole message: TraceHeaders carries the dispatch span context.
							// Enqueue never blocks: a full buffer parks the frame in the
							// overflow queue so the receive loop keeps reading control
							// frames such as task_cancel instead of stalling behind a
							// pending WriteAsync.
							tasks.Enqueue(msg);

							// The control plane starts the lease at dispatch. Renew it while
							// this task waits in the bounded buffer behind a long-running task;
							// the execution heartbeat takes over once the serial loop starts it.
							_ = new TaskLeaseRenewer(
								frame => SendLocked(ws, sendGate, frame, cancellationToken),
								TimeSpan.FromSeconds(5)).RunAsync(queuedTask, leaseCts.Token);
						}
						catch
						{
							if (pendingLeaseCts.TryRemove(leaseKey, out var removed))
							{
								removed.Dispose();
							}
							throw;
						}
						continue;
					}

					if (string.Equals(msg.Type, "task_cancel", StringComparison.Ordinal) && msg.TaskCancel != null)
					{
						// A cancel for a task still sitting in the bounded buffer is
						// remembered so the serial loop skips it before it ever runs its
						// action. Cancels that do not match any task are harmless.
						queuedCancels.Add(msg.TaskCancel);

						bool matches;
						lock (executionGate)
						{
							matches =
							  currentTaskCts != null &&
							  string.Equals(currentTaskId, msg.TaskCancel.TaskId, StringComparison.Ordinal) &&
							  currentAttempt == msg.TaskCancel.Attempt;
						}

						if (matches)
						{
							lock (executionGate)
							{
								// Remember why the token fired so the watchdog does not
								// misreport a server cancel as a timeout.
								currentTaskCancelledByServer = true;
							}

							try
							{
								currentTaskCts!.Cancel();
							}
							catch
							{
								// Intentionally swallowed: the CTS may already be disposed
								// by the execution loop after a completed task; cancel is
								// best-effort.
							}
						}
					}
				}
			}
			catch
			{
				// Receiver loop stops; outer loop will reconnect.
			}
			finally
			{
				// The websocket is gone: heartbeats and results can no longer reach the
				// control plane, so continuing the in-flight action only burns Steam
				// side effects for a result that would be dropped. Mark the reason and
				// cancel it now; the reconnecting RunOnce picks up fresh dispatches
				// instead.
				lock (executionGate)
				{
					currentTaskCancelledByDisconnect = true;
					try
					{
						currentTaskCts?.Cancel();
					}
					catch
					{
						// Intentionally swallowed: the CTS may already be disposed; the
						// socket is gone either way, so cancel is best-effort.
					}
				}
				tasks.Complete();
			}
		}, cancellationToken);

		try
		{
			while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
			{
				tasks.FlushOverflow();
				WSMessage dispatch = await tasks.Reader.ReadAsync(cancellationToken);
				JobTask task = dispatch.Task!;
				bool queuedCanceled = queuedCancels.TryRemove(task, out _);
				var executionLeaseKey = (task.Id, task.Attempt);
				if (pendingLeaseCts.TryRemove(executionLeaseKey, out var pendingLease))
				{
					pendingLease.Cancel();
					pendingLease.Dispose();
				}

				Console.WriteLine($"task received: id={task.Id} action={task.Action} target={task.Target}");

				if (queuedCanceled)
				{
					// Cancel arrived before this task started: do not run its action.
					// CancelJob already settled its terminal state in the store; a
					// task_result would be rejected as not-running and could not change it.
					Console.WriteLine($"task skipped (canceled before start): id={task.Id}");
					continue;
				}

				// CA2000 suppressed: both sources are disposed by their using declarations
				// at the end of every loop-iteration path. The analyzer flags the creations
				// only because the execution token is linked to further sources inside
				// HostActionExecutor (a pessimistic interprocedural escape model); the
				// disposals themselves are compiler-guaranteed here.
#pragma warning disable CA2000
				using var executeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, executeCts.Token);
#pragma warning restore CA2000

				lock (executionGate)
				{
					currentTaskCts = executeCts;
					currentTaskId = task.Id;
					currentAttempt = task.Attempt;
					currentTaskCancelledByServer = false;
					currentTaskCancelledByDisconnect = false;
				}

				if (taskTimeoutPolicy.IsEnabled)
				{
					// Watchdog over the whole task: the per-action timeouts fire first,
					// this is the hard outer bound that keeps the serial loop serving.
					executeCts.CancelAfter(taskTimeoutPolicy.Timeout);
				}

				// CA2025 suppressed: heartbeatTask is awaited below (after heartbeatCts.Cancel)
				// before this scope exits; it is not left orphaned.
#pragma warning disable CA2025
				var heartbeatTask = Task.Run(
					() => HeartbeatLoop(ws, sendGate, task, heartbeatCts.Token),
					heartbeatCts.Token
				);
#pragma warning restore CA2025

				bool success;
				string? error;
				IReadOnlyDictionary<string, object?>? output;
				string? replyTraceparent;
				bool timedOut = false;
				bool suppressed = false;
				using (Activity? execute = VaporAgentTracing.StartExecuteSpan(task, dispatch.TraceHeaders))
				{
					try
					{
						if (hostActions.TryGetValue(task.Action.Trim(), out var hostAction))
						{
							// Host-scoped action: runs on this machine, no bot session.
							(success, error, output) = await HostActionExecutor.ExecuteAsync(
								hostAction,
								task,
								agentId,
								logger,
								executeCts.Token
							);
						}
						else
						{
							(success, error, output) = await AgentTaskExecutor.ExecuteAsync(
								task,
								sessionManager,
								logger,
								executeCts.Token,
								actionRegistry
							);
						}

						// The session executor reports outer cancels as a plain "canceled"
						// result; reclassify the watchdog case so the control plane sees a
						// structured timeout instead.
						timedOut = taskTimeoutPolicy.IsTaskTimeout(
							executeCts.IsCancellationRequested,
							cancellationToken.IsCancellationRequested,
							currentTaskCancelledByServer);
						if (timedOut)
						{
							success = false;
							error = taskTimeoutPolicy.TimeoutError;
							output = null;
						}

						execute?.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error);
						replyTraceparent = execute?.Id;
					}
					catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !currentTaskCancelledByServer && !currentTaskCancelledByDisconnect)
					{
						// Host actions rethrow caller cancels. Anything that is neither an
						// agent shutdown nor a server task_cancel can only be the watchdog
						// (the only other canceller of executeCts); report it as a
						// structured failure instead of letting it drop the connection
						// (which would lose the result and force a reconnect).
						timedOut = true;
						success = false;
						error = taskTimeoutPolicy.TimeoutError;
						output = null;
						execute?.SetStatus(ActivityStatusCode.Error, error);
						replyTraceparent = execute?.Id;
					}
					finally
					{
						lock (executionGate)
						{
							// A result is suppressed only on shutdown or an explicit server
							// cancel — a watchdog firing (even just after completion) must
							// still report, so the lease is always settled with a terminal
							// state. A disconnect cancel is also suppressed: the socket is
							// gone, so the result could never be delivered.
							suppressed = cancellationToken.IsCancellationRequested
								|| (currentTaskCancelledByServer && !timedOut)
								|| currentTaskCancelledByDisconnect;
							currentTaskCts = null;
							currentTaskId = null;
							currentAttempt = 0;
							currentTaskCancelledByServer = false;
							currentTaskCancelledByDisconnect = false;
						}
					}
				}

				TaskResult result = new(
					TaskId: task.Id,
					Success: success,
					Error: error,
					Output: output,
					FinishedAt: DateTimeOffset.UtcNow,
					Attempt: task.Attempt
				);

				heartbeatCts.Cancel();
				try
				{
					await heartbeatTask;
				}
				catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
				{
				}

				if (!suppressed)
				{
					await SendLocked(ws, sendGate, new WSMessage(
						"task_result", null, null, result,
						TraceHeaders: replyTraceparent != null
							? new Dictionary<string, string> { ["traceparent"] = replyTraceparent }
							: null), cancellationToken);
				}
			}
		}
		finally
		{
			foreach (var lease in pendingLeaseCts.Values)
			{
				lease.Cancel();
				lease.Dispose();
			}
			pendingLeaseCts.Clear();
			queuedCancels.Clear();
			try
			{
				await receiver;
			}
			catch
			{
				// Intentionally swallowed: the receiver loop already handles its own
				// exceptions and exits; the outer RunOnce reconnect loop owns recovery.
			}
		}
	}

	static async Task<T> Receive<T>(ClientWebSocket ws, CancellationToken cancellationToken)
	{
		ArraySegment<byte> chunk = new(new byte[16 * 1024]);
		using var ms = new MemoryStream();

		while (true)
		{
			WebSocketReceiveResult r = await ws.ReceiveAsync(chunk, cancellationToken);
			if (r.MessageType == WebSocketMessageType.Close)
			{
				throw new IOException("websocket closed");
			}

			ms.Write(chunk.Array!, chunk.Offset, r.Count);
			if (r.EndOfMessage)
			{
				break;
			}
		}

		return JsonSerializer.Deserialize<T>(ms.ToArray(), JsonDefaults.Options) ?? throw new InvalidOperationException("invalid json");
	}

	static async Task Send<T>(ClientWebSocket ws, T value, CancellationToken cancellationToken)
	{
		byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
		await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
	}

	static async Task SendLocked<T>(ClientWebSocket ws, SemaphoreSlim sendGate, T value, CancellationToken cancellationToken)
	{
		await sendGate.WaitAsync(cancellationToken);
		try
		{
			await Send(ws, value, cancellationToken);
		}
		finally
		{
			sendGate.Release();
		}
	}

	static async Task HeartbeatLoop(ClientWebSocket ws, SemaphoreSlim sendGate, JobTask task, CancellationToken cancellationToken)
	{
		static async Task SendHeartbeat(ClientWebSocket ws, SemaphoreSlim sendGate, JobTask task, CancellationToken cancellationToken)
		{
			if (ws.State != WebSocketState.Open)
			{
				return;
			}

			var hb = new TaskHeartbeat(TaskId: task.Id, Attempt: task.Attempt, Ts: DateTimeOffset.UtcNow);
			var msg = new WSMessage(Type: "task_heartbeat", Hello: null, Task: null, TaskResult: null, TaskHeartbeat: hb);
			await SendLocked(ws, sendGate, msg, cancellationToken);
		}

		try
		{
			await SendHeartbeat(ws, sendGate, task, cancellationToken);

			using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				await SendHeartbeat(ws, sendGate, task, cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch
		{
			// Best-effort: if the websocket is disconnected or errors, don't fail the task itself.
		}
	}
}
