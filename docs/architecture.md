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
  next drop-bearing app, stopping when the queue is empty;
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
  dispatching.

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

