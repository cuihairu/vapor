using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the account routes.</summary>
internal static class AccountEndpoints
{
	internal static void MapAccountEndpoints(this WebApplication app, ILogger auditLogger)
	{
		// ── Accounts (declared farm accounts: desired state, assignment hints; credentials stay agent-side) ──

		app.MapGet("/v1/accounts", (HttpContext ctx, Config cfg, AccountStore accounts, string? state, string? region, string? agent) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountDesiredState? desiredState = null;
			if (!string.IsNullOrWhiteSpace(state))
			{
				if (!Enum.TryParse(state, ignoreCase: true, out AccountDesiredState parsed))
				{
					return Results.BadRequest(new ErrorResponse($"unknown desired state '{state}' (expected offline, online or idle)"));
				}

				desiredState = parsed;
			}

			IEnumerable<AccountSpec> matches = accounts.List();
			if (desiredState is not null)
			{
				matches = matches.Where(a => a.DesiredState == desiredState);
			}

			if (!string.IsNullOrWhiteSpace(region))
			{
				matches = matches.Where(a => string.Equals(a.Region, region.Trim(), StringComparison.OrdinalIgnoreCase));
			}

			if (!string.IsNullOrWhiteSpace(agent))
			{
				matches = matches.Where(a => string.Equals(a.AgentId, agent.Trim(), StringComparison.OrdinalIgnoreCase));
			}

			return Results.Ok(new { accounts = matches });
		})
			.WithTags("Accounts")
			.WithSummary("List declared accounts (filter: state, region, agent)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/accounts/{name}", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			AccountStore accounts,
			SessionTracker sessions,
			AuthChallengeTracker challenges,
			IJobStore store,
			DesiredStateReconciler reconciler,
			string name) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountSpec? spec = accounts.Get(name);
			if (spec is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
			}

			IReadOnlyList<JobTask> recentTasks = await store.ListRecentTasksForTarget(spec.AccountName, 10, ctx.RequestAborted);

			SessionSnapshot? session = sessions.Get(spec.AccountName);
			AccountOrchestrationView? orchestration = reconciler.GetOrchestrationView(spec.AccountName);

			return Results.Ok(new
			{
				spec,
				session,
				// Convergence surface: the account's actual session state and the
				// reconciler's most recent decision for it, projected to the top level
				// so the dashboard row needs no knowledge of the nested shapes.
				observed = session?.State,
				reason = orchestration?.LastDeviation,
				orchestration,
				pendingChallenge = challenges.Get(spec.AccountName),
				recentTasks
			});
		})
			.WithTags("Accounts")
			.WithSummary("Get an account aggregate view: spec, session, orchestration state, pending challenge and recent tasks")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPut("/v1/accounts/{name}", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IAuditStore audit,
			AccountStore accounts,
			string name,
			PutAccountRequest req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (string.IsNullOrWhiteSpace(name))
			{
				return Results.BadRequest(new ErrorResponse("account name is required"));
			}

			AccountSpec spec;
			try
			{
				spec = accounts.Upsert(name, req.Enabled, req.DesiredState, req.IdleApps, req.Region, req.AgentId, req.Note, req.UpdatedBy, req.MarketListingsEnabled, req.BoostTargets, req.TradePolicy, req.FarmPolicy, req.Role, req.SteamId);
			}
			catch (ArgumentException ex)
			{
				return Results.BadRequest(new ErrorResponse(ex.Message));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.spec.updated",
				accountName: spec.AccountName,
				details: new Dictionary<string, object?>
				{
					["updatedBy"] = req.UpdatedBy,
					["enabled"] = spec.Enabled,
					["desiredState"] = spec.DesiredState.ToString(),
					["idleApps"] = spec.IdleApps,
					["region"] = spec.Region,
					["agentId"] = spec.AgentId,
					["marketListingsEnabled"] = spec.MarketListingsEnabled,
					["boostTargets"] = spec.BoostTargets?.Select(t => new Dictionary<string, object?> { ["appId"] = t.AppId, ["targetHours"] = t.TargetHours }).ToList(),
					["tradePolicy"] = spec.TradePolicy is null
						? null
						: new Dictionary<string, object?>
						{
							["autoAcceptGifts"] = spec.TradePolicy.AutoAcceptGifts,
							["partnerWhitelist"] = spec.TradePolicy.PartnerWhitelist
						},
					["farmPolicy"] = spec.FarmPolicy is null
						? null
						: new Dictionary<string, object?>
						{
							["perGameHourBudget"] = spec.FarmPolicy.PerGameHourBudget,
							["priorityOrder"] = spec.FarmPolicy.PriorityOrder.ToString(),
							["priorityApps"] = spec.FarmPolicy.PriorityApps
						},
					["role"] = spec.Role.ToString(),
					["steamId"] = spec.SteamId,
					["version"] = spec.Version?.Version
				});
			return Results.Ok(new { spec });
		})
			.WithTags("Accounts")
			.WithSummary("Declare or replace an account (create or full update)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		app.MapDelete("/v1/accounts/{name}", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, string name) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountSpec? removed = accounts.Remove(name);
			if (removed is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.spec.removed",
				accountName: removed.AccountName,
				details: new Dictionary<string, object?>
				{
					["desiredState"] = removed.DesiredState.ToString(),
					["version"] = removed.Version?.Version
				});
			return Results.NoContent();
		})
			.WithTags("Accounts")
			.WithSummary("Remove a declared account")
			.Produces(204)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/enable", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, string name) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountSpec? spec = accounts.SetEnabled(name, enabled: true);
			if (spec is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.enabled",
				accountName: spec.AccountName,
				details: new Dictionary<string, object?>
				{
					["desiredState"] = spec.DesiredState.ToString(),
					["version"] = spec.Version?.Version
				});
			return Results.Ok(new { spec });
		})
			.WithTags("Accounts")
			.WithSummary("Enable a declared account (resumes orchestration, keeps desired state and config)")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/disable", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, string name) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountSpec? spec = accounts.SetEnabled(name, enabled: false);
			if (spec is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"account.disabled",
				accountName: spec.AccountName,
				details: new Dictionary<string, object?>
				{
					["desiredState"] = spec.DesiredState.ToString(),
					["version"] = spec.Version?.Version
				});
			return Results.Ok(new { spec });
		})
			.WithTags("Accounts")
			.WithSummary("Disable a declared account (stops orchestration, keeps desired state and config)")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapPost("/v1/accounts/{name}/reconcile", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, DesiredStateReconciler reconciler, string name) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountSpec? spec = accounts.Get(name);
			if (spec is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
			}

			// Force one reconcile pass now instead of waiting for the periodic tick —
			// the pass is the existing engine logic, this endpoint only triggers it.
			await reconciler.ReconcileOnce(ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"orchestration.reconcile",
				accountName: spec.AccountName,
				details: new Dictionary<string, object?>
				{
					["desiredState"] = spec.DesiredState.ToString(),
					["reason"] = reconciler.GetOrchestrationView(spec.AccountName)?.LastDeviation
				});
			return Results.Ok(new { status = "reconciled" });
		})
			.WithTags("Accounts")
			.WithSummary("Run one reconcile pass immediately (forced convergence; audit-logged)")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/orchestration/standing", (HttpContext ctx, Config cfg, DesiredStateReconciler reconciler) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			return Results.Ok(new { standing = reconciler.GetStandingSummaries() });
		})
			.WithTags("Accounts")
			.WithSummary("Standing snapshot for every account the orchestrator tracks")
			.Produces(200)
			.Produces(401);
		app.MapGet("/v1/orchestration/farm", (HttpContext ctx, Config cfg, DesiredStateReconciler reconciler) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			return Results.Ok(new { farm = reconciler.GetFarmSummaries() });
		})
			.WithTags("Accounts")
			.WithSummary("Farm-loop snapshot for every account the orchestrator tracks (queue, progress, efficiency counters)")
			.Produces(200)
			.Produces(401);
		// Storage-collect (round 39 P-b; git history): fans one loot_inventory task per enabled farm
		// account into a single job (its SSE stream is the progress channel), waits
		// bounded, then chains sender-side mobile confirmations. The loot+partner
		// semantics are reused deliberately — no dedicated warehouse table, the jobs
		// are the record (the snapshot endpoint reads them back).

		app.MapPost("/v1/orchestration/storage/collect", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, IJobStore store, DesiredStateReconciler reconciler, StorageCollectRequest? req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			// Normalize the body up front: an absent body is just a malformed request,
			// and the storage guard below rejects it with the same 400 an empty field
			// gets. Having done that, the rest of the handler can read the request
			// directly instead of null-conditionally at every use.
			StorageCollectRequest request = req ?? new StorageCollectRequest();
			string? storageName = request.Storage?.Trim();
			if (string.IsNullOrWhiteSpace(storageName))
			{
				return Results.BadRequest(new ErrorResponse("storage (target account name) is required"));
			}

			AccountSpec? storage = accounts.Get(storageName);
			if (storage is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{storageName}' is not declared"));
			}

			if (storage.Role != AccountRole.Storage)
			{
				return Results.BadRequest(new ErrorResponse($"account '{storage.AccountName}' is not declared as a storage account (role=storage)"));
			}

			if (string.IsNullOrWhiteSpace(storage.SteamId))
			{
				return Results.BadRequest(new ErrorResponse($"storage account '{storage.AccountName}' has no steam_id declared; set steamId on the account before collecting"));
			}

			// Standing quarantine (round 38 P2; git history) gates explicit dispatches too: a warehouse
			// with a detected ban must not receive a batch of offers unless the
			// operator forces it. Never-checked accounts (no reconcile history) are
			// unknown, not bad, and pass.
			AccountStandingView? storageStanding = reconciler.GetStandingView(storage.AccountName);
			bool storageQuarantined = storageStanding is { Quarantined: true };
			if (storageQuarantined && !request.Force)
			{
				return Results.BadRequest(new ErrorResponse(
					$"storage account '{storage.AccountName}' is quarantined by the standing check (standing={storageStanding!.Standing ?? "unknown"}); pass force=true to override"));
			}

			IReadOnlyList<AccountSpec> farms = StorageCollectRunner.SelectFarmSources(accounts.List());
			if (farms.Count == 0)
			{
				return Results.BadRequest(new ErrorResponse("no enabled farm accounts to collect from (sources are enabled farm accounts not declared offline)"));
			}

			(List<AccountSpec> eligibleFarms, List<string> skippedFarms) =
				StorageCollectRunner.PartitionByStanding(farms, name => reconciler.GetStandingView(name) is { Quarantined: true });
			if (eligibleFarms.Count == 0)
			{
				return Results.BadRequest(new ErrorResponse(
					$"all {skippedFarms.Count} farm account(s) are quarantined by the standing check; loot one individually with force=true or wait for a clean re-check"));
			}

			CollectRun run = await StorageCollectRunner.DispatchCollectAsync(store, storage, eligibleFarms, request.Message, request.AppIds, ctx.RequestAborted);

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"storage.collect",
				accountName: storage.AccountName,
				jobId: run.JobId,
				details: new Dictionary<string, object?>
				{
					["storage"] = storage.AccountName,
					["steamId"] = storage.SteamId,
					["farmCount"] = eligibleFarms.Count,
					["skippedQuarantined"] = skippedFarms,
					["forcedQuarantine"] = storageQuarantined,
					["completed"] = run.Completed,
					["succeeded"] = run.Tasks.Count(t => t.Status == JobTaskStatus.Finished),
					["failed"] = run.Tasks.Count(t => t.Status != JobTaskStatus.Finished)
				});

			if (!run.Completed)
			{
				return Results.Accepted($"/v1/jobs/{run.JobId}", new
				{
					job_id = run.JobId,
					storage = storage.AccountName,
					storage_steam_id = storage.SteamId,
					farm_accounts = eligibleFarms.Select(f => f.AccountName),
					skipped_quarantined = skippedFarms,
					status = PendingStatus
				});
			}

			return Results.Ok(new
			{
				job_id = run.JobId,
				storage = storage.AccountName,
				storage_steam_id = storage.SteamId,
				farm_accounts = eligibleFarms.Select(f => f.AccountName),
				skipped_quarantined = skippedFarms,
				results = run.Tasks
			});
		})
			.WithTags("Accounts")
			.WithSummary("Collect farm accounts' tradable items into a storage account: one loot_inventory task per enabled farm source, chained mobile confirmations; 200 with per-account results, 202 while still in flight")
			.Produces(200)
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/orchestration/storage/snapshot", async (HttpContext ctx, Config cfg, IJobStore store) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			IReadOnlyList<CollectSnapshotEntry> entries = await StorageCollectRunner.BuildSnapshotAsync(store, ctx.RequestAborted);
			return Results.Ok(new { accounts = entries });
		})
			.WithTags("Accounts")
			.WithSummary("Per-account last storage-collect statistics, rebuilt from recent collect jobs (source account, storage, item count, offer and confirmation outcome)")
			.Produces(200)
			.Produces(401);
		app.MapPost("/v1/accounts/{name}/standing-check", async (HttpContext ctx, Config cfg, IAuditStore audit, AccountStore accounts, DesiredStateReconciler reconciler, string name) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			AccountSpec? spec = accounts.Get(name);
			if (spec is null)
			{
				return Results.NotFound(new ErrorResponse($"account '{name}' is not declared"));
			}

			if (!reconciler.RequestStandingCheck(name))
			{
				return Results.Conflict(new ErrorResponse("standing check not scheduled: a job is active or standing checks are disabled"));
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"standing_check_requested",
				accountName: spec.AccountName,
				details: new Dictionary<string, object?>
				{
					["lastStanding"] = reconciler.GetOrchestrationView(spec.AccountName)?.Standing
				});
			return Results.Accepted($"/v1/accounts/{name}", new { scheduled = true });
		})
			.WithTags("Accounts")
			.WithSummary("Schedule an immediate standing check on the next reconcile pass")
			.Produces(202)
			.Produces<ErrorResponse>(404)
			.Produces<ErrorResponse>(409)
			.Produces(401);
	}
}
