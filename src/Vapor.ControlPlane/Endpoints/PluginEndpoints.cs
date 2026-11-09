using Vapor.Protocol;
using static Vapor.ControlPlane.EndpointHelpers;

namespace Vapor.ControlPlane;

/// <summary>Registers the plugin routes.</summary>
internal static class PluginEndpoints
{
	internal static void MapPluginEndpoints(this WebApplication app, ILogger auditLogger)
	{
		app.MapGet("/v1/plugins/catalog", async Task<IResult> (HttpContext ctx, Config cfg, PluginCatalogService catalog) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			PluginCatalog snapshot = await catalog.GetCatalogAsync(ctx.RequestAborted);
			if (!snapshot.Configured)
			{
				return Results.Ok(new { configured = false, entries = Array.Empty<PluginIndexEntry>() });
			}

			return Results.Ok(new { configured = true, source = snapshot.Source, fetchedAt = snapshot.FetchedAt, error = snapshot.Error, entries = snapshot.Entries });
		})
			.WithTags("Plugins")
			.WithSummary("Browse the plugin index source (cached 60s)")
			.Produces(200)
			.Produces(401);
		app.MapGet("/v1/plugins/installed", (HttpContext ctx, Config cfg, PluginInventory inventory) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			var agents = inventory.Snapshot().OrderBy(kv => kv.Key, StringComparer.Ordinal)
				.Select(kv => new { agentId = kv.Key, reportedAt = kv.Value.ReportedAt, plugins = kv.Value.Plugins, updates = kv.Value.Updates })
				.ToList();
			return Results.Ok(new { agents });
		})
			.WithTags("Plugins")
			.WithSummary("Last-reported plugin inventory per agent (mirrored from plugin_* task outputs)")
			.Produces(200)
			.Produces(401);
		app.MapPost("/v1/plugins/install", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, IJobStore store, AgentRegistry agents, PluginCatalogService catalog, PluginInstallRequest request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			string? url = request.Url?.Trim();
			string? sha256 = Sha256Normalizer.Normalize(request.Sha256);
			if (request.AgentIds is not { Count: > 0 })
			{
				return Results.BadRequest(new ErrorResponse("agentIds must name at least one agent"));
			}

			string? pluginId = request.PluginId?.Trim();
			PluginIndexEntry? catalogEntry = null;
			if (string.IsNullOrEmpty(url))
			{
				// Catalog mode: resolve url/sha256 from the index by plugin id.
				if (string.IsNullOrEmpty(pluginId))
				{
					return Results.BadRequest(new ErrorResponse("either url or pluginId is required"));
				}

				PluginCatalog snapshot = await catalog.GetCatalogAsync(ctx.RequestAborted);
				PluginIndexEntry? entry = snapshot.Entries.FirstOrDefault(e => string.Equals(e.Id, pluginId, StringComparison.OrdinalIgnoreCase));
				if (entry is null)
				{
					return Results.NotFound(new ErrorResponse($"plugin '{pluginId}' is not in the catalog"));
				}

				url = entry.Url;
				sha256 = entry.Sha256;
				pluginId = entry.Id;
				catalogEntry = entry;
			}
			else if (sha256 is null)
			{
				return Results.BadRequest(new ErrorResponse("sha256 must be a 64-character hex digest of the package"));
			}

			// sha256 is non-null here by construction: the direct mode returned above on
			// a missing digest, and catalog entries always carry a normalized checksum.
			var payload = new Dictionary<string, object?>
			{
				["url"] = url!,
				["sha256"] = sha256
			};
			if (!string.IsNullOrEmpty(pluginId))
			{
				payload["pluginId"] = pluginId;
			}

			if (request.Version is { } version && version.Trim().Length > 0)
			{
				payload["version"] = version.Trim();
			}

			// Catalog mode carries the index's own trust/permissions/apiVersion/publisher
			// declaration into the instruction: the agent reconciles it against the package
			// manifest, so the metadata the operator browsed is verified together with the
			// content, not on separate channels. Fields the index does not declare are not
			// checked. A declared package signature becomes a verification instruction.
			if (catalogEntry is { } declared)
			{
				if (declared.Trust is { } declaredTrust)
				{
					payload["expectedTrust"] = declaredTrust;
				}

				if (declared.Permissions is { Count: > 0 } declaredPermissions)
				{
					payload["expectedPermissions"] = declaredPermissions;
				}

				payload["expectedApiVersion"] = declared.ApiVersion;

				if (declared.Publisher is { } declaredPublisher)
				{
					payload["expectedPublisher"] = declaredPublisher;
				}

				// The declared package signature travels as two keys: a trust
				// instruction that is only meaningful when both halves are staged, so a
				// half declaration is left un-staged rather than sent half-built.
				if (declared.Signing is { } declaredSigning)
				{
					payload["expectedSigningPublicKey"] = declaredSigning.PublicKey;
					payload["expectedSigningSignature"] = declaredSigning.Signature;
				}
			}

			var dispatched = new List<object>();
			foreach (string agentId in request.AgentIds.Distinct(StringComparer.Ordinal))
			{
				string? region = agents.Get(agentId)?.Hello.Region;
				var created = await store.CreateJob(new CreateJobRequest(
					"plugin_install",
					region,
					[HostTaskTarget.For(agentId)],
					payload,
					new Dictionary<string, string> { ["origin"] = "plugins-api" }
				), ctx.RequestAborted);
				dispatched.Add(new { agentId, jobId = created.Job.Id, taskId = created.Tasks.Single().Id });
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"plugin_install_dispatched",
				details: new Dictionary<string, object?>
				{
					["pluginId"] = pluginId,
					["version"] = request.Version,
					["publisher"] = catalogEntry?.Publisher,
					["signed"] = catalogEntry?.Signing is not null,
					["url"] = url,
					["agents"] = request.AgentIds,
					["jobs"] = dispatched.Count
				});
			return Results.Accepted("/v1/plugins/installed", new { jobs = dispatched });
		})
			.WithTags("Plugins")
			.WithSummary("Dispatch plugin installs to the named agents (202; watch /v1/jobs/{id})")
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(404)
			.Produces(401);
		app.MapPost("/v1/plugins/uninstall/{pluginId}", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, IJobStore store, AgentRegistry agents, string pluginId, PluginUninstallRequest request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			if (request.AgentIds is not { Count: > 0 })
			{
				return Results.BadRequest(new ErrorResponse("agentIds must name at least one agent"));
			}

			string id = pluginId.Trim();
			if (id.Length == 0)
			{
				return Results.BadRequest(new ErrorResponse("pluginId is required"));
			}

			var payload = new Dictionary<string, object?> { ["pluginId"] = id };
			var dispatched = new List<object>();
			foreach (string agentId in request.AgentIds.Distinct(StringComparer.Ordinal))
			{
				string? region = agents.Get(agentId)?.Hello.Region;
				var created = await store.CreateJob(new CreateJobRequest(
					"plugin_uninstall",
					region,
					[HostTaskTarget.For(agentId)],
					payload,
					new Dictionary<string, string> { ["origin"] = "plugins-api" }
				), ctx.RequestAborted);
				dispatched.Add(new { agentId, jobId = created.Job.Id, taskId = created.Tasks.Single().Id });
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"plugin_uninstall_dispatched",
				details: new Dictionary<string, object?>
				{
					["pluginId"] = id,
					["agents"] = request.AgentIds,
					["jobs"] = dispatched.Count
				});
			return Results.Accepted("/v1/plugins/installed", new { jobs = dispatched });
		})
			.WithTags("Plugins")
			.WithSummary("Dispatch plugin uninstalls to the named agents (202; watch /v1/jobs/{id})")
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces(401);
		app.MapPost("/v1/plugins/inventory/refresh", async Task<IResult> (HttpContext ctx, Config cfg, IJobStore store, AgentRegistry agents, PluginInventory inventory, RefreshInventoryRequest? request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			List<string> targets = request?.AgentIds is { Count: > 0 }
				? request.AgentIds.Distinct(StringComparer.Ordinal).ToList()
				: agents.ListConnected().Select(a => a.Hello.AgentId).ToList();
			if (targets.Count == 0)
			{
				return Results.Conflict(new ErrorResponse("no connected agents to refresh"));
			}

			var dispatched = new List<object>();
			foreach (string agentId in targets)
			{
				string? region = agents.Get(agentId)?.Hello.Region;
				var created = await store.CreateJob(new CreateJobRequest(
					"plugin_list",
					region,
					[HostTaskTarget.For(agentId)],
					new Dictionary<string, object?>(),
					new Dictionary<string, string> { ["origin"] = "plugins-api" }
				), ctx.RequestAborted);
				dispatched.Add(new { agentId, jobId = created.Job.Id, taskId = created.Tasks.Single().Id });
			}

			// Explicit refresh intent: stale mirror entries for agents that no longer
			// report drop out of the snapshot view.
			foreach (string agentId in inventory.Snapshot().Keys.Where(id => !targets.Contains(id, StringComparer.Ordinal)).ToList())
			{
				inventory.Remove(agentId);
			}

			return Results.Accepted("/v1/plugins/installed", new { jobs = dispatched });
		})
			.WithTags("Plugins")
			.WithSummary("Re-run plugin_list on the named (or all connected) agents to re-sync the mirror")
			.Produces(202)
			.Produces<ErrorResponse>(409)
			.Produces(401);
		app.MapPost("/v1/plugins/update-check", async Task<IResult> (HttpContext ctx, Config cfg, IAuditStore audit, IJobStore store, AgentRegistry agents, PluginCatalogService catalog, PluginUpdateCheckRequest? request) =>
		{
			if (!Auth.TryAdmin(cfg, GetAuthorization(ctx), out _))
			{
				return Results.Unauthorized();
			}

			PluginCatalog snapshot = await catalog.GetCatalogAsync(ctx.RequestAborted);
			if (!snapshot.Configured)
			{
				return Results.BadRequest(new ErrorResponse("plugin index is not configured (set Vapor_PLUGIN_INDEX_URL)"));
			}

			List<string> targets = request?.AgentIds is { Count: > 0 }
				? request.AgentIds.Distinct(StringComparer.Ordinal).ToList()
				: agents.ListConnected().Select(a => a.Hello.AgentId).ToList();
			if (targets.Count == 0)
			{
				return Results.Conflict(new ErrorResponse("no connected agents to check"));
			}

			// The whole index rides along as candidates: each agent classifies every
			// candidate against its own loaded plugins (updateAvailable / upToDate /
			// notComparable / notInstalled) and reports back — nothing is downloaded and
			// nothing is replaced by this action. The declared publisher rides with each
			// candidate so the agent can flag a would-be publisher change on an update.
			var payload = new Dictionary<string, object?>
			{
				["candidates"] = snapshot.Entries
					.OrderBy(e => e.Id, StringComparer.Ordinal)
					.Select(e =>
					{
						// Undeclared stays absent rather than riding as a null the payload
						// store would persist: candidates are compared per declared claim.
						var candidate = new Dictionary<string, object?> { ["id"] = e.Id, ["version"] = e.Version };
						if (e.Publisher is { } declaredPublisher)
						{
							candidate["publisher"] = declaredPublisher;
						}

						return (object)candidate;
					})
					.ToList()
			};

			var dispatched = new List<object>();
			foreach (string agentId in targets)
			{
				string? region = agents.Get(agentId)?.Hello.Region;
				var created = await store.CreateJob(new CreateJobRequest(
					"plugin_update_check",
					region,
					[HostTaskTarget.For(agentId)],
					payload,
					new Dictionary<string, string> { ["origin"] = "plugins-api" }
				), ctx.RequestAborted);
				dispatched.Add(new { agentId, jobId = created.Job.Id, taskId = created.Tasks.Single().Id });
			}

			await WriteAuditLog(
				auditLogger,
				audit,
				ctx,
				"plugin_update_check_dispatched",
				details: new Dictionary<string, object?>
				{
					["agents"] = targets,
					["candidates"] = snapshot.Entries.Count,
					["jobs"] = dispatched.Count
				});
			return Results.Accepted("/v1/plugins/installed", new { jobs = dispatched });
		})
			.WithTags("Plugins")
			.WithSummary("Dispatch plugin_update_check to the named (or all connected) agents (202; watch /v1/jobs/{id})")
			.Produces(202)
			.Produces<ErrorResponse>(400)
			.Produces<ErrorResponse>(409)
			.Produces(401);
	}
}
