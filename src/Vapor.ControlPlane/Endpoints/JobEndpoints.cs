using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the job routes.</summary>
internal static class JobEndpoints
{
	internal static void MapJobEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapPost("/v1/jobs", async Task<Results<Accepted<CreateJobResponse>, BadRequest<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
			HttpContext ctx,
			Config cfg,
			IJobStore store,
			IAuditStore audit,
			IEventBroker events,
			CreateJobRequest req
		) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return TypedResults.Unauthorized();
			}

			if (string.IsNullOrWhiteSpace(req.Action))
			{
				return TypedResults.BadRequest(new ErrorResponse("action is required"));
			}

			if (req.Targets is not { Count: > 0 })
			{
				return TypedResults.BadRequest(new ErrorResponse("targets is required"));
			}

			if (req.Schedule != null)
			{
				try
				{
					ScheduleClock.Validate(req.Schedule);
				}
				catch (ArgumentException ex)
				{
					return TypedResults.BadRequest(new ErrorResponse(ex.Message));
				}
			}

			var created = await store.CreateJob(req, ctx.RequestAborted);
			events.Publish(created.Job.Id, "job.created", new Dictionary<string, object?> { ["action"] = created.Job.Action, ["targets"] = created.Job.Targets.Count });
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"job.created",
				jobId: created.Job.Id,
				details: new Dictionary<string, object?>
				{
					["action"] = created.Job.Action,
					["region"] = created.Job.Region,
					["targetCount"] = created.Job.Targets.Count,
					["payload"] = req.Payload,
					["meta"] = req.Meta
				});

			return TypedResults.Accepted($"/v1/jobs/{created.Job.Id}", new CreateJobResponse(created.Job));
		})
			.WithTags("Jobs")
			.WithSummary("Create a job (dispatched to a capable agent in the job's region)")
			.Produces<CreateJobResponse>(202)
			.Produces<ErrorResponse>(400)
			.Produces(401);
		app.MapGet("/v1/jobs", async Task<Results<Ok<object>, UnauthorizedHttpResult, ProblemHttpResult>> (HttpContext ctx, Config cfg, IJobStore store, int? limit, string? account) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return TypedResults.Unauthorized();
			}

			int capped = Math.Clamp(limit ?? 50, 1, 500);
			var jobs = await store.ListJobs(capped, account, ctx.RequestAborted);
			return TypedResults.Ok<object>(new { jobs });
		})
			.WithTags("Jobs")
			.WithSummary("List recent jobs (limit query parameter, clamped to 1-500, default 50; optional account filter)")
			.Produces(200)
			.Produces(401);
		app.MapGet("/v1/jobs/{jobId}", async Task<Results<Ok<JobWithTasks>, NotFound<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
			HttpContext ctx,
			Config cfg,
			IJobStore store,
			string jobId
		) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return TypedResults.Unauthorized();
			}

			try
			{
				var jwt = await store.GetJob(jobId, ctx.RequestAborted);
				return TypedResults.Ok(jwt);
			}
			catch (NotFoundException)
			{
				return TypedResults.NotFound(new ErrorResponse("job not found"));
			}
		})
			.WithTags("Jobs")
			.WithSummary("Get one job with its tasks (per-task status, attempt, error and output)")
			.Produces<JobWithTasks>(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/jobs/{jobId}/cancel", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IJobStore store,
			IAuditStore audit,
			IEventBroker events,
			AgentRegistry agents,
			string jobId
		) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			try
			{
				var cancels = await store.CancelJob(jobId, ctx.RequestAborted);
				events.Publish(jobId, "job.canceled", null);
				await WriteAuditLog(auditLogger, audit, ctx, "job.canceled", jobId: jobId, details: new Dictionary<string, object?> { ["cancelCount"] = cancels.Count });

				if (cancels.Count > 0)
				{
					int rejected = 0;
					foreach (var agent in agents.ListConnected())
					{
						foreach (var cancel in cancels)
						{
							if (!agent.EnqueueTaskCancel(cancel))
							{
								rejected++;
							}
						}
					}
					if (rejected > 0)
					{
						events.Publish(jobId, "job.cancel_rejected", new Dictionary<string, object?> { ["rejected"] = rejected });
					}
				}

				return Results.Ok(new { ok = true });
			}
			catch (NotFoundException)
			{
				return Results.NotFound(new ErrorResponse("job not found"));
			}
		})
			.WithTags("Jobs")
			.WithSummary("Cancel all queued/running tasks of a job")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/jobs/secret-payloads/purge", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IJobStore store,
			SecretPayloadPurgeRequest? req
		) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			// Preview is the default; a destructive scrub requires an explicit confirm.
			bool dryRun = req is null || req.DryRun != false;
			if (!dryRun && req?.Confirm != true)
			{
				return Results.BadRequest(new ErrorResponse("set confirm=true to scrub historical secret payloads"));
			}

			try
			{
				(int jobs, int tasks) = await store.PurgeSecretPayloadsAsync(dryRun, req?.BackupPath, ctx.RequestAborted);
				return Results.Ok(new { dryRun, jobs, tasks, backupPath = req?.BackupPath });
			}
			catch (InvalidOperationException ex)
			{
				return Results.BadRequest(new ErrorResponse(ex.Message));
			}
		})
			.WithTags("Jobs")
			.WithSummary("Preview or scrub login credentials persisted in historical job/task payloads (dry-run default; confirm=true to write, optional backupPath snapshots first)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces(401);
		app.MapGet("/v1/jobs/{jobId}/events", async Task (HttpContext ctx, Config cfg, IJobStore store, IEventBroker events, string jobId) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}

			try
			{
				_ = await store.GetJob(jobId, ctx.RequestAborted);
			}
			catch (NotFoundException)
			{
				ctx.Response.StatusCode = StatusCodes.Status404NotFound;
				await ctx.Response.WriteAsJsonAsync(new ErrorResponse("job not found"), cancellationToken: ctx.RequestAborted);

				return;
			}

			ctx.Response.Headers.ContentType = "text/event-stream";
			ctx.Response.Headers.CacheControl = "no-cache";
			ctx.Response.Headers.Connection = "keep-alive";

			await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
			await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

			await foreach (var e in events.Subscribe(ctx.RequestAborted, jobId))
			{
				var json = JsonSerializer.Serialize(e, Vapor.Protocol.JsonDefaults.Options);
				await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {json}\n\n", ctx.RequestAborted);
				await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
			}
		})
			.WithTags("Jobs")
			.WithSummary("SSE stream of this job's lifecycle events (task.started/finished/failed/dispatch_failed, ...)")
			.Produces(200, contentType: "text/event-stream")
			.Produces(401)
			.Produces(404);
		// Global job events stream (all jobs)

		app.MapGet("/v1/jobs/events", async Task (HttpContext ctx, Config cfg, IEventBroker events) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}

			ctx.Response.Headers.ContentType = "text/event-stream";
			ctx.Response.Headers.CacheControl = "no-cache";
			ctx.Response.Headers.Connection = "keep-alive";

			await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
			await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

			await foreach (var e in events.Subscribe(ctx.RequestAborted, "*"))
			{
				var json = JsonSerializer.Serialize(e, Vapor.Protocol.JsonDefaults.Options);
				await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {json}\n\n", ctx.RequestAborted);
				await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
			}
		})
			.WithTags("Jobs")
			.WithSummary("SSE stream of lifecycle events across all jobs")
			.Produces(200, contentType: "text/event-stream")
			.Produces(401);
		app.MapGet("/v1/agent/ws", async Task (HttpContext ctx, Config cfg, AgentRegistry registry, IJobStore store, IAuditStore audit, IEventBroker events, PluginInventory pluginsInventory) =>
		{
			var auth = GetAuthorization(ctx);
			if (!Auth.TryAgentIdentity(cfg, auth, out string? boundAgentId))
			{
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}

			if (!ctx.WebSockets.IsWebSocketRequest)
			{
				ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
				await ctx.Response.WriteAsJsonAsync(new ErrorResponse("websocket required"), cancellationToken: ctx.RequestAborted);
				return;
			}

			var agentId = (string?)ctx.Request.Query["agentId"];
			var region = (string?)ctx.Request.Query["region"];
			if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(region))
			{
				ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
				await ctx.Response.WriteAsJsonAsync(new ErrorResponse("agentId and region are required"), cancellationToken: ctx.RequestAborted);
				return;
			}
			if (!string.Equals(agentId, boundAgentId, StringComparison.OrdinalIgnoreCase))
			{
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				await ctx.Response.WriteAsJsonAsync(new ErrorResponse("agentId does not match key"), cancellationToken: ctx.RequestAborted);
				return;
			}

			using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

			var first = await WebSocketJson.Receive<WSMessage>(ws, ctx.RequestAborted);
			if (!string.Equals(first.Type, "hello", StringComparison.Ordinal) || first.Hello == null || first.Hello.AgentId != agentId || first.Hello.Region != region)
			{
				await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "hello required", ctx.RequestAborted);
				return;
			}

			var agent = registry.Register(first.Hello, ws, ctx.RequestAborted);
			events.Publish(null, "agent.connected", new Dictionary<string, object?> { ["agentId"] = agent.Hello.AgentId, ["region"] = agent.Hello.Region });

			// Cleanup is spelled as a catch-all-rethrow plus a tail call rather than
			// try/finally: coverlet's sequence point on the `finally` keyword line is
			// credited by neither the normal path nor the exception path (see
			// tests/TESTING.md), while both arms of this shape are observable. The
			// semantics are identical — cleanup runs on every exit and exceptions
			// propagate unchanged.
			void DisconnectAgent()
			{
				registry.Unregister(agent.Hello.AgentId);
				events.Publish(null, "agent.disconnected", new Dictionary<string, object?> { ["agentId"] = agent.Hello.AgentId, ["region"] = agent.Hello.Region });
			}

			try
			{
				while (!ctx.RequestAborted.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open)
				{
					var msg = await WebSocketJson.Receive<WSMessage>(ws, ctx.RequestAborted);
					switch (msg)
					{
						default:
							if (string.Equals(msg.Type, "task_heartbeat", StringComparison.Ordinal) && msg.TaskHeartbeat != null)
							{
								// Unknown or stale heartbeats are reported as false, not as an error.
								_ = await store.HeartbeatTask(msg.TaskHeartbeat.TaskId, msg.TaskHeartbeat.Attempt, ctx.RequestAborted);
							}
							if (string.Equals(msg.Type, "task_result", StringComparison.Ordinal) && msg.TaskResult != null)
							{
								// Continue the distributed trace: parent the result span onto the
								// agent's task.execute span via the traceparent it returned.
								ActivityContext resultParent = default;
								bool hasResultParent = VaporTracing.TryExtractContext(msg.TraceHeaders, out resultParent);
								using Activity? resultSpan = VaporTracing.Source.StartActivity("task.result", ActivityKind.Consumer, hasResultParent ? resultParent : default);
								resultSpan?.SetTag("vapor.task_id", msg.TaskResult.TaskId);
								resultSpan?.SetStatus(msg.TaskResult.Success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, msg.TaskResult.Error);

								try
								{
									var (task, job) = await store.SetTaskResult(msg.TaskResult, ctx.RequestAborted);
									events.Publish(task.JobId, "task.finished", new Dictionary<string, object?> { ["taskId"] = task.Id, ["success"] = msg.TaskResult.Success, ["job"] = job.Status.ToString() });

									// Plugin host actions always return the agent's full installed list,
									// so the mirror is rebuilt wholesale on every plugin_* result —
									// failed installs included (they still report current state).
									if (task.Action.StartsWith("plugin_", StringComparison.Ordinal))
									{
										pluginsInventory.Update(agent.Hello.AgentId, DateTimeOffset.UtcNow, msg.TaskResult.Output);
									}

									if (IsSensitiveTaskAction(task.Action))
									{
										await WriteAuditLog(
											auditLogger,
											audit,
											ctx,
											"task.result.reported",
											accountName: task.Target,
											jobId: task.JobId,
											details: new Dictionary<string, object?>
											{
												["action"] = task.Action,
												["taskId"] = task.Id,
												["success"] = msg.TaskResult.Success
											});
									}
								}
								catch (NotFoundException)
								{
								}
							}
							break;
					}
				}
			}
			catch (Exception)
			{
				DisconnectAgent();
				throw;
			}

			DisconnectAgent();
		})
			.WithTags("Agents")
			.WithSummary("Agent WebSocket tunnel (agent token; requires agentId/region query params and a hello frame)")
			.Produces(401)
			.Produces<ErrorResponse>(400);
	}
}
