using Microsoft.AspNetCore.Http.HttpResults;
using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the fault routes.</summary>
internal static class FaultEndpoints
{
	internal static void MapFaultEndpoints(this WebApplication app)
	{
		// Fault-injection drills (roadmap §8). Admin-only, runtime-toggled, and
		// self-healing by construction: every armed fault carries a budget and a TTL,
		// and these endpoints are exempt from api-request injection so a drill can
		// always be observed and stopped. See docs/roadmap.md §8 and production.md.

		app.MapGet("/v1/faults", (HttpContext ctx, Config cfg, FaultInjector faults) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			return Results.Ok(faults.List().Select(ToFaultView).ToList());
		})
			.WithTags("Faults")
			.WithSummary("List armed fault injections (admin)")
			.Produces(200)
			.Produces(401);
		app.MapPost("/v1/faults", async Task<Results<Created<FaultView>, BadRequest<ErrorResponse>, UnauthorizedHttpResult>> (
			HttpContext ctx, Config cfg, IAuditStore audit, ILogger<Program> logger, FaultInjector faults, EnableFaultRequest req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return TypedResults.Unauthorized();
			}

			// Distinct messages for the two mandatory fields so a bad mode on a good kind
			// is reported precisely; a missing body reads as a null kind either way.
			if (!TryFaultKind(req.Kind, out FaultKind kind))
			{
				return TypedResults.BadRequest(new ErrorResponse("kind must be 'task-dispatch' or 'api-request'"));
			}

			if (!TryFaultMode(req.Mode, out FaultMode mode))
			{
				return TypedResults.BadRequest(new ErrorResponse("mode must be 'error' or 'delay'"));
			}

			if (req.HttpStatus is int status && (status < FaultInjector.MinHttpStatusCode || status > FaultInjector.MaxHttpStatusCode))
			{
				return TypedResults.BadRequest(new ErrorResponse($"httpStatus must be {FaultInjector.MinHttpStatusCode}..{FaultInjector.MaxHttpStatusCode}"));
			}

			if (req.DelayMs is int delay && (delay < FaultInjector.MinDelayMs || delay > FaultInjector.MaxDelayMs))
			{
				return TypedResults.BadRequest(new ErrorResponse($"delayMs must be {FaultInjector.MinDelayMs}..{FaultInjector.MaxDelayMs}"));
			}

			if (req.Budget is long budget && (budget < FaultInjector.MinBudget || budget > FaultInjector.MaxBudget))
			{
				return TypedResults.BadRequest(new ErrorResponse($"budget must be {FaultInjector.MinBudget}..{FaultInjector.MaxBudget}"));
			}

			if (req.TtlSeconds is int ttl && (ttl < FaultInjector.MinTtlSeconds || ttl > FaultInjector.MaxTtlSeconds))
			{
				return TypedResults.BadRequest(new ErrorResponse($"ttlSeconds must be {FaultInjector.MinTtlSeconds}..{FaultInjector.MaxTtlSeconds}"));
			}

			// Selectors are plane-specific: rejecting the wrong-plane ones keeps an armed
			// fault from silently never firing.
			if (kind == FaultKind.TaskDispatch && (req.Route is not null || req.Method is not null))
			{
				return TypedResults.BadRequest(new ErrorResponse("route/method selectors apply to kind 'api-request' only"));
			}

			if (kind == FaultKind.ApiRequest && (req.Action is not null || req.Region is not null))
			{
				return TypedResults.BadRequest(new ErrorResponse("action/region selectors apply to kind 'task-dispatch' only"));
			}

			FaultSpec spec = faults.Enable(kind, mode, req.Action, req.Region, req.Route, req.Method, req.HttpStatus, req.DelayMs, req.Budget, req.TtlSeconds);
			await WriteAuditLog(logger, audit, ctx, "faults.enable", details: new Dictionary<string, object?>
			{
				["faultId"] = spec.Id,
				["kind"] = FaultKindText(kind),
				["mode"] = FaultModeText(mode),
				["budget"] = spec.Budget,
				["ttlSeconds"] = (int)(spec.ExpiresAt - spec.CreatedAt).TotalSeconds,
			});

			return TypedResults.Created($"/v1/faults/{spec.Id}", ToFaultView(spec));
		})
			.WithTags("Faults")
			.WithSummary("Arm a fault injection (admin): error or delay on task dispatch or /v1 requests, bounded by budget and TTL")
			.Produces<FaultView>(201)
			.Produces<ErrorResponse>(400)
			.Produces(401);
		app.MapDelete("/v1/faults/{faultId}", async Task<Results<Ok, NotFound<ErrorResponse>, UnauthorizedHttpResult>> (
			HttpContext ctx, Config cfg, IAuditStore audit, ILogger<Program> logger, FaultInjector faults, string faultId) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return TypedResults.Unauthorized();
			}

			bool removed = faults.Disable(faultId);
			await WriteAuditLog(logger, audit, ctx, "faults.disable", details: new Dictionary<string, object?> { ["faultId"] = faultId, ["removed"] = removed });
			return removed ? TypedResults.Ok() : TypedResults.NotFound(new ErrorResponse($"fault {faultId} is not armed"));
		})
			.WithTags("Faults")
			.WithSummary("Disarm one fault injection (admin)")
			.Produces(200)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapDelete("/v1/faults", async Task<Results<Ok<ClearFaultsResponse>, UnauthorizedHttpResult>> (
			HttpContext ctx, Config cfg, IAuditStore audit, ILogger<Program> logger, FaultInjector faults) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return TypedResults.Unauthorized();
			}

			int removed = faults.ClearAll();
			await WriteAuditLog(logger, audit, ctx, "faults.clear", details: new Dictionary<string, object?> { ["removed"] = removed });
			return TypedResults.Ok(new ClearFaultsResponse(removed));
		})
			.WithTags("Faults")
			.WithSummary("Disarm every fault injection — panic button (admin)")
			.Produces(200)
			.Produces(401);
	}
}
