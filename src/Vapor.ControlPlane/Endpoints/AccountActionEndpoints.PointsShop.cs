using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account points-shop routes (summary, claim).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountPointsShopEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/accounts/{name}/points-shop/summary", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string? definitionIds, bool? freeOnly) =>
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

			// definition_ids arrives as a comma-separated query string; anything that
			// does not parse is a caller mistake and gets a 400 rather than a silent skip.
			var payload = new Dictionary<string, object?>();
			uint[] ids = [];
			if (!string.IsNullOrWhiteSpace(definitionIds))
			{
				var parsed = new List<uint>();
				foreach (string part in definitionIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					if (!uint.TryParse(part, out uint id) || id == 0)
					{
						return Results.BadRequest(new ErrorResponse($"invalid definition_ids value '{part}'"));
					}

					parsed.Add(id);
				}

				if (parsed.Count > 0)
				{
					payload["definition_ids"] = parsed;
					ids = [.. parsed];
				}
			}

			if (freeOnly == true)
			{
				payload["free_only"] = true;
			}

			TaskRunResult read = await AccountTaskRunner.ReadPointsShopSummaryAsync(store, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"points_shop.summary",
				accountName: spec.AccountName,
				jobId: read.JobId,
				details: new Dictionary<string, object?>
				{
					["requestedDefinitions"] = ids.Length,
					["freeOnly"] = freeOnly == true,
					["outcome"] = read.Status.ToString()
				});

			if (read.Status == JobTaskStatus.Finished)
			{
				return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, points_shop = read.Output });
			}

			if (read.Status != JobTaskStatus.Queued)
			{
				return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
			}

			return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("Read an account's points shop balance and, via definition_ids (comma-separated), the reward definitions behind those ids (free_only filters the list to point_cost == 0; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/points-shop/claim", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, PointsShopClaimRequest? req) =>
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

			if (req?.DefinitionIds is not { Count: > 0 })
			{
				return Results.BadRequest(new ErrorResponse("definition_ids is required"));
			}

			if (req.DefinitionIds.Any(id => id == 0))
			{
				return Results.BadRequest(new ErrorResponse("definition_ids must be positive"));
			}

			// force only enters the payload when true: an absent key is the agent-side
			// default (free definitions only), so a false must never masquerade as a choice.
			bool force = req.Force ?? false;
			var payload = new Dictionary<string, object?>
			{
				["definition_ids"] = req.DefinitionIds.Distinct().ToList()
			};
			if (force)
			{
				payload["force"] = true;
			}

			TaskRunResult run = await AccountTaskRunner.ClaimPointsShopItemsAsync(store, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"points_shop.claim",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["itemCount"] = req.DefinitionIds.Count,
					["force"] = force,
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
			.WithSummary("Redeem points shop reward definitions (free ones by default; force=true redeems paid ones and spends points; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
