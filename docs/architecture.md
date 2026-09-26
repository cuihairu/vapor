# Vapor Architecture (ASF-inspired)

This project implements an API-controlled, headless Steam automation platform inspired by ArchiSteamFarm (ASF).
The goal is to support large-scale, multi-account operations with multi-region deployment and centralized management.

## Goals

- Provide **public Internet-facing HTTP API** for submitting and managing automation jobs.
- Execute jobs in **regional agents** close to Steam/partners/users to reduce latency and handle geo-specific routing.
- Support **high-volume batch operations** with reliable orchestration (idempotency, retries, partial success).
- Keep **Steam session logic** isolated and reusable (ASF-style “Bot session” + “Actions”).
- Offer **event streaming** for state changes and interactive auth challenges (SteamGuard/2FA).
- Be secure by default (strong authN/authZ, audit logs, secret handling).

Implementation language: **C#/.NET** (to stay close to ASF patterns and ecosystem).

## Non-goals

- Replacing the official Steam client UI.
- Circumventing Steam security controls or rate limits.
- Anything that violates Steam ToS or applicable laws.

## ASF concepts we reuse

- **Session = Bot**: one long-lived Steam account session with its own state machine (connect/login/refresh/retry).
- **Actions layer**: all external operations are expressed as actions invoked on a session, enabling reuse from API, CLI, or jobs.
- **API as a first-class interface**: typed endpoints + consistent responses + OpenAPI.
- **Events**: stream logs/state transitions to clients (ASF uses WebSocket for logs; we extend that to job/session events).
- **Extensibility**: a real plugin system in the agent — isolated (collectible ALC) loading with a manifest, SemVer API compatibility, trust/permission gating and capability interfaces for actions, commands, web routes and session events (see `docs/plugins.md`).

## High-level architecture

### Control Plane (public API)

Responsibilities:
- Authentication/authorization (API keys/OIDC, RBAC, quotas, rate limiting).
- Persisted models: accounts (metadata), regions, agents, jobs, tasks, audit records.
- Job orchestration: split jobs into tasks, route to regions/agents, retries, cancellation.
- Event aggregation: expose job/session events to clients (SSE/WebSocket) and webhooks.
- Observability: metrics, structured logs, tracing.

Public interfaces:
- REST API (submit jobs, query status, manage accounts/regions/agents).
- Event API (SSE/WebSocket).

### Regional Agents (data plane)

Agents run in each region and **initiate an outbound** persistent connection to the control plane (no public inbound ports required).

Responsibilities:
- Maintain session engine: many concurrent Steam sessions.
- Execute tasks: action dispatch to sessions, batching, per-account rate limiting.
- Emit events: session status changes, auth challenges, progress, errors.
- Store minimal local state: session caches and transient runtime info.

Interfaces:
- Outbound Agent Tunnel: WebSocket (initial) or gRPC stream (future) with mTLS.
- Optional localhost admin/health endpoint.

## Execution flow (job → tasks → results)

1. Client submits `POST /v1/jobs` with `action`, `targets` (accounts), and `payload`.
2. Control plane creates a `Job` and splits it into per-target `Task`s.
3. Control plane routes tasks to a region and dispatches them through a queue/tunnel to a connected agent.
4. Agent executes the task by invoking an action handler:
   - Locate/create the session (Bot-style) for the target account.
   - Run the action with timeouts, retries, rate limits, and necessary locks.
5. Agent reports `TaskResult` + emits events (progress, errors, auth challenge needed).
6. Control plane persists results and notifies clients via polling, SSE/WebSocket, and/or webhooks.

## Data model (minimal)

- `Region`: `id`, `name`, `labels`.
- `Agent`: `id`, `regionId`, `connected`, `capabilities`, `lastSeen`.
- `Account`: `id`, `regionHint`, `labels`, `enabled` (secrets stored separately).
- `Job`: `id`, `action`, `createdAt`, `status`, `summary`.
- `Task`: `id`, `jobId`, `targetAccountId`, `status`, `attempt`, `result`, `error`.
- `Event`: `id`, `jobId?`, `accountId?`, `type`, `ts`, `payload`.

## Security model (public Internet API)

- Control plane API:
  - Prefer OIDC + RBAC for humans; API keys/service tokens for automation.
  - Enforce per-tenant quotas, rate limits, and audit logs.
  - Never expose raw secrets (passwords, refresh tokens, 2FA seeds) over API.
- Agent tunnel:
  - Agents authenticate with short-lived tokens or mTLS certs.
  - Control plane authorizes what each agent can execute (region scoping, capabilities).
  - All messages signed/authenticated; replay protection via `taskId` + nonce.
- Secret storage:
  - Use KMS/Vault/Keychain for encryption and rotation.
  - Agent should fetch only region-scoped secrets needed for active tasks.

## Queueing and reliability

Production recommendation:
- Use a durable queue (e.g. NATS JetStream / Kafka / RabbitMQ) for `Task` dispatch.
- Use a DB-backed state machine for job/task status and idempotency keys.
- Ensure at-least-once delivery with idempotent task execution and safe retries.

Development / MVP:
- In-memory queue and in-memory agent registry, with a clear interface boundary to swap in MQ later.

## Multi-region deployment

- Agents run per region (e.g. `us-east`, `eu-west`, `ap-sg`) and connect outbound to control plane.
- Control plane can be single-region initially; later move to:
  - active/active control plane with global DB strategy, or
  - a primary control plane + regional read replicas.
- Routing strategies:
  - static: account pinned to a region
  - dynamic: route by current agent capacity/health
  - policy-driven: labels, compliance constraints, proxy requirements

## Next implementation milestones

1. ~~Control plane skeleton: job/task models, HTTP API, agent tunnel.~~ (Completed)
2. ~~Agent skeleton: task executor with action registry (stub actions initially).~~ (Completed)
3. ~~Persisted storage and durable queue integration.~~ (Completed)
4. ~~Steam session engine: Bot-style session + Actions (SteamKit2 integration).~~ (Completed)
5. ~~Comprehensive test suite: hundreds of tests covering Actions, BotSession, SessionManager, SteamClientManager.~~ (Completed)
6. ~~Event streaming, auth challenge workflow, admin UI.~~ (Completed)

## Completed Features

### Event Streaming System

Enhanced event broker with support for:

- **Session Events**: Real-time session state changes
  - `GET /v1/sessions/events` - Subscribe to session events (SSE)
  - `POST /v1/sessions/events` - Agents publish session events

- **Auth Challenge Events**: Real-time authentication challenge notifications
  - `GET /v1/auth/challenges/events` - Subscribe to auth challenge events (SSE)
  - `POST /v1/auth/challenges/{accountName}/code` - Submit auth codes

### Auth Challenge Workflow

Interactive authentication flow for Steam Guard:

1. Session enters `ConnectingWaitAuthCode` or `ConnectingWait2FA` state
2. Agent publishes auth challenge event to Control Plane
3. Admin UI displays challenge notification
4. User submits auth code via UI or API
5. Agent receives code via SSE stream and continues login

**Automatic 2FA answering (agent-side, opt-in):** with
`AGENT_2FA_AUTO_SUBMIT=true`, a `TwoFactorAutoResponder` also subscribes
to session events on the agent. When a `TwoFactorCodeNeeded` challenge
arrives and the account's mobile authenticator shared secret is stored
in the agent's local credential store, it generates a Steam TOTP
(`SteamTotp`, synced against Steam server time) and answers it directly
— the challenge is still published for visibility, but no human reply is
needed. Accounts without a stored secret (e.g. email Steam Guard) keep
using the manual channel. Secrets stay on the agent and repeat answers
are cooldown-limited (60s per account).

### Admin UI

Modern web-based admin interface (`/admin.html`):

- Dashboard with statistics (total jobs, active jobs, agents, completed)
- Create and manage jobs
- View connected agents
- Handle auth challenges interactively
- Real-time event log
- Responsive design with dark theme

### API Endpoints

#### Accounts
- `PUT /v1/accounts/{name}` - Create or replace an account spec (desired state, idle apps, region/agent pinning, note, boost targets)
- `GET /v1/accounts` - List accounts (filters: `state`, `region`, `agent`)
- `GET /v1/accounts/{name}` - Aggregate view (spec + live session + orchestration state + pending challenge + recent tasks)
- `POST /v1/accounts/{name}/enable` - Enable the account
- `POST /v1/accounts/{name}/disable` - Disable the account (stops sessions, keeps the spec)
- `DELETE /v1/accounts/{name}` - Remove the account spec

#### Jobs
- `POST /v1/jobs` - Create new job (one-shot, or recurring when `schedule` is set)
- `GET /v1/jobs` - List jobs (filters: `limit`, `account` — jobs whose tasks target the account)
- `GET /v1/jobs/{id}` - Get job details
- `POST /v1/jobs/{id}/cancel` - Cancel job (for scheduled templates this stops the recurrence)
- `GET /v1/jobs/{id}/events` - Job event stream (SSE)

### Recurring Jobs

`POST /v1/jobs` accepts an optional `schedule` object to create a recurring
job template: `intervalSeconds` (fixed cadence, >= 5) or `cron` (5-field
UTC expression), plus `missed` and `overlap` policies (both default
`skip`). The template itself runs nothing — it carries the action,
targets, payload and its persisted next trigger point
(`JobStatus.Scheduled`, no tasks). A `RecurringJobScheduler` background
service checks due templates every second and, per trigger point,
atomically creates a regular child job (with tasks, `meta.scheduledFrom`
= template id) and advances the template:

- **missed=skip**: trigger points lost while the control plane was down
  are dropped; the template resumes at the next future point.
  **missed=run_once**: the newest lost point fires one catch-up run
  (marked `meta.scheduledMissedCount`), guarding against storm catch-up.
- **overlap=skip**: a trigger point is deferred while the previous run is
  still queued or running; **overlap=allow** runs in parallel.
- Canceling the template (`POST /v1/jobs/{id}/cancel`) stops the
  recurrence. Crons that can never match again retire the template
  automatically.

Schedule outcomes are exported as `vapor_controlplane_schedule_triggers_total`
(`triggered` / `overlap_skipped` / `missed_skipped` / `missed_catchup`); each
trigger also emits `job.scheduled_triggered` / `job.scheduled_skipped`
events on the event stream.

#### Sessions
- `GET /v1/sessions` - List active sessions (filter: `account`)
- `GET /v1/sessions/events` - Session event stream (SSE)
- `POST /v1/sessions/events` - Publish session event (agent)

#### Auth Challenges
- `GET /v1/auth/challenges/events` - Auth challenge stream (SSE)
- `POST /v1/auth/challenges/{accountName}/code` - Submit auth code

#### Agents
- `GET /v1/agents` - List agents
- `GET /v1/agents/status` - Get agent status
- `WS /v1/agent/ws` - Agent WebSocket tunnel

#### Audit
- `GET /v1/audit/logs` - Query persisted audit logs (filters: `action`, `account`, `jobId`, `fromMs`, `toMs`; paging via `limit`/`offset`)

Audit records cover configuration changes, job lifecycle, auth code submissions,
login session transitions (`session.login`), and sensitive task results
(trade/redeem actions, `task.result.reported`). Sensitive detail values
(passwords, tokens, codes, keys) are redacted before persistence.

### Account Orchestration

Accounts are managed declaratively: operators publish a desired state
(`offline` / `online` / `idle` with idle app ids / `farm` / `boost` with
playtime targets, optional region/agent pinning, note) through the
accounts API and the `DesiredStateReconciler` runs a periodic reconcile
loop (default 15s) that converges actual session state onto it:

- deviating accounts get a login job dispatched to a capable agent —
  region constraints, capability check and per-agent capacity caps apply,
  selection is least-loaded then agent-id order (deterministic);
- `idle` accounts additionally get a `play_games` job, and a stop job when
  switched back to `online`;
- `farm` accounts periodically re-query remaining card drops and play the
  next drop-bearing app, stopping when the queue is empty. An optional
  per-account farm policy (`FarmPolicy` on the spec, set via the accounts
  API) shapes the loop: `priorityOrder` re-orders the queue (by remaining
  card count descending — the report order — ascending, or by app id),
  `priorityApps` pins apps to the head of the queue in declaration order,
  and `perGameHourBudget` acts as a fuse against dead farming — once an app
  has been idled for the whole budget in the current session it is marked
  budget-skipped and the loop rotates to the next game. Apps that leave the
  drops report are marked completed (queue-diff detection) and, together
  with budget-skipped apps, never re-enter the queue even when a lagging
  report still lists them. Draining the queue stops idling with a one-shot
  completion notice; a later report with fresh drops starts a new round.
  Spec updates are policy edits, not resets: completion marks, skip marks
  and the efficiency counters survive them — only the per-game budget clock
  restarts and the queue is force-refreshed. Card statistics accumulate
  monotonically (`collected` grows by the decrease between consecutive
  report totals; newly queued games never read as negative progress); the
  farm snapshot endpoint `GET /v1/orchestration/farm` exposes per-account
  queue, counters and cards-per-hour, and the `account.farm_progress` broker
  event (kinds `app_completed` / `app_budget_exhausted` / `queue_empty`)
  is webhook-forwardable;
- `boost` accounts periodically re-query total playtime and idle every app
  below its target hours in one multi-app `play_games` job (IdleApps still
  act as an exclusion list), stopping once every target is met; query
  failures only mark a deviation and wait out the refresh interval;
- observed login failures back off exponentially (cooldown
  `base × 2^(n-1)`, capped at 15 min) and throttle the account after
  `MaxLoginAttempts` consecutive failures — updating the spec resets the
  budget (retry lever for operators);
- when the assigned agent disconnects, the account is rebalanced to another
  capable agent and its in-flight orchestration job is cancelled;
- disabled / `offline` accounts are unassigned and their in-flight jobs
  cancelled;
- dry-run mode reports every deviation via audit and metrics without
  dispatching;
- every 6 hours by default (`ReconcileStandingRefreshSeconds`) the orchestrator
  runs an abnormal-standing check per connected account: the agent resolves the
  account's Steam Web API key from its logged-in web session and queries
  GetPlayerBans plus GetSteamLevel; a `banned` aggregate (VAC / community /
  game / economy ban) quarantines the account — the trade loop skips all
  dispatches for it — and emits a `standing_quarantined` audit entry plus an
  `account.standing_alert` broker event; a later `clean` result releases the
  quarantine symmetrically. Operators can force a check via
  `POST /v1/accounts/{name}/standing-check` and read per-account standing via
  `GET /v1/orchestration/standing`.

All orchestration decisions are audited (`account.reconciled`,
`account.spec.updated/enabled/disabled/removed`) and exported as Prometheus
metrics (`vapor_controlplane_reconcile_actions_total`,
`vapor_controlplane_accounts_by_desired_state`). Credentials never enter
the control plane — agents keep them in their local `FileCredentialStore`.

### Notifications

A `NotificationService` background service subscribes to the full
EventBroker streams (jobs, sessions, auth challenges) and fans every event
out to registered `INotificationSink` implementations. Each sink carries a
`NotificationRule` (event-category / type / account allowlists; empty = all)
and is isolated individually — a failing sink never affects the others or
the pipelines. The built-in `WebhookNotificationSink` POSTs a JSON envelope
per event, optionally signing it with HMAC-SHA256
(`X-Vapor-Timestamp` + `X-Vapor-Signature` over `"{timestamp}.{body}"`),
retrying with exponential backoff (`base × 2^attempt`). Auth-challenge
notifications only carry a `codeSupplied` flag — codes themselves never
leave the control plane. Delivery counters are exported as
`vapor_controlplane_notifications_total{sink,outcome}` and
`vapor_controlplane_notification_retries_total{sink}`.

### Plugin Ecosystem (PluginStore)

Plugins install at runtime through three host actions (`plugin_install` /
`plugin_uninstall` / `plugin_list`) that need no bot session — tasks targeted
with the `agent:{id}` prefix are routed by the scheduler directly to the named
agent (bypassing region pick) and executed as host actions. The ControlPlane
never brokers binaries: an install job tells the agent a package `url` +
mandatory `sha256`; the agent downloads, verifies the digest, unpacks to a
staging directory (zip-slip checked, manifest-at-root required), validates the
manifest against the requested `pluginId`/`version`, then hot-swaps
(unload → retire old directory → move → load). Every action's output carries
the full installed list, which the ControlPlane mirrors per agent
(`PluginInventory`) and surfaces via `/v1/plugins/catalog` (fetched from
`Vapor_PLUGIN_INDEX_URL`), `/v1/plugins/installed` and the install / uninstall
/ inventory-refresh fan-out endpoints; the admin console's PluginStore panel
renders the catalog with per-agent batch install. The checksum pins integrity
against the supplied digest, not package origin — `trust` remains manifest-
declared and the host's `MinimumTrust` policy is unchanged.

## Steam Transport Adapter

All Steam network protocol access is isolated behind `ISteamTransport`
(protocol-agnostic connection lifecycle, logon flow, auth-code staging,
redeem/play actions, callback pump, token refresh). `SteamClientManager` is
the SteamKit2-backed adapter; session, action and agent code depend only on
the protocol-agnostic shapes (`TransportLogOnDetails`, `SteamResult`,
`RedeemKeyResult`), so a SteamKit2 major-version upgrade or a replacement
stack is confined to the adapter implementation. `SteamResult` mirrors the
Steam wire encoding numerically so persisted task outputs keep their
meaning across a transport swap.

## Trade Safety Layer

All trade actions run through three enforcement layers before touching Steam:

- **`TradeOfferStateMachine`** — legal transition checks: accept requires a
  received, `Active`, unexpired offer whose sender matches the expected partner;
  decline only applies to received offers; cancel only to sent offers
  (including `CreatedNeedsConfirmation`).
- **`TradeAssetValidator`** — ownership verification before sending: every asset
  must exist in the sender's inventory, be tradable, be off trade cooldown, and
  be available in sufficient quantity (duplicate references are aggregated).
- **`TradeRateLimiter`** — per-account sliding-window quota (default 5 ops / 5 min)
  plus a concurrency gate (default 1 concurrent op) to avoid Steam rate limiting.

Validation is on by default; payloads may pass `skip_verification=true`
(send) or `verify_state=false` (accept/decline/cancel) to bypass explicitly.

## Log Redaction

`RedactingLoggerProvider` wraps any logger sink and redacts sensitive values from
messages, structured state, scopes and exception content before output. The
Agent enables it via `AddRedactingConsole()`.

## Data & Caching

- Models: `GameInfo`, `PriceOverview`, `GameSearchResult`,
  `MarketListing`/`MarketListingsPage` (with cache key helpers and `FetchedAt`
  freshness markers).
- `IVaporCache` abstraction with two implementations: per-entry
  TTL (default 10 min), LRU eviction, hit/miss/stale-hit counters, single-flight
  factory deduplication (cache stampede protection), injectable clock for
  testing, prefix-based invalidation (`RemoveByPrefix`) and a
  stale-while-revalidate mode (`GetOrSetStaleWhileRevalidateAsync`: within a
  grace window after the fresh TTL expires, requests are served instantly from
  the stale entry while a single background refresh repopulates it).
  - `MemoryVaporCache` (default): in-process, injectable clock.
  - `RedisVaporCache` (opt-in via `VAPOR_REDIS`): stores JSON envelopes under
    `vapor:cache:<key>` with absolute fresh/stale expiry timestamps (Redis TTL
    is only the outer safety net), shares entries across agent replicas,
    deduplicates SWR refreshes across instances with a `SET NX PX` lock +
    token-checked release, invalidates by prefix via `SCAN`, and clears only
    its own indexed keys (never `FLUSHDB`). `Count` is approximate (shared
    index-set size); hit/miss counters remain per-process.
- Tiered freshness policy (`SteamCacheTtl`): each data kind gets a fresh TTL and
  stale window sized to how often it changes — search 1h/6h, game info
  30min/2h, market listings 5min/30min, prices 3min/15min (staleness tolerated
  only while a background refresh is in flight).
- Store data actions (`get_game_info`, `search_games`, `get_price`,
  `get_market_listings`) resolve data through `SteamStoreApiClient`
  (appdetails / storesearch / community market endpoints) and are cached by
  default; callers can override TTL per call via `cache_ttl_seconds` (0
  disables), force a fresh fetch that repopulates the cache via
  `force_refresh: true`, and expire data on demand via the `cache_invalidate`
  action (`prefix` or `clear_all`).

## HTTP Resilience

`SteamWebHandler` applies a unified middleware-style pipeline:

- **Retry with differentiated backoff**: 429 responses honor the `Retry-After`
  header (capped by config); 5xx responses use exponential backoff. Both stay
  within the configured retry budget.
- **Circuit breaker** (`HttpCircuitBreaker`): opens after N consecutive
  failures, half-opens after a cool-down allowing a single probe, closes on
  probe success. Rejected requests throw `CircuitBreakerOpenException`
  without touching the network.
- **Metrics** (`WebRequestMetrics`): totals, successes, 429/5xx/4xx splits,
  network failures, retries and circuit-breaker rejections, exposed as an
  immutable snapshot for observability pipelines.

## Security & Key Management

- **Encryption at rest**: credentials stored in `~/.vapor/credentials.json` use
  versioned format v2 with AES-GCM encrypted tokens. Legacy v1 (plain) files are
  migrated transparently on first load.
- **Corruption recovery**: writes are atomic (temp file + replace); the previous
  file is backed up as `.bak` and used to recover from corruption.
- **File permissions**: credential files are tightened to owner-only (600) on Unix.
- **Master key sources** (priority order):
  - `VAPOR_ENCRYPTION_KEY_BASE64` - raw key bytes as base64 (KMS/Vault workflow)
  - `VAPOR_ENCRYPTION_KEY_FILE` - key file (Docker/K8s secrets; base64 content decoded when valid)
  - `VAPOR_ENCRYPTION_KEY` - plain text
- **Key rotation**: `tools/Vapor.KeyRotation` CLI re-encrypts the credential store
  from an old to a new key (supports `base64:`/`file:`/`env:` key specs, `--dry-run`,
  aborts without modification when any account fails to decrypt).

## Testing

The project includes a comprehensive test suite for the Steam.Core module:

- **228 Fact/Theory methods** across 13 test classes
- **~4,600 lines** of test code
- **xUnit** + **Moq** for unit testing
- **Coverlet** for code coverage reporting
- **Unit**, **integration**, and **performance** coverage

See `tests/TESTING.md` for detailed testing documentation.

### Test Coverage

- ✅ Actions: Ping, Echo, Login, Idle, RedeemKey
- ✅ Core Components: ActionRegistry, BotSession, SessionManager, SteamClientManager, Models, edge cases
- ✅ Integration: end-to-end workflows, multi-account scenarios
- ✅ Performance: concurrency and stress testing

## Distributed-systems design notes (2026-09)

The capability map in `docs/roadmap.md` surveys where Vapor stands as an
API-driven distributed system. This section records the load-bearing design
decisions behind that map: what was chosen, what was considered and rejected,
and why. The recurring theme: **Vapor optimizes for a single control plane
with a fleet of dumb-ish edges**, buying transactional consistency and zero
coordination at the cost of HA — a cost consciously deferred, not ignored.

### Transport: outbound WebSocket tunnel

**Decision.** Agents connect *out* to the control plane over one persistent
WebSocket (`/v1/agent/ws`) using a closed four-message JSON envelope
(hello/task/task_result/heartbeat + task_cancel). Tasks, results, heartbeats
and the lease all ride this one connection.

**Alternatives considered.**

- *gRPC bidirectional streaming*: better typing and flow control, but drags
  protobuf tooling into every plugin author's loop and adds nothing the closed
  envelope doesn't already give at job granularity (this is not a hot path —
  tasks per second is bounded by Steam, not by the tunnel).
- *Message-queue backbone (NATS/Kafka)*: would buy broker-level durability and
  multi-consumer dispatch, but introduces a stateful broker to operate and a
  second consistency domain to reconcile against the job store. With a single
  control plane, the WS tunnel plus the lease/requeue mechanism already gives
  at-least-once delivery.
- *Agent long-polling HTTP*: no persistent connection to break, but doubles
  latency for every dispatch and makes server-push cancellation awkward.

**Why WS + JSON wins here.** Agents live wherever Steam egress is best
(residential proxies, cheap VPSen behind NAT) with **zero inbound ports**; the
outbound tunnel makes that a deployment non-feature. JSON keeps the protocol
debuggable with the same tooling as the REST surface, and the envelope is
protected the cheap way: it is closed, and every record round-trips under
property tests. Forward compatibility lives in optional members, not in
schema negotiation.

### Control plane: single writer on SQLite

**Decision.** One control-plane process owns all state (jobs, accounts,
audit, crawl) in SQLite; task claiming is lease-based with heartbeats and
requeue on lease expiry.

**Alternatives considered.** Postgres + multiple stateless CP replicas
(write scaling, HA) and leader election over an external store (etcd/Consul).
Both were deferred deliberately: with one writer, every job-state transition
is one transaction with no cross-instance coordination, and SQLite's
single-writer model is a feature — it makes the dispatch loop's
serialization explicit instead of emergent.

**Consequences.** Restart = brief orchestration pause (agents keep sessions
via token restore; leases re-expire; nothing is lost). This is the largest
single item on the roadmap (§9 there) and is sequenced *behind* the
execution-timeout and observability hardening: HA multiplies failure modes,
so the single-instance failure modes get bounded first.

### Agent task loop: serial, with a watchdog

**Decision.** One task at a time per agent connection; three timeout layers
stacked:

1. **Per-action declared `TimeoutSeconds`** (session path via `BotSession`,
   host path via `HostActionExecutor`) — the precise, action-aware bound that
   fires first with a structured `action timeout` result.
2. **Agent task watchdog** (`AGENT_TASK_TIMEOUT_SECONDS`, default 900 s) —
   the belt over actions that declare no timeout or hang below their token's
   observation points. Cancels the task, reports `task timeout after Ns`,
   keeps the loop serving.
3. **CP lease reclaim** (`Vapor_TASK_LEASE_SECONDS`) — recovers from agent
   death or tunnel loss by requeueing.

Each layer exists because the one below it cannot recover that failure: an
undeclared hang holds the serial loop forever while its heartbeats keep the
lease alive (layer 2's raison d'être), and a dead agent obviously cannot
report anything (layer 3). The default sits above the largest in-tree action
bound (600 s) so the precise errors win when both are armed.

**Alternatives considered.** Parallel per-session task loops on the agent —
rejected because the contended resource is the *session* (one Steam login
per account, shared CM client); a serial loop makes that exclusivity
structural instead of a locking discipline.

### State sync: desired-state reconciliation

**Decision.** Accounts are declarative specs (versioned, optimistic
concurrency) converged by a reconcile loop; tasks are at-least-once with
attempt-tracked, idempotent execution; session state syncs eventually into
the CP tracker.

**Alternative considered.** Imperative orchestration (CP commands each
transition). Reconciliation is self-healing by construction — every
deviation (agent loss, failed login, missed report) is just tomorrow's
converge target — and idempotency requirements fall out naturally, which is
also what makes at-least-once task delivery safe.

### API surface: JSON + OpenAPI, RED metrics, opt-in edge rate limiting

REST with System.Text.Json everywhere (camelCase, enum-as-string, nulls
omitted — the same options object as the tunnel, one wire dialect); protobuf
was rejected for the same reasons as on the tunnel. Request-level
observability is hand-rolled RED counters on `/metrics`
(`vapor_controlplane_http_requests_total{method,route,status}` + duration
sums), deliberately independent of the optional OTel pipeline so the scrape
endpoint is a complete story on its own; tracing crosses the tunnel as W3C
`traceparent` in the envelope (no SDK coupling on the wire). Rate limiting
sits at the CP edge per credential (sliding window, off by default) because
that is the only ingress — protecting it is protecting the system.

