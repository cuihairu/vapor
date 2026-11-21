using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account inventory route.</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountInventoryEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/accounts/{name}/inventory", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string? appIds, string? appId, string? contextId, string? steamId, bool? tradableOnly, bool? marketableOnly) =>
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

			List<uint>? appIdList = null;
			if (!string.IsNullOrWhiteSpace(appIds))
			{
				appIdList = [];
				foreach (string part in appIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					if (!uint.TryParse(part, out uint parsed) || parsed == 0)
					{
						return Results.BadRequest(new ErrorResponse($"app_ids entries must be positive app ids (got '{part}')"));
					}

					appIdList.Add(parsed);
				}

				if (appIdList.Count == 0)
				{
					return Results.BadRequest(new ErrorResponse("app_ids must name at least one app"));
				}
			}

			var payload = new Dictionary<string, object?>();
			if (appIdList is { Count: > 0 })
			{
				payload["app_ids"] = appIdList;
			}

			if (!string.IsNullOrWhiteSpace(appId))
			{
				payload["app_id"] = appId.Trim();
			}

			if (!string.IsNullOrWhiteSpace(contextId))
			{
				payload["context_id"] = contextId.Trim();
			}

			if (!string.IsNullOrWhiteSpace(steamId))
			{
				payload["steam_id"] = steamId.Trim();
			}

			if (tradableOnly == true)
			{
				payload["tradable_only"] = true;
			}

			if (marketableOnly == true)
			{
				payload["marketable_only"] = true;
			}

			TaskRunResult read = await AccountTaskRunner.DispatchAsync(store, AccountTaskRunner.GetInventoryAction, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"inventory.read",
				accountName: spec.AccountName,
				jobId: read.JobId,
				details: new Dictionary<string, object?>
				{
					["appIds"] = appIdList,
					["tradableOnly"] = tradableOnly == true,
					["outcome"] = read.Status.ToString()
				});

			if (read.Status == JobTaskStatus.Finished)
			{
				return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, inventory = read.Output });
			}

			if (read.Status != JobTaskStatus.Queued)
			{
				return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
			}

			return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("Read an account's inventory (dispatches get_inventory and waits for the agent; app_ids=753,730 scans several apps, tradable_only/marketable_only filter; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
