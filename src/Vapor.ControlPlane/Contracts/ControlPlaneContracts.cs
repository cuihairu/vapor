using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Request/response bodies bound by the minimal-API endpoint groups. Kept in one
/// file because they are a single wire contract, not behaviour.
/// </summary>
// Request type for session events from agents
public sealed record SessionEventRequest(
	string AccountName,
	string? EventType,
	string? State,
	string? Message
);

public sealed record PutGlobalConfigRequest(
	IReadOnlyDictionary<string, object?>? Settings,
	string? UpdatedBy
);

public sealed record PutAccountConfigRequest(
	bool Enabled = true,
	string? Region = null,
	IReadOnlyList<string>? Labels = null,
	IReadOnlyDictionary<string, object?>? Settings = null,
	string? UpdatedBy = null
);

// Request type for declaring/replacing a farm account (desired state metadata only; credentials stay agent-side)
public sealed record PutAccountRequest(
	bool Enabled = true,
	AccountDesiredState DesiredState = AccountDesiredState.Offline,
	IReadOnlyList<string>? IdleApps = null,
	string? Region = null,
	string? AgentId = null,
	string? Note = null,
	string? UpdatedBy = null,
	bool? MarketListingsEnabled = null,
	IReadOnlyList<BoostTarget>? BoostTargets = null,
	TradePolicy? TradePolicy = null,
	FarmPolicy? FarmPolicy = null,
	AccountRole Role = AccountRole.Farm,
	string? SteamId = null
);

// Request body for trade offer accept/decline endpoints
public sealed record TradeOfferDecisionRequest(
	string? PartnerSteamId = null,
	bool? VerifyState = null,
	bool? AutoConfirm = null
);

// Request body for the batch mobile confirmation endpoint
public sealed record ConfirmationsBatchRequest(
	string? Operation = null,
	string? Type = null
);

// Request body for the market listing cancel endpoint (all filters optional
// together, but a real run — dry_run=false — requires at least one)
public sealed record MarketCancelRequest(
	uint? AppId = null,
	string? MarketHashName = null,
	int? MinPriceCents = null,
	int? MaxPriceCents = null,
	int? OlderThanSeconds = null,
	bool? DryRun = null
);

// Request body for creating one market listing. Price on either side
// (exactly one); send=false (default) is a dry run reporting the pricing plan.
public sealed record MarketCreateListingRequest(
	uint? AppId = null,
	string? ContextId = null,
	string? AssetId = null,
	int? Amount = null,
	int? SellerProceedsCents = null,
	int? BuyerPriceCents = null,
	bool? Send = null
);

// Request body for the points shop claim endpoint (redeem reward definitions)
public sealed record PointsShopClaimRequest(
	List<uint>? DefinitionIds = null,
	bool? Force = null
);

// Request body for the loot endpoint (send tradable inventory to a partner).
// force=true overrides a standing-check quarantine on the source account
// (audited) — the rescue path for emptying an account Steam has already
// flagged; default false keeps flagged accounts quiet.
public sealed record LootRequest(
	string? PartnerSteamId = null,
	string? TradeUrl = null,
	string? Message = null,
	int[]? AppIds = null,
	bool Force = false
);

// Request body for the storage-collect orchestration (round 39 P-b; git history): the storage
// account is named explicitly (role=storage + its own SteamId64 declared on
// the spec), the sources are derived from the declared fleet. force=true
// overrides a standing-check quarantine on the storage target (audited);
// quarantined farm sources are skipped regardless — loot one individually
// with the loot endpoint's force to reach it.
public sealed record StorageCollectRequest(
	string? Storage = null,
	string? Message = null,
	int[]? AppIds = null,
	bool Force = false
);

public sealed record SetProxyRequest(string? Proxy = null);

// Request body for the first-party QR render endpoint
public sealed record QrRenderRequest(string? Text = null);

// Request body for the 1:1 swap endpoint (match duplicates and offer a swap)
public sealed record SwapOfferRequest(
	string? PartnerSteamId = null,
	string? TradeUrl = null,
	string? Message = null,
	int[]? AppIds = null,
	int? Keep = null,
	int? MaxSwaps = null,
	bool Send = false
);

// Request body for the free-license endpoint (addlicense)
public sealed record LicenseRequest(
	int[]? AppIds = null,
	int[]? SubIds = null
);

// Request body for the achievement unlock/reset endpoints. The name list is the
// whole contract: there is no "all achievements" path — names must be explicit
// and non-empty; reset additionally requires confirm: true (both enforced here
// and again at the action layer).
public sealed record AchievementWriteRequest(
	uint? AppId = null,
	List<string>? Names = null,
	bool? Confirm = null
);

// Request body for arming a fault injection. kind/mode are mandatory strings
// ("task-dispatch"/"api-request", "error"/"delay"); the selector fields are
// plane-specific (action/region for dispatch drills, route/method for API-edge
// drills — null means "any"), and the bounds mirror FaultInjector's clamps.
// The status field is HttpStatus so the camelCase wire name is "httpStatus".
public sealed record EnableFaultRequest(
	string? Kind = null,
	string? Mode = null,
	string? Action = null,
	string? Region = null,
	string? Route = null,
	string? Method = null,
	int? HttpStatus = null,
	int? DelayMs = null,
	long? Budget = null,
	int? TtlSeconds = null
);

// Read model of an armed fault; kind/mode are the canonical API strings, and
// budgetRemaining/expiresAt are the self-healing bounds an operator watches.
public sealed record FaultView(
	string Id,
	string Kind,
	string Mode,
	string? Action,
	string? Region,
	string? Route,
	string? Method,
	int HttpStatus,
	int DelayMs,
	long BudgetRemaining,
	long Fired,
	DateTimeOffset CreatedAt,
	DateTimeOffset ExpiresAt
);

// Response of the panic button (DELETE /v1/faults).
public sealed record ClearFaultsResponse(int Removed);


// Request body for creating a crawl plan (game-data harvesting). One-shot unless
// Cron or IntervalSeconds is set; StartNow=false arms a recurring schedule later.
public sealed record CreateCrawlPlanRequest(
	string? Name = null,
	List<string>? AppIds = null,
	List<string>? Accounts = null,
	Dictionary<string, string>? Overrides = null,
	int? ShardSize = null,
	int? IntervalMs = null,
	string? Cc = null,
	string? Cron = null,
	int? IntervalSeconds = null,
	bool? StartNow = null,
	bool? Enabled = null
);

// Request bodies for the plugin ecosystem. Install resolves url+sha256 from the
// catalog when only pluginId is given; direct installs must carry the checksum.
public sealed record PluginInstallRequest(
	string? PluginId = null,
	string? Url = null,
	string? Sha256 = null,
	string? Version = null,
	List<string>? AgentIds = null
);

// Body for creating or replacing a stored script (script repository).
public sealed record ExecuteScriptRequest(string AgentId, int? TimeoutSeconds = null);

// Body for creating or replacing a script flow (ordered orchestration steps).
public sealed record UpsertFlowStepRequest(string ScriptId, string AgentId, string? OnFailure = null);

// Body for creating or replacing a script flow (ordered orchestration steps).
// Schedule is the optional recurring plan — the same JobSchedule shape the
// scheduled job templates use; omitting it keeps the flow manual-only (and a
// PUT without a schedule disarms a previously scheduled flow).
public sealed record UpsertFlowRequest(
	string Name,
	string? Description = null,
	List<UpsertFlowStepRequest>? Steps = null,
	JobSchedule? Schedule = null
);

public sealed record UpsertScriptRequest(
	string Name,
	string? Description = null,
	string? Language = null,
	string Content = ""
);

public sealed record PluginUninstallRequest(
	List<string>? AgentIds = null
);

public sealed record RefreshInventoryRequest(
	List<string>? AgentIds = null
);

public sealed record PluginUpdateCheckRequest(
	List<string>? AgentIds = null
);

// Request body for updating a crawl plan; omitted fields keep their current values.
public sealed record UpdateCrawlPlanRequest(
	string? Name = null,
	List<string>? AppIds = null,
	List<string>? Accounts = null,
	Dictionary<string, string>? Overrides = null,
	int? ShardSize = null,
	int? IntervalMs = null,
	string? Cc = null,
	string? Cron = null,
	int? IntervalSeconds = null,
	bool? StartNow = null,
	bool? Enabled = null
);

// Request body for previewing/scubbing historical secret payloads. Dry run is the
// default; a destructive scrub requires confirm=true and may snapshot the database
// first via backupPath.
public sealed record SecretPayloadPurgeRequest(
	bool? DryRun = null,
	bool? Confirm = null,
	string? BackupPath = null
);
