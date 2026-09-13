using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Utilities;

var builder = WebApplication.CreateBuilder(args);
VaporCryptoHelper.ConfigureFromEnvironment(Environment.GetEnvironmentVariable);
VaporCryptoHelper.EnsureSafeForEnvironment(Environment.GetEnvironmentVariable);

Config startupConfig = Config.LoadFromEnvironment();
builder.Services.AddSingleton(startupConfig);
builder.Services.AddSingleton<IEventBroker, EventBroker>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<AuthChallengeTracker>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<AccountStore>();

builder.Services.AddSingleton<IJobStore>(sp =>
{
	var cfg = sp.GetRequiredService<Config>();
	return new SqliteJobStore(cfg.DbPath);
});

builder.Services.AddSingleton<IAuditStore>(sp =>
{
	var cfg = sp.GetRequiredService<Config>();
	return new SqliteAuditStore(cfg.AuditDbPath);
});

builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<TaskSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskSchedulerService>());
builder.Services.AddSingleton<DesiredStateReconciler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DesiredStateReconciler>());
builder.Services.AddSingleton<RecurringJobScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecurringJobScheduler>());

// Notification sinks: wired only when at least one delivery target is configured.
if (!string.IsNullOrWhiteSpace(startupConfig.WebhookNotificationsUrl))
{
	builder.Services.AddSingleton<INotificationSink>(sp => new WebhookNotificationSink(
		new Uri(startupConfig.WebhookNotificationsUrl),
		startupConfig.WebhookNotificationsSecret,
		startupConfig.WebhookNotificationsMaxRetries,
		TimeSpan.FromMilliseconds(startupConfig.WebhookNotificationsRetryBaseDelayMs),
		sp.GetRequiredService<ILogger<WebhookNotificationSink>>())
	{
		Rule = BuildNotificationRule(startupConfig.WebhookNotificationsEvents),
	});
	builder.Services.AddHostedService<NotificationService>();
}

static NotificationRule BuildNotificationRule(string? eventsFilter)
{
	if (string.IsNullOrWhiteSpace(eventsFilter))
	{
		return NotificationRule.MatchAll;
	}

	return new NotificationRule(Types: new HashSet<string>(
		eventsFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
		StringComparer.OrdinalIgnoreCase));
}

// Distributed tracing: enabled when the standard OTLP endpoint variable is set.
// Without it no OpenTelemetry SDK is registered and the ActivitySources stay inert.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
{
	builder.Services.AddOpenTelemetry()
		.ConfigureResource(resource => resource.AddService("vapor-controlplane"))
		.WithTracing(tracing => tracing
			.AddSource(VaporTracing.SourceName)
			.AddAspNetCoreInstrumentation()
			.AddOtlpExporter());
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
	options.SwaggerDoc("v1", new OpenApiInfo { Title = "Vapor Control Plane API", Version = "v1" });

	options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
	{
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

builder.Services.ConfigureHttpJsonOptions(options =>
{
	options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
	options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
	options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();
var auditLogger = app.Logger;

app.UseStaticFiles();
app.UseWebSockets();

var cfg = app.Services.GetRequiredService<Config>();
if (cfg.EnableSwagger)
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.MapGet("/healthz", () => Results.Json(new { ok = true }))
	.WithTags("System")
	.WithSummary("Liveness probe (public, unauthenticated)")
	.Produces(200);

// Prometheus metrics endpoint (public like the agent's /metrics; protect at the network layer).
app.MapGet("/metrics", async (HttpContext ctx, IJobStore store, AgentRegistry agents, TaskSchedulerService scheduler, AccountStore accounts, DesiredStateReconciler reconciler, RecurringJobScheduler recurringJobs, IEnumerable<INotificationSink> notificationSinks) =>
{
	IReadOnlyDictionary<JobTaskStatus, int> taskCounts = await store.GetTaskStatusCounts(ctx.RequestAborted);

	var sb = new System.Text.StringBuilder();
	sb.Append("# HELP vapor_controlplane_tasks_by_status Task count by status across all jobs.\n");
	sb.Append("# TYPE vapor_controlplane_tasks_by_status gauge\n");
	foreach (JobTaskStatus status in Enum.GetValues<JobTaskStatus>())
	{
		sb.Append("vapor_controlplane_tasks_by_status{status=\"").Append(status).Append("\"} ")
			.Append(taskCounts.GetValueOrDefault(status)).Append('\n');
	}

	sb.Append("# HELP vapor_controlplane_agents_connected Currently connected agents.\n");
	sb.Append("# TYPE vapor_controlplane_agents_connected gauge\n");
	sb.Append("vapor_controlplane_agents_connected ").Append(agents.ListConnected().Count()).Append('\n');

	sb.Append("# HELP vapor_controlplane_dispatch_failures_total Task dispatch failures since startup.\n");
	sb.Append("# TYPE vapor_controlplane_dispatch_failures_total counter\n");
	sb.Append("vapor_controlplane_dispatch_failures_total{reason=\"no_capable_agent\"} ").Append(scheduler.DispatchNoCapableAgent).Append('\n');
	sb.Append("vapor_controlplane_dispatch_failures_total{reason=\"enqueue_failed\"} ").Append(scheduler.DispatchEnqueueFailed).Append('\n');
	sb.Append("vapor_controlplane_dispatch_failures_total{reason=\"attempts_exhausted\"} ").Append(scheduler.DispatchAttemptsExhausted).Append('\n');

	sb.Append("# HELP vapor_controlplane_accounts_by_desired_state Declared accounts by desired state.\n");
	sb.Append("# TYPE vapor_controlplane_accounts_by_desired_state gauge\n");
	IReadOnlyList<AccountSpec> accountSpecs = accounts.List();
	foreach (AccountDesiredState state in Enum.GetValues<AccountDesiredState>())
	{
		sb.Append("vapor_controlplane_accounts_by_desired_state{state=\"").Append(state).Append("\"} ")
			.Append(accountSpecs.Count(a => a.DesiredState == state)).Append('\n');
	}

	sb.Append("# HELP vapor_controlplane_reconcile_actions_total Desired-state orchestration actions since startup.\n");
	sb.Append("# TYPE vapor_controlplane_reconcile_actions_total counter\n");
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"login_dispatched\"} ").Append(reconciler.LoginsDispatched).Append('\n');
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"idle_dispatched\"} ").Append(reconciler.PlaysDispatched).Append('\n');
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"rebalanced\"} ").Append(reconciler.Rebalances).Append('\n');
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"unassigned\"} ").Append(reconciler.Unassignments).Append('\n');
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"throttled_skip\"} ").Append(reconciler.ThrottledSkips).Append('\n');
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"no_agent_skip\"} ").Append(reconciler.NoAgentSkips).Append('\n');
	sb.Append("vapor_controlplane_reconcile_actions_total{action=\"dry_run_deviation\"} ").Append(reconciler.DryRunDeviations).Append('\n');

	sb.Append("# HELP vapor_controlplane_schedule_triggers_total Recurring job schedule outcomes since startup.\n");
	sb.Append("# TYPE vapor_controlplane_schedule_triggers_total counter\n");
	sb.Append("vapor_controlplane_schedule_triggers_total{outcome=\"triggered\"} ").Append(recurringJobs.TriggeredRuns).Append('\n');
	sb.Append("vapor_controlplane_schedule_triggers_total{outcome=\"overlap_skipped\"} ").Append(recurringJobs.SkippedOverlaps).Append('\n');
	sb.Append("vapor_controlplane_schedule_triggers_total{outcome=\"missed_skipped\"} ").Append(recurringJobs.MissedDropped).Append('\n');
	sb.Append("vapor_controlplane_schedule_triggers_total{outcome=\"missed_catchup\"} ").Append(recurringJobs.MissedCatchUps).Append('\n');

	foreach (INotificationSink sink in notificationSinks)
	{
		sb.Append("# HELP vapor_controlplane_notifications_total Notification deliveries since startup.\n");
		sb.Append("# TYPE vapor_controlplane_notifications_total counter\n");
		sb.Append("vapor_controlplane_notifications_total{sink=\"").Append(sink.Name).Append("\",outcome=\"sent\"} ").Append(sink.Sent).Append('\n');
		sb.Append("vapor_controlplane_notifications_total{sink=\"").Append(sink.Name).Append("\",outcome=\"failed\"} ").Append(sink.Failed).Append('\n');

		sb.Append("# HELP vapor_controlplane_notification_retries_total Notification retry attempts since startup.\n");
		sb.Append("# TYPE vapor_controlplane_notification_retries_total counter\n");
		sb.Append("vapor_controlplane_notification_retries_total{sink=\"").Append(sink.Name).Append("\"} ").Append(sink.Retried).Append('\n');
	}

	ctx.Response.Headers.ContentType = "text/plain; version=0.0.4; charset=utf-8";
	return Results.Text(sb.ToString());
})
	.WithTags("System")
	.WithSummary("Prometheus metrics (task counts by status, connected agents)")
	.Produces(200, contentType: "text/plain");

// Admin UI redirect
app.MapGet("/", () => Results.Redirect("/admin.html"))
	.WithTags("System")
	.WithSummary("Redirect to the admin UI")
	.Produces(302);

app.MapGet("/v1/agents", (HttpContext ctx, Config cfg, AgentRegistry agents) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	var list = agents.List();
	return Results.Ok(new { agents = list });
})
	.WithTags("Agents")
	.WithSummary("List registered agents (including offline ones)")
	.Produces(200)
	.Produces<ErrorResponse>(401);

app.MapGet("/v1/config", (HttpContext ctx, Config cfg, ConfigStore configStore) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	return Results.Ok(new
	{
		global = configStore.GetGlobal(),
		accounts = configStore.ListAccounts()
	});
})
	.WithTags("Config")
	.WithSummary("Get global and per-account configuration")
	.Produces(200)
	.Produces<ErrorResponse>(401);

app.MapPut("/v1/config/global", async (HttpContext ctx, Config cfg, IAuditStore audit, ConfigStore configStore, PutGlobalConfigRequest req) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
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

app.MapPut("/v1/config/account/{name}", async (HttpContext ctx, Config cfg, IAuditStore audit, ConfigStore configStore, string name, PutAccountConfigRequest req) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(name))
	{
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

// ── Accounts (declared farm accounts: desired state, assignment hints; credentials stay agent-side) ──

app.MapGet("/v1/accounts", (HttpContext ctx, Config cfg, AccountStore accounts, string? state, string? region, string? agent) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountDesiredState? desiredState = null;
	if (!string.IsNullOrWhiteSpace(state))
	{
		if (!Enum.TryParse(state, ignoreCase: true, out AccountDesiredState parsed))
		{
			return Results.BadRequest(new ErrorResponse($"unknown desired state '{state}' (expected offline, online or idle)"));
		}

		desiredState = parsed;
	}

	IEnumerable<AccountSpec> matches = accounts.List();
	if (desiredState is not null)
	{
		matches = matches.Where(a => a.DesiredState == desiredState);
	}

	if (!string.IsNullOrWhiteSpace(region))
	{
		matches = matches.Where(a => string.Equals(a.Region, region.Trim(), StringComparison.OrdinalIgnoreCase));
	}

	if (!string.IsNullOrWhiteSpace(agent))
	{
		matches = matches.Where(a => string.Equals(a.AgentId, agent.Trim(), StringComparison.OrdinalIgnoreCase));
	}

	return Results.Ok(new { accounts = matches });
})
	.WithTags("Accounts")
	.WithSummary("List declared accounts (filter: state, region, agent)")
	.Produces(200)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(401);

app.MapGet("/v1/accounts/{name}", async Task<IResult> (
	HttpContext ctx,
	Config cfg,
	AccountStore accounts,
	SessionTracker sessions,
	AuthChallengeTracker challenges,
	IJobStore store,
	DesiredStateReconciler reconciler,
	string name) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? spec = accounts.Get(name);
	if (spec is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	IReadOnlyList<JobTask> recentTasks = await store.ListRecentTasksForTarget(spec.AccountName, 10, ctx.RequestAborted);

	return Results.Ok(new
	{
		spec,
		session = sessions.Get(spec.AccountName),
		orchestration = reconciler.GetOrchestrationView(spec.AccountName),
		pendingChallenge = challenges.Get(spec.AccountName),
		recentTasks
	});
})
	.WithTags("Accounts")
	.WithSummary("Get an account aggregate view: spec, session, orchestration state, pending challenge and recent tasks")
	.Produces(200)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapPut("/v1/accounts/{name}", async Task<IResult> (
	HttpContext ctx,
	Config cfg,
	IAuditStore audit,
	AccountStore accounts,
	string name,
	PutAccountRequest req) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(name))
	{
		return Results.BadRequest(new ErrorResponse("account name is required"));
	}

	AccountSpec spec;
	try
	{
		spec = accounts.Upsert(name, req.Enabled, req.DesiredState, req.IdleApps, req.Region, req.AgentId, req.Note, req.UpdatedBy);
	}
	catch (ArgumentException ex)
	{
		return Results.BadRequest(new ErrorResponse(ex.Message));
	}

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"account.spec.updated",
		accountName: spec.AccountName,
		details: new Dictionary<string, object?>
		{
			["updatedBy"] = req.UpdatedBy,
			["enabled"] = spec.Enabled,
			["desiredState"] = spec.DesiredState.ToString(),
			["idleApps"] = spec.IdleApps,
			["region"] = spec.Region,
			["agentId"] = spec.AgentId,
			["version"] = spec.Version?.Version
		});
	return Results.Ok(new { spec });
})
	.WithTags("Accounts")
	.WithSummary("Declare or replace an account (create or full update)")
	.Produces(200)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(401);

app.MapDelete("/v1/accounts/{name}", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, string name) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? removed = accounts.Remove(name);
	if (removed is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"account.spec.removed",
		accountName: removed.AccountName,
		details: new Dictionary<string, object?>
		{
			["desiredState"] = removed.DesiredState.ToString(),
			["version"] = removed.Version?.Version
		});
	return Results.NoContent();
})
	.WithTags("Accounts")
	.WithSummary("Remove a declared account")
	.Produces(204)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapPost("/v1/accounts/{name}/enable", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, string name) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? spec = accounts.SetEnabled(name, enabled: true);
	if (spec is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"account.enabled",
		accountName: spec.AccountName,
		details: new Dictionary<string, object?>
		{
			["desiredState"] = spec.DesiredState.ToString(),
			["version"] = spec.Version?.Version
		});
	return Results.Ok(new { spec });
})
	.WithTags("Accounts")
	.WithSummary("Enable a declared account (resumes orchestration, keeps desired state and config)")
	.Produces(200)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapPost("/v1/accounts/{name}/disable", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, string name) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? spec = accounts.SetEnabled(name, enabled: false);
	if (spec is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"account.disabled",
		accountName: spec.AccountName,
		details: new Dictionary<string, object?>
		{
			["desiredState"] = spec.DesiredState.ToString(),
			["version"] = spec.Version?.Version
		});
	return Results.Ok(new { spec });
})
	.WithTags("Accounts")
	.WithSummary("Disable a declared account (stops orchestration, keeps desired state and config)")
	.Produces(200)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapGet("/v1/accounts/{name}/trade-offers", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, bool? activeOnly) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? spec = accounts.Get(name.Trim());
	if (spec is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	bool activeOnlyValue = activeOnly ?? true;
	TaskRunResult read = await AccountTaskRunner.ReadTradeOffersAsync(store, spec.AccountName, activeOnlyValue, ctx.RequestAborted);

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"trade_offers.read",
		accountName: spec.AccountName,
		jobId: read.JobId,
		details: new Dictionary<string, object?>
		{
			["activeOnly"] = activeOnlyValue,
			["outcome"] = read.Status.ToString()
		});

	if (read.Status == JobTaskStatus.Finished)
	{
		return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, offers = read.Output });
	}

	if (read.Status != JobTaskStatus.Queued)
	{
		return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
	}

	return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = "pending" });
})
	.WithTags("Accounts")
	.WithSummary("List an account's trade offers (dispatches get_trade_offers and waits for the agent; 202 + job id when still pending, 502 when the task fails)")
	.Produces(200)
	.Produces(202)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapPost("/v1/accounts/{name}/trade-offers/{offerId}/accept", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string offerId, TradeOfferDecisionRequest? req) =>
{
	ulong offerIdValue = 0;
	if (!ulong.TryParse(offerId, out offerIdValue) || offerIdValue == 0)
	{
		return Results.BadRequest(new ErrorResponse("offerId must be a positive integer"));
	}

	if (req is null || string.IsNullOrWhiteSpace(req.PartnerSteamId) || !ulong.TryParse(req.PartnerSteamId.Trim(), out ulong partnerSteamId) || partnerSteamId == 0)
	{
		return Results.BadRequest(new ErrorResponse("partner_steam_id is required (the offer's partner_steam_id from the trade-offers listing)"));
	}

	return await RunOfferDecisionAsync(ctx, cfg, audit, auditLogger, accounts, store, name, AccountTaskRunner.AcceptTradeOfferAction, offerIdValue,
		new Dictionary<string, object?>
		{
			["trade_offer_id"] = offerIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["partner_steam_id"] = partnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["verify_state"] = req.VerifyState ?? true
		},
		auditAction: "trade_offer.accept",
		details: new Dictionary<string, object?> { ["offerId"] = offerId, ["partner"] = partnerSteamId.ToString(), ["verifyState"] = req.VerifyState ?? true },
		autoConfirm: req.AutoConfirm ?? true);
})
	.WithTags("Accounts")
	.WithSummary("Accept one of an account's incoming trade offers (dispatches accept_trade_offer; 202 + job id when still pending, 502 when the task fails)")
	.Produces(200)
	.Produces(202)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapPost("/v1/accounts/{name}/trade-offers/{offerId}/decline", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string offerId, TradeOfferDecisionRequest? req) =>
{
	ulong offerIdValue = 0;
	if (!ulong.TryParse(offerId, out offerIdValue) || offerIdValue == 0)
	{
		return Results.BadRequest(new ErrorResponse("offerId must be a positive integer"));
	}

	return await RunOfferDecisionAsync(ctx, cfg, audit, auditLogger, accounts, store, name, AccountTaskRunner.DeclineTradeOfferAction, offerIdValue,
		new Dictionary<string, object?>
		{
			["trade_offer_id"] = offerIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["verify_state"] = req?.VerifyState ?? true
		},
		auditAction: "trade_offer.decline",
		details: new Dictionary<string, object?> { ["offerId"] = offerId, ["verifyState"] = req?.VerifyState ?? true });
})
	.WithTags("Accounts")
	.WithSummary("Decline one of an account's incoming trade offers (dispatches decline_trade_offer; 202 + job id when still pending, 502 when the task fails)")
	.Produces(200)
	.Produces(202)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

app.MapPost("/v1/accounts/{name}/confirmations/accept-all", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, ConfirmationsBatchRequest? req) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? spec = accounts.Get(name.Trim());
	if (spec is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	var operation = (req?.Operation ?? "allow").Trim().ToLowerInvariant();
	if (operation is not ("allow" or "cancel"))
	{
		return Results.BadRequest(new ErrorResponse("operation must be 'allow' or 'cancel'"));
	}

	var type = (req?.Type ?? "all").Trim().ToLowerInvariant();
	if (type is not ("all" or "trade" or "market"))
	{
		return Results.BadRequest(new ErrorResponse("type must be 'all', 'trade' or 'market'"));
	}

	// The identity secret never travels through the control plane: the agent-side
	// confirm_all_confirmations action reads it from the agent's credential store.
	TaskRunResult run = await AccountTaskRunner.DispatchAsync(
		store,
		AccountTaskRunner.ConfirmAllConfirmationsAction,
		spec.AccountName,
		new Dictionary<string, object?>
		{
			["operation"] = operation,
			["type"] = type
		},
		ctx.RequestAborted);

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		"trade_confirmations.accept_all",
		accountName: spec.AccountName,
		jobId: run.JobId,
		details: new Dictionary<string, object?>
		{
			["operation"] = operation,
			["type"] = type,
			["outcome"] = run.Status.ToString()
		});

	if (run.Status == JobTaskStatus.Finished)
	{
		return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output });
	}

	if (run.Status != JobTaskStatus.Queued)
	{
		return Results.Json(new { job_id = run.JobId, error = run.Error ?? $"task ended as {run.Status}" }, statusCode: 502);
	}

	return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = "pending" });
})
	.WithTags("Accounts")
	.WithSummary("Respond to all of an account's pending mobile confirmations in one batch (optionally filtered by type; 202 + job id when still pending, 502 when the task fails)")
	.Produces(200)
	.Produces(202)
	.Produces<ErrorResponse>(400)
	.Produces<ErrorResponse>(404)
	.Produces<ErrorResponse>(401);

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
			foreach (var agent in agents.ListConnected())
			{
				foreach (var cancel in cancels)
				{
					agent.EnqueueTaskCancel(cancel);
				}
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

// Session events streaming endpoint
app.MapGet("/v1/sessions/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) =>
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

	await foreach (var e in events.SubscribeSessions(ctx.RequestAborted, accountName))
	{
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
app.MapGet("/v1/auth/challenges/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) =>
{
	var auth = GetAuthorization(ctx);
	var isAdmin = Auth.TryAdmin(cfg, auth, out _);
	var isAgent = !isAdmin && Auth.TryAgent(cfg, auth, out _);
	if (!isAdmin && !isAgent)
	{
		ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return;
	}

	ctx.Response.Headers.ContentType = "text/event-stream";
	ctx.Response.Headers.CacheControl = "no-cache";
	ctx.Response.Headers.Connection = "keep-alive";

	await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
	await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

	await foreach (var e in events.SubscribeAuthChallenges(ctx.RequestAborted, accountName))
	{
		if (isAgent && !e.ChallengeType.StartsWith("code_provided_", StringComparison.Ordinal))
		{
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
app.MapGet("/v1/auth/challenges", (HttpContext ctx, Config cfg, AuthChallengeTracker tracker) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
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
) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	if (!body.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
	{
		return Results.BadRequest(new ErrorResponse("code is required"));
	}

	code = code.Trim();

	if (!body.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
	{
		type = "email"; // Default to email guard
	}
	else
	{
		type = type.Trim().ToLowerInvariant();
	}

	type = type switch
	{
		"email" => "email",
		"totp" => "totp",
		"2fa" => "2fa",
		_ => null
	};

	if (type == null)
	{
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
app.MapGet("/v1/agents/status", (HttpContext ctx, Config cfg, AgentRegistry agents) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	var list = agents.ListConnected().Select(a => new
	{
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
) =>
{
	// Allow both admin and agent tokens for this endpoint
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _) &&
		!Auth.TryAgent(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	if (string.IsNullOrWhiteSpace(req.AccountName))
	{
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
	if (IsLoginAuditEvent(normalizedType, state))
	{
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
	if (IsAuthChallengeRequired(normalizedType, state))
	{
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
	}
	else
	{
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
app.MapGet("/v1/sessions", (HttpContext ctx, Config cfg, SessionTracker sessions, string? account) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	IReadOnlyList<SessionSnapshot> snapshots = string.IsNullOrWhiteSpace(account)
		? sessions.List()
		: sessions.List().Where(s => string.Equals(s.AccountName, account.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

	return Results.Ok(new { sessions = snapshots });
})
	.WithTags("Sessions")
	.WithSummary("List known sessions and their latest state (optional account filter)")
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
) =>
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	if (fromMs.HasValue && toMs.HasValue && fromMs.Value > toMs.Value)
	{
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

app.MapGet("/v1/agent/ws", async Task (HttpContext ctx, Config cfg, AgentRegistry registry, IJobStore store, IAuditStore audit, IEventBroker events) =>
{
	if (!Auth.TryAgent(cfg, GetAuthorization(ctx), out _))
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

	using var ws = await ctx.WebSockets.AcceptWebSocketAsync();

	var first = await WebSocketJson.Receive<WSMessage>(ws, ctx.RequestAborted);
	if (!string.Equals(first.Type, "hello", StringComparison.Ordinal) || first.Hello == null || first.Hello.AgentId != agentId || first.Hello.Region != region)
	{
		await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, "hello required", ctx.RequestAborted);
		return;
	}

	var agent = registry.Register(first.Hello, ws, ctx.RequestAborted);
	events.Publish(null, "agent.connected", new Dictionary<string, object?> { ["agentId"] = agent.Hello.AgentId, ["region"] = agent.Hello.Region });

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
						try
						{
							_ = await store.HeartbeatTask(msg.TaskHeartbeat.TaskId, msg.TaskHeartbeat.Attempt, ctx.RequestAborted);
						}
						catch (NotFoundException)
						{
						}
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
	finally
	{
		registry.Unregister(agent.Hello.AgentId);
		events.Publish(null, "agent.disconnected", new Dictionary<string, object?> { ["agentId"] = agent.Hello.AgentId, ["region"] = agent.Hello.Region });
	}
})
	.WithTags("Agents")
	.WithSummary("Agent WebSocket tunnel (agent token; requires agentId/region query params and a hello frame)")
	.Produces(401)
	.Produces<ErrorResponse>(400);

app.Run();

static StringValues GetAuthorization(HttpContext ctx)
{
	if (ctx.Request.Headers.TryGetValue("Authorization", out var header) && !StringValues.IsNullOrEmpty(header))
	{
		return header;
	}

	if (ctx.Request.Query.TryGetValue("authorization", out var token) && token.Count > 0 && !string.IsNullOrWhiteSpace(token[0]))
	{
		var rawValue = token[0];
		if (string.IsNullOrWhiteSpace(rawValue))
		{
			return StringValues.Empty;
		}

		var raw = rawValue.Trim();
		if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
		{
			return new StringValues(raw);
		}

		return new StringValues($"Bearer {raw}");
	}

	return StringValues.Empty;
}

static string ToSnakeCase(string value)
{
	if (string.IsNullOrWhiteSpace(value))
	{
		return string.Empty;
	}

	var s = value.Trim();
	var sb = new System.Text.StringBuilder(s.Length + 8);
	for (int i = 0; i < s.Length; i++)
	{
		char c = s[i];
		if (c == '-' || c == ' ')
		{
			sb.Append('_');
			continue;
		}

		if (char.IsUpper(c))
		{
			if (i > 0 && sb.Length > 0 && sb[sb.Length - 1] != '_')
			{
				sb.Append('_');
			}
			sb.Append(char.ToLowerInvariant(c));
		}
		else
		{
			sb.Append(char.ToLowerInvariant(c));
		}
	}
	return sb.ToString();
}

static string NormalizeSessionEventType(string? eventType)
{
	if (string.IsNullOrWhiteSpace(eventType))
	{
		return "state_changed";
	}

	var v = eventType.Trim();
	bool hasUpper = false;
	for (int i = 0; i < v.Length; i++)
	{
		if (char.IsUpper(v[i]))
		{
			hasUpper = true;
			break;
		}
	}
	return v switch
	{
		"StateChanged" => "state_changed",
		"Connected" => "connected",
		"Disconnected" => "disconnected",
		"AuthCodeNeeded" => "auth_code_required",
		"TwoFactorCodeNeeded" => "2fa_required",
		_ => hasUpper ? ToSnakeCase(v) : v
	};
}

static bool IsAuthChallengeRequired(string normalizedEventType, string state)
{
	if (string.Equals(normalizedEventType, "auth_code_required", StringComparison.Ordinal) ||
		string.Equals(normalizedEventType, "2fa_required", StringComparison.Ordinal))
	{
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

	try
	{
		await auditStore.RecordAsync(entry, ctx.RequestAborted);
	}
	catch (OperationCanceledException)
	{
		throw;
	}
	catch (Exception ex)
	{
		// Audit persistence must never block the API response.
		logger.LogError(ex, "Failed to persist audit entry {Action}", action);
	}
}

static async Task<IResult> RunOfferDecisionAsync(
	HttpContext ctx,
	Config cfg,
	IAuditStore audit,
	ILogger auditLogger,
	AccountStore accounts,
	IJobStore store,
	string name,
	string action,
	ulong offerId,
	Dictionary<string, object?> payload,
	string auditAction,
	Dictionary<string, object?> details,
	bool autoConfirm = false)
{
	if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
	{
		return Results.Unauthorized();
	}

	AccountSpec? spec = accounts.Get(name.Trim());
	if (spec is null)
	{
		return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
	}

	TaskRunResult run = await AccountTaskRunner.DispatchAsync(store, action, spec.AccountName, payload, ctx.RequestAborted);

	await WriteAuditLog(
		auditLogger,
		audit,
		ctx,
		auditAction,
		accountName: spec.AccountName,
		jobId: run.JobId,
		details: new Dictionary<string, object?>(details) { ["outcome"] = run.Status.ToString() });

	if (run.Status == JobTaskStatus.Finished)
	{
		Dictionary<string, object?>? mobileConfirmation = null;

		// Accept succeeded but Steam still wants a mobile confirmation — finish the
		// loop with confirm_trade_offer. The identity secret never leaves the agent:
		// the confirm action reads it from the agent-side credential store.
		if (autoConfirm && OutputFlagIsTrue(run.Output, "requires_mobile_confirmation"))
		{
			TaskRunResult confirm = await AccountTaskRunner.DispatchAsync(
				store,
				AccountTaskRunner.ConfirmTradeOfferAction,
				spec.AccountName,
				new Dictionary<string, object?>
				{
					["trade_offer_id"] = offerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
				},
				ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"trade_offer.confirm",
				accountName: spec.AccountName,
				jobId: confirm.JobId,
				details: new Dictionary<string, object?> { ["offerId"] = offerId.ToString(), ["outcome"] = confirm.Status.ToString() });

			mobileConfirmation = new Dictionary<string, object?> { ["attempted"] = true, ["job_id"] = confirm.JobId };
			if (confirm.Status == JobTaskStatus.Finished)
			{
				mobileConfirmation["confirmed"] = OutputFlagIsTrue(confirm.Output, "confirmed");
			}
			else if (confirm.Status != JobTaskStatus.Queued)
			{
				mobileConfirmation["confirmed"] = false;
				mobileConfirmation["error"] = confirm.Error ?? $"task ended as {confirm.Status}";
			}
			// Still queued when the window closed: confirmed/error stay absent and the
			// caller keeps polling via the confirm job id.
		}

		return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output, mobile_confirmation = mobileConfirmation });
	}

	if (run.Status != JobTaskStatus.Queued)
	{
		return Results.Json(new { job_id = run.JobId, error = run.Error ?? $"task ended as {run.Status}" }, statusCode: 502);
	}

	return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = "pending" });
}

/// <summary>
/// Reads a boolean flag from a task output dictionary, tolerating both in-memory
/// values and the JsonElement shapes a SQLite JSON round-trip produces.
/// </summary>
static bool OutputFlagIsTrue(IReadOnlyDictionary<string, object?>? output, string key)
{
	if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
	{
		return false;
	}

	return raw switch
	{
		bool b => b,
		JsonElement { ValueKind: JsonValueKind.True } => true,
		JsonElement { ValueKind: JsonValueKind.String } s => bool.TryParse(s.GetString(), out bool parsed) && parsed,
		_ => false
	};
}

static bool IsLoginAuditEvent(string normalizedEventType, string state)
{
	return string.Equals(state, "Connected", StringComparison.Ordinal) ||
		   string.Equals(state, "LoggedOn", StringComparison.Ordinal) ||
		   string.Equals(state, "LoginFailed", StringComparison.Ordinal) ||
		   string.Equals(state, "LoggedOff", StringComparison.Ordinal) ||
		   string.Equals(state, "Disconnected", StringComparison.Ordinal) ||
		   normalizedEventType.Contains("login", StringComparison.OrdinalIgnoreCase);
}

static bool IsSensitiveTaskAction(string action)
{
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

// Request type for declaring/replacing a farm account (desired state metadata only; credentials stay agent-side)
public sealed record PutAccountRequest(
	bool Enabled = true,
	AccountDesiredState DesiredState = AccountDesiredState.Offline,
	IReadOnlyList<string>? IdleApps = null,
	string? Region = null,
	string? AgentId = null,
	string? Note = null,
	string? UpdatedBy = null
);

// Request body for trade offer accept/decline endpoints
public sealed record TradeOfferDecisionRequest(
	string? PartnerSteamId = null,
	bool? VerifyState = null,
	bool? AutoConfirm = null
);

// Request body for the batch mobile confirmation endpoint
public sealed record ConfirmationsBatchRequest(
	string? Operation = null,
	string? Type = null
);

