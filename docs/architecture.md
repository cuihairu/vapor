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

1. Control plane skeleton: job/task models, HTTP API, agent tunnel.
2. Agent skeleton: task executor with action registry (stub actions initially).
3. Persisted storage and durable queue integration.
4. Steam session engine: Bot-style session + Actions (library chosen per language).
5. Event streaming, auth challenge workflow, admin UI.
