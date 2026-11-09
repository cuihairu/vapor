using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the system routes.</summary>
internal static class SystemEndpoints
{
	internal static void MapSystemEndpoints(this WebApplication app)
	{
		app.MapGet("/healthz", () => Results.Json(new { ok = true }))
			.WithTags("System")
			.WithSummary("Liveness probe (public, unauthenticated)")
			.Produces(200);
		// Prometheus metrics endpoint (public like the agent's /metrics; protect at the network layer).

		app.MapGet("/metrics", async (HttpContext ctx, IJobStore store, AgentRegistry agents, TaskSchedulerService scheduler, AccountStore accounts, DesiredStateReconciler reconciler, RecurringJobScheduler recurringJobs, CrawlRunWorker crawl, IEnumerable<INotificationSink> notificationSinks, ApiRequestMetrics apiMetrics, FaultInjector faults) =>
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
			sb.Append("vapor_controlplane_agents_connected ").Append(agents.ListConnected().Count).Append('\n');

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
			sb.Append("vapor_controlplane_reconcile_actions_total{action=\"trade_accept_dispatched\"} ").Append(reconciler.TradeAcceptsDispatched).Append('\n');
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

			sb.Append("# HELP vapor_controlplane_crawl_runs_total Crawl run outcomes since startup.\n");
			sb.Append("# TYPE vapor_controlplane_crawl_runs_total counter\n");
			sb.Append("vapor_controlplane_crawl_runs_total{outcome=\"triggered\"} ").Append(crawl.RunsTriggered).Append('\n');
			sb.Append("vapor_controlplane_crawl_runs_total{outcome=\"completed\"} ").Append(crawl.RunsCompleted).Append('\n');
			sb.Append("vapor_controlplane_crawl_runs_total{outcome=\"failed\"} ").Append(crawl.RunsFailed).Append('\n');
			sb.Append("vapor_controlplane_crawl_runs_total{outcome=\"timeout\"} ").Append(crawl.RunsTimedOut).Append('\n');
			sb.Append("# HELP vapor_controlplane_crawl_apps_total Crawl per-app outcomes persisted since startup.\n");
			sb.Append("# TYPE vapor_controlplane_crawl_apps_total counter\n");
			sb.Append("vapor_controlplane_crawl_apps_total{outcome=\"ok\"} ").Append(crawl.AppsSucceeded).Append('\n');
			sb.Append("vapor_controlplane_crawl_apps_total{outcome=\"failed\"} ").Append(crawl.AppsFailed).Append('\n');
			sb.Append("# HELP vapor_controlplane_crawl_tasks_dispatched get_game_info_batch jobs dispatched for crawl runs.\n");
			sb.Append("# TYPE vapor_controlplane_crawl_tasks_dispatched counter\n");
			sb.Append("vapor_controlplane_crawl_tasks_dispatched ").Append(crawl.TasksDispatched).Append('\n');
			sb.Append("# HELP vapor_controlplane_crawl_overlap_skips Ticks skipped because the plan's previous run was still in flight.\n");
			sb.Append("# TYPE vapor_controlplane_crawl_overlap_skips counter\n");
			sb.Append("vapor_controlplane_crawl_overlap_skips ").Append(crawl.OverlapSkips).Append('\n');

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

			apiMetrics.Render(sb);
			faults.Render(sb);

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
		// First-party QR rendering for console challenge URLs: the SVG is produced
		// in-process so the login token never leaves the control plane (a third-party
		// image service would leak it). State-free render, hence no audit entry.

		app.MapPost("/v1/qr", (HttpContext ctx, Config cfg, QrRenderRequest? body) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			string? text = body?.Text?.Trim();
			if (string.IsNullOrWhiteSpace(text))
			{
				return Results.BadRequest(new ErrorResponse("text is required"));
			}

			if (System.Text.Encoding.UTF8.GetByteCount(text) > QrEncoder.MaxByteLength)
			{
				return Results.BadRequest(new ErrorResponse($"text exceeds the {QrEncoder.MaxByteLength}-byte QR capacity"));
			}

			return Results.Text(QrEncoder.ToSvg(QrEncoder.Encode(text)), "image/svg+xml", System.Text.Encoding.UTF8);
		})
			.WithTags("Auth")
			.WithSummary("Render text as a QR-code SVG (admin console helper for challenge URLs)")
			.Produces(200, contentType: "image/svg+xml")
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
		// Aggregated internal status view: control-plane self health (DB latency,
		// job queue, scheduler heartbeat, reconciler last pass, plugins), proxy probe
		// history, connected agents and account desired-vs-actual state. Read-only;
		// credentials and challenge codes never appear in the response.

		app.MapGet("/v1/system/status", async (
			HttpContext ctx,
			Config cfg,
			SystemStatusService status) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			SystemStatusReport report = await status.BuildAsync(ctx.RequestAborted).ConfigureAwait(false);
			return Results.Ok(report);
		})
			.WithTags("System")
			.WithSummary("Aggregated internal status: control plane self health, proxy probes, connected agents, account desired-vs-actual state and overall health")
			.Produces<SystemStatusReport>(200)
			.Produces<ErrorResponse>(401);
	}
}
