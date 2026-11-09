using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Vapor.Protocol;
using Vapor.Steam.Core.Utilities;

namespace Vapor.ControlPlane;

/// <summary>
/// Shared request-side helpers for the endpoint groups: auth header extraction,
/// audit writes, request validation and the task-result accessors. Formerly
/// top-level local functions of the single-file Program.
/// </summary>
internal static class EndpointHelpers
{
	// Accepted job responses report this lightweight status until the durable queue
	// publishes its first authoritative JobStatus event.
	internal const string PendingStatus = "pending";

	// Must mirror the agent-side ScriptExecAction bounds: the metadata timeout is
	// 300s and the per-execution payload timeout can only shorten it.
	internal const int ScriptExecMaxTimeoutSeconds = 300;

	// Flow definition limits: an inline run walks every step inside one request,
	// so the step count is bounded.
	internal const string FlowOnFailureStop = "stop";
	internal const string FlowOnFailureContinue = "continue";
	internal const int FlowMaxSteps = 50;

	internal static StringValues GetAuthorization(HttpContext ctx)
	{
		if (ctx.Request.Headers.TryGetValue("Authorization", out var header) && !StringValues.IsNullOrEmpty(header))
		{
			return header;
		}

		if (ctx.Request.Query.TryGetValue("authorization", out var token) && token.Count > 0 && !string.IsNullOrWhiteSpace(token[0]))
		{
			var raw = token[0]!.Trim(); // the guard above already ruled out a null/whitespace token[0] — the ?? arm would be an unreachable probe
			if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
			{
				return new StringValues(raw);
			}

			return new StringValues($"Bearer {raw}");
		}

		return StringValues.Empty;
	}

	internal static string ToSnakeCase(string value)
	{
		// Callers (NormalizeSessionEventType) guarantee a non-blank input.
		var s = value.Trim();
		var sb = new System.Text.StringBuilder(s.Length + 8);
		for (int i = 0; i < s.Length; i++)
		{
			char c = s[i];
			if (c == '-' || c == ' ')
			{
				sb.Append('_');
				continue;
			}

			if (char.IsUpper(c))
			{
				if (i > 0 && sb.Length > 0 && sb[sb.Length - 1] != '_')
				{
					sb.Append('_');
				}
				sb.Append(char.ToLowerInvariant(c));
			}
			else
			{
				sb.Append(char.ToLowerInvariant(c));
			}
		}
		return sb.ToString();
	}

	internal static string NormalizeSessionEventType(string? eventType)
	{
		if (string.IsNullOrWhiteSpace(eventType))
		{
			return "state_changed";
		}

		var v = eventType.Trim();
		bool hasUpper = false;
		for (int i = 0; i < v.Length; i++)
		{
			if (char.IsUpper(v[i]))
			{
				hasUpper = true;
				break;
			}
		}
		return v switch
		{
			"StateChanged" => "state_changed",
			"Connected" => "connected",
			"Disconnected" => "disconnected",
			"AuthCodeNeeded" => "auth_code_required",
			"TwoFactorCodeNeeded" => "2fa_required",
			_ => hasUpper ? ToSnakeCase(v) : v
		};
	}

	internal static bool IsAuthChallengeRequired(string normalizedEventType, string state)
	{
		if (string.Equals(normalizedEventType, "auth_code_required", StringComparison.Ordinal) ||
			string.Equals(normalizedEventType, "2fa_required", StringComparison.Ordinal) ||
			string.Equals(normalizedEventType, "qr_required", StringComparison.Ordinal))
		{
			return true;
		}

		return string.Equals(state, "ConnectingWaitAuthCode", StringComparison.Ordinal) ||
			   string.Equals(state, "ConnectingWait2FA", StringComparison.Ordinal) ||
			   string.Equals(state, "ConnectingWaitQr", StringComparison.Ordinal);
	}

	// Validates a script create/replace request; returns an error message, null when valid.
	internal static string? ValidateScriptRequest(UpsertScriptRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Name))
		{
			return "name is required";
		}

		if (string.IsNullOrWhiteSpace(request.Content))
		{
			return "content is required";
		}

		return null;
	}

	// Script language is a free-form lowercase tag; omitted or blank means "shell".
	internal static string NormalizeScriptLanguage(string? language)
	{
		string normalized = language?.Trim().ToLowerInvariant() ?? "";
		return normalized.Length == 0 ? "shell" : normalized;
	}

	// Validates a flow create/replace request; returns an error message, null when valid.
	internal static string? ValidateFlowRequest(UpsertFlowRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Name))
		{
			return "name is required";
		}

		if (request.Steps is null || request.Steps.Count == 0)
		{
			return "at least one step is required";
		}

		if (request.Steps.Count > FlowMaxSteps)
		{
			return $"a flow supports at most {FlowMaxSteps} steps";
		}

		foreach (UpsertFlowStepRequest step in request.Steps)
		{
			if (string.IsNullOrWhiteSpace(step.ScriptId))
			{
				return "every step needs a scriptId";
			}

			if (string.IsNullOrWhiteSpace(step.AgentId))
			{
				return "every step needs an agentId";
			}

			string onFailure = NormalizeFlowOnFailure(step.OnFailure);
			if (onFailure is not (FlowOnFailureStop or FlowOnFailureContinue))
			{
				return $"onFailure must be '{FlowOnFailureStop}' or '{FlowOnFailureContinue}'";
			}
		}

		// Recurring plan uses the same cadence rules as scheduled job templates;
		// a plan whose cron can never match again would sit armed-but-undead, so
		// the cursor computation doubles as the last validation gate.
		if (request.Schedule is not null)
		{
			try
			{
				ScheduleClock.Validate(request.Schedule);
				if (ScheduleClock.NextRun(request.Schedule, DateTimeOffset.UtcNow) is null)
				{
					return "schedule cron never matches again";
				}
			}
			catch (ArgumentException ex)
			{
				return ex.Message;
			}
		}

		return null;
	}

	// First recurring trigger point for a flow schedule, or null when the flow
	// stays manual (no schedule) — the value stored as the scheduler's due cursor.
	// Validation runs in the same request and already proved a present schedule
	// always has a next point (never-matching crons are a 400), so the cursor
	// computation carries no null arm of its own.
	internal static long? ArmFlowCursor(JobSchedule? schedule, DateTimeOffset now)
	{
		if (schedule is null)
		{
			return null;
		}

		DateTimeOffset? next = ScheduleClock.NextRun(schedule, now);
		return next!.Value.ToUnixTimeMilliseconds();
	}

	// Failure policy is a lowercase tag; omitted or blank means "stop".
	internal static string NormalizeFlowOnFailure(string? onFailure)
	{
		string normalized = onFailure?.Trim().ToLowerInvariant() ?? "";
		return normalized.Length == 0 ? FlowOnFailureStop : normalized;
	}

	// Validates a crawl plan request (create, or a merged update); returns an error message, null when valid.
	internal static string? ValidateCrawlPlanRequest(CreateCrawlPlanRequest req, Config cfg, AccountStore accounts)
	{
		if (string.IsNullOrWhiteSpace(req.Name))
		{
			return "name is required";
		}

		if (req.AppIds is not { Count: > 0 })
		{
			return "app_ids is required";
		}

		if (req.AppIds.Count > cfg.CrawlMaxAppsPerPlan)
		{
			return $"app_ids exceeds the plan limit of {cfg.CrawlMaxAppsPerPlan}";
		}

		foreach (string entry in req.AppIds)
		{
			if (!uint.TryParse(entry, out uint appId) || appId == 0)
			{
				return $"invalid app_ids entry: '{entry}'";
			}
		}

		if (req.ShardSize is < 1 or > CrawlShardPlanner.MaxShardApps)
		{
			return $"shard_size must be between 1 and {CrawlShardPlanner.MaxShardApps}";
		}

		if (req.IntervalMs is < 0 or > 60000)
		{
			return "interval_ms must be between 0 and 60000";
		}

		// One-shot plans (no cadence at all) are the default shape; ScheduleClock
		// only governs recurring combinations, so validate those exclusively.
		if (!string.IsNullOrWhiteSpace(req.Cron) || (req.IntervalSeconds ?? 0) > 0)
		{
			try
			{
				ScheduleClock.Validate(new JobSchedule(
					IntervalSeconds: req.IntervalSeconds ?? 0,
					Cron: string.IsNullOrWhiteSpace(req.Cron) ? null : req.Cron));
			}
			catch (ArgumentException ex)
			{
				return ex.Message;
			}
		}

		foreach (string name in req.Accounts ?? new List<string>())
		{
			if (accounts.Get(name.Trim()) == null)
			{
				return $"unknown account '{name}'";
			}
		}

		foreach ((string key, string target) in req.Overrides ?? new Dictionary<string, string>())
		{
			if (!uint.TryParse(key, out uint appId) || appId == 0)
			{
				return $"invalid overrides key: '{key}'";
			}

			if (accounts.Get(target.Trim()) == null)
			{
				return $"unknown account '{target}' in overrides";
			}
		}

		return null;
	}

	// Materializes a validated request into a plan row (shared by create and update).
	internal static CrawlPlan BuildCrawlPlan(
		CreateCrawlPlanRequest req,
		Config cfg,
		string id,
		DateTimeOffset createdAt,
		DateTimeOffset updatedAt,
		int runCount,
		string? lastRunId,
		DateTimeOffset? lastRunAt,
		DateTimeOffset? nextRun) => new CrawlPlan(
		Id: id,
		Name: req.Name!.Trim(),
		AppIds: req.AppIds!.Select(uint.Parse).ToList(),
		Accounts: req.Accounts is { Count: > 0 } ? req.Accounts.Select(a => a.Trim()).ToList() : null,
		Overrides: req.Overrides is { Count: > 0 } ? req.Overrides.ToDictionary(kv => uint.Parse(kv.Key), kv => kv.Value) : null,
		ShardSize: Math.Clamp(req.ShardSize ?? 50, 1, CrawlShardPlanner.MaxShardApps),
		IntervalMs: Math.Clamp(req.IntervalMs ?? cfg.CrawlIntervalMs, 0, 60000),
		Cc: string.IsNullOrWhiteSpace(req.Cc) ? "us" : req.Cc.Trim(),
		Cron: string.IsNullOrWhiteSpace(req.Cron) ? null : req.Cron.Trim(),
		IntervalSeconds: req.IntervalSeconds ?? 0,
		Enabled: req.Enabled ?? true,
		CreatedAt: createdAt,
		UpdatedAt: updatedAt,
		RunCount: runCount,
		LastRunId: lastRunId,
		LastRunAt: lastRunAt,
		NextRunAt: nextRun);

	internal static bool TryFaultKind(string? raw, out FaultKind kind)
	{
		if (string.Equals(raw?.Trim(), "task-dispatch", StringComparison.OrdinalIgnoreCase))
		{
			kind = FaultKind.TaskDispatch;
			return true;
		}

		if (string.Equals(raw?.Trim(), "api-request", StringComparison.OrdinalIgnoreCase))
		{
			kind = FaultKind.ApiRequest;
			return true;
		}

		kind = default;
		return false;
	}

	internal static bool TryFaultMode(string? raw, out FaultMode mode)
	{
		if (string.Equals(raw?.Trim(), "error", StringComparison.OrdinalIgnoreCase))
		{
			mode = FaultMode.Error;
			return true;
		}

		if (string.Equals(raw?.Trim(), "delay", StringComparison.OrdinalIgnoreCase))
		{
			mode = FaultMode.Delay;
			return true;
		}

		mode = default;
		return false;
	}

	internal static string FaultKindText(FaultKind kind) => kind == FaultKind.TaskDispatch ? "task-dispatch" : "api-request";

	internal static string FaultModeText(FaultMode mode) => mode == FaultMode.Error ? "error" : "delay";

	internal static FaultView ToFaultView(FaultSpec spec) => new(
		spec.Id,
		FaultKindText(spec.Kind),
		FaultModeText(spec.Mode),
		spec.Action,
		spec.Region,
		spec.Route,
		spec.Method,
		spec.HttpStatusCode,
		spec.DelayMs,
		spec.BudgetRemaining,
		spec.Fired,
		spec.CreatedAt,
		spec.ExpiresAt);


	/// <summary>
	/// Extracts the W3C trace id from the inbound `traceparent` header. The audit
	/// sink records `trace_id` as the only cross-boundary id (see the Execution
	/// ID convention in docs/architecture.md); a missing or malformed header simply
	/// leaves the field null.
	/// </summary>
	internal static string? TraceIdFromTraceparent(HttpContext ctx)
	{
		if (!ctx.Request.Headers.TryGetValue("traceparent", out StringValues traceparent))
		{
			return null;
		}

		return ActivityContext.TryParse(traceparent, null, out ActivityContext traceContext)
			? traceContext.TraceId.ToString()
			: null;
	}

	internal static async Task WriteAuditLog(
		ILogger logger,
		IAuditStore auditStore,
		HttpContext ctx,
		string action,
		string? accountName = null,
		string? jobId = null,
		IReadOnlyDictionary<string, object?>? details = null)
	{
		string? traceId = TraceIdFromTraceparent(ctx);
		var payload = JsonSerializer.Serialize(details ?? new Dictionary<string, object?>(), Vapor.Protocol.JsonDefaults.Options);
		logger.LogInformation(
			"AUDIT action={Action} actor={Actor} ip={RemoteIp} account={AccountName} jobId={JobId} traceId={TraceId} details={Details}",
			action,
			GetAuditActor(ctx),
			ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			SensitiveDataRedactor.SanitizeLogValue(accountName),
			SensitiveDataRedactor.SanitizeLogValue(jobId),
			SensitiveDataRedactor.SanitizeLogValue(traceId),
			SensitiveDataRedactor.Redact(payload));

		// Persist with the same redaction guarantees as the structured log.
		var entry = AuditStoreExtensions.CreateEntry(
			action,
			GetAuditActor(ctx),
			remoteIp: ctx.Connection.RemoteIpAddress?.ToString(),
			accountName: accountName,
			jobId: jobId,
			traceId: traceId,
			details: details);

		try
		{
			await auditStore.RecordAsync(entry, ctx.RequestAborted);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Audit persistence must never block the API response.
			logger.LogError(ex, "Failed to persist audit entry {Action}", action);
		}
	}

	internal static async Task<IResult> RunOfferDecisionAsync(
		HttpContext ctx,
		Config cfg,
		IAuditStore audit,
		ILogger auditLogger,
		AccountStore accounts,
		IJobStore store,
		string name,
		string action,
		ulong offerId,
		Dictionary<string, object?> payload,
		string auditAction,
		Dictionary<string, object?> details,
		bool autoConfirm = false)
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

		TaskRunResult run = await AccountTaskRunner.DispatchAsync(store, action, spec.AccountName, payload, ctx.RequestAborted);

		await WriteAuditLog(
			auditLogger,
			audit,
			ctx,
			auditAction,
			accountName: spec.AccountName,
			jobId: run.JobId,
			details: new Dictionary<string, object?>(details) { ["outcome"] = run.Status.ToString() });

		if (run.Status == JobTaskStatus.Finished)
		{
			Dictionary<string, object?>? mobileConfirmation = null;

			// Accept succeeded but Steam still wants a mobile confirmation — finish the
			// loop with confirm_trade_offer. The identity secret never leaves the agent:
			// the confirm action reads it from the agent-side credential store.
			if (autoConfirm && OutputFlagIsTrue(run.Output, "requires_mobile_confirmation"))
			{
				TaskRunResult confirm = await AccountTaskRunner.DispatchAsync(
					store,
					AccountTaskRunner.ConfirmTradeOfferAction,
					spec.AccountName,
					new Dictionary<string, object?>
					{
						["trade_offer_id"] = offerId.ToString(System.Globalization.CultureInfo.InvariantCulture)
					},
					ctx.RequestAborted);

				await WriteAuditLog(
					auditLogger,
					audit,
					ctx,
					"trade_offer.confirm",
					accountName: spec.AccountName,
					jobId: confirm.JobId,
					details: new Dictionary<string, object?> { ["offerId"] = offerId.ToString(), ["outcome"] = confirm.Status.ToString() });

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
				// Still queued when the window closed: confirmed/error stay absent and the
				// caller keeps polling via the confirm job id.
			}

			return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, result = run.Output, mobile_confirmation = mobileConfirmation });
		}

		if (run.Status != JobTaskStatus.Queued)
		{
			return Results.Json(new { job_id = run.JobId, error = run.Error ?? $"task ended as {run.Status}" }, statusCode: 502);
		}

		return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
	}

	/// <summary>
	/// Shared body of the achievement unlock/reset endpoints: the control-plane
	/// side of the double gate from round 33 (git history) — names must be an explicit non-empty list
	/// (no implicit full-batch path exists) and reset additionally demands
	/// confirm: true. The action layer enforces the same two gates independently.
	/// </summary>
	internal static async Task<IResult> DispatchAchievementWrite(
		HttpContext ctx,
		Config cfg,
		IAuditStore audit,
		ILogger auditLogger,
		AccountStore accounts,
		IJobStore store,
		string name,
		AchievementWriteRequest? req,
		bool unlock)
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

		if (req is null || req.AppId is not { } appId || appId == 0)
		{
			return Results.BadRequest(new ErrorResponse("appId is required (positive app id)"));
		}

		List<string> names = (req.Names ?? [])
			.Where(n => !string.IsNullOrWhiteSpace(n))
			.Select(n => n.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (names.Count == 0)
		{
			return Results.BadRequest(new ErrorResponse("names is required: an explicit non-empty list of achievement API names (no implicit full-batch path exists)"));
		}

		if (!unlock && req.Confirm != true)
		{
			return Results.BadRequest(new ErrorResponse("confirm must be explicitly true for a reset (destructive operation)"));
		}

		TaskRunResult run = unlock
			? await AccountTaskRunner.UnlockAchievementsAsync(store, spec.AccountName, appId, names, ctx.RequestAborted)
			: await AccountTaskRunner.ResetAchievementsAsync(store, spec.AccountName, appId, names, ctx.RequestAborted);

		await WriteAuditLog(
			auditLogger,
			audit,
			ctx,
			unlock ? "achievement.unlock" : "achievement.reset",
			accountName: spec.AccountName,
			jobId: run.JobId,
			details: new Dictionary<string, object?>
			{
				["appId"] = appId,
				["names"] = names.ToList(),
				["outcome"] = run.Status.ToString()
			});

		if (run.Status == JobTaskStatus.Finished)
		{
			return Results.Ok(new { job_id = run.JobId, account = spec.AccountName, app_id = appId, result = run.Output });
		}

		if (run.Status != JobTaskStatus.Queued)
		{
			return Results.Json(new { job_id = run.JobId, error = run.Error ?? $"task ended as {run.Status}" }, statusCode: 502);
		}

		return Results.Accepted($"/v1/jobs/{run.JobId}", new { job_id = run.JobId, status = PendingStatus });
	}

	/// <summary>
	/// Reads a boolean flag from a task output dictionary, tolerating both in-memory
	/// values and the JsonElement shapes a SQLite JSON round-trip produces.
	/// </summary>
	internal static bool OutputFlagIsTrue(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
		{
			return false;
		}

		return raw switch
		{
			bool b => b,
			JsonElement { ValueKind: JsonValueKind.True } => true,
			JsonElement { ValueKind: JsonValueKind.String } s => bool.TryParse(s.GetString(), out bool parsed) && parsed,
			_ => false
		};
	}

	/// <summary>
	/// Reads a string field out of a task output dictionary, tolerating both live string
	/// values and the JsonElement shape a SQLite JSON round-trip produces.
	/// </summary>
	internal static string? OutputString(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
		{
			return null;
		}

		return raw switch
		{
			string s => s,
			JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
			_ => raw.ToString()
		};
	}

	/// <summary>
	/// Strips embedded credentials from a proxy endpoint before it lands in an
	/// audit trail: any scheme://user:pass@host form is recorded as
	/// scheme://&lt;redacted&gt;@host; endpoints without credentials pass through unchanged.
	/// </summary>
	internal static string MaskProxyCredentials(string proxy)
	{
		return System.Text.RegularExpressions.Regex.Replace(proxy, "//[^@/]+@", "//<redacted>@");
	}

	internal static bool IsLoginAuditEvent(string normalizedEventType, string state)
	{
		return string.Equals(state, "Connected", StringComparison.Ordinal) ||
			   string.Equals(state, "LoggedOn", StringComparison.Ordinal) ||
			   string.Equals(state, "LoginFailed", StringComparison.Ordinal) ||
			   string.Equals(state, "LoggedOff", StringComparison.Ordinal) ||
			   string.Equals(state, "Disconnected", StringComparison.Ordinal) ||
			   normalizedEventType.Contains("login", StringComparison.OrdinalIgnoreCase);
	}

	internal static bool IsSensitiveTaskAction(string action)
	{
		return action.StartsWith("SendTradeOffer", StringComparison.OrdinalIgnoreCase) ||
			   action.StartsWith("AcceptTradeOffer", StringComparison.OrdinalIgnoreCase) ||
			   action.StartsWith("DeclineTradeOffer", StringComparison.OrdinalIgnoreCase) ||
			   action.StartsWith("CancelTradeOffer", StringComparison.OrdinalIgnoreCase) ||
			   action.StartsWith("GetInventory", StringComparison.OrdinalIgnoreCase) ||
			   action.StartsWith("RedeemKey", StringComparison.OrdinalIgnoreCase);
	}

	internal static string GetAuditActor(HttpContext ctx)
	{
		if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor) && !StringValues.IsNullOrEmpty(forwardedFor))
		{
			return forwardedFor.ToString();
		}

		return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
	}
}
