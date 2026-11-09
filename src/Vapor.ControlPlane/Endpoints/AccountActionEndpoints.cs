using System.Globalization;
using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account action routes.</summary>
internal static class AccountActionEndpoints
{
	internal static void MapAccountActionEndpoints(this WebApplication app, ILogger auditLogger)
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
