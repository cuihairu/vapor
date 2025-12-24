# SteamControl Architecture (ASF-inspired)

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
- **Extensibility**: plugin-like action registry and optional custom endpoints in the agent.

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

### Admin UI

Modern web-based admin interface (`/admin.html`):

- Dashboard with statistics (total jobs, active jobs, agents, completed)
- Create and manage jobs
- View connected agents
- Handle auth challenges interactively
- Real-time event log
- Responsive design with dark theme

### API Endpoints

#### Jobs
- `POST /v1/jobs` - Create new job
- `GET /v1/jobs` - List jobs
- `GET /v1/jobs/{id}` - Get job details
- `POST /v1/jobs/{id}/cancel` - Cancel job
- `GET /v1/jobs/{id}/events` - Job event stream (SSE)

#### Sessions
- `GET /v1/sessions` - List active sessions
- `GET /v1/sessions/events` - Session event stream (SSE)
- `POST /v1/sessions/events` - Publish session event (agent)

#### Auth Challenges
- `GET /v1/auth/challenges/events` - Auth challenge stream (SSE)
- `POST /v1/auth/challenges/{accountName}/code` - Submit auth code

#### Agents
- `GET /v1/agents` - List agents
- `GET /v1/agents/status` - Get agent status
- `WS /v1/agent/ws` - Agent WebSocket tunnel

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
