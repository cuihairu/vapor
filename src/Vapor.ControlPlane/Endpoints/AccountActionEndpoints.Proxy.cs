using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account proxy route.</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountProxyEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapPost("/v1/accounts/{name}/proxy", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, AgentRegistry agents, IJobStore store, string name, SetProxyRequest? req) =>
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

			// The field is mandatory: a body-less or null-field POST must never
			// silently strip an account's pinned exit. An explicit empty string clears.
			string? proxy = req?.Proxy?.Trim();
			if (proxy is null)
			{
				return Results.BadRequest(new ErrorResponse("proxy is required (an explicit empty string clears the account's pinned exit)"));
			}

			// A proxy persists in the pinned agent's local credential store — without
			// an agent pin there is no defined home for the secret, so the assignment
			// is refused instead of written to whichever agent claims the task.
			if (string.IsNullOrWhiteSpace(spec.AgentId))
			{
				return Results.BadRequest(new ErrorResponse("account has no pinned agent (agentId); a proxy persists agent-side and requires one"));
			}

			ConnectedAgent? pinned = agents.Get(spec.AgentId);
			if (pinned is null)
			{
				return Results.NotFound(new ErrorResponse($"pinned agent '{spec.AgentId}' is not connected; connect it and retry"));
			}

			bool clearing = proxy.Length == 0;

			TaskRunResult run = await AccountTaskRunner.DispatchHostActionAsync(
				store,
				AccountTaskRunner.SetProxyAction,
				spec.AgentId,
				new Dictionary<string, object?> { ["account"] = spec.AccountName, ["proxy"] = proxy },
				ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.proxy_set",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["proxy"] = clearing ? "(cleared)" : MaskProxyCredentials(proxy),
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

			// The assignment landed — chain a check_proxy so the caller sees the exit
			// the account actually presents now. A failed verification does not roll
			// the assignment back; it is reported alongside the accepted state.
			TaskRunResult verification = await AccountTaskRunner.DispatchAsync(
				store,
				AccountTaskRunner.CheckProxyAction,
				spec.AccountName,
				new Dictionary<string, object?>(),
				ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.proxy_check",
				accountName: spec.AccountName,
				jobId: verification.JobId,
				details: new Dictionary<string, object?> { ["outcome"] = verification.Status.ToString() });

			Dictionary<string, object?> verificationView = new() { ["job_id"] = verification.JobId };
			if (verification.Status == JobTaskStatus.Finished)
			{
				verificationView["status"] = "finished";
				verificationView["output"] = verification.Output;
			}
			else if (verification.Status == JobTaskStatus.Queued)
			{
				verificationView["status"] = PendingStatus;
			}
			else
			{
				verificationView["status"] = verification.Status.ToString().ToLowerInvariant();
				verificationView["error"] = verification.Error ?? $"task ended as {verification.Status}";
			}

			return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output, verification = verificationView });
		})
			.WithTags("Accounts")
			.WithSummary("Pin (or clear) an account's egress proxy via the agent's set_proxy action, then verify the live exit with check_proxy; 202 + job id when still pending, 502 when the task fails")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
