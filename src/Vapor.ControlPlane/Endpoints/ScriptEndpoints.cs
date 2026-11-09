using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the script routes.</summary>
internal static class ScriptEndpoints
{
	internal static void MapScriptEndpoints(this WebApplication app, ILogger auditLogger)
	{
		// ── Script repository: operator scripts stored control-plane side. Content is
		// opaque text; execution is a separate dispatch surface, the repository only
		// owns identity, metadata and bytes.

		app.MapGet("/v1/scripts", async Task<IResult> (HttpContext ctx, Config cfg, SqliteScriptStore scripts) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			List<ScriptRecord> all = await scripts.ListAsync(ctx.RequestAborted);
			return Results.Ok(new { scripts = all });
		})
			.WithTags("Scripts")
			.WithSummary("List the script repository (ordered by name)")
			.Produces(200)
			.Produces(401);
		app.MapGet("/v1/scripts/{id}", async Task<IResult> (HttpContext ctx, Config cfg, SqliteScriptStore scripts, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptRecord? script = await scripts.GetAsync(id, ctx.RequestAborted);
			return script is null ? Results.NotFound(new ErrorResponse($"script '{id}' does not exist")) : Results.Ok(script);
		})
			.WithTags("Scripts")
			.WithSummary("Fetch one script with its content")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/scripts", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteScriptStore scripts, UpsertScriptRequest request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			string? error = ValidateScriptRequest(request);
			if (error is not null)
			{
				return Results.BadRequest(new ErrorResponse(error));
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;
			ScriptRecord script = new(
				Id: Id.New(),
				Name: request.Name.Trim(),
				Description: request.Description?.Trim() ?? "",
				Language: NormalizeScriptLanguage(request.Language),
				Content: request.Content,
				CreatedAtMs: now.ToUnixTimeMilliseconds(),
				UpdatedAtMs: now.ToUnixTimeMilliseconds());
			await scripts.UpsertAsync(script, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"script_created",
				details: new Dictionary<string, object?>
				{
					["scriptId"] = script.Id,
					["name"] = script.Name,
					["language"] = script.Language,
					["bytes"] = script.Content.Length
				});
			return Results.Created($"/v1/scripts/{script.Id}", script);
		})
			.WithTags("Scripts")
			.WithSummary("Store a new script (name and content required; language defaults to shell)")
			.Produces(201)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		app.MapPut("/v1/scripts/{id}", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteScriptStore scripts, string id, UpsertScriptRequest request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptRecord? existing = await scripts.GetAsync(id, ctx.RequestAborted);
			if (existing is null)
			{
				return Results.NotFound(new ErrorResponse($"script '{id}' does not exist"));
			}

			string? error = ValidateScriptRequest(request);
			if (error is not null)
			{
				return Results.BadRequest(new ErrorResponse(error));
			}

			ScriptRecord updated = existing with
			{
				Name = request.Name.Trim(),
				Description = request.Description?.Trim() ?? "",
				Language = NormalizeScriptLanguage(request.Language),
				Content = request.Content,
				UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			};
			await scripts.UpsertAsync(updated, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"script_updated",
				details: new Dictionary<string, object?>
				{
					["scriptId"] = updated.Id,
					["name"] = updated.Name,
					["language"] = updated.Language,
					["bytes"] = updated.Content.Length
				});
			return Results.Ok(updated);
		})
			.WithTags("Scripts")
			.WithSummary("Replace a stored script (name and content required; created timestamp is preserved)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapDelete("/v1/scripts/{id}", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteScriptStore scripts, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (!await scripts.DeleteAsync(id, ctx.RequestAborted))
			{
				return Results.NotFound(new ErrorResponse($"script '{id}' does not exist"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"script_deleted",
				details: new Dictionary<string, object?> { ["scriptId"] = id });
			return Results.NoContent();
		})
			.WithTags("Scripts")
			.WithSummary("Delete a stored script")
			.Produces(204)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/scripts/{id}/execute", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteScriptStore scripts, AgentRegistry agents, IJobStore store, string id, ExecuteScriptRequest? req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptRecord? script = await scripts.GetAsync(id.Trim(), ctx.RequestAborted);
			if (script is null)
			{
				return Results.NotFound(new ErrorResponse($"script '{id}' does not exist"));
			}

			string? agentId = req?.AgentId?.Trim();
			if (string.IsNullOrWhiteSpace(agentId))
			{
				return Results.BadRequest(new ErrorResponse("agentId is required (script execution is host-scoped and targets one agent)"));
			}

			// All request-shape validation precedes the runtime connectivity check:
			// a malformed request is a client error, not a 404 for a machine that may
			// legitimately be offline.
			if (req?.TimeoutSeconds is < 1 or > ScriptExecMaxTimeoutSeconds)
			{
				return Results.BadRequest(new ErrorResponse($"timeoutSeconds must be between 1 and {ScriptExecMaxTimeoutSeconds}"));
			}

			// Host-scoped dispatch rides the agent:{id} target convention: a stored
			// script runs on exactly the machine the operator names, never on a
			// region-picked stand-in.
			ConnectedAgent? agent = agents.Get(agentId);
			if (agent is null)
			{
				return Results.NotFound(new ErrorResponse($"agent '{agentId}' is not connected; connect it and retry"));
			}

			var payload = new Dictionary<string, object?>
			{
				["scriptId"] = script.Id,
				["name"] = script.Name,
				["language"] = script.Language,
				["content"] = script.Content
			};
			if (req?.TimeoutSeconds is not null)
			{
				payload["timeoutSeconds"] = req.TimeoutSeconds;
			}

			TaskRunResult run = await AccountTaskRunner.DispatchHostActionAsync(
				store,
				"script_exec",
				agentId,
				payload,
				ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"script_exec_dispatched",
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["scriptId"] = script.Id,
					["name"] = script.Name,
					["agentId"] = agentId,
					["bytes"] = script.Content.Length,
					["outcome"] = run.Status.ToString()
				});

			if (run.Status == JobTaskStatus.Queued)
			{
				return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
			}

			if (run.Status != JobTaskStatus.Finished)
			{
				return Results.Json(new { job_id = run.JobId, error = run.Error ?? $"task ended as {run.Status}" }, statusCode: 502);
			}

			return Results.Ok(new { job_id = run.JobId, script = script.Id, result = run.Output });
		})
			.WithTags("Scripts")
			.WithSummary("Dispatch a stored script to one connected agent via the script_exec host action; 200 with output when the task finishes inside the wait window, 202 + job id while still pending, 502 when the task fails")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		// ── Script flows: ordered orchestration over the script repository. Each step
		// pairs one stored script with one target agent and a failure policy; a run
		// walks the steps in order on the control plane, persisting per-step status
		// and auditing every outcome.

		app.MapGet("/v1/script-flows", async Task<IResult> (HttpContext ctx, Config cfg, SqliteFlowStore flows) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			List<ScriptFlowRecord> all = await flows.ListFlowsAsync(ctx.RequestAborted);
			return Results.Ok(new { flows = all });
		})
			.WithTags("Scripts")
			.WithSummary("List the script flows (ordered by name)")
			.Produces(200)
			.Produces(401);
		app.MapGet("/v1/script-flows/{id}", async Task<IResult> (HttpContext ctx, Config cfg, SqliteFlowStore flows, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptFlowRecord? flow = await flows.GetFlowAsync(id, ctx.RequestAborted);
			return flow is null ? Results.NotFound(new ErrorResponse($"flow '{id}' does not exist")) : Results.Ok(flow);
		})
			.WithTags("Scripts")
			.WithSummary("Fetch one script flow with its ordered steps")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/script-flows", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteFlowStore flows, UpsertFlowRequest request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			string? error = ValidateFlowRequest(request);
			if (error is not null)
			{
				return Results.BadRequest(new ErrorResponse(error));
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;
			// Validation proved the step list non-empty; the compiler cannot see it.
			List<UpsertFlowStepRequest> flowSteps = request.Steps!;
			ScriptFlowRecord flow = new(
				Id: Id.New(),
				Name: request.Name.Trim(),
				Description: request.Description?.Trim() ?? "",
				Steps: flowSteps.Select(s => new FlowStep(
					ScriptId: s.ScriptId.Trim(),
					AgentId: s.AgentId.Trim(),
					OnFailure: NormalizeFlowOnFailure(s.OnFailure))).ToList(),
				CreatedAtMs: now.ToUnixTimeMilliseconds(),
				UpdatedAtMs: now.ToUnixTimeMilliseconds(),
				Schedule: request.Schedule,
				NextRunAtMs: ArmFlowCursor(request.Schedule, now));
			await flows.UpsertFlowAsync(flow, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"flow_created",
				details: new Dictionary<string, object?>
				{
					["flowId"] = flow.Id,
					["name"] = flow.Name,
					["steps"] = flow.Steps.Count,
					["scheduled"] = request.Schedule is not null
				});
			return Results.Created($"/v1/script-flows/{flow.Id}", flow);
		})
			.WithTags("Scripts")
			.WithSummary("Store a new script flow (name plus at least one ordered step; onFailure defaults to stop)")
			.Produces(201)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		app.MapPut("/v1/script-flows/{id}", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteFlowStore flows, string id, UpsertFlowRequest request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptFlowRecord? existing = await flows.GetFlowAsync(id, ctx.RequestAborted);
			if (existing is null)
			{
				return Results.NotFound(new ErrorResponse($"flow '{id}' does not exist"));
			}

			string? error = ValidateFlowRequest(request);
			if (error is not null)
			{
				return Results.BadRequest(new ErrorResponse(error));
			}

			// Validation proved the step list non-empty; the compiler cannot see it.
			List<UpsertFlowStepRequest> flowSteps = request.Steps!;
			DateTimeOffset updatedNow = DateTimeOffset.UtcNow;
			ScriptFlowRecord updated = existing with
			{
				Name = request.Name.Trim(),
				Description = request.Description?.Trim() ?? "",
				Steps = flowSteps.Select(s => new FlowStep(
					ScriptId: s.ScriptId.Trim(),
					AgentId: s.AgentId.Trim(),
					OnFailure: NormalizeFlowOnFailure(s.OnFailure))).ToList(),
				UpdatedAtMs = updatedNow.ToUnixTimeMilliseconds(),
				// PUT replaces the whole definition: a schedule re-arms from now, and
				// leaving it out disarms (the flow falls back to manual-only).
				Schedule = request.Schedule,
				NextRunAtMs = ArmFlowCursor(request.Schedule, updatedNow)
			};
			await flows.UpsertFlowAsync(updated, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"flow_updated",
				details: new Dictionary<string, object?>
				{
					["flowId"] = updated.Id,
					["name"] = updated.Name,
					["steps"] = updated.Steps.Count,
					["scheduled"] = request.Schedule is not null
				});
			return Results.Ok(updated);
		})
			.WithTags("Scripts")
			.WithSummary("Replace a script flow (created timestamp is preserved)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapDelete("/v1/script-flows/{id}", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteFlowStore flows, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (!await flows.DeleteFlowAsync(id, ctx.RequestAborted))
			{
				return Results.NotFound(new ErrorResponse($"flow '{id}' does not exist"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"flow_deleted",
				details: new Dictionary<string, object?> { ["flowId"] = id });
			return Results.NoContent();
		})
			.WithTags("Scripts")
			.WithSummary("Delete a script flow (past runs are kept as history)")
			.Produces(204)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/script-flows/{id}/run", async Task<IResult> (HttpContext ctx, Config cfg, SqliteFlowStore flows, ScriptFlowRunner runner, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptFlowRecord? flow = await flows.GetFlowAsync(id.Trim(), ctx.RequestAborted);
			if (flow is null)
			{
				return Results.NotFound(new ErrorResponse($"flow '{id}' does not exist"));
			}

			// The executor is the shared ScriptFlowRunner: the same walk backs the
			// recurring flow scheduler (which passes its own actor). A pre-flight
			// refusal surfaces here as the same 404 shape the inline version used.
			FlowRunStart start = await runner.RunAsync(flow, GetAuditActor(ctx), ctx.RequestAborted);
			if (start.Error is not null)
			{
				return Results.NotFound(new ErrorResponse(start.Error));
			}

			return Results.Ok(start.Run);
		})
			.WithTags("Scripts")
			.WithSummary("Run a script flow: walk the steps in order, dispatching each stored script to its target agent via script_exec; per-step status lands in the run record and the audit log")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapGet("/v1/script-flows/{id}/runs", async Task<IResult> (HttpContext ctx, Config cfg, SqliteFlowStore flows, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			List<ScriptFlowRunRecord> runs = await flows.ListRunsAsync(id, ctx.RequestAborted);
			return Results.Ok(new { runs });
		})
			.WithTags("Scripts")
			.WithSummary("List the runs of one script flow (most recent first)")
			.Produces(200)
			.Produces(401);
		app.MapGet("/v1/script-flows/runs/{runId}", async Task<IResult> (HttpContext ctx, Config cfg, SqliteFlowStore flows, string runId) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			ScriptFlowRunRecord? run = await flows.GetRunAsync(runId, ctx.RequestAborted);
			return run is null ? Results.NotFound(new ErrorResponse($"flow run '{runId}' does not exist")) : Results.Ok(run);
		})
			.WithTags("Scripts")
			.WithSummary("Fetch one flow run with its per-step statuses")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
	}
}
