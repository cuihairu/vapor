using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi.Models;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Utilities;

var builder = WebApplication.CreateBuilder(args);
VaporCryptoHelper.ConfigureFromEnvironment(Environment.GetEnvironmentVariable);
VaporCryptoHelper.EnsureSafeForEnvironment(Environment.GetEnvironmentVariable);

builder.Services.AddSingleton<Config>(_ => Config.LoadFromEnvironment());
builder.Services.AddSingleton<IEventBroker, EventBroker>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<AuthChallengeTracker>();
builder.Services.AddSingleton<ConfigStore>();

builder.Services.AddSingleton<IJobStore>(sp => {
	var cfg = sp.GetRequiredService<Config>();
	return new SqliteJobStore(cfg.DbPath);
});

builder.Services.AddSingleton<IAuditStore>(sp => {
	var cfg = sp.GetRequiredService<Config>();
	return new SqliteAuditStore(cfg.AuditDbPath);
});

builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddHostedService<TaskSchedulerService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => {
	options.SwaggerDoc("v1", new OpenApiInfo { Title = "Vapor Control Plane API", Version = "v1" });

	options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme {
		Type = SecuritySchemeType.Http,
		Scheme = "bearer",
		BearerFormat = "token",
		Description = "Send `Authorization: Bearer <token>`"
	});

	options.AddSecurityRequirement(new OpenApiSecurityRequirement {
		{
			new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "bearer" } },
			Array.Empty<string>()
		}
	});
});

builder.Services.ConfigureHttpJsonOptions(options => {
	options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
	options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
	options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
var auditLogger = app.Logger;

app.UseStaticFiles();
app.UseWebSockets();

var cfg = app.Services.GetRequiredService<Config>();
if (cfg.EnableSwagger) {
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.MapGet("/healthz", () => Results.Json(new { ok = true }))
	.WithTags("System")
	.WithSummary("Liveness probe (public, unauthenticated)")
	.Produces(200);

// Admin UI redirect
app.MapGet("/", () => Results.Redirect("/admin.html"))
	.WithTags("System")
	.WithSummary("Redirect to the admin UI")
	.Produces(302);

app.MapGet("/v1/agents", (HttpContext ctx, Config cfg, AgentRegistry agents) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	var list = agents.List();
	return Results.Ok(new { agents = list });
})
	.WithTags("Agents")
	.WithSummary("List registered agents (including offline ones)")
	.Produces(200)
	.Produces<ErrorResponse>(401);

app.MapGet("/v1/config", (HttpContext ctx, Config cfg, ConfigStore configStore) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	return Results.Ok(new {
		global = configStore.GetGlobal(),
		accounts = configStore.ListAccounts()
	});
})
	.WithTags("Config")
	.WithSummary("Get global and per-account configuration")
	.Produces(200)
	.Produces<ErrorResponse>(401);

app.MapPut("/v1/config/global", async (HttpContext ctx, Config cfg, IAuditStore audit, ConfigStore configStore, PutGlobalConfigRequest req) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	var updated = configStore.SetGlobal(req.Settings, req.UpdatedBy);
	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"config.global.updated",
		details: new Dictionary<string, object?>
		{
			["updatedBy"] = req.UpdatedBy,
			["settings"] = req.Settings
		});
	return Results.Ok(updated);
})
	.WithTags("Config")
	.WithSummary("Replace global settings")
	.Produces(200)
	.Produces<ErrorResponse>(401);

app.MapPut("/v1/config/account/{name}", async (HttpContext ctx, Config cfg, IAuditStore audit, ConfigStore configStore, string name, PutAccountConfigRequest req) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(name)) {
		return Results.BadRequest(new ErrorResponse("account name is required"));
	}

	var updated = configStore.SetAccount(name, req.Enabled, req.Region, req.Labels, req.Settings, req.UpdatedBy);
	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"config.account.updated",
		accountName: name,
		details: new Dictionary<string, object?>
		{
			["updatedBy"] = req.UpdatedBy,
			["enabled"] = req.Enabled,
			["region"] = req.Region,
			["labels"] = req.Labels,
			["settings"] = req.Settings
		});
	return Results.Ok(updated);
})
	.WithTags("Config")
	.WithSummary("Replace per-account settings (enabled, region, labels, settings)")
	.Produces(200)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(401);

app.MapPost("/v1/jobs", async Task<Results<Accepted<CreateJobResponse>, BadRequest<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
	HttpContext ctx,
	Config cfg,
	IJobStore store,
	IAuditStore audit,
	IEventBroker events,
	CreateJobRequest req
) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return TypedResults.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(req.Action)) {
		return TypedResults.BadRequest(new ErrorResponse("action is required"));
	}

	if (req.Targets is not { Count: > 0 }) {
		return TypedResults.BadRequest(new ErrorResponse("targets is required"));
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

app.MapGet("/v1/jobs", async Task<Results<Ok<object>, UnauthorizedHttpResult, ProblemHttpResult>> (HttpContext ctx, Config cfg, IJobStore store, int? limit) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return TypedResults.Unauthorized();
	}

	int capped = Math.Clamp(limit ?? 50, 1, 500);
	var jobs = await store.ListJobs(capped, ctx.RequestAborted);
	return TypedResults.Ok<object>(new { jobs });
})
	.WithTags("Jobs")
	.WithSummary("List recent jobs (limit query parameter, clamped to 1-500, default 50)")
	.Produces(200)
	.Produces(401);

app.MapGet("/v1/jobs/{jobId}", async Task<Results<Ok<JobWithTasks>, NotFound<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
	HttpContext ctx,
	Config cfg,
	IJobStore store,
	string jobId
) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return TypedResults.Unauthorized();
	}

	try {
		var jwt = await store.GetJob(jobId, ctx.RequestAborted);
		return TypedResults.Ok(jwt);
	} catch (NotFoundException) {
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
) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	try {
		var cancels = await store.CancelJob(jobId, ctx.RequestAborted);
		events.Publish(jobId, "job.canceled", null);
		await WriteAuditLog(auditLogger, audit, ctx, "job.canceled", jobId: jobId, details: new Dictionary<string, object?> { ["cancelCount"] = cancels.Count });

		if (cancels.Count > 0) {
			foreach (var agent in agents.ListConnected()) {
				foreach (var cancel in cancels) {
					agent.EnqueueTaskCancel(cancel);
				}
			}
		}

		return Results.Ok(new { ok = true });
	} catch (NotFoundException) {
		return Results.NotFound(new ErrorResponse("job not found"));
	}
})
	.WithTags("Jobs")
	.WithSummary("Cancel all queued/running tasks of a job")
	.Produces(200)
	.Produces<ErrorResponse>(404)
	.Produces(401);

app.MapGet("/v1/jobs/{jobId}/events", async Task (HttpContext ctx, Config cfg, IJobStore store, IEventBroker events, string jobId) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	try {
		_ = await store.GetJob(jobId, ctx.RequestAborted);
	} catch (NotFoundException) {
		ctx.Response.StatusCode = StatusCodes.Status404NotFound;
		await ctx.Response.WriteAsJsonAsync(new ErrorResponse("job not found"), cancellationToken: ctx.RequestAborted);

		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.Subscribe(ctx.RequestAborted, jobId)) {
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
app.MapGet("/v1/jobs/events", async Task (HttpContext ctx, Config cfg, IEventBroker events) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.Subscribe(ctx.RequestAborted, "*")) {
		var json = JsonSerializer.Serialize(e, Vapor.Protocol.JsonDefaults.Options);
		await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {json}\n\n", ctx.RequestAborted);
		await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
	}
})
	.WithTags("Jobs")
	.WithSummary("SSE stream of lifecycle events across all jobs")
	.Produces(200, contentType: "text/event-stream")
	.Produces(401);

// Session events streaming endpoint
app.MapGet("/v1/sessions/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.SubscribeSessions(ctx.RequestAborted, accountName)) {
		var json = JsonSerializer.Serialize(e, Vapor.Protocol.JsonDefaults.Options);
		await ctx.Response.WriteAsync($"event: session.{e.EventType}\ndata: {json}\n\n", ctx.RequestAborted);
		await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
	}
})
	.WithTags("Sessions")
	.WithSummary("SSE stream of session events (optionally filtered by accountName)")
	.Produces(200, contentType: "text/event-stream")
	.Produces(401);

// Auth challenge events streaming endpoint
app.MapGet("/v1/auth/challenges/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) => {
	var auth = GetAuthorization(ctx);
	var isAdmin = Auth.TryAdmin(cfg, auth, out _);
	var isAgent = !isAdmin && Auth.TryAgent(cfg, auth, out _);
	if (!isAdmin && !isAgent) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.SubscribeAuthChallenges(ctx.RequestAborted, accountName)) {
		if (isAgent && !e.ChallengeType.StartsWith("code_provided_", StringComparison.Ordinal)) {
			continue;
		}
		var payload = isAdmin && e.ChallengeType.StartsWith("code_provided_", StringComparison.Ordinal)
			? e with { Code = null }
			: e;
		var json = JsonSerializer.Serialize(payload, Vapor.Protocol.JsonDefaults.Options);
		await ctx.Response.WriteAsync($"event: auth.{e.ChallengeType}\ndata: {json}\n\n", ctx.RequestAborted);
		await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
	}
})
	.WithTags("Auth")
	.WithSummary("SSE stream of login challenges (admin sees all; agents only code_provided_* events)")
	.Produces(200, contentType: "text/event-stream")
	.Produces(401);

// List pending auth challenges (useful for UI refresh)
app.MapGet("/v1/auth/challenges", (HttpContext ctx, Config cfg, AuthChallengeTracker tracker) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	return Results.Ok(new { challenges = tracker.List() });
})
	.WithTags("Auth")
	.WithSummary("List pending login challenges (2FA / auth code prompts)")
	.Produces(200)
	.Produces<ErrorResponse>(401);

// Submit auth code endpoint
app.MapPost("/v1/auth/challenges/{accountName}/code", async (
	HttpContext ctx,
	Config cfg,
	IAuditStore audit,
	IEventBroker events,
	AuthChallengeTracker tracker,
	string accountName,
	Dictionary<string, string?> body
) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	if (!body.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) {
		return Results.BadRequest(new ErrorResponse("code is required"));
	}

	code = code.Trim();

	if (!body.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type)) {
		type = "email"; // Default to email guard
	} else {
		type = type.Trim().ToLowerInvariant();
	}

	type = type switch {
		"email" => "email",
		"totp" => "totp",
		"2fa" => "2fa",
		_ => null
	};

	if (type == null) {
		return Results.BadRequest(new ErrorResponse("type must be one of: email, totp, 2fa"));
	}

	tracker.Clear(accountName);

	// Publish the auth code response event
	// The agent will listen for this event and use the code to continue login
	events.PublishAuthChallenge(accountName, $"code_provided_{type}", $"Auth code provided for {type}", code);
	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"auth.code.submitted",
		accountName: accountName,
		details: new Dictionary<string, object?>
		{
			["type"] = type,
			["code"] = code
		});

	return Results.Ok(new { ok = true, accountName, type });
})
	.WithTags("Auth")
	.WithSummary("Submit a login challenge code (type: email, totp, 2fa)")
	.Produces(200)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(401);

// List active agents with their sessions
app.MapGet("/v1/agents/status", (HttpContext ctx, Config cfg, AgentRegistry agents) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	var list = agents.ListConnected().Select(a => new {
		id = a.Hello.AgentId,
		region = a.Hello.Region,
		capabilities = a.Hello.Capabilities,
		connected = true,
		connectedAt = a.ConnectedAt
	});

	return Results.Ok(new { agents = list });
})
	.WithTags("Agents")
	.WithSummary("List currently connected agents with region and capabilities")
	.Produces(200)
	.Produces<ErrorResponse>(401);

// Receive session events from agents
app.MapPost("/v1/sessions/events", async (
	HttpContext ctx,
	Config cfg,
	IAuditStore audit,
	IEventBroker events,
	SessionTracker sessions,
	AuthChallengeTracker challenges,
	SessionEventRequest req
) => {
	// Allow both admin and agent tokens for this endpoint
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _) &&
	    !Auth.TryAgent(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(req.AccountName)) {
		return Results.BadRequest(new ErrorResponse("accountName is required"));
	}

	var normalizedType = NormalizeSessionEventType(req.EventType);
	var state = string.IsNullOrWhiteSpace(req.State) ? "unknown" : req.State;

	// Publish the session event
	events.PublishSession(req.AccountName, normalizedType, state, req.Message);
	sessions.Update(req.AccountName, normalizedType, state, req.Message);
	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"session.event.received",
		accountName: req.AccountName,
		details: new Dictionary<string, object?>
		{
			["eventType"] = normalizedType,
			["state"] = state,
			["message"] = req.Message
		});

	// Persist login-relevant transitions as dedicated audit records.
	if (IsLoginAuditEvent(normalizedType, state)) {
		await WriteAuditLog(
			auditLogger,
			audit,
			ctx,
			"session.login",
			accountName: req.AccountName,
			details: new Dictionary<string, object?>
			{
				["eventType"] = normalizedType,
				["state"] = state
			});
	}

	// Publish auth challenge events when sessions require user input
	if (IsAuthChallengeRequired(normalizedType, state)) {
		var challengeType =
			string.Equals(normalizedType, "2fa_required", StringComparison.Ordinal) ||
			string.Equals(state, "ConnectingWait2FA", StringComparison.Ordinal)
				? "2fa_required"
				: "auth_code_required";
		var evt = new Vapor.ControlPlane.AuthChallengeEvent(
			Id: Guid.NewGuid().ToString("N"),
			AccountName: req.AccountName,
			ChallengeType: challengeType,
			Message: req.Message,
			Code: null,
			Timestamp: DateTimeOffset.UtcNow,
			JobId: null
		);
		challenges.Upsert(evt);
		events.PublishAuthChallenge(req.AccountName, challengeType, req.Message);
	} else {
		// Clear any stale "needs code/2FA" prompt once the session progresses.
		challenges.Clear(req.AccountName);
	}

	return Results.Ok(new { ok = true });
})
	.WithTags("Sessions")
	.WithSummary("Report a session event (admin or agent token; feeds SSE, tracker and auth challenges)")
	.Produces(200)
	.Produces<ErrorResponse>(400)
	.Produces(401);

// List active sessions
app.MapGet("/v1/sessions", (HttpContext ctx, Config cfg, SessionTracker sessions) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	return Results.Ok(new { sessions = sessions.List() });
})
	.WithTags("Sessions")
	.WithSummary("List known sessions and their latest state")
	.Produces(200)
	.Produces<ErrorResponse>(401);

// Query persisted audit logs
app.MapGet("/v1/audit/logs", async Task<IResult> (
	HttpContext ctx,
	Config cfg,
	IAuditStore audit,
	int? limit,
	int? offset,
	string? action,
	string? account,
	string? jobId,
	long? fromMs,
	long? toMs
) => {
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _)) {
		return Results.Unauthorized();
	}

	if (fromMs.HasValue && toMs.HasValue && fromMs.Value > toMs.Value) {
		return Results.BadRequest(new ErrorResponse("fromMs must not be greater than toMs"));
	}

	var query = new AuditQuery(
		Action: action,
		AccountName: account,
		JobId: jobId,
		From: fromMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(fromMs.Value) : null,
		To: toMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(toMs.Value) : null,
		Limit: Math.Clamp(limit ?? 100, 1, 500),
		Offset: Math.Max(offset ?? 0, 0)
	);

	IReadOnlyList<AuditEntry> logs = await audit.QueryAsync(query, ctx.RequestAborted);
	int total = await audit.CountAsync(query, ctx.RequestAborted);

	return Results.Ok(new { logs, total, limit = query.Limit, offset = query.Offset });
})
	.WithTags("Audit")
	.WithSummary("Query persisted audit logs (filter by action, account, jobId, time range; limit 1-500)")
	.Produces(200)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(401);

app.MapGet("/v1/agent/ws", async Task (HttpContext ctx, Config cfg, AgentRegistry registry, IJobStore store, IAuditStore audit, IEventBroker events) => {
	if (!Auth.TryAgent(cfg, GetAuthorization(ctx), out _)) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	if (!ctx.WebSockets.IsWebSocketRequest) {
		ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
		await ctx.Response.WriteAsJsonAsync(new ErrorResponse("websocket required"), cancellationToken: ctx.RequestAborted);
		return;
	}

	var agentId = (string?) ctx.Request.Query["agentId"];
	var region = (string?) ctx.Request.Query["region"];
	if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(region)) {
		ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
		await ctx.Response.WriteAsJsonAsync(new ErrorResponse("agentId and region are required"), cancellationToken: ctx.RequestAborted);
		return;
	}

	using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

	var first = await WebSocketJson.Receive<WSMessage>(ws, ctx.RequestAborted);
	if (!string.Equals(first.Type, "hello", StringComparison.Ordinal) || first.Hello == null || first.Hello.AgentId != agentId || first.Hello.Region != region) {
		await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "hello required", ctx.RequestAborted);
		return;
	}

	var agent = registry.Register(first.Hello, ws, ctx.RequestAborted);
	events.Publish(null, "agent.connected", new Dictionary<string, object?> { ["agentId"] = agent.Hello.AgentId, ["region"] = agent.Hello.Region });

	try {
		while (!ctx.RequestAborted.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open) {
			var msg = await WebSocketJson.Receive<WSMessage>(ws, ctx.RequestAborted);
			switch (msg) {
				default:
					if (string.Equals(msg.Type, "task_heartbeat", StringComparison.Ordinal) && msg.TaskHeartbeat != null) {
						try {
							_ = await store.HeartbeatTask(msg.TaskHeartbeat.TaskId, msg.TaskHeartbeat.Attempt, ctx.RequestAborted);
						} catch (NotFoundException) {
						}
					}
				if (string.Equals(msg.Type, "task_result", StringComparison.Ordinal) && msg.TaskResult != null) {
					try {
						var (task, job) = await store.SetTaskResult(msg.TaskResult, ctx.RequestAborted);
						events.Publish(task.JobId, "task.finished", new Dictionary<string, object?> { ["taskId"] = task.Id, ["success"] = msg.TaskResult.Success, ["job"] = job.Status.ToString() });

						if (IsSensitiveTaskAction(task.Action)) {
							await WriteAuditLog(
								auditLogger,
								audit,
								ctx,
								"task.result.reported",
								accountName: task.Target,
								jobId: task.JobId,
								details: new Dictionary<string, object?> {
									["action"] = task.Action,
									["taskId"] = task.Id,
									["success"] = msg.TaskResult.Success
								});
						}
					} catch (NotFoundException) {
					}
				}
					break;
			}
		}
	} finally {
		registry.Unregister(agent.Hello.AgentId);
		events.Publish(null, "agent.disconnected", new Dictionary<string, object?> { ["agentId"] = agent.Hello.AgentId, ["region"] = agent.Hello.Region });
	}
})
	.WithTags("Agents")
	.WithSummary("Agent WebSocket tunnel (agent token; requires agentId/region query params and a hello frame)")
	.Produces(401)
	.Produces<ErrorResponse>(400);

app.Run();

static StringValues GetAuthorization(HttpContext ctx) {
	if (ctx.Request.Headers.TryGetValue("Authorization", out var header) && !StringValues.IsNullOrEmpty(header)) {
		return header;
	}

	if (ctx.Request.Query.TryGetValue("authorization", out var token) && token.Count > 0 && !string.IsNullOrWhiteSpace(token[0])) {
		var rawValue = token[0];
		if (string.IsNullOrWhiteSpace(rawValue)) {
			return StringValues.Empty;
		}

		var raw = rawValue.Trim();
		if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
			return new StringValues(raw);
		}

		return new StringValues($"Bearer {raw}");
	}

	return StringValues.Empty;
}

static string ToSnakeCase(string value) {
	if (string.IsNullOrWhiteSpace(value)) {
		return string.Empty;
	}

	var s = value.Trim();
	var sb = new System.Text.StringBuilder(s.Length + 8);
	for (int i = 0; i < s.Length; i++) {
		char c = s[i];
		if (c == '-' || c == ' ') {
			sb.Append('_');
			continue;
		}

		if (char.IsUpper(c)) {
			if (i > 0 && sb.Length > 0 && sb[sb.Length - 1] != '_') {
				sb.Append('_');
			}
			sb.Append(char.ToLowerInvariant(c));
		} else {
			sb.Append(char.ToLowerInvariant(c));
		}
	}
	return sb.ToString();
}

static string NormalizeSessionEventType(string? eventType) {
	if (string.IsNullOrWhiteSpace(eventType)) {
		return "state_changed";
	}

	var v = eventType.Trim();
	bool hasUpper = false;
	for (int i = 0; i < v.Length; i++) {
		if (char.IsUpper(v[i])) {
			hasUpper = true;
			break;
		}
	}
	return v switch {
		"StateChanged" => "state_changed",
		"Connected" => "connected",
		"Disconnected" => "disconnected",
		"AuthCodeNeeded" => "auth_code_required",
		"TwoFactorCodeNeeded" => "2fa_required",
		_ => hasUpper ? ToSnakeCase(v) : v
	};
}

static bool IsAuthChallengeRequired(string normalizedEventType, string state) {
	if (string.Equals(normalizedEventType, "auth_code_required", StringComparison.Ordinal) ||
	    string.Equals(normalizedEventType, "2fa_required", StringComparison.Ordinal)) {
		return true;
	}

	return string.Equals(state, "ConnectingWaitAuthCode", StringComparison.Ordinal) ||
	       string.Equals(state, "ConnectingWait2FA", StringComparison.Ordinal);
}

static async Task WriteAuditLog(
	ILogger logger,
	IAuditStore auditStore,
	HttpContext ctx,
	string action,
	string? accountName = null,
	string? jobId = null,
	IReadOnlyDictionary<string, object?>? details = null)
{
	var payload = JsonSerializer.Serialize(details ?? new Dictionary<string, object?>(), Vapor.Protocol.JsonDefaults.Options);
	logger.LogInformation(
		"AUDIT action={Action} actor={Actor} ip={RemoteIp} account={AccountName} jobId={JobId} details={Details}",
		action,
		GetAuditActor(ctx),
		ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
		accountName ?? string.Empty,
		jobId ?? string.Empty,
		SensitiveDataRedactor.Redact(payload));

	// Persist with the same redaction guarantees as the structured log.
	var entry = AuditStoreExtensions.CreateEntry(
		action,
		GetAuditActor(ctx),
		remoteIp: ctx.Connection.RemoteIpAddress?.ToString(),
		accountName: accountName,
		jobId: jobId,
		details: details);

	try {
		await auditStore.RecordAsync(entry, ctx.RequestAborted);
	} catch (OperationCanceledException) {
		throw;
	} catch (Exception ex) {
		// Audit persistence must never block the API response.
		logger.LogError(ex, "Failed to persist audit entry {Action}", action);
	}
}

static bool IsLoginAuditEvent(string normalizedEventType, string state) {
	return string.Equals(state, "LoggedOn", StringComparison.Ordinal) ||
	       string.Equals(state, "LoginFailed", StringComparison.Ordinal) ||
	       string.Equals(state, "LoggedOff", StringComparison.Ordinal) ||
	       string.Equals(state, "Disconnected", StringComparison.Ordinal) ||
	       normalizedEventType.Contains("login", StringComparison.OrdinalIgnoreCase);
}

static bool IsSensitiveTaskAction(string action) {
	return action.StartsWith("SendTradeOffer", StringComparison.OrdinalIgnoreCase) ||
	       action.StartsWith("AcceptTradeOffer", StringComparison.OrdinalIgnoreCase) ||
	       action.StartsWith("DeclineTradeOffer", StringComparison.OrdinalIgnoreCase) ||
	       action.StartsWith("CancelTradeOffer", StringComparison.OrdinalIgnoreCase) ||
	       action.StartsWith("GetInventory", StringComparison.OrdinalIgnoreCase) ||
	       action.StartsWith("RedeemKey", StringComparison.OrdinalIgnoreCase);
}

static string GetAuditActor(HttpContext ctx)
{
	if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && !StringValues.IsNullOrEmpty(forwardedFor))
	{
		return forwardedFor.ToString();
	}

	return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// Request type for session events from agents
public sealed record SessionEventRequest(
	string AccountName,
	string? EventType,
	string? State,
	string? Message
);

public sealed record PutGlobalConfigRequest(
	IReadOnlyDictionary<string, object?>? Settings,
	string? UpdatedBy
);

public sealed record PutAccountConfigRequest(
	bool Enabled = true,
	string? Region = null,
	IReadOnlyList<string>? Labels = null,
	IReadOnlyDictionary<string, object?>? Settings = null,
	string? UpdatedBy = null
);

