using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account market listing routes (list, cancel, create).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountMarketEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/accounts/{name}/market/listings", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, int? start, int? count) =>
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

			int startValue = Math.Max(start ?? 0, 0);
			int countValue = Math.Clamp(count ?? 100, 1, 500);
			TaskRunResult read = await AccountTaskRunner.ReadMarketListingsAsync(store, spec.AccountName, startValue, countValue, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"market_listings.read",
				accountName: spec.AccountName,
				jobId: read.JobId,
				details: new Dictionary<string, object?>
				{
					["start"] = startValue,
					["count"] = countValue,
					["outcome"] = read.Status.ToString()
				});

			if (read.Status == JobTaskStatus.Finished)
			{
				return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, market_listings = read.Output });
			}

			if (read.Status != JobTaskStatus.Queued)
			{
				return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
			}

			return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("List an account's own community market listings (dispatches get_my_market_listings and waits for the agent; start/count page through total_count; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/market/listings/cancel", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, MarketCancelRequest? req) =>
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

			bool dryRun = req?.DryRun ?? true;
			bool hasFilter = req is not null &&
				(req.AppId is not null ||
				 !string.IsNullOrWhiteSpace(req.MarketHashName) ||
				 req.MinPriceCents is not null ||
				 req.MaxPriceCents is not null ||
				 req.OlderThanSeconds is not null);
			if (!dryRun && !hasFilter)
			{
				// A dry run with no filter previews everything and is allowed; a real
				// run with no filter would wipe every listing — refuse it.
				return Results.BadRequest(new ErrorResponse("refusing to cancel with no filter: pass at least one filter, or keep dry_run=true to preview"));
			}

			var payload = new Dictionary<string, object?> { ["dry_run"] = dryRun };
			if (req?.AppId is > 0)
			{
				payload["app_id"] = req.AppId;
			}

			if (!string.IsNullOrWhiteSpace(req?.MarketHashName))
			{
				payload["market_hash_name"] = req.MarketHashName.Trim();
			}

			if (req?.MinPriceCents is not null)
			{
				payload["min_price_cents"] = req.MinPriceCents;
			}

			if (req?.MaxPriceCents is not null)
			{
				payload["max_price_cents"] = req.MaxPriceCents;
			}

			if (req?.OlderThanSeconds is > 0)
			{
				payload["older_than_seconds"] = req.OlderThanSeconds;
			}

			TaskRunResult run = await AccountTaskRunner.CancelMarketListingsAsync(store, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"market_listings.cancel",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["dryRun"] = dryRun,
					["hasFilter"] = hasFilter,
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

			return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("Cancel an account's own market listings matching a filter (app / hash name / price range / age; dry_run=true by default only previews the matched listings; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/market/listings", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, MarketCreateListingRequest? req) =>
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

			// Real listing creation is the ToS-gray-zone core: besides the agent-side
			// switch, the account spec must explicitly opt in (default off). A dry run
			// (send absent or false) only reports the pricing plan and is unrestricted.
			bool send = req?.Send ?? false;
			if (send && !spec.MarketListingsEnabled)
			{
				return Results.BadRequest(new ErrorResponse("refusing to list: this account has not enabled market listings (set marketListingsEnabled=true on the account spec)"));
			}

			// send only enters the payload when true: an absent key is the agent's
			// dry-run default, so a false must never masquerade as a choice.
			var payload = new Dictionary<string, object?>();
			if (req?.AppId is > 0)
			{
				payload["app_id"] = req.AppId;
			}

			if (!string.IsNullOrWhiteSpace(req?.ContextId))
			{
				payload["context_id"] = req.ContextId.Trim();
			}

			if (!string.IsNullOrWhiteSpace(req?.AssetId))
			{
				payload["asset_id"] = req.AssetId.Trim();
			}

			if (req?.Amount is > 0)
			{
				payload["amount"] = req.Amount;
			}

			if (req?.SellerProceedsCents is not null)
			{
				payload["seller_proceeds_cents"] = req.SellerProceedsCents;
			}

			if (req?.BuyerPriceCents is not null)
			{
				payload["buyer_price_cents"] = req.BuyerPriceCents;
			}

			if (send)
			{
				payload["send"] = true;
			}

			TaskRunResult run = await AccountTaskRunner.CreateMarketListingAsync(store, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"market_listings.create",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["send"] = send,
					["accountMarketListingsEnabled"] = spec.MarketListingsEnabled,
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

			return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("Create one market listing (dry run by default: send omitted reports the fee-aware pricing plan only; a real listing needs send=true, the account's marketListingsEnabled switch and the agent's AGENT_MARKET_LISTINGS_ENABLED; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
