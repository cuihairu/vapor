using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Utilities;

namespace Vapor.ControlPlane;

/// <summary>
/// One flow-run attempt: a started run, or the pre-flight error that kept the
/// flow from starting at all ("a run either starts with all prerequisites
/// satisfied or does not start at all"). <see cref="Error"/> carries the same
/// message the run endpoint reports as a 404.
/// </summary>
public sealed record FlowRunStart(ScriptFlowRunRecord? Run, string? Error)
{
	/// <summary>Shorthand for a run that started and finished.</summary>
	public static FlowRunStart Of(ScriptFlowRunRecord run) => new(run, null);

	/// <summary>Shorthand for a pre-flight refusal (no run was created).</summary>
	public static FlowRunStart Refused(string error) => new(null, error);
}

/// <summary>
/// Walks a stored script flow: pre-flights every step, then dispatches each
/// stored script to its target agent via the bounded <c>script_exec</c> host
/// action, strictly in order. Every step outcome is persisted as it happens
/// (so a concurrent reader sees progress) and audited; the run record carries
/// the per-step statuses and the terminal run status. Shared by the manual
/// run endpoint and the recurring flow scheduler — the only difference between
/// the two is the audit actor.
/// </summary>
public sealed class ScriptFlowRunner
{
	/// <summary>Failure policies: "stop" halts the run, "continue" keeps walking.</summary>
	public const string FlowOnFailureStop = "stop";
	public const string FlowOnFailureContinue = "continue";

	private readonly SqliteScriptStore _scripts;
	private readonly SqliteFlowStore _flows;
	private readonly IJobStore _store;
	private readonly AgentRegistry _agents;
	private readonly IAuditStore _audit;
	private readonly ILogger<ScriptFlowRunner> _logger;

	public ScriptFlowRunner(
		SqliteScriptStore scripts,
		SqliteFlowStore flows,
		IJobStore store,
		AgentRegistry agents,
		IAuditStore audit,
		ILogger<ScriptFlowRunner> logger)
	{
		_scripts = scripts;
		_flows = flows;
		_store = store;
		_agents = agents;
		_audit = audit;
		_logger = logger;
	}

	/// <summary>
	/// Runs one flow end to end. Per step the script existence check precedes the
	/// agent connectivity check, mirroring the single-script execute route; a
	/// failed pre-flight refuses the whole run before touching any machine.
	/// </summary>
	public async Task<FlowRunStart> RunAsync(ScriptFlowRecord flow, string actor, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(flow);

		// Pre-flight every step before touching any machine.
		for (int i = 0; i < flow.Steps.Count; i++)
		{
			FlowStep step = flow.Steps[i];
			if (await _scripts.GetAsync(step.ScriptId, cancellationToken).ConfigureAwait(false) is null)
			{
				return FlowRunStart.Refused($"step {i} references script '{step.ScriptId}' which does not exist");
			}

			if (_agents.Get(step.AgentId) is null)
			{
				return FlowRunStart.Refused($"step {i} targets agent '{step.AgentId}' which is not connected; connect it and retry");
			}
		}

		var started = new ScriptFlowRunRecord(
			Id: Id.New(),
			FlowId: flow.Id,
			FlowName: flow.Name,
			Status: "running",
			Steps: flow.Steps.Select((s, i) => new FlowRunStep(i, s.ScriptId, s.AgentId, "pending")).ToList(),
			StartedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		await _flows.CreateRunAsync(started, cancellationToken).ConfigureAwait(false);
		await AuditAsync(
			"flow_run_started",
			actor,
			details: new Dictionary<string, object?>
			{
				["flowId"] = flow.Id,
				["flowName"] = flow.Name,
				["runId"] = started.Id,
				["steps"] = flow.Steps.Count
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);

		// Steps walk strictly in order; each rides the same bounded script_exec
		// dispatch as the single-script execute route.
		List<FlowRunStep> steps = started.Steps.Select(s => s with { }).ToList();
		bool halt = false;       // a stop-policy failure ends the run
		bool unresolved = false; // a step never resolved inside the wait window
		bool anyFailed = false;
		for (int i = 0; i < flow.Steps.Count; i++)
		{
			FlowStep step = flow.Steps[i];
			if (halt || unresolved)
			{
				steps[i] = steps[i] with { Status = "skipped" };
				continue;
			}

			ScriptRecord script = (await _scripts.GetAsync(step.ScriptId, cancellationToken).ConfigureAwait(false))!;
			var payload = new Dictionary<string, object?>
			{
				["scriptId"] = script.Id,
				["name"] = script.Name,
				["language"] = script.Language,
				["content"] = script.Content
			};

			TaskRunResult result = await AccountTaskRunner.DispatchHostActionAsync(
				_store,
				"script_exec",
				step.AgentId,
				payload,
				cancellationToken).ConfigureAwait(false);

			if (result.Status == JobTaskStatus.Queued)
			{
				// The step job stays pollable via the jobs surface; later steps
				// must not run out of order, so the run ends unresolved.
				steps[i] = new FlowRunStep(i, step.ScriptId, step.AgentId, "pending", result.JobId);
				unresolved = true;
				await AuditAsync(
					"flow_step_pending",
					actor,
					jobId: result.JobId,
					details: new Dictionary<string, object?>
					{
						["runId"] = started.Id,
						["index"] = i,
						["scriptId"] = step.ScriptId,
						["agentId"] = step.AgentId
					},
					cancellationToken: cancellationToken).ConfigureAwait(false);
			}
			else if (result.Status == JobTaskStatus.Finished)
			{
				steps[i] = new FlowRunStep(i, step.ScriptId, step.AgentId, "succeeded", result.JobId, Output: result.Output);
				await AuditAsync(
					"flow_step_completed",
					actor,
					jobId: result.JobId,
					details: new Dictionary<string, object?>
					{
						["runId"] = started.Id,
						["index"] = i,
						["scriptId"] = step.ScriptId,
						["agentId"] = step.AgentId
					},
					cancellationToken: cancellationToken).ConfigureAwait(false);
			}
			else
			{
				steps[i] = new FlowRunStep(i, step.ScriptId, step.AgentId, "failed", result.JobId, result.Error ?? $"task ended as {result.Status}");
				anyFailed = true;
				await AuditAsync(
					"flow_step_failed",
					actor,
					jobId: result.JobId,
					details: new Dictionary<string, object?>
					{
						["runId"] = started.Id,
						["index"] = i,
						["scriptId"] = step.ScriptId,
						["agentId"] = step.AgentId,
						["error"] = steps[i].Error
					},
					cancellationToken: cancellationToken).ConfigureAwait(false);
				if (step.OnFailure == FlowOnFailureStop)
				{
					halt = true;
				}
			}

			await _flows.UpdateRunAsync(started with { Steps = steps }, cancellationToken).ConfigureAwait(false);
		}

		string status = unresolved ? "pending" : halt ? "failed" : anyFailed ? "completed_with_failures" : "completed";
		ScriptFlowRunRecord finished = started with
		{
			Status = status,
			Steps = steps,
			FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
		};
		await _flows.UpdateRunAsync(finished, cancellationToken).ConfigureAwait(false);
		await AuditAsync(
			"flow_run_finished",
			actor,
			details: new Dictionary<string, object?>
			{
				["flowId"] = flow.Id,
				["flowName"] = flow.Name,
				["runId"] = started.Id,
				["status"] = status,
				["succeeded"] = steps.Count(s => s.Status == "succeeded"),
				["failed"] = steps.Count(s => s.Status == "failed"),
				["pending"] = steps.Count(s => s.Status == "pending"),
				["skipped"] = steps.Count(s => s.Status == "skipped")
			},
			cancellationToken: cancellationToken).ConfigureAwait(false);
		return FlowRunStart.Of(finished);
	}

	/// <summary>
	/// Same audit guarantees as the API layer's WriteAuditLog: the structured log
	/// line and the persisted entry share one redacted payload, and persistence
	/// failures never fail the run they describe. There is no HTTP context here,
	/// so remote IP and trace id stay unset (scheduled runs have neither).
	/// </summary>
	private async Task AuditAsync(
		string action,
		string actor,
		IReadOnlyDictionary<string, object?> details,
		string? jobId = null,
		CancellationToken cancellationToken = default)
	{
		// details is required: every caller has one, and an optional null with
		// a coalesce fallback would be a branch no caller can reach.
		string payload = JsonSerializer.Serialize(details, JsonDefaults.Options);
		_logger.LogInformation(
			"AUDIT action={Action} actor={Actor} jobId={JobId} details={Details}",
			action,
			actor,
			SensitiveDataRedactor.SanitizeLogValue(jobId),
			SensitiveDataRedactor.Redact(payload));

		var entry = AuditStoreExtensions.CreateEntry(action, actor, jobId: jobId, details: details);
		try
		{
			await _audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Audit persistence must never block the run.
			_logger.LogError(ex, "Failed to persist audit entry {Action}", action);
		}
	}
}
