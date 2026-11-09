using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the crawl routes.</summary>
internal static class CrawlEndpoints
{
	internal static void MapCrawlEndpoints(this WebApplication app, ILogger auditLogger)
	{
		// ── Crawl (game-data harvesting: shard app lists over account pools, persist per-app outcomes) ──

		app.MapGet("/v1/crawl/plans", async (HttpContext ctx, Config cfg, SqliteCrawlStore crawl) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			return Results.Ok(new { plans = await crawl.ListPlansAsync(ctx.RequestAborted) });
		})
			.WithTags("Crawl")
			.WithSummary("List all crawl plans")
			.Produces(200)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/crawl/plans", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IAuditStore audit,
			AccountStore accounts,
			SqliteCrawlStore crawl,
			CreateCrawlPlanRequest req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			string? validationError = ValidateCrawlPlanRequest(req, cfg, accounts);
			if (validationError != null)
			{
				return Results.BadRequest(new ErrorResponse(validationError));
			}

			// One-shots are always due immediately; recurring plans honor StartNow=false
			// to arm the schedule without an immediate first run.
			bool oneShot = string.IsNullOrWhiteSpace(req.Cron) && req.IntervalSeconds is null or <= 0;
			DateTimeOffset now = DateTimeOffset.UtcNow;
			DateTimeOffset? nextRun = oneShot || req.StartNow != false ? now : null;

			CrawlPlan plan = BuildCrawlPlan(req, cfg, Id.New(), now, now, runCount: 0, lastRunId: null, lastRunAt: null, nextRun);

			await crawl.UpsertPlanAsync(plan, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"crawl.plan.created",
				details: new Dictionary<string, object?>
				{
					["planId"] = plan.Id,
					["name"] = plan.Name,
					["apps"] = plan.AppIds.Count,
					["accounts"] = plan.Accounts,
					["cron"] = plan.Cron,
					["intervalSeconds"] = plan.IntervalSeconds
				});

			return Results.Accepted($"/v1/crawl/plans/{plan.Id}", plan);
		})
			.WithTags("Crawl")
			.WithSummary("Create a crawl plan (one-shot by default; optionally recurring via cron or intervalSeconds)")
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/crawl/plans/{id}", async Task<IResult> (HttpContext ctx, Config cfg, SqliteCrawlStore crawl, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			CrawlPlan? plan = await crawl.GetPlanAsync(id, ctx.RequestAborted);
			return plan == null ? Results.NotFound(new ErrorResponse("crawl plan not found")) : Results.Ok(plan);
		})
			.WithTags("Crawl")
			.WithSummary("Get one crawl plan")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPut("/v1/crawl/plans/{id}", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IAuditStore audit,
			AccountStore accounts,
			SqliteCrawlStore crawl,
			string id,
			UpdateCrawlPlanRequest req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			CrawlPlan? existing = await crawl.GetPlanAsync(id, ctx.RequestAborted);
			if (existing == null)
			{
				return Results.NotFound(new ErrorResponse("crawl plan not found"));
			}

			// Merge onto the current definition: null/omitted fields keep their values,
			// so a rename never silently rewrites the app list.
			var merged = new CreateCrawlPlanRequest(
				Name: req.Name ?? existing.Name,
				AppIds: req.AppIds ?? existing.AppIds.Select(a => a.ToString()).ToList(),
				Accounts: req.Accounts ?? existing.Accounts?.ToList(),
				Overrides: req.Overrides ?? (existing.Overrides is { Count: > 0 } existingOverrides
					? existingOverrides.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
					: null),
				ShardSize: req.ShardSize ?? existing.ShardSize,
				IntervalMs: req.IntervalMs ?? existing.IntervalMs,
				Cc: req.Cc ?? existing.Cc,
				Cron: req.Cron ?? existing.Cron,
				IntervalSeconds: req.IntervalSeconds ?? existing.IntervalSeconds,
				StartNow: req.StartNow,
				Enabled: req.Enabled ?? existing.Enabled);

			string? validationError = ValidateCrawlPlanRequest(merged, cfg, accounts);
			if (validationError != null)
			{
				return Results.BadRequest(new ErrorResponse(validationError));
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;
			DateTimeOffset? nextRun = existing.NextRunAt;
			if (req.StartNow == true)
			{
				nextRun = now; // explicit re-arm; StartNow=false keeps the current cursor
			}

			CrawlPlan updated = BuildCrawlPlan(
				merged, cfg, existing.Id, existing.CreatedAt, now,
				existing.RunCount, existing.LastRunId, existing.LastRunAt, nextRun);

			await crawl.UpsertPlanAsync(updated, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"crawl.plan.updated",
				details: new Dictionary<string, object?> { ["planId"] = updated.Id, ["enabled"] = updated.Enabled });

			return Results.Ok(updated);
		})
			.WithTags("Crawl")
			.WithSummary("Update a crawl plan (omitted fields keep their current values)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapDelete("/v1/crawl/plans/{id}", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteCrawlStore crawl, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			bool deleted = await crawl.DeletePlanAsync(id, ctx.RequestAborted);
			if (!deleted)
			{
				return Results.NotFound(new ErrorResponse("crawl plan not found"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"crawl.plan.deleted",
				details: new Dictionary<string, object?> { ["planId"] = id, ["resultsKept"] = true });

			return Results.NoContent();
		})
			.WithTags("Crawl")
			.WithSummary("Delete a crawl plan (persisted results are kept)")
			.Produces(204)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/crawl/plans/{id}/trigger", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, SqliteCrawlStore crawl, string id) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			CrawlPlan? plan = await crawl.GetPlanAsync(id, ctx.RequestAborted);
			if (plan == null)
			{
				return Results.NotFound(new ErrorResponse("crawl plan not found"));
			}

			if (!plan.Enabled)
			{
				return Results.BadRequest(new ErrorResponse("crawl plan is disabled"));
			}

			await crawl.SetNextRunAsync(id, DateTimeOffset.UtcNow, ctx.RequestAborted);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"crawl.plan.triggered",
				details: new Dictionary<string, object?> { ["planId"] = id });

			return Results.Accepted($"/v1/crawl/plans/{id}", plan);
		})
			.WithTags("Crawl")
			.WithSummary("Trigger a crawl plan now (overlapping runs are skipped by the worker)")
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/crawl/plans/{id}/runs", async Task<IResult> (HttpContext ctx, Config cfg, SqliteCrawlStore crawl, string id, int? limit) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			CrawlPlan? plan = await crawl.GetPlanAsync(id, ctx.RequestAborted);
			if (plan == null)
			{
				return Results.NotFound(new ErrorResponse("crawl plan not found"));
			}

			return Results.Ok(new { runs = await crawl.ListRunsAsync(id, Math.Clamp(limit ?? 20, 1, 100), ctx.RequestAborted) });
		})
			.WithTags("Crawl")
			.WithSummary("List recent runs of a crawl plan (aggregated, newest first)")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/crawl/results", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			SqliteCrawlStore crawl,
			string? planId,
			string? runId,
			long? appId,
			string? account,
			bool? ok,
			int? limit,
			int? offset) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			var query = new CrawlResultQuery(
				PlanId: planId,
				RunId: runId,
				AppId: appId is > 0 ? (uint)appId : null,
				Account: account,
				Ok: ok,
				Limit: Math.Clamp(limit ?? 100, 1, 500),
				Offset: Math.Max(offset ?? 0, 0));

			return Results.Ok(new
			{
				results = await crawl.QueryResultsAsync(query, ctx.RequestAborted),
				total = await crawl.CountResultsAsync(query, ctx.RequestAborted),
				limit = query.Limit,
				offset = query.Offset
			});
		})
			.WithTags("Crawl")
			.WithSummary("Query persisted crawl results (filter by plan, run, app, account, outcome; limit 1-500)")
			.Produces(200)
			.Produces<ErrorResponse>(401);
	}
}
