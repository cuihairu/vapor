using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vapor.ControlPlane;
using static Vapor.ControlPlane.EndpointHelpers;
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
builder.Services.AddSingleton<AuthChallengeOutbox>();
builder.Services.AddHostedService(sp => new AuthChallengeOutboxCleaner(
	sp.GetRequiredService<AuthChallengeOutbox>(),
	TimeSpan.FromSeconds(30)));
// Declared-state persistence: account specs and settings write through to this
// database so a control-plane restart no longer erases the farm declaration.
builder.Services.AddSingleton(sp =>
{
	var cfg = sp.GetRequiredService<Config>();
	return new SqliteConfigStore(cfg.ConfigDbPath);
});
builder.Services.AddSingleton<ConfigStore>(sp => new ConfigStore(sp.GetRequiredService<SqliteConfigStore>()));
builder.Services.AddSingleton<AccountStore>(sp => new AccountStore(sp.GetRequiredService<SqliteConfigStore>()));

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
// Script flows share the script repository database; the runner backs both the
// manual run endpoint and the recurring flow scheduler (actor "scheduler").
builder.Services.AddSingleton<ScriptFlowRunner>();
builder.Services.AddSingleton<RecurringFlowScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecurringFlowScheduler>());
builder.Services.AddSingleton<SqliteCrawlStore>(sp => new SqliteCrawlStore(sp.GetRequiredService<Config>().CrawlDbPath));
builder.Services.AddSingleton<SqliteScriptStore>(sp => new SqliteScriptStore(sp.GetRequiredService<Config>().ScriptDbPath));
// Flows share the script repository database: both aggregates are the same
// operator-tooling feature and are always used together.
builder.Services.AddSingleton<SqliteFlowStore>(sp => new SqliteFlowStore(sp.GetRequiredService<Config>().ScriptDbPath));
// Webhook delivery log: always registered so the deliveries endpoint exists
// even without a configured webhook (it just reads an empty log).
builder.Services.AddSingleton<IWebhookDeliveryStore>(sp => new SqliteWebhookDeliveryStore(sp.GetRequiredService<Config>().WebhookNotificationsDbPath));
builder.Services.AddSingleton<CrawlRunWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CrawlRunWorker>());

// Plugin ecosystem: the catalog (index source) and the per-agent inventory mirror.
builder.Services.AddSingleton<PluginInventory>();
builder.Services.AddSingleton<SystemStatusService>();
builder.Services.AddSingleton<ApiRequestMetrics>();
builder.Services.AddSingleton(new ApiKeyRateLimiter(startupConfig.ApiRateLimitPerMinute));
builder.Services.AddSingleton<FaultInjector>();
builder.Services.AddSingleton(sp =>
{
	var cfg = sp.GetRequiredService<Config>();
	// CA2000 suppressed: ownership of the client transfers to the catalog service.
#pragma warning disable CA2000
	var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
#pragma warning restore CA2000
	return new PluginCatalogService(http, sp.GetRequiredService<ILogger<PluginCatalogService>>(), () => cfg.PluginIndexUrl);
});

// Notification sinks: wired only when at least one delivery target is configured.
if (!string.IsNullOrWhiteSpace(startupConfig.WebhookNotificationsUrl))
{
	builder.Services.AddSingleton<INotificationSink>(sp => new WebhookNotificationSink(
		new Uri(startupConfig.WebhookNotificationsUrl),
		startupConfig.WebhookNotificationsSecret,
		startupConfig.WebhookNotificationsMaxRetries,
		TimeSpan.FromMilliseconds(startupConfig.WebhookNotificationsRetryBaseDelayMs),
		sp.GetRequiredService<ILogger<WebhookNotificationSink>>(),
		deliveryLog: sp.GetRequiredService<IWebhookDeliveryStore>())
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

	// Microsoft.OpenApi 2.x + Swashbuckle 10: scheme references are
	// first-class OpenApiSecuritySchemeReference resolved against the
	// document (hence the factory signature; OpenApiReference is gone).
	options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
	{
		[new OpenApiSecuritySchemeReference("bearer", document)] = []
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

// Static pages resolve next to the deployed binary (see StaticPages); the
// deployment-layout fallback arm lives there too, excluded from coverage.
StaticPages.Use(app);

app.UseWebSockets();

var cfg = app.Services.GetRequiredService<Config>();
if (cfg.EnableSwagger)
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

// API-edge observability and protection. Runs after routing so ctx.GetEndpoint()
// resolves on entry: only endpoint-matched requests carry a route pattern worth
// recording (static files and unmatched paths fall through unrecorded), and the
// pattern (not the concrete path) keeps label cardinality bounded by the route
// table. Rate limiting applies to /v1 only — the console pages, /healthz and
// /metrics stay reachable even when a key is exhausted.
app.Use(async (ctx, next) =>
{
	if (ctx.GetEndpoint() is not RouteEndpoint routeEndpoint)
	{
		await next();
		return;
	}

	// Every route in this app is a literal template string, so RawText is
	// always populated; PathText alternatives do not exist on RoutePattern.
	string route = routeEndpoint.RoutePattern.RawText!;
	string method = ctx.Request.Method;
	// W3C trace id of the ambient request activity, rendered as the exemplar on
	// every counter sample this request produces (metric → trace bridge).
	// Null only when no activity is current (hosting always starts one in
	// practice; a test clears it to pin the arm).
	string? traceId = Activity.Current?.TraceId.ToHexString();
	ApiKeyRateLimiter limiter = ctx.RequestServices.GetRequiredService<ApiKeyRateLimiter>();
	ApiRequestMetrics metrics = ctx.RequestServices.GetRequiredService<ApiRequestMetrics>();

	if (limiter.IsEnabled && ctx.Request.Path.StartsWithSegments("/v1"))
	{
		string authHeader = ctx.Request.Headers.Authorization.ToString();
		(bool allowed, int retryAfterSeconds) = limiter.TryAcquire(string.IsNullOrEmpty(authHeader) ? "anonymous" : authHeader);
		if (!allowed)
		{
			metrics.RecordRateLimited();
			metrics.RecordRequest(method, route, StatusCodes.Status429TooManyRequests, 0, traceId);
			ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
			ctx.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
			await ctx.Response.WriteAsJsonAsync(new ErrorResponse($"rate limit exceeded: retry after {retryAfterSeconds}s"));
			return;
		}
	}

	// The timestamp is taken before the fault-injection drill point so a delay
	// fault's added latency lands in the recorded RED duration (the error arm
	// short-circuits below and records its own status with a zero duration).
	long started = Stopwatch.GetTimestamp();

	// Fault-injection drill point (roadmap §8): /v1 routes only, and never the
	// fault endpoints themselves — the panic button and the drill's readout
	// (/v1/faults, /metrics, /healthz) must stay reachable mid-drill.
	if (ctx.Request.Path.StartsWithSegments("/v1") && !ctx.Request.Path.StartsWithSegments("/v1/faults")
		&& ctx.RequestServices.GetRequiredService<FaultInjector>().TryInjectApiRequest(route, method) is { } apiFault)
	{
		if (apiFault.Mode == FaultMode.Delay)
		{
			// Bounded (<= 60 s), deliberately not tied to RequestAborted: the delay
			// must complete so the request is recorded with its inflated duration.
			await Task.Delay(TimeSpan.FromMilliseconds(apiFault.DelayMs));
		}
		else
		{
			metrics.RecordRequest(method, route, apiFault.HttpStatusCode, 0, traceId);
			ctx.Response.StatusCode = apiFault.HttpStatusCode;
			await ctx.Response.WriteAsJsonAsync(new ErrorResponse($"fault injection {apiFault.Id}"));
			return;
		}
	}

	try
	{
		await next();
	}
	finally
	{
		metrics.RecordRequest(method, route, ctx.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalSeconds, traceId);
	}
});

// Read-only viewer access (demo / shared console credential): a request
// carrying the viewer key on a safe method is transparently upgraded to the
// admin identity before the endpoint's own auth check runs, so every GET
// endpoint accepts it without threading a second key through ~80 call sites.
// Unsafe methods and the agent tunnel still see the raw viewer token and
// reject it — the viewer key can never create, mutate, or dispatch anything.
// With no viewer key configured the guard fails closed and this is a no-op.
app.Use(async (ctx, next) =>
{
	if (Auth.TryViewer(cfg, GetAuthorization(ctx), ctx.Request.Method))
	{
		ctx.Request.Headers.Authorization = $"Bearer {cfg.AdminApiKey}";
	}

	await next();
});

app.MapSystemEndpoints();
app.MapConfigurationEndpoints(auditLogger);
app.MapAccountEndpoints(auditLogger);
app.MapAccountActionEndpoints(auditLogger);
app.MapPluginEndpoints(auditLogger);
app.MapScriptEndpoints(auditLogger);
app.MapJobEndpoints(auditLogger);
app.MapSessionEndpoints(auditLogger);
app.MapCrawlEndpoints(auditLogger);
app.MapFaultEndpoints();

app.Run();
