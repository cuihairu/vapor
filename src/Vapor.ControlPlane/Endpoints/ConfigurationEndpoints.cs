using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the configuration routes.</summary>
internal static class ConfigurationEndpoints
{
	internal static void MapConfigurationEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/config", (HttpContext ctx, Config cfg, ConfigStore configStore) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			return Results.Ok(new
			{
				global = configStore.GetGlobal(),
				accounts = configStore.ListAccounts()
			});
		})
			.WithTags("Config")
			.WithSummary("Get global and per-account configuration")
			.Produces(200)
			.Produces<ErrorResponse>(401);
		app.MapPut("/v1/config/global", async (HttpContext ctx, Config cfg, IAuditStore audit, ConfigStore configStore, PutGlobalConfigRequest req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			var updated = configStore.SetGlobal(req.Settings, req.UpdatedBy);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"config.global.updated",
				details: new Dictionary<string, object?>
				{
					["updatedBy"] = req.UpdatedBy,
					["settings"] = req.Settings
				});
			return Results.Ok(updated);
		})
			.WithTags("Config")
			.WithSummary("Replace global settings")
			.Produces(200)
			.Produces<ErrorResponse>(401);
		app.MapPut("/v1/config/account/{name}", async (HttpContext ctx, Config cfg, IAuditStore audit, ConfigStore configStore, string name, PutAccountConfigRequest req) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (string.IsNullOrWhiteSpace(name))
			{
				return Results.BadRequest(new ErrorResponse("account name is required"));
			}

			var updated = configStore.SetAccount(name, req.Enabled, req.Region, req.Labels, req.Settings, req.UpdatedBy);
			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"config.account.updated",
				accountName: name,
				details: new Dictionary<string, object?>
				{
					["updatedBy"] = req.UpdatedBy,
					["enabled"] = req.Enabled,
					["region"] = req.Region,
					["labels"] = req.Labels,
					["settings"] = req.Settings
				});
			return Results.Ok(updated);
		})
			.WithTags("Config")
			.WithSummary("Replace per-account settings (enabled, region, labels, settings)")
			.Produces(200)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(401);
	}
}
