using System.Text.Json;
using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the session routes.</summary>
internal static class SessionEndpoints
{
	internal static void MapSessionEndpoints(this WebApplication app, ILogger auditLogger)
	{
		// Session events streaming endpoint

		app.MapGet("/v1/sessions/events", async Task (HttpContext ctx, Config cfg, IEventBroker events, string? accountName) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}

			ctx.Response.Headers.ContentType = "text/event-stream";
			ctx.Response.Headers.CacheControl = "no-cache";
			ctx.Response.Headers.Connection = "keep-alive";

			await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
			await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

			await foreach (var e in events.SubscribeSessions(ctx.RequestAborted, accountName))
			{
				var json = JsonSerializer.Serialize(e, Vapor.Protocol.JsonDefaults.Options);
				await ctx.Response.WriteAsync($"event: session.{e.EventType}\ndata: {json}\n\n", ctx.RequestAborted);
				await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
			}
		})
			.WithTags("Sessions")
			.WithSummary("SSE stream of session events (optionally filtered by accountName)")
			.Produces(200, contentType: "text/event-stream")
			.Produces(401);
		// Auth challenge events streaming endpoint

		app.MapGet("/v1/auth/challenges/events", async Task (
			HttpContext ctx,
			Config cfg,
			IEventBroker events,
			AccountStore accounts,
			AuthChallengeOutbox outbox,
			string? accountName) =>
		{
			var auth = GetAuthorization(ctx);
			var isAdmin = Auth.TryAdmin(cfg, auth, out _);
			string? agentId = null;
			var isAgent = !isAdmin && Auth.TryAgentIdentity(cfg, auth, out agentId);
			if (!isAdmin && !isAgent)
			{
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				return;
			}

			string? requestedAccountName = string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim();
			bool AccountBelongsToAgent(string? accountName)
			{
				AccountSpec? account = accounts.Get(accountName ?? string.Empty);
				return account is not null
					&& !string.IsNullOrWhiteSpace(account.AgentId)
					&& string.Equals(account.AgentId, agentId, StringComparison.OrdinalIgnoreCase);
			}

			if (!isAdmin && requestedAccountName is not null && !AccountBelongsToAgent(requestedAccountName))
			{
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			if (!isAdmin && requestedAccountName is null)
			{
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}

			ctx.Response.Headers.ContentType = "text/event-stream";
			ctx.Response.Headers.CacheControl = "no-cache";
			ctx.Response.Headers.Connection = "keep-alive";

			await ctx.Response.WriteAsync("event: ready\ndata: {}\n\n", ctx.RequestAborted);
			await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

			// ARCH-09 short replay: only the owning agent gets an unconfirmed code again.
			// Admin streams deliberately get real-time projection only and no replay.
			if (isAgent
				&& requestedAccountName is not null
				&& outbox.TryGet(requestedAccountName) is { } staged)
			{
				await ctx.Response.WriteAsync($"event: auth.{staged.ChallengeType}\ndata: {JsonSerializer.Serialize(staged, Vapor.Protocol.JsonDefaults.Options)}\n\n", ctx.RequestAborted);
				await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
				outbox.Acknowledge(staged.AccountName, staged.ChallengeType, staged.Code);
			}

			await foreach (var e in events.SubscribeAuthChallenges(ctx.RequestAborted, requestedAccountName))
			{
				if (isAgent && !e.ChallengeType.StartsWith("code_provided_", StringComparison.Ordinal))
				{
					continue;
				}

				if (isAgent && !AccountBelongsToAgent(e.AccountName))
				{
					continue;
				}

				var payload = isAdmin && e.ChallengeType.StartsWith("code_provided_", StringComparison.Ordinal)
					? e with { Code = null }
					: e;
				var json = JsonSerializer.Serialize(payload, Vapor.Protocol.JsonDefaults.Options);
				await ctx.Response.WriteAsync($"event: auth.{e.ChallengeType}\ndata: {json}\n\n", ctx.RequestAborted);
				await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
			}
		})
			.WithTags("Auth")
			.WithSummary("SSE stream of login challenges (admin sees all; agents only code_provided_* events)")
			.Produces(200, contentType: "text/event-stream")
			.Produces(401);
		// List pending auth challenges (useful for UI refresh)

		app.MapGet("/v1/auth/challenges", (HttpContext ctx, Config cfg, AuthChallengeTracker tracker) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			return Results.Ok(new { challenges = tracker.List() });
		})
			.WithTags("Auth")
			.WithSummary("List pending login challenges (2FA / auth code prompts)")
			.Produces(200)
			.Produces<ErrorResponse>(401);
		// Submit auth code endpoint

		app.MapPost("/v1/auth/challenges/{accountName}/code", async (
			HttpContext ctx,
			Config cfg,
			IAuditStore audit,
			IEventBroker events,
			AuthChallengeTracker tracker,
			AuthChallengeOutbox outbox,
			string accountName,
			Dictionary<string, string?> body
		) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (!body.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
			{
				return Results.BadRequest(new ErrorResponse("code is required"));
			}

			code = code.Trim();

			if (!body.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
			{
				type = "email"; // Default to email guard
			}
			else
			{
				type = type.Trim().ToLowerInvariant();
			}

			type = type switch
			{
				"email" => "email",
				"totp" => "totp",
				"2fa" => "2fa",
				_ => null
			};

			if (type == null)
			{
				return Results.BadRequest(new ErrorResponse("type must be one of: email, totp, 2fa"));
			}

			// Stage before clearing: the pending challenge cannot disappear into a lost
			// live event while the owning agent's short replay is still available (ARCH-09).
			outbox.Stage(new AuthChallengeEvent(
				Id: Guid.NewGuid().ToString("N"),
				AccountName: accountName,
				ChallengeType: $"code_provided_{type}",
				Message: $"Auth code provided for {type}",
				Code: code,
				Timestamp: DateTimeOffset.UtcNow,
				JobId: null), cfg.AuthChallengeDeliveryTtl);
			tracker.Clear(accountName);

			// Publish the auth code response event
			// The agent will listen for this event and use the code to continue login
			events.PublishAuthChallenge(accountName, $"code_provided_{type}", $"Auth code provided for {type}", code);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"auth.code.submitted",
				accountName: accountName,
				details: new Dictionary<string, object?>
				{
					["type"] = type,
					["code"] = code
				});

			return Results.Ok(new { ok = true, accountName, type });
		})
			.WithTags("Auth")
			.WithSummary("Submit a login challenge code (type: email, totp, 2fa)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		// Receive session events from agents

		app.MapPost("/v1/sessions/events", async (
			HttpContext ctx,
			Config cfg,
			IAuditStore audit,
			IEventBroker events,
			SessionTracker sessions,
			AuthChallengeTracker challenges,
			SessionEventRequest req
		) =>
		{
			// Allow both admin and agent tokens for this endpoint
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _) &&
				!Auth.TryAgent(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (string.IsNullOrWhiteSpace(req.AccountName))
			{
				return Results.BadRequest(new ErrorResponse("accountName is required"));
			}

			var normalizedType = NormalizeSessionEventType(req.EventType);
			var state = string.IsNullOrWhiteSpace(req.State) ? "unknown" : req.State;

			// Publish the session event
			events.PublishSession(req.AccountName, normalizedType, state, req.Message);
			sessions.Update(req.AccountName, normalizedType, state, req.Message);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"session.event.received",
				accountName: req.AccountName,
				details: new Dictionary<string, object?>
				{
					["eventType"] = normalizedType,
					["state"] = state,
					["message"] = req.Message
				});

			// Persist login-relevant transitions as dedicated audit records.
			if (IsLoginAuditEvent(normalizedType, state))
			{
				await WriteAuditLog(
					auditLogger,
					audit,
					ctx,
					"session.login",
					accountName: req.AccountName,
					details: new Dictionary<string, object?>
					{
						["eventType"] = normalizedType,
						["state"] = state
					});
			}

			// Publish auth challenge events when sessions require user input
			if (IsAuthChallengeRequired(normalizedType, state))
			{
				var challengeType =
					string.Equals(normalizedType, "qr_required", StringComparison.Ordinal) ||
					string.Equals(state, "ConnectingWaitQr", StringComparison.Ordinal)
						? "qr_required"
						: string.Equals(normalizedType, "2fa_required", StringComparison.Ordinal) ||
						  string.Equals(state, "ConnectingWait2FA", StringComparison.Ordinal)
							? "2fa_required"
							: "auth_code_required";
				var evt = new AuthChallengeEvent(
					Id: Guid.NewGuid().ToString("N"),
					AccountName: req.AccountName,
					ChallengeType: challengeType,
					Message: req.Message,
					Code: null,
					Timestamp: DateTimeOffset.UtcNow,
					JobId: null
				);
				evt = challenges.Upsert(evt);
				events.PublishAuthChallenge(req.AccountName, challengeType, req.Message, attempt: evt.Attempt);
			}
			else
			{
				// Clear any stale "needs code/2FA" prompt once the session progresses.
				challenges.Clear(req.AccountName);
			}

			return Results.Ok(new { ok = true });
		})
			.WithTags("Sessions")
			.WithSummary("Report a session event (admin or agent token; feeds SSE, tracker and auth challenges)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces(401);
		// List active sessions

		app.MapGet("/v1/sessions", (HttpContext ctx, Config cfg, SessionTracker sessions, string? account) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			IReadOnlyList<SessionSnapshot> snapshots = string.IsNullOrWhiteSpace(account)
				? sessions.List()
				: sessions.List().Where(s => string.Equals(s.AccountName, account.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

			return Results.Ok(new { sessions = snapshots });
		})
			.WithTags("Sessions")
			.WithSummary("List known sessions and their latest state (optional account filter)")
			.Produces(200)
			.Produces<ErrorResponse>(401);
		// Query persisted audit logs

		app.MapGet("/v1/audit/logs", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IAuditStore audit,
			int? limit,
			int? offset,
			string? action,
			string? account,
			string? jobId,
			string? agentId,
			string? taskId,
			long? fromMs,
			long? toMs
		) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (fromMs.HasValue && toMs.HasValue && fromMs.Value > toMs.Value)
			{
				return Results.BadRequest(new ErrorResponse("fromMs must not be greater than toMs"));
			}

			var query = new AuditQuery(
				Action: action,
				AccountName: account,
				JobId: jobId,
				AgentId: agentId,
				TaskId: taskId,
				From: fromMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(fromMs.Value) : null,
				To: toMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(toMs.Value) : null,
				Limit: Math.Clamp(limit ?? 100, 1, 500),
				Offset: Math.Max(offset ?? 0, 0)
			);

			IReadOnlyList<AuditEntry> logs = await audit.QueryAsync(query, ctx.RequestAborted);
			int total = await audit.CountAsync(query, ctx.RequestAborted);

			return Results.Ok(new { logs, total, limit = query.Limit, offset = query.Offset });
		})
			.WithTags("Audit")
			.WithSummary("Query persisted audit logs (filter by action, account, jobId, agentId, taskId, time range; limit 1-500)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
		app.MapGet("/v1/notifications/deliveries", async Task<IResult> (
			HttpContext ctx,
			Config cfg,
			IWebhookDeliveryStore deliveries,
			string? notificationId,
			string? outcome,
			int? limit,
			int? offset) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			var query = (
				NotificationId: notificationId,
				Outcome: outcome,
				Limit: Math.Clamp(limit ?? 100, 1, 500),
				Offset: Math.Max(offset ?? 0, 0));

			IReadOnlyList<WebhookDeliveryRecord> rows = await deliveries.QueryAsync(
				query.NotificationId, query.Outcome, query.Limit, query.Offset, ctx.RequestAborted);
			int total = await deliveries.CountAsync(query.NotificationId, query.Outcome, ctx.RequestAborted);

			return Results.Ok(new { deliveries = rows, total = total, limit = query.Limit, offset = query.Offset });
		})
			.WithTags("Notifications")
			.WithSummary("Query the webhook delivery log (per-attempt rows; filter by notificationId and outcome; limit 1-500)")
			.Produces(200)
			.Produces<ErrorResponse>(401);
	}
}
