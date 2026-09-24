# REST API reference

The control plane exposes every operational capability as an HTTP API under `/v1` — 50 routes (58 operations) — plus `/`, `/healthz` and `/metrics`. The same surface drives the admin console, so anything the UI can do, this reference documents how to do with `curl`. An OpenAPI document is available at `/swagger` when `Vapor_ENABLE_SWAGGER=true`.

Base URL in the compose dev setup: `http://127.0.0.1:8080`. Key management, TLS and network hardening for real deployments: [production.md](production.md). A walkthrough that strings these endpoints into a working farm: [getting-started.md](getting-started.md).

## 1. General conventions

### Base URL

- No base URL is hard-coded; the control plane is a plain ASP.NET Core `WebApplication` (listen address via standard `ASPNETCORE_URLS`).
- The Docker image `EXPOSE 8080` and `docker-compose.yml` maps `${VAPOR_CONTROLPLANE_PORT:-8080}:8080`, so `http://<host>:8080` is the de-facto base URL.
- Swagger UI (when `Vapor_ENABLE_SWAGGER=true`) is mounted at `/swagger` with document title "Vapor Control Plane API" (`v1`).
- `GET /` 302-redirects to `/admin.html` (static admin UI served from `wwwroot`).

### Authentication

Two static bearer token sets, both configured by environment variables (`Config.LoadFromEnvironment`):

| Role | Env var | Format |
|---|---|---|
| Admin | `VAPOR_ADMIN_API_KEY` | single token (compose dev default `admin-dev-token`) |
| Agent | `VAPOR_AGENT_API_KEYS` | comma-separated list of tokens (dev default `agent-dev-token`) |

Header convention (matching the OpenAPI security definition `bearer`): `Authorization: Bearer <token>`.

- `Auth.TryAdmin` — token equals `AdminApiKey` exactly (ordinal).
- `Auth.TryAgent` — token is a member of `AgentApiKeys` (ordinal).
- **Query-string fallback** (`GetAuthorization`): when the `Authorization` header is absent, a `?authorization=` query parameter is accepted, with or without the `Bearer ` prefix (added if missing). This exists for SSE/WebSocket clients that cannot set headers.
- **Exempt from auth** (public): `GET /`, `GET /healthz`, `GET /metrics`, static files under `wwwroot` (`/admin.html`, …), `/swagger` (when enabled).
- **Dual-auth endpoints** (admin OR agent key): `POST /v1/sessions/events`, `GET /v1/auth/challenges/events` (agents see a filtered view), `GET /v1/agent/ws` (agent key only).
- Every other `/v1` endpoint requires the **admin** key.

Unauthorized responses: `Results.Unauthorized()` / `TypedResults.Unauthorized()` / bare status-code write on SSE — a **401 with an empty body** (some `.Produces<ErrorResponse>(401)` metadata exists on routes, but the handlers emit no body).

### JSON conventions

`ConfigureHttpJsonOptions` and `Vapor.Protocol.JsonDefaults.Options` (used for SSE payloads and WebSocket frames) are identical:

- `PropertyNamingPolicy = CamelCase` → record properties bind/serialize camelCase (e.g. `PartnerSteamId` → `partnerSteamId`).
- `DefaultIgnoreCondition = WhenWritingNull` → null properties are **omitted** from responses.
- `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` → enums serialize as camelCase strings (`desiredState: "offline"`, `status: "queued"`, `priorityOrder: "cardsDescending"`, `missed: "runOnce"`, `overlap: "allow"`, `passwordFormat: "plainText"`).
- Several inline response bodies deliberately use **snake_case** keys typed verbatim in C# anonymous objects (`job_id`, `app_id`, `market_listings`, `points_shop`, `mobile_confirmation`) — these are copied literally below; the camelCase policy does not rename them (it only lowercases the first character).

### Error body shape

`ErrorResponse(string Error)` → `{ "error": "<message>" }`. Used for 400/404/409 bodies. `401` has no body (see above). Account-task endpoints additionally return a distinct **502** body `{ "job_id": "...", "error": "..." }` when the dispatched agent task ends in a non-queued failure state.

### Route parameter constraints

No ASP.NET route constraints are declared (`{name}`, `{offerId}`, `{jobId}`, `{accountName}`, `{pluginId}`, `{id}` are plain strings); validation is done inside handlers (e.g. `offerId` must parse as `ulong > 0`, `appId` query must parse as `uint > 0`).

---

## 2. SSE event endpoints — mechanics

Shared by `GET /v1/jobs/{jobId}/events`, `GET /v1/jobs/events`, `GET /v1/sessions/events`, `GET /v1/auth/challenges/events`:

- Response headers: `Content-Type: text/event-stream`, `Cache-Control: no-cache`, `Connection: keep-alive`.
- First frame sent immediately: `event: ready\ndata: {}\n\n`.
- Every subsequent event: `event: <type>\ndata: <json>\n\n`, JSON serialized with `JsonDefaults.Options` (camelCase, enums as camelCase strings, nulls omitted).
- Auth via the standard bearer check — browsers use the `?authorization=` query fallback.
- Backed by `EventBroker` in-memory bounded channels (capacity 256, `DropOldest`): slow consumers may silently drop events. No replay/history.
- Query filtering: session and challenge streams accept an optional `accountName` query parameter (no value = all accounts, key `*`).

Event payload shapes:

- **Job events** (`Event` record): `{ id, jobId, type, ts, payload? }` where `payload` is an open `Dictionary<string, object?>`. Frame `event:` name equals the event `type`. Types published by the code:
  - `job.created` (payload `action`, `targets`), `job.canceled` (no payload)
  - `task.dispatched` (`taskId`, `agentId`), `task.finished` (`taskId`, `success`, `job`), `task.failed` (`taskId`, `error`, `job`), `task.dispatch_failed` / `task.enqueue_failed` (`taskId`, `attempt`, `error`, `agentId?`)
  - `job.scheduled_triggered` / `job.scheduled_skipped` / `job.scheduled_completed` (recurring scheduler)
  - `trade.auto_accepted`, `account.farm_progress`, `account.standing_alert`, `account.standing_released`, `account.reconciled` (orchestrator; some with `jobId`, some global)
  - `agent.connected` / `agent.disconnected` (`agentId`, `region`; `jobId` null)
  - `crawl.run_triggered`, `crawl.run_completed`, `crawl.run_skipped` (crawl worker; payload `plan_id`, `run_id`, `status` (`completed`|`failed`|`timeout`|`skipped`), `total`, `ok`, `failed` — snake_case keys). Note: `crawl.run_failed` (dispatch exception) is written to the audit log only, never to the SSE broker.
- **Session events** (`SessionEvent` record): frame `event: session.<eventType>`; payload `{ id, accountName, eventType, state, message?, timestamp }`.
- **Auth challenge events** (`AuthChallengeEvent` record): frame `event: auth.<challengeType>`; payload `{ id, accountName, challengeType, message?, code?, timestamp, jobId? }`. Access rules:
  - Admin: sees all challenges, but `code_provided_*` events have `code` stripped (`null`) before sending.
  - Agent: sees **only** `code_provided_*` events, with the `code` intact (that is the delivery channel to the agent).
  - `challengeType` values: `auth_code_required`, `2fa_required`, `qr_required`, `code_provided_email`, `code_provided_totp`, `code_provided_2fa`.

---

## 3. Agent WebSocket — auth handshake

`GET /v1/agent/ws` — the agent tunnel. Auth sequence:

1. Bearer token must match one of `VAPOR_AGENT_API_KEYS` (`Auth.TryAgent`) — header or `?authorization=` fallback; otherwise 401 (empty body).
2. Must be a WebSocket upgrade request, else 400 `{ "error": "websocket required" }`.
3. Query parameters `agentId` and `region` are both required, else 400 `{ "error": "agentId and region are required" }`.
4. First WebSocket frame must be JSON `{ "type": "hello", "hello": { "agentId": "...", "region": "...", "capabilities": { "<action>": true|false, ... }, "meta": { ... } } }` (`WSMessage`/`AgentHello`) with `hello.agentId` / `hello.region` equal to the query params; otherwise the socket is closed with close status `PolicyViolation`, message "hello required".
5. After registration the server pushes `{type: "task", task: JobTask, traceHeaders?}` frames, `{type: "task_cancel", taskCancel: {taskId, attempt, ts, reason?}}` frames, and accepts inbound `task_result` (`{taskId, success, error?, output?, finishedAt, attempt}`) and `task_heartbeat` (`{taskId, attempt, ts}`) frames (unknown/stale heartbeats are ignored, not errors).

---

## 4. Endpoints by resource domain

Legend for the account-task pattern: many `/v1/accounts/{name}/...` endpoints dispatch a one-target job to the account's agent and **block up to 30 s** polling the task (`AccountTaskRunner.WaitWindow`, poll 200 ms):

- Task `Finished` → `200` with the task output embedded.
- Still `Queued` when the window closes → `202` with `Location: /v1/jobs/{jobId}` and body `{ "job_id": "<id>", "status": "pending" }` (caller keeps polling `GET /v1/jobs/{jobId}`).
- Task `Failed`/`Canceled` (or any non-queued terminal state) → `502` with `{ "job_id": "<id>", "error": "<msg>" }`.
- Account not declared → `404 { "error": "account '<name>' is not declared" }`.

---

### 4.1 Auth / challenges

#### `GET /v1/auth/challenges`
- Purpose: list pending login challenges (2FA / auth-code prompts) currently held in the in-memory tracker.
- Auth: admin.
- Body: none. Query: none.
- 200: `{ "challenges": [ AuthChallengeEvent ] }` — `AuthChallengeEvent = { id, accountName, challengeType, message?, code?, timestamp, jobId? }`, newest first.
- Errors: 401.

#### `POST /v1/auth/challenges/{accountName}/code`
- Purpose: submit a login challenge code for an account; clears the pending challenge and publishes a `code_provided_<type>` auth-challenge event that the agent consumes via SSE.
- Auth: admin.
- Body: free-form JSON object (bound as `Dictionary<string, string?>`):
  - `code` — string, **required** (blank → 400 `code is required`); trimmed.
  - `type` — string, optional; one of `email` | `totp` | `2fa` (lower-cased); **default `email`**; other values → 400 `type must be one of: email, totp, 2fa`.
- 200: `{ "ok": true, "accountName": "<name>", "type": "<email|totp|2fa>" }`
- Errors: 400 (missing code / bad type), 401.
- Route param: `{accountName}` — plain string.
- Audit: `auth.code.submitted` (the `code` value is included in the details dict but redacted by `SensitiveDataRedactor` before persistence — the stored audit never contains the plain code).

#### `GET /v1/auth/challenges/events` (SSE)
- Purpose: live stream of login challenges; admin sees all (with codes redacted on `code_provided_*`), agent sees only `code_provided_*` (with code).
- Auth: admin OR agent.
- Query: `accountName` (string, optional) — filter to one account.
- 200: `text/event-stream`, frames `event: auth.<challengeType>` with `AuthChallengeEvent` JSON payloads (see §2). Initial `event: ready`.
- Errors: 401 (no body).

---

### 4.2 Accounts (declared farm accounts, trade, market, inventory, duplicates, achievements, points shop, loot, standing, licenses, confirmations)

Account spec record — `AccountSpec`: `{ accountName, enabled, desiredState, idleApps?, region?, agentId?, note?, version?, marketListingsEnabled, boostTargets?, tradePolicy?, farmPolicy? }` where
`version = { version: int, updatedAt, updatedBy? }` (`ConfigVersion`),
`boostTargets = [ { appId: uint, targetHours: double } ]` (`BoostTarget`),
`tradePolicy = { autoAcceptGifts: bool, partnerWhitelist?: ulong[] }` (`TradePolicy`),
`farmPolicy = { perGameHourBudget?: double, priorityOrder: "cardsDescending"|"cardsAscending"|"appIdAscending", priorityApps?: uint[] }` (`FarmPolicy`).

#### `GET /v1/accounts`
- Purpose: list declared accounts with filters.
- Auth: admin.
- Query: `state` (optional; case-insensitive `AccountDesiredState` name — `offline` | `online` | `idle` | `farm` | `boost`; unparseable → 400 `unknown desired state '<state>' (expected offline, online or idle)`), `region` (optional, case-insensitive match), `agent` (optional, matches `agentId` case-insensitively).
- 200: `{ "accounts": [ AccountSpec ] }`
- Errors: 400, 401.

#### `GET /v1/accounts/{name}`
- Purpose: aggregate view for one account: spec, live session snapshot, orchestrator view, pending challenge, 10 most recent tasks.
- Auth: admin.
- Body: none.
- 200: `{ "spec": AccountSpec, "session": SessionSnapshot|null, "orchestration": AccountOrchestrationView|null, "pendingChallenge": AuthChallengeEvent|null, "recentTasks": [ JobTask ] }` (nulls omitted).
- Errors: 404 `account '<name>' is not declared`, 401.

#### `PUT /v1/accounts/{name}`
- Purpose: declare or fully replace an account spec (create or full update; version counter increments on update; omitted `marketListingsEnabled` keeps the previous value).
- Auth: admin.
- Body (`PutAccountRequest`, all fields optional unless noted):
  - `enabled` — bool, default `true`
  - `desiredState` — string enum, default `"offline"` (`offline`|`online`|`idle`|`farm`|`boost`)
  - `idleApps` — string[] of numeric app ids, optional (non-numeric entries → 400)
  - `region` — string, optional
  - `agentId` — string, optional
  - `note` — string, optional
  - `updatedBy` — string, optional (audit metadata)
  - `marketListingsEnabled` — bool, optional (null keeps existing value on update)
  - `boostTargets` — `[{ appId: uint, targetHours: number }]`, optional; required to be non-empty when `desiredState = "boost"` (→ 400 otherwise); duplicate app with different targetHours → 400
  - `tradePolicy` — `{ autoAcceptGifts: bool (default false), partnerWhitelist?: string(ulong)[] }`, optional; `autoAcceptGifts=true` with empty whitelist → 400
  - `farmPolicy` — `{ perGameHourBudget?: number, priorityOrder?: string, priorityApps?: uint[] }`, optional; non-finite/≤0 budget → 400
- 200: `{ "spec": AccountSpec }`
- Errors: 400 (blank name or validation via `ArgumentException` message), 401.
- Audit: `account.spec.updated`.

#### `DELETE /v1/accounts/{name}`
- Purpose: remove a declared account spec.
- Auth: admin.
- Body: none.
- 204: empty.
- Errors: 404, 401. Audit: `account.spec.removed`.

#### `POST /v1/accounts/{name}/enable` / `POST /v1/accounts/{name}/disable`
- Purpose: flip `enabled` (resume / pause orchestration), keeping desired state and config.
- Auth: admin. Body: none.
- 200: `{ "spec": AccountSpec }`. Errors: 404, 401. Audit: `account.enabled` / `account.disabled`.

#### `POST /v1/accounts/{name}/standing-check`
- Purpose: schedule an immediate Steam standing check on the account at the next reconcile pass.
- Auth: admin. Body: none.
- 202: `Location: /v1/accounts/{name}`, body `{ "scheduled": true }`.
- Errors: 404, 409 `{ "error": "standing check not scheduled: a job is active or standing checks are disabled" }`, 401. Audit: `standing_check_requested`.

#### `GET /v1/accounts/{name}/trade-offers`
- Purpose: list the account's incoming trade offers (dispatches `get_trade_offers`, bounded wait).
- Auth: admin.
- Query: `activeOnly` — bool, optional, **default `true`**.
- 200: `{ "job_id", "account", "offers": <agent task output> }`.
- 202 / 502: account-task pattern. Errors: 404, 401. Audit: `trade_offers.read`.

#### `POST /v1/accounts/{name}/trade-offers/{offerId}/accept`
- Purpose: accept one incoming trade offer (dispatches `accept_trade_offer`); auto-dispatches `confirm_trade_offer` when the output flags `requires_mobile_confirmation` and `autoConfirm` is not false.
- Auth: admin.
- Route param: `{offerId}` — must parse as `ulong > 0`, else 400 `offerId must be a positive integer`.
- Body (`TradeOfferDecisionRequest`):
  - `partnerSteamId` — string (SteamID64), **required** (must parse as `ulong > 0`, else 400 `partner_steam_id is required (the offer's partner_steam_id from the trade-offers listing)`)
  - `verifyState` — bool, optional, default `true` (forwarded as `verify_state` to the agent)
  - `autoConfirm` — bool, optional, default `true` (mobile-confirmation auto-confirm)
- 200: `{ "job_id", "account", "result": <agent output>, "mobile_confirmation": { "attempted": true, "job_id", "confirmed"?: bool, "error"?: string } | null }` (omitted when null; `confirmed`/`error` absent while the confirm task is still queued).
- 202 / 502: account-task pattern. Errors: 400, 404, 401. Audit: `trade_offer.accept` (+ `trade_offer.confirm`).

#### `POST /v1/accounts/{name}/trade-offers/{offerId}/decline`
- Purpose: decline one incoming trade offer (dispatches `decline_trade_offer`).
- Auth: admin.
- Route param: `{offerId}` — `ulong > 0`, else 400.
- Body (`TradeOfferDecisionRequest`):
  - `partnerSteamId` — **not required** for decline (ignored).
  - `verifyState` — bool, optional, default `true`.
  - `autoConfirm` — unused (decline never auto-confirms).
- 200: `{ "job_id", "account", "result", "mobile_confirmation" }` (same shapes as accept; `mobile_confirmation` always null here). 202 / 502 pattern. Errors: 400, 404, 401. Audit: `trade_offer.decline`.

#### `POST /v1/accounts/{name}/confirmations/accept-all`
- Purpose: batch-respond to all pending mobile confirmations (dispatches `confirm_all_confirmations`; the identity secret stays agent-side).
- Auth: admin.
- Body (`ConfirmationsBatchRequest`):
  - `operation` — string, optional, default `"allow"`; must be `allow` | `cancel`, else 400 `operation must be 'allow' or 'cancel'`.
  - `type` — string, optional, default `"all"`; must be `all` | `trade` | `market`, else 400 `type must be 'all', 'trade' or 'market'`.
- 200: `{ "job_id", "account", "result" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `trade_confirmations.accept_all`.

#### `GET /v1/accounts/{name}/inventory`
- Purpose: read the account's inventory (dispatches `get_inventory`); optional per-app/context filters.
- Auth: admin.
- Query: `appIds` (comma-separated uint list, each > 0; any bad entry → 400 `app_ids entries must be positive app ids (got '<part>')`; empty list → 400), `appId` (string, optional), `contextId` (string, optional), `steamId` (string, optional), `tradableOnly` (bool, optional), `marketableOnly` (bool, optional).
- 200: `{ "job_id", "account", "inventory": <agent output> }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `inventory.read`.

#### `GET /v1/accounts/{name}/duplicates`
- Purpose: find duplicate items (trading cards) beyond a kept copy count (dispatches `find_duplicates`).
- Auth: admin.
- Query: `appIds` (comma-separated uint list, same validation as inventory), `keep` (int, optional; must be 1–100, else 400 `keep must be an integer between 1 and 100`).
- 200: `{ "job_id", "account", "duplicates": <agent output> }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `inventory.duplicates`.

#### `GET /v1/accounts/{name}/achievements?appId=`
- Purpose: list one game's achievements for the account (dispatches `get_achievements`).
- Auth: admin.
- Query: `appId` — **required**, positive uint, else 400 `appId must be a positive app id`.
- 200: `{ "job_id", "account", "app_id": <uint>, "achievements": <agent output> }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `achievement.read`.

#### `POST /v1/accounts/{name}/achievements/unlock`
- Purpose: unlock the named achievements of one game (dispatches `unlock_achievements`); explicit name list is the whole contract (no batch path).
- Auth: admin.
- Body (`AchievementWriteRequest`):
  - `appId` — uint, **required**, > 0 (else 400 `appId is required (positive app id)`)
  - `names` — string[] of achievement API names, **required non-empty** after trim/dedupe (else 400 `names is required: an explicit non-empty list of achievement API names (no implicit full-batch path exists)`)
  - `confirm` — not used for unlock.
- 200: `{ "job_id", "account", "app_id", "result": <agent output> }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `achievement.unlock`.

#### `POST /v1/accounts/{name}/achievements/reset`
- Purpose: reset (clear) the named achievements (dispatches `reset_achievements`); same gates as unlock plus an explicit destructive-operation confirmation.
- Auth: admin.
- Body: `AchievementWriteRequest` as above, **plus** `confirm` — bool, **must be explicitly `true`** (else 400 `confirm must be explicitly true for a reset (destructive operation)`).
- 200: `{ "job_id", "account", "app_id", "result" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `achievement.reset`.

#### `GET /v1/accounts/{name}/market/listings`
- Purpose: list the account's own community market listings (dispatches `get_my_market_listings`).
- Auth: admin.
- Query: `start` (int, optional, default 0, clamped ≥ 0), `count` (int, optional, default 100, clamped 1–500).
- 200: `{ "job_id", "account", "market_listings": <agent output> }`. 202 / 502 pattern. Errors: 404, 401. Audit: `market_listings.read`.

#### `POST /v1/accounts/{name}/market/listings`
- Purpose: create one market listing; dry run (pricing plan report) by default; a real listing needs `send=true` plus the account spec opt-in `marketListingsEnabled` (and the agent's own `AGENT_MARKET_LISTINGS_ENABLED` switch).
- Auth: admin.
- Body (`MarketCreateListingRequest`, all optional):
  - `appId` — uint, optional (> 0 to be forwarded)
  - `contextId` — string, optional
  - `assetId` — string, optional
  - `amount` — int, optional (> 0 to be forwarded)
  - `sellerProceedsCents` — int, optional (price on either side — exactly one)
  - `buyerPriceCents` — int, optional
  - `send` — bool, optional, **default `false`** (dry run); `send=true` on a non-opted-in account → 400 `refusing to list: this account has not enabled market listings (set marketListingsEnabled=true on the account spec)`
- 200: `{ "job_id", "account", "result" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `market_listings.create`.

#### `POST /v1/accounts/{name}/market/listings/cancel`
- Purpose: cancel the account's own market listings matching a filter; `dry_run=true` (default) only previews matched listings; a real run with no filter is refused.
- Auth: admin.
- Body (`MarketCancelRequest`, all optional):
  - `appId` — uint, optional
  - `marketHashName` — string, optional
  - `minPriceCents` — int, optional
  - `maxPriceCents` — int, optional
  - `olderThanSeconds` — int, optional (> 0 to be forwarded)
  - `dryRun` — bool, optional, **default `true`**
  - Rule: `dryRun=false` with none of the four filters set → 400 `refusing to cancel with no filter: pass at least one filter, or keep dry_run=true to preview`.
- 200: `{ "job_id", "account", "result" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `market_listings.cancel`.

#### `GET /v1/accounts/{name}/points-shop/summary`
- Purpose: read the account's points-shop balance and (optionally) specific reward definitions (dispatches `get_points_shop_summary`).
- Auth: admin.
- Query: `definitionIds` (comma-separated uint list; any non-parse → 400 `invalid definition_ids value '<part>'`), `freeOnly` (bool, optional; forwards `free_only: true`).
- 200: `{ "job_id", "account", "points_shop": <agent output> }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `points_shop.summary`.

#### `POST /v1/accounts/{name}/points-shop/claim`
- Purpose: redeem points-shop reward definitions (dispatches `claim_points_shop_items`); free definitions by default, paid ones with `force`.
- Auth: admin.
- Body (`PointsShopClaimRequest`):
  - `definitionIds` — uint[], **required non-empty** (else 400 `definition_ids is required`); entries must be > 0 (else 400 `definition_ids must be positive`); deduplicated before dispatch.
  - `force` — bool, optional, default `false` (`true` redeems paid definitions, spending points; only forwarded when true).
- 200: `{ "job_id", "account", "result" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `points_shop.claim`.

#### `POST /v1/accounts/{name}/loot`
- Purpose: send all of the account's tradable items to a partner (dispatches `loot_inventory`); auto-confirms the mobile step when Steam requires it.
- Auth: admin.
- Body (`LootRequest`):
  - `partnerSteamId` — string (SteamID64), optional; must parse as `ulong > 0` when present (else 400 `partner_steam_id must be a positive 64-bit SteamID`)
  - `tradeUrl` — string, optional (token-bearing URL reaches non-friends)
  - At least one of the two is **required** (else 400 `either partner_steam_id or trade_url is required (a token-bearing trade URL reaches non-friends)`); `trade_url` wins when both are set.
  - `message` — string, optional (trade message)
  - `appIds` — int[] (`int[]? AppIds` in the record), optional filter of which games' items to send
- 200: `{ "job_id", "account", "result", "mobile_confirmation" }` (same `mobile_confirmation` shape as trade-offer accept). 202 / 502 pattern. Errors: 400, 404, 401. Audit: `account.loot` (+ `trade_offer.confirm`).

#### `POST /v1/accounts/{name}/swap-offers`
- Purpose: match duplicates against a partner and offer a 1:1 swap (dispatches `swap_duplicates`); dry run unless `send=true`; auto-confirms the mobile step when sending.
- Auth: admin.
- Body (`SwapOfferRequest`):
  - `partnerSteamId` — string, optional (same validation/rule as loot; one of partner/trade URL required)
  - `tradeUrl` — string, optional
  - `message` — string, optional
  - `appIds` — int[], optional
  - `keep` — int, optional (copies to keep per item)
  - `maxSwaps` — int, optional
  - `send` — bool, **default `false`** (non-nullable in the record; dry run when omitted)
- 200: `{ "job_id", "account", "result", "mobile_confirmation" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `trade.swap_offer` (+ `trade_offer.confirm`).

#### `POST /v1/accounts/{name}/licenses`
- Purpose: claim free Steam content on the account (dispatches `add_license`; `app_ids` via the client protocol, `sub_ids` via store checkout).
- Auth: admin.
- Body (`LicenseRequest`):
  - `appIds` — int[], optional (entries must be > 0, else 400 `app_ids must be positive`)
  - `subIds` — int[], optional (entries must be > 0, else 400 `sub_ids must be positive`)
  - At least one of the two **required** (else 400 `either app_ids or sub_ids is required`).
- 200: `{ "job_id", "account", "result" }`. 202 / 502 pattern. Errors: 400, 404, 401. Audit: `account.add_license`.

---

### 4.3 Orchestration

#### `GET /v1/orchestration/standing`
- Purpose: standing snapshot for every account the orchestrator tracks.
- Auth: admin. Body: none. Query: none.
- 200: `{ "standing": [ AccountStandingView ] }` — `AccountStandingView = { accountName, standing?: string, quarantined: bool, checkedAt? }`.
- Errors: 401.

#### `GET /v1/orchestration/farm`
- Purpose: farm-loop snapshot for every tracked account (queue, progress, efficiency counters).
- Auth: admin. Body: none. Query: none.
- 200: `{ "farm": [ FarmAccountView ] }` — `FarmAccountView = { accountName, farmingAppId?: uint, queue?: uint[], cardsRemaining?: int, cardsCollected: int, cardsPerHour?: double, completedApps?: uint[], skippedApps?: uint[], queueRefreshedAt?, statsStartedAt? }`.
- Errors: 401.

(See also `AccountOrchestrationView` embedded in `GET /v1/accounts/{name}`: `{ assignedAgent?, activeJobId?, activeJobAction?, loginAttempts, nextAttemptAt?, idling, farmingAppId?, farmQueue?, farmQueueCheckedAt?, boostUnmetApps?, boostCheckedAt?, tradeOffersToAccept?: ulong[], tradeOfferCheckedAt?, lastAction?, lastActionAt?, lastDeviation?, standing?, standingQuarantined, standingCheckedAt?, farmCompletedApps?, farmSkippedApps?, farmAppStartedAt?, farmStatsStartedAt?, farmCardsRemaining?, farmCardsCollected, farmCardsPerHour? }`.)

---

### 4.4 Agents

#### `GET /v1/agents`
- Purpose: list registered agents (summary says "including offline ones"; in practice the registry holds currently-registered hellos).
- Auth: admin. Body: none.
- 200: `{ "agents": [ AgentHello ] }` — `AgentHello = { agentId, region, capabilities?: { "<action>": bool }, meta?: { string: string } }`.
- Errors: 401.

#### `GET /v1/agents/status`
- Purpose: list currently connected agents with region and capabilities.
- Auth: admin. Body: none.
- 200: `{ "agents": [ { "id", "region", "capabilities", "connected": true, "connectedAt" } ] }`.
- Errors: 401.

#### `GET /v1/agent/ws` (WebSocket)
- Purpose: agent tunnel for task dispatch / results / heartbeats.
- Auth: **agent key** (see §3 for the full handshake: `agentId` + `region` query params, first `hello` frame must match).
- 401 (no body) / 400 `{ "error": "websocket required" }` or `{ "error": "agentId and region are required" }`; close code `PolicyViolation` ("hello required") on a bad first frame.

---

### 4.5 Jobs (+ events)

Job records: `Job = { id, action, region?, targets: string[], meta?: {string:string}, status, createdAt, updatedAt, schedule?, nextRunAt? }` (status: `queued|running|scheduled|finished|failed|canceled`);
`JobTask = { id, jobId, target, action, region?, payload?, status, attempt, createdAt, updatedAt, error?, output? }` (task status: `queued|running|finished|failed|canceled`);
`JobWithTasks = { job, tasks: [JobTask] }`;
`JobSchedule = { intervalSeconds: int (default 0), cron?: string, missed: "skip"|"runOnce" (default "skip"), overlap: "skip"|"allow" (default "allow") }` — either `intervalSeconds` or `cron` (5-field, UTC) must be set; cron wins when both given.

#### `POST /v1/jobs`
- Purpose: create a job (dispatched by the scheduler to a capable agent in the job's region; a job with a `schedule` becomes a recurring template).
- Auth: admin.
- Body (`CreateJobRequest`):
  - `action` — string, **required** (blank → 400 `action is required`)
  - `region` — string, optional (null/absent lets the scheduler pick)
  - `targets` — string[], **required non-empty** (else 400 `targets is required`)
  - `payload` — object (open string-keyed map), optional
  - `meta` — object (`string → string`), optional
  - `schedule` — `JobSchedule`, optional; invalid cadence → 400 with the `ScheduleClock.Validate` message
- 202: `Location: /v1/jobs/{id}`, body `CreateJobResponse` = `{ "job": Job }`.
- Errors: 400 (`ErrorResponse`), 401 (empty). Publishes SSE `job.created` and audit `job.created`.

#### `GET /v1/jobs`
- Purpose: list recent jobs.
- Auth: admin.
- Query: `limit` (int, optional, default 50, clamped 1–500), `account` (string, optional filter).
- 200: `{ "jobs": [ Job ] }`.
- Errors: 401 (empty).

#### `GET /v1/jobs/{jobId}`
- Purpose: one job with its tasks (per-task status, attempt, error, output).
- Auth: admin. Body: none.
- 200: `JobWithTasks`.
- Errors: 404 `{ "error": "job not found" }`, 401 (empty).

#### `POST /v1/jobs/{jobId}/cancel`
- Purpose: cancel all queued/running tasks of a job (also pushes `task_cancel` frames to connected agents).
- Auth: admin. Body: none.
- 200: `{ "ok": true }`.
- Errors: 404 `{ "error": "job not found" }`, 401. Publishes SSE `job.canceled`, audit `job.canceled`.

#### `GET /v1/jobs/{jobId}/events` (SSE)
- Purpose: SSE stream of this job's lifecycle events.
- Auth: admin. Query: none (job scope from the route).
- 200 `text/event-stream` (frames per §2; includes `task.*`, `job.*` for this job only).
- Errors: 401 (no body), 404 `{ "error": "job not found" }` (before the stream opens).

#### `GET /v1/jobs/events` (SSE)
- Purpose: SSE stream of lifecycle events across **all** jobs (broker key `*`; also carries agent/account/crawl events with null `jobId`).
- Auth: admin. Body: none. Query: none.
- 200 `text/event-stream`. Errors: 401 (no body).

---

### 4.6 Sessions (+ events)

`SessionSnapshot = { accountName, state, eventType, message?, updatedAt }`.

#### `GET /v1/sessions`
- Purpose: list known sessions and their latest state.
- Auth: admin.
- Query: `account` (string, optional; case-insensitive account-name filter).
- 200: `{ "sessions": [ SessionSnapshot ] }`.
- Errors: 401.

#### `POST /v1/sessions/events`
- Purpose: receive a session event report from an agent (or admin); updates the tracker, feeds the SSE stream, and raises/clears auth-challenge prompts (event types `auth_code_required`/`2fa_required`/`qr_required` or states `ConnectingWaitAuthCode`/`ConnectingWait2FA`/`ConnectingWaitQr` create challenges; anything else clears the account's pending challenge). Login-relevant transitions also persist a dedicated `session.login` audit record.
- Auth: **admin OR agent**.
- Body (`SessionEventRequest`):
  - `accountName` — string, **required** (blank → 400 `accountName is required`)
  - `eventType` — string, optional; normalized (e.g. `StateChanged` → `state_changed`, `AuthCodeNeeded` → `auth_code_required`, `TwoFactorCodeNeeded` → `2fa_required`, CamelCase → snake_case); blank → `state_changed`
  - `state` — string, optional; blank → `"unknown"`
  - `message` — string, optional
- 200: `{ "ok": true }`.
- Errors: 400, 401 (empty). Audit: `session.event.received` (+ `session.login`).

#### `GET /v1/sessions/events` (SSE)
- Purpose: SSE stream of session events.
- Auth: admin.
- Query: `accountName` (string, optional filter).
- 200 `text/event-stream`, frames `event: session.<eventType>` with `SessionEvent` payloads.
- Errors: 401 (no body).

---

### 4.7 Audit

#### `GET /v1/audit/logs`
- Purpose: query persisted audit logs.
- Auth: admin.
- Query: `limit` (int, default 100, clamped 1–500), `offset` (int, default 0, clamped ≥ 0), `action` (string, optional), `account` (string, optional), `jobId` (string, optional), `fromMs` (long, unix-ms, optional), `toMs` (long, unix-ms, optional); `fromMs > toMs` → 400 `fromMs must not be greater than toMs`.
- 200: `{ "logs": [ AuditEntry ], "total": int, "limit": int, "offset": int }` — `AuditEntry = { id, timestamp, action, actor, remoteIp?, accountName?, jobId?, details? }`.
- Errors: 400, 401.

---

### 4.8 Config

`GlobalConfig = { version: ConfigVersion, settings?: object }` (`encryptionKey?` exists on the record but is never set by these endpoints, so it is omitted). `AccountConfig = { accountName, enabled, region?, labels?, settings?, version? }` (`password`/`passwordFormat` are never set by the API and thus omitted from responses).

#### `GET /v1/config`
- Purpose: get global and per-account configuration.
- Auth: admin. Body: none.
- 200: `{ "global": GlobalConfig, "accounts": [ AccountConfig ] }`.
- Errors: 401.

#### `PUT /v1/config/global`
- Purpose: replace global settings (bumps version).
- Auth: admin.
- Body (`PutGlobalConfigRequest`):
  - `settings` — object (open map), optional (null → empty map)
  - `updatedBy` — string, optional
- 200: `GlobalConfig`.
- Errors: 401. Audit: `config.global.updated`.

#### `PUT /v1/config/account/{name}`
- Purpose: replace per-account settings (enabled, region, labels, settings); bumps version.
- Auth: admin.
- Route param: `{name}` — required non-blank (blank/whitespace → 400 `account name is required`).
- Body (`PutAccountConfigRequest`):
  - `enabled` — bool, default `true`
  - `region` — string, optional
  - `labels` — string[], optional
  - `settings` — object, optional
  - `updatedBy` — string, optional
- 200: `AccountConfig`.
- Errors: 400, 401. Audit: `config.account.updated`.

---

### 4.9 Crawl

`CrawlPlan = { id, name, appIds: uint[], accounts?: string[], overrides?: { "<appId>": "<account>" }, shardSize, intervalMs, cc, cron?, intervalSeconds, enabled, createdAt, updatedAt, runCount, lastRunId?, lastRunAt?, nextRunAt? }`;
`CrawlRunSummary = { runId, planId, startedAt, finishedAt?, total, ok, failed }`;
`CrawlResultRow = { id: long, planId, runId, appId: uint, account, jobId?, ok, error?, data?, fetchedAt }`.

#### `GET /v1/crawl/plans`
- Purpose: list all crawl plans.
- Auth: admin. Body: none.
- 200: `{ "plans": [ CrawlPlan ] }`. Errors: 401.

#### `POST /v1/crawl/plans`
- Purpose: create a crawl plan (one-shot by default; recurring via `cron` or `intervalSeconds`; `startNow=false` arms a recurring schedule without an immediate first run).
- Auth: admin.
- Body (`CreateCrawlPlanRequest`, all optional unless noted — validated by `ValidateCrawlPlanRequest`):
  - `name` — string, **required** (else 400 `name is required`)
  - `appIds` — string[] of app ids, **required non-empty** (`app_ids is required`); max `CRAWL_MAX_APPS_PER_PLAN` (default 500) entries; each entry must parse as uint > 0
  - `accounts` — string[] of declared account names, optional (unknown name → 400 `unknown account '<name>'`)
  - `overrides` — map `"<appId uint>" → "<accountName>"`, optional (bad key / unknown account → 400)
  - `shardSize` — int, optional (1–200 → `shard_size must be between 1 and 200`)
  - `intervalMs` — int, optional (0–60000)
  - `cc` — string, optional (country code; defaults to `"us"` when blank)
  - `cron` — string, optional (5-field, UTC; validated with the job scheduler rules)
  - `intervalSeconds` — int, optional
  - `startNow` — bool, optional (one-shots are always immediate; recurring honors `false`)
  - `enabled` — bool, optional, default `true`
- 202: `Location: /v1/crawl/plans/{id}`, body = `CrawlPlan`.
- Errors: 400, 401. Audit: `crawl.plan.created`.

#### `GET /v1/crawl/plans/{id}`
- Purpose: get one crawl plan.
- Auth: admin. Body: none.
- 200: `CrawlPlan` (bare, no wrapper). Errors: 404 `{ "error": "crawl plan not found" }`, 401.

#### `PUT /v1/crawl/plans/{id}`
- Purpose: update a crawl plan; omitted fields keep their current values (merge semantics); `startNow=true` re-arms the next run immediately.
- Auth: admin.
- Body (`UpdateCrawlPlanRequest`): identical field set to `CreateCrawlPlanRequest`, all optional.
- 200: `CrawlPlan`. Errors: 400, 404, 401. Audit: `crawl.plan.updated`.

#### `DELETE /v1/crawl/plans/{id}`
- Purpose: delete a crawl plan (persisted results are kept).
- Auth: admin. Body: none.
- 204: empty. Errors: 404, 401. Audit: `crawl.plan.deleted` (`resultsKept: true`).

#### `POST /v1/crawl/plans/{id}/trigger`
- Purpose: trigger a crawl plan now (sets next run to now; overlapping runs are skipped by the worker).
- Auth: admin. Body: none.
- 202: `Location: /v1/crawl/plans/{id}`, body = `CrawlPlan`.
- Errors: 400 `{ "error": "crawl plan is disabled" }`, 404, 401. Audit: `crawl.plan.triggered`.

#### `GET /v1/crawl/plans/{id}/runs`
- Purpose: list recent runs of a plan (aggregated, newest first).
- Auth: admin.
- Query: `limit` (int, default 20, clamped 1–100).
- 200: `{ "runs": [ CrawlRunSummary ] }`. Errors: 404, 401.

#### `GET /v1/crawl/results`
- Purpose: query persisted per-app crawl results.
- Auth: admin.
- Query: `planId` (string, optional), `runId` (string, optional), `appId` (long, optional, > 0), `account` (string, optional), `ok` (bool, optional), `limit` (int, default 100, clamped 1–500), `offset` (int, default 0).
- 200: `{ "results": [ CrawlResultRow ], "total": int, "limit": int, "offset": int }`.
- Errors: 401.

---

### 4.10 Plugins

`PluginIndexEntry = { id, name, version, apiVersion, url, sha256, description?, trust?, permissions: string[] }`;
`PluginInventoryEntry = { id, name, version, apiVersion, trust?, permissions: string[], actions: string[] }`.

#### `GET /v1/plugins/catalog`
- Purpose: browse the plugin index source (cached 60 s).
- Auth: admin. Body: none.
- 200 (index not configured): `{ "configured": false, "entries": [] }`.
- 200 (configured): `{ "configured": true, "source": "<url>", "fetchedAt": <ts>, "error"?: string, "entries": [ PluginIndexEntry ] }`.
- Errors: 401.

#### `GET /v1/plugins/installed`
- Purpose: last-reported plugin inventory per agent (mirrored from `plugin_*` task outputs).
- Auth: admin. Body: none.
- 200: `{ "agents": [ { "agentId", "reportedAt", "plugins": [ PluginInventoryEntry ] } ] }` (ordered by `agentId`).
- Errors: 401.

#### `POST /v1/plugins/install`
- Purpose: dispatch plugin installs to named agents (`plugin_install` job per agent; catalog mode resolves `url`/`sha256` from the index by `pluginId`).
- Auth: admin.
- Body (`PluginInstallRequest`):
  - `agentIds` — string[], **required non-empty** (else 400 `agentIds must name at least one agent`)
  - `pluginId` — string, optional (catalog mode key)
  - `url` — string, optional (direct mode)
  - `sha256` — string, optional; required in direct mode as a 64-char hex digest (else 400 `sha256 must be a 64-character hex digest of the package`)
  - `version` — string, optional
  - Rule: neither `url` nor `pluginId` → 400 `either url or pluginId is required`; `pluginId` not in catalog → 404 `plugin '<id>' is not in the catalog`.
- 202: `Location: /v1/plugins/installed`, body `{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`.
- Errors: 400, 404, 401. Audit: `plugin_install_dispatched`.

#### `POST /v1/plugins/uninstall/{pluginId}`
- Purpose: dispatch plugin uninstalls to named agents (`plugin_uninstall` job per agent).
- Auth: admin.
- Route param: `{pluginId}` — trimmed; empty → 400 `pluginId is required`.
- Body (`PluginUninstallRequest`): `agentIds` — string[], **required non-empty** (else 400 `agentIds must name at least one agent`).
- 202: `Location: /v1/plugins/installed`, body `{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`.
- Errors: 400, 401. Audit: `plugin_uninstall_dispatched`.

#### `POST /v1/plugins/inventory/refresh`
- Purpose: re-run `plugin_list` on the named (or all connected) agents to re-sync the inventory mirror; stale mirror entries for agents not targeted are dropped.
- Auth: admin.
- Body (`RefreshInventoryRequest`, optional — may be omitted entirely): `agentIds` — string[], optional (default: all connected agents).
- 202: `Location: /v1/plugins/installed`, body `{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`.
- Errors: 409 `{ "error": "no connected agents to refresh" }`, 401.

---

### 4.11 System (public)

#### `GET /`
- Purpose: 302 redirect to `/admin.html` (admin UI).
- Auth: none. Body: none. 302.

#### `GET /healthz`
- Purpose: liveness probe (public, unauthenticated).
- Auth: none. Body: none.
- 200: `{ "ok": true }`.

#### `GET /metrics`
- Purpose: Prometheus scrape endpoint (public; protect at the network layer). Counters/gauges: `vapor_controlplane_tasks_by_status`, `vapor_controlplane_agents_connected`, `vapor_controlplane_dispatch_failures_total{reason=...}`, `vapor_controlplane_accounts_by_desired_state{state=...}`, `vapor_controlplane_reconcile_actions_total{action=...}`, `vapor_controlplane_schedule_triggers_total{outcome=...}`, `vapor_controlplane_crawl_runs_total` / `crawl_apps_total` / `crawl_tasks_dispatched` / `crawl_overlap_skips`, `vapor_controlplane_notifications_total{sink,outcome}` / `vapor_controlplane_notification_retries_total{sink}`.
- Auth: none. Body: none.
- 200: `text/plain; version=0.0.4; charset=utf-8` (Prometheus exposition).

#### `GET /v1/system/status`
- Purpose: aggregated internal status view — one read-only report combining control-plane self health, proxy probe history, connected agents and account desired-vs-actual state. The dashboard "内部状态总览" panel renders this report.
- Auth: admin. Body: none.
- 200: `SystemStatusReport`:
  - `overall` — `{ status: "healthy"|"degraded"|"unhealthy", reasons: string[] }`. Unhealthy when the job store is unreachable; degraded when the last reconcile pass failed, declared accounts exist while no agent is connected, or auth challenges are pending; otherwise healthy.
  - `controlPlane` —
    - `db` — `{ available: bool, latencyMs: long, error?: string }`; one `GetTaskStatusCounts` round-trip doubles as the liveness probe and its wall time is the latency.
    - `jobs` — task counts by status: `{ queued, running, finished, failed, canceled }`.
    - `scheduler` — `{ lastTickAt?: timestamp, dispatchNoCapableAgent, dispatchEnqueueFailed, dispatchAttemptsExhausted }`; `lastTickAt` is the dispatch loop heartbeat (omitted before the first tick).
    - `reconciler` — `{ lastPassAt?: timestamp, lastPassDurationMs?: long, lastPassFailed: bool, loginsDispatched, playsDispatched, cardDropsDispatched, playtimesDispatched, tradeAcceptsDispatched, rebalances, unassignments, throttledSkips, noAgentSkips, dryRunDeviations }`.
    - `recurringJobs` — `{ triggered, overlapSkipped, missedDropped, missedCatchUps }`.
    - `plugins` — `{ agentsReporting, entries, byTrust: { "<trust>": count } }`.
  - `proxies` — `{ probesOk, probesFailed, proxyDisabled, recent: [ { account, success, proxy?, exitIp?, latencyMs?, error?, checkedAt } ] }`. Aggregated from recent `check_proxy` task outputs (last 100 jobs). There is no control-plane proxy pool registry — proxies are per-account agent-side configuration; the `proxy` value, when present, is the agent-side masked endpoint. Disabled-proxy and not-yet-finished tasks count toward the tallies but are not listed.
  - `agents` — `{ connected: int, regions: string[], entries: [ { id, region, connectedAt, capabilities } ] }`.
  - `accounts` — `{ total, byDesiredState: { "<state>": count }, mismatches: [ { account, desiredState, actualSessionState?, assignedAgent? } ], sessionsTracked, pendingChallenges, challengeTypes: { "<type>": count } }`. A mismatch means the declared desired state and the latest session snapshot disagree (missing snapshot counts as "no session"; `unknown` session states are never treated as positive evidence of a live session).
  - Redaction: credentials, proxy passwords (already masked agent-side) and Steam Guard / 2FA challenge **codes** never appear — challenges contribute counters only.
- Errors: 401.

---

## 5. Audit actions reference (written by these endpoints)

`account.spec.updated`, `account.spec.removed`, `account.enabled`, `account.disabled`, `account.loot`, `account.add_license`, `standing_check_requested`, `trade_offers.read`, `trade_offer.accept`, `trade_offer.decline`, `trade_offer.confirm`, `trade.swap_offer`, `trade_confirmations.accept_all`, `inventory.read`, `inventory.duplicates`, `achievement.read`, `achievement.unlock`, `achievement.reset`, `market_listings.read`, `market_listings.create`, `market_listings.cancel`, `points_shop.summary`, `points_shop.claim`, `job.created`, `job.canceled`, `session.event.received`, `session.login`, `auth.code.submitted`, `config.global.updated`, `config.account.updated`, `plugin_install_dispatched`, `plugin_uninstall_dispatched`, `crawl.plan.created`, `crawl.plan.updated`, `crawl.plan.deleted`, `crawl.plan.triggered`, plus task-result audits (`task.result.reported` for sensitive actions: `SendTradeOffer*`, `AcceptTradeOffer*`, `DeclineTradeOffer*`, `CancelTradeOffer*`, `GetInventory*`, `RedeemKey*`) and crawl-run audits from the worker.

Actor = `X-Forwarded-For` header when present, else the remote IP; details are redacted before persistence (`SensitiveDataRedactor`).
