using System.Globalization;
using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account trade-offer routes (list, accept, decline).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountTradeOfferEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/accounts/{name}/trade-offers", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, bool? activeOnly) =>
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

			bool activeOnlyValue = activeOnly ?? true;
			TaskRunResult read = await AccountTaskRunner.ReadTradeOffersAsync(store, spec.AccountName, activeOnlyValue, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"trade_offers.read",
				accountName: spec.AccountName,
				jobId: read.JobId,
				details: new Dictionary<string, object?>
				{
					["activeOnly"] = activeOnlyValue,
					["outcome"] = read.Status.ToString()
				});

			if (read.Status == JobTaskStatus.Finished)
			{
				return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, offers = read.Output });
			}

			if (read.Status != JobTaskStatus.Queued)
			{
				return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
			}

			return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("List an account's trade offers (dispatches get_trade_offers and waits for the agent; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/trade-offers/{offerId}/accept", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string offerId, TradeOfferDecisionRequest? req) =>
		{
			ulong offerIdValue = 0;
			if (!ulong.TryParse(offerId, out offerIdValue) || offerIdValue == 0)
			{
				return Results.BadRequest(new ErrorResponse("offerId must be a positive integer"));
			}

			if (req is null || string.IsNullOrWhiteSpace(req.PartnerSteamId) || !ulong.TryParse(req.PartnerSteamId.Trim(), out ulong partnerSteamId) || partnerSteamId == 0)
			{
				return Results.BadRequest(new ErrorResponse("partner_steam_id is required (the offer's partner_steam_id from the trade-offers listing)"));
			}

			return await RunOfferDecisionAsync(ctx, cfg, audit, auditLogger, accounts, store, name, AccountTaskRunner.AcceptTradeOfferAction, offerIdValue,
				new Dictionary<string, object?>
				{
					["trade_offer_id"] = offerIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
					["partner_steam_id"] = partnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
					["verify_state"] = req.VerifyState ?? true
				},
				auditAction: "trade_offer.accept",
				details: new Dictionary<string, object?> { ["offerId"] = offerId, ["partner"] = partnerSteamId.ToString(), ["verifyState"] = req.VerifyState ?? true },
				autoConfirm: req.AutoConfirm ?? true);
		})
			.WithTags("Accounts")
			.WithSummary("Accept one of an account's incoming trade offers (dispatches accept_trade_offer; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/trade-offers/{offerId}/decline", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string offerId, TradeOfferDecisionRequest? req) =>
		{
			ulong offerIdValue = 0;
			if (!ulong.TryParse(offerId, out offerIdValue) || offerIdValue == 0)
			{
				return Results.BadRequest(new ErrorResponse("offerId must be a positive integer"));
			}

			return await RunOfferDecisionAsync(ctx, cfg, audit, auditLogger, accounts, store, name, AccountTaskRunner.DeclineTradeOfferAction, offerIdValue,
				new Dictionary<string, object?>
				{
					["trade_offer_id"] = offerIdValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
					["verify_state"] = req?.VerifyState ?? true
				},
				auditAction: "trade_offer.decline",
				details: new Dictionary<string, object?> { ["offerId"] = offerId, ["verifyState"] = req?.VerifyState ?? true });
		})
			.WithTags("Accounts")
			.WithSummary("Decline one of an account's incoming trade offers (dispatches decline_trade_offer; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
