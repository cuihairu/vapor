using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account item-collection routes (loot, duplicates, swap offers).</summary>
internal static partial class AccountActionEndpoints
{
	internal static void MapAccountItemCollectionEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapPost("/v1/accounts/{name}/loot", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, DesiredStateReconciler reconciler, string name, LootRequest? req) =>
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

			// Standing quarantine (round 38 P2; git history) gates explicit dispatches too: an account
			// with a detected ban must not create offers in its name unless the
			// operator forces the (audited) rescue. Never-checked accounts (no
			// reconcile history) are unknown, not bad, and pass.
			AccountStandingView? standing = reconciler.GetStandingView(spec.AccountName);
			bool quarantined = standing is { Quarantined: true };
			if (quarantined && req?.Force != true)
			{
				return Results.BadRequest(new ErrorResponse(
					$"account '{spec.AccountName}' is quarantined by the standing check (standing={standing!.Standing ?? "unknown"}); pass force=true to override"));
			}

			string? partnerSteamId = req?.PartnerSteamId?.Trim();
			string? tradeUrl = req?.TradeUrl?.Trim();

			if (string.IsNullOrWhiteSpace(partnerSteamId) && string.IsNullOrWhiteSpace(tradeUrl))
			{
				return Results.BadRequest(new ErrorResponse("either partner_steam_id or trade_url is required (a token-bearing trade URL reaches non-friends)"));
			}

			if (!string.IsNullOrWhiteSpace(partnerSteamId) && (!ulong.TryParse(partnerSteamId, out ulong partnerValue) || partnerValue == 0))
			{
				return Results.BadRequest(new ErrorResponse("partner_steam_id must be a positive 64-bit SteamID"));
			}

			var payload = new Dictionary<string, object?>();
			if (!string.IsNullOrWhiteSpace(tradeUrl))
			{
				payload["trade_url"] = tradeUrl;
			}
			else
			{
				payload["partner_steam_id"] = partnerSteamId;
			}

			if (!string.IsNullOrWhiteSpace(req?.Message))
			{
				payload["message"] = req.Message.Trim();
			}

			if (req?.AppIds is { Length: > 0 })
			{
				payload["app_ids"] = req.AppIds;
			}

			TaskRunResult run = await AccountTaskRunner.DispatchAsync(store, AccountTaskRunner.LootInventoryAction, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.loot",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["partner"] = string.IsNullOrWhiteSpace(partnerSteamId) ? "trade_url" : partnerSteamId,
					["appIds"] = req?.AppIds,
					["forced_quarantine"] = quarantined,
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

			Dictionary<string, object?>? mobileConfirmation = null;

			// Loot succeeded but Steam still wants a mobile confirmation — finish the loop with
			// confirm_trade_offer. The offer id comes from the loot output; the identity secret
			// never leaves the agent (the confirm action reads the agent-side credential store).
			if (OutputFlagIsTrue(run.Output, "requires_mobile_confirmation"))
			{
				string? offerId = OutputString(run.Output, "trade_offer_id");
				if (!string.IsNullOrEmpty(offerId))
				{
					TaskRunResult confirm = await AccountTaskRunner.DispatchAsync(
						store,
						AccountTaskRunner.ConfirmTradeOfferAction,
						spec.AccountName,
						new Dictionary<string, object?> { ["trade_offer_id"] = offerId },
						ctx.RequestAborted);

					await WriteAuditLog(
						auditLogger,
						audit,
						ctx,
						"trade_offer.confirm",
						accountName: spec.AccountName,
						jobId: confirm.JobId,
						details: new Dictionary<string, object?> { ["offerId"] = offerId, ["outcome"] = confirm.Status.ToString() });

					mobileConfirmation = new Dictionary<string, object?> { ["attempted"] = true, ["job_id"] = confirm.JobId };
					if (confirm.Status == JobTaskStatus.Finished)
					{
						mobileConfirmation["confirmed"] = OutputFlagIsTrue(confirm.Output, "confirmed");
					}
					else if (confirm.Status != JobTaskStatus.Queued)
					{
						mobileConfirmation["confirmed"] = false;
						mobileConfirmation["error"] = confirm.Error ?? $"task ended as {confirm.Status}";
					}
				}
			}

			return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output, mobile_confirmation = mobileConfirmation });
		})
			.WithTags("Accounts")
			.WithSummary("Send all of an account's tradable items to a partner (loot; dispatches loot_inventory and auto-confirms the mobile step; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/accounts/{name}/duplicates", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, string? appIds, int? keep) =>
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

			if (keep is < 1 or > 100)
			{
				return Results.BadRequest(new ErrorResponse("keep must be an integer between 1 and 100"));
			}

			var payload = new Dictionary<string, object?>();
			if (appIdList is { Count: > 0 })
			{
				payload["app_ids"] = appIdList;
			}

			if (keep is not null)
			{
				payload["keep"] = keep;
			}

			TaskRunResult read = await AccountTaskRunner.DispatchAsync(store, AccountTaskRunner.FindDuplicatesAction, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"inventory.duplicates",
				accountName: spec.AccountName,
				jobId: read.JobId,
				details: new Dictionary<string, object?>
				{
					["appIds"] = appIdList,
					["keep"] = keep,
					["outcome"] = read.Status.ToString()
				});

			if (read.Status == JobTaskStatus.Finished)
			{
				return Results.Ok(new { job_id = read.JobId, account = spec.AccountName, duplicates = read.Output });
			}

			if (read.Status != JobTaskStatus.Queued)
			{
				return Results.Json(new { job_id = read.JobId, error = read.Error ?? $"task ended as {read.Status}" }, statusCode: 502);
			}

			return Results.Accepted($"/v1/jobs/{read.JobId}", new { job_id = read.JobId, status = PendingStatus });
		})
			.WithTags("Accounts")
			.WithSummary("Find an account's duplicate items (trading cards) beyond keep copies (dispatches find_duplicates; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/swap-offers", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, string name, SwapOfferRequest? req) =>
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

			string? partnerSteamId = req?.PartnerSteamId?.Trim();
			string? tradeUrl = req?.TradeUrl?.Trim();

			if (string.IsNullOrWhiteSpace(partnerSteamId) && string.IsNullOrWhiteSpace(tradeUrl))
			{
				return Results.BadRequest(new ErrorResponse("either partner_steam_id or trade_url is required (a token-bearing trade URL reaches non-friends)"));
			}

			if (!string.IsNullOrWhiteSpace(partnerSteamId) && (!ulong.TryParse(partnerSteamId, out ulong partnerValue) || partnerValue == 0))
			{
				return Results.BadRequest(new ErrorResponse("partner_steam_id must be a positive 64-bit SteamID"));
			}

			bool send = req?.Send == true;

			var payload = new Dictionary<string, object?>();
			if (!string.IsNullOrWhiteSpace(tradeUrl))
			{
				payload["trade_url"] = tradeUrl;
			}
			else
			{
				payload["partner_steam_id"] = partnerSteamId;
			}

			if (!string.IsNullOrWhiteSpace(req?.Message))
			{
				payload["message"] = req.Message.Trim();
			}

			if (req?.AppIds is { Length: > 0 })
			{
				payload["app_ids"] = req.AppIds;
			}

			if (req?.Keep is not null)
			{
				payload["keep"] = req.Keep;
			}

			if (req?.MaxSwaps is not null)
			{
				payload["max_swaps"] = req.MaxSwaps;
			}

			if (send)
			{
				payload["send"] = true;
			}

			TaskRunResult run = await AccountTaskRunner.DispatchAsync(store, AccountTaskRunner.SwapDuplicatesAction, spec.AccountName, payload, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"trade.swap_offer",
				accountName: spec.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["partner"] = string.IsNullOrWhiteSpace(partnerSteamId) ? "trade_url" : partnerSteamId,
					["send"] = send,
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

			Dictionary<string, object?>? mobileConfirmation = null;

			// A sent swap succeeded but Steam still wants a mobile confirmation — finish the
			// loop with confirm_trade_offer (same flow as loot). The identity secret never
			// leaves the agent (the confirm action reads the agent-side credential store).
			if (send && OutputFlagIsTrue(run.Output, "requires_mobile_confirmation"))
			{
				string? offerId = OutputString(run.Output, "trade_offer_id");
				if (!string.IsNullOrEmpty(offerId))
				{
					TaskRunResult confirm = await AccountTaskRunner.DispatchAsync(
						store,
						AccountTaskRunner.ConfirmTradeOfferAction,
						spec.AccountName,
						new Dictionary<string, object?> { ["trade_offer_id"] = offerId },
						ctx.RequestAborted);

					await WriteAuditLog(
						auditLogger,
						audit,
						ctx,
						"trade_offer.confirm",
						accountName: spec.AccountName,
						jobId: confirm.JobId,
						details: new Dictionary<string, object?> { ["offerId"] = offerId, ["outcome"] = confirm.Status.ToString() });

					mobileConfirmation = new Dictionary<string, object?> { ["attempted"] = true, ["job_id"] = confirm.JobId };
					if (confirm.Status == JobTaskStatus.Finished)
					{
						mobileConfirmation["confirmed"] = OutputFlagIsTrue(confirm.Output, "confirmed");
					}
					else if (confirm.Status != JobTaskStatus.Queued)
					{
						mobileConfirmation["confirmed"] = false;
						mobileConfirmation["error"] = confirm.Error ?? $"task ended as {confirm.Status}";
					}
				}
			}

			return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output, mobile_confirmation = mobileConfirmation });
		})
			.WithTags("Accounts")
			.WithSummary("Match duplicates against a partner and offer a 1:1 swap (dry run unless send=true; dispatches swap_duplicates and auto-confirms the mobile step when sending; 202 + job id when still pending, 502 when the task fails)")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
	}
}
