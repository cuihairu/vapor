using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account achievement routes (list, unlock, reset).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountAchievementEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/accounts/{name}/achievements", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string appId) =>
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

			if (string.IsNullOrWhiteSpace(appId) || !uint.TryParse(appId.Trim(), out uint appIdValue) || appIdValue == 0)
			{
				return Results.BadRequest(new ErrorResponse("appId must be a positive app id"));
			}

			TaskRunResult read = await AccountTaskRunner.ReadAchievementsAsync(store, spec.AccountName, appIdValue, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"achievement.read",
				accountName: spec.AccountName,
				jobId: read.JobId,
				details: new Dictionary<string, object?>
				{
					["appId"] = appIdValue,
					["outcome"] = read.Status.ToString()
				});

			if (read.Status == JobTaskStatus.Finished)
			{
				return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, app_id = appIdValue, achievements = read.Output });
			}

			if (read.Status != JobTaskStatus.Queued)
			{
				return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
			}

			return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("List an account's achievements for one game (dispatches get_achievements and waits for the agent; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/achievements/unlock", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, AchievementWriteRequest? req) =>
		{
			return await DispatchAchievementWrite(ctx, cfg, audit, auditLogger, accounts, store, name, req, unlock: true);
		})
			.WithTags("Accounts")
			.WithSummary("Unlock the named achievements of one game (explicit name list required; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/achievements/reset", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, AchievementWriteRequest? req) =>
		{
			return await DispatchAchievementWrite(ctx, cfg, audit, auditLogger, accounts, store, name, req, unlock: false);
		})
			.WithTags("Accounts")
			.WithSummary("Reset (clear) the named achievements of one game (explicit names + confirm:true required; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
