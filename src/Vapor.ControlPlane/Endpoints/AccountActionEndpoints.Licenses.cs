using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account license route (claim free content).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountLicenseEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapPost("/v1/accounts/{name}/licenses", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, LicenseRequest? req) =>
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

			int[]? appIds = req?.AppIds;
			int[]? subIds = req?.SubIds;

			if (appIds is not { Length: > 0 } && subIds is not { Length: > 0 })
			{
				return Results.BadRequest(new ErrorResponse("either app_ids or sub_ids is required"));
			}

			if (appIds is { Length: > 0 } && appIds.Any(id => id <= 0))
			{
				return Results.BadRequest(new ErrorResponse("app_ids must be positive"));
			}

			if (subIds is { Length: > 0 } && subIds.Any(id => id <= 0))
			{
				return Results.BadRequest(new ErrorResponse("sub_ids must be positive"));
			}

			var payload = new Dictionary<string, object?>();
			if (appIds is { Length: > 0 })
			{
				payload["app_ids"] = appIds;
			}

			if (subIds is { Length: > 0 })
			{
				payload["sub_ids"] = subIds;
			}

			TaskRunResult run = await AccountTaskRunner.DispatchAsync(store, AccountTaskRunner.AddLicenseAction, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.add_license",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["appIds"] = appIds,
					["subIds"] = subIds,
					["outcome"] = run.Status.ToString()
				});

			if (run.Status != JobTaskStatus.Finished)
			{
				if (run.Status == JobTaskStatus.Queued)
				{
					return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
				}

				return Results.Json(new { job_id = run.JobId, error = run.Error ?? $"task ended as {run.Status}" }, statusCode: 502);
			}

			return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output });
		})
			.WithTags("Accounts")
			.WithSummary("Claim free Steam content on an account (dispatches add_license; app_ids via the client protocol, sub_ids via the store checkout; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
