using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.OpenApi.Models;
using SteamControl.ControlPlane;
using SteamControl.Protocol;

// Request type for session events from agents
public sealed record SessionEventRequest(
	string AccountName,
	string? EventType,
	string? State,
	string? Message
);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Config>(_ => Config.LoadFromEnvironment());
builder.Services.AddSingleton<IEventBroker, EventBroker>();

builder.Services.AddSingleton<IJobStore>(sp => {
	var cfg = sp.GetRequiredService<Config>();
	return new SqliteJobStore(cfg.DbPath);
});

builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddHostedService<TaskSchedulerService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => {
	options.SwaggerDoc("v1", new OpenApiInfo { Title = "SteamControl Control Plane API", Version = "v1" });

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

app.UseStaticFiles();
app.UseWebSockets();

var cfg = app.Services.GetRequiredService<Config>();
if (cfg.EnableSwagger) {
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.MapGet("/healthz", () => Results.Json(new { ok = true }));

// Admin UI redirect
app.MapGet("/", () => Results.Redirect("/admin.html"));

app.MapGet("/v1/agents", (HttpContext ctx, Config cfg, AgentRegistry agents) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	var list = agents.List();
	return TypedResults.Ok(new { agents = list });
});

app.MapPost("/v1/jobs", async Task<Results<Accepted<CreateJobResponse>, BadRequest<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
	HttpContext ctx,
	Config cfg,
	IJobStore store,
	IEventBroker events,
	CreateJobRequest req
) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
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

	return TypedResults.Accepted($"/v1/jobs/{created.Job.Id}", new CreateJobResponse(created.Job));
});

app.MapGet("/v1/jobs", async Task<Results<Ok<object>, UnauthorizedHttpResult, ProblemHttpResult>> (HttpContext ctx, Config cfg, IJobStore store, int? limit) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	int capped = Math.Clamp(limit ?? 50, 1, 500);
	var jobs = await store.ListJobs(capped, ctx.RequestAborted);
	return TypedResults.Ok(new { jobs });
});

app.MapGet("/v1/jobs/{jobId}", async Task<Results<Ok<JobWithTasks>, NotFound<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
	HttpContext ctx,
	Config cfg,
	IJobStore store,
	string jobId
) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	try {
		var jwt = await store.GetJob(jobId, ctx.RequestAborted);
		return TypedResults.Ok(jwt);
	} catch (NotFoundException) {
		return TypedResults.NotFound(new ErrorResponse("job not found"));
	}
});

app.MapPost("/v1/jobs/{jobId}/cancel", async Task<Results<Ok<object>, NotFound<ErrorResponse>, UnauthorizedHttpResult, ProblemHttpResult>> (
	HttpContext ctx,
	Config cfg,
	IJobStore store,
	IEventBroker events,
	string jobId
) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	try {
		await store.CancelJob(jobId, ctx.RequestAborted);
		events.Publish(jobId, "job.canceled", null);

		return TypedResults.Ok(new { ok = true });
	} catch (NotFoundException) {
		return TypedResults.NotFound(new ErrorResponse("job not found"));
	}
});

app.MapGet("/v1/jobs/{jobId}/events", async Task (HttpContext ctx, Config cfg, IJobStore store, IEventBroker events, string jobId) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
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
		var json = JsonSerializer.Serialize(e, SteamControl.Protocol.JsonDefaults.Options);
		await ctx.Response.WriteAsync($"event: {e.Type}\ndata: {json}\n\n", ctx.RequestAborted);
		await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
	}
});

// Session events streaming endpoint
app.MapGet("/v1/sessions/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.SubscribeSessions(ctx.RequestAborted, accountName)) {
		var json = JsonSerializer.Serialize(e, SteamControl.Protocol.JsonDefaults.Options);
		await ctx.Response.WriteAsync($"event: session.{e.EventType}\ndata: {json}\n\n", ctx.RequestAborted);
		await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
	}
});

// Auth challenge events streaming endpoint
app.MapGet("/v1/auth/challenges/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.SubscribeAuthChallenges(ctx.RequestAborted, accountName)) {
		var json = JsonSerializer.Serialize(e, SteamControl.Protocol.JsonDefaults.Options);
		await ctx.Response.WriteAsync($"event: auth.{e.ChallengeType}\ndata: {json}\n\n", ctx.RequestAborted);
		await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
	}
});

// Submit auth code endpoint
app.MapPost("/v1/auth/challenges/{accountName}/code", async Task<Results<Ok<object>, NotFound<ErrorResponse>, UnauthorizedHttpResult, BadRequest<ErrorResponse>>> (
	HttpContext ctx,
	Config cfg,
	IEventBroker events,
	string accountName,
	Dictionary<string, string?> body
) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	if (!body.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) {
		return TypedResults.BadRequest(new ErrorResponse("code is required"));
	}

	if (!body.TryGetValue("type", out var type)) {
		type = "email"; // Default to email guard
	}

	// Publish the auth code response event
	// The agent will listen for this event and use the code to continue login
	events.PublishAuthChallenge(accountName, $"code_provided_{type}", $"Auth code provided for {type}");

	return TypedResults.Ok(new { ok = true, accountName, type });
});

// List active agents with their sessions
app.MapGet("/v1/agents/status", (HttpContext ctx, Config cfg, AgentRegistry agents) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	var list = agents.List().Select(a => new {
		id = a.Hello.AgentId,
		region = a.Hello.Region,
		capabilities = a.Hello.Capabilities,
		connected = true,
		connectedAt = DateTimeOffset.UtcNow
	});

	return TypedResults.Ok(new { agents = list });
});

// Receive session events from agents
app.MapPost("/v1/sessions/events", async Task<Results<Ok<object>, UnauthorizedHttpResult, BadRequest<ErrorResponse>>> (
	HttpContext ctx,
	Config cfg,
	IEventBroker events,
	SessionEventRequest req
) => {
	// Allow both admin and agent tokens for this endpoint
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _) &&
	    !Auth.TryAgent(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(req.AccountName)) {
		return TypedResults.BadRequest(new ErrorResponse("accountName is required"));
	}

	// Publish the session event
	events.PublishSession(req.AccountName, req.EventType ?? "state_changed", req.State ?? "unknown", req.Message);

	return TypedResults.Ok(new { ok = true });
});

// List active sessions
app.MapGet("/v1/sessions", (HttpContext ctx, Config cfg) => {
	if (!Auth.TryAdmin(cfg, ctx.Request.Headers["Authorization"], out _)) {
		return TypedResults.Unauthorized();
	}

	// TODO: Return actual session list from SessionManager
	// For now, return empty list
	return TypedResults.Ok(new { sessions = new List<object>() });
});

app.MapGet("/v1/agent/ws", async Task (HttpContext ctx, Config cfg, AgentRegistry registry, IJobStore store, IEventBroker events) => {
	if (!Auth.TryAgent(cfg, ctx.Request.Headers["Authorization"], out _)) {
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
					if (string.Equals(msg.Type, "task_result", StringComparison.Ordinal) && msg.TaskResult != null) {
						try {
							var (task, job) = await store.SetTaskResult(msg.TaskResult, ctx.RequestAborted);
							events.Publish(task.JobId, "task.finished", new Dictionary<string, object?> { ["taskId"] = task.Id, ["success"] = msg.TaskResult.Success, ["job"] = job.Status.ToString() });
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
});

app.Run();
