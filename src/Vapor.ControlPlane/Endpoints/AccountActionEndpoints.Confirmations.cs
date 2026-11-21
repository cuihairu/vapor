using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account confirmation route (accept all pending).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountConfirmationEndpoints(this WebApplication app, ILogger auditLogger)
	{
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

			return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("Respond to all of an account's pending mobile confirmations in one batch (optionally filtered by type; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
