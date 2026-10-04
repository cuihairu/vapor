# Domain Model Dictionary

This document defines the ten core objects of the Vapor domain — Account, Session, Agent, Job, Task, Attempt, Action, Capability, Event, Plugin — each with five facets: who owns it, how it lives and dies, where it persists, how it is identified, and what consistency it promises. It is a reading of the code as it exists (verified 2026-10-05); every object section ends with the exact types and files it describes. The relationship skeleton follows the convergence-plan diagram (review §31, verified against current state).

Consistency wording is kept aligned with [`consistency.md`](consistency.md) §1 (state ownership map); this document adds the object-level view, it does not change any guarantee.

## Object map

| Object | One-line definition | Code types |
|--------|--------------------|------------|
| Account | A declared farm account: desired state plus routing metadata | `AccountSpec`, `AccountDesiredState`, `AccountStore` |
| Session | A live Steam login for one account, owned by an agent | `BotSession`, `SessionState`, `SessionSnapshot`, `SessionTracker` |
| Agent | A connected worker machine behind an outbound tunnel | `AgentHello`, `ConnectedAgent`, `AgentRegistry` |
| Job | One requested run of an action over a target set | `Job`, `JobStatus`, `JobSchedule` |
| Task | One target's slice of a job, the unit of dispatch | `JobTask`, `JobTaskStatus` |
| Attempt | The redispatch counter that fences results and caps retries | `JobTask.Attempt`, `TaskResult.Attempt`, `ActionSemantics` |
| Action | A named, metadata-annotated unit of Steam work | `IAction`, `ActionMetadata`, `ActionSafety`, `ActionRegistry` |
| Capability | An agent's advertised action support, used as a routing filter | `AgentHello.Capabilities`, `ConnectedAgent.SupportsAction` |
| Event | A fire-and-forget notification on the SSE streams | `Event`, `SessionEvent`, `AuthChallengeEvent`, `EventBroker` |
| Plugin | A versioned extension package loaded into an isolated ALC | `PluginManifest`, `PluginInfo`, `PluginManager`, `PluginInventory` |

## Relationship skeleton

```text
              Control Plane                                Agent
   ┌──────────────────────────────────┐        ┌──────────────────────────┐
   │ AccountSpec ──▶ DesiredState     │        │ BotSession (SessionState)│
   │                  Reconciler      │        │   │ credentials           │
   │                     │            │        │   ▼                     │
   │                     ▼            │ tunnel │ Session ──▶ Action       │
   │   Job ──▶ Task ──▶ Attempt       │◀──────▶│   ▲        (registry +  │
   │              │         │         │  WSS   │   │         capabilities)│
   │              ▼         ▼         │        │ PluginManager ──▶ Plugin │
   │   AgentRegistry ◀── AgentHello   │        │   │ filesystem install   │
   │   SessionTracker ◀─ events       │        │   ▼                     │
   │   PluginInventory ◀─ task output │        │ PluginInventory mirror  │
   │   EventBroker ──▶ SSE            │        │   (source)              │
   └──────────────────────────────────┘        └──────────────────────────┘
```

The load-bearing path reads top to bottom: a declared Account is converged by the Reconciler into Jobs, which fan out into Tasks, which carry Attempts and dispatch to a capable Agent, whose Session executes the Action. Everything else — Capability, Event, Plugin — is either a routing filter on that path or an observation of it.

---

## 1. Account

A farm account is declared intent, not a live thing: `PUT /v1/accounts/{name}` writes a spec, the orchestrator converges reality toward it. Credentials are deliberately not part of the object — the spec is metadata only, secrets live in the pinned agent's encrypted store.

| Facet | Definition |
|-------|-----------|
| Owner | Control plane. The credential half of an account is owned by the pinned agent and never crosses to the CP. |
| Lifecycle | Created by `PUT /v1/accounts/{name}`; toggled via `Enabled` and `DesiredState` (one of `Offline` / `Online` / `Idle` / `Farm` / `Boost`); deleted by `DELETE`. The Reconciler drives the desired→actual convergence per account. |
| Persistence | Read path is in-memory (`AccountStore`), write-through to SQLite (`SqliteConfigStore`, `Vapor_CONFIG_DB_PATH`); rehydrated at CP startup — survives restart. |
| Identity | `AccountName`, matched case-insensitively (`StringComparer.OrdinalIgnoreCase` throughout `AccountStore` and `SessionTracker`). |
| Consistency | Single-writer CP store; `ConfigVersion` is the monotonic trigger the reconciler consumes. Desired state and runtime state are separate: the spec is authoritative, observed session state converges eventually. |

Code index: `AccountSpec` (14 fields incl. `IdleApps`, `BoostTargets`, `TradePolicy`, `FarmPolicy`, `MarketListingsEnabled`, `Role`, `SteamId`) — `src/Vapor.Protocol/Accounts.cs:117`; `AccountDesiredState` — `Accounts.cs:6`; `AccountStore` — `src/Vapor.ControlPlane/AccountStore.cs`; reconciliation runtime (assigned agent, login-attempt budget, active job) is in-memory per process — `DesiredStateReconciler.cs`.

## 2. Session

One Steam login per account, running inside an agent. The agent owns the truth; the control plane keeps an eventually-consistent snapshot for display and orchestration decisions.

| Facet | Definition |
|-------|-----------|
| Owner | The agent, end to end: session state machine, credentials, tokens. The CP's `SessionTracker` holds snapshots reported via session events — it is a mirror, not an owner. |
| Lifecycle | Ten-state machine (`Disconnected`, `Connecting`, `ConnectingWaitAuthCode`, `ConnectingWait2FA`, `ConnectingWaitQr`, `Connected`, `Reconnecting`, `DisconnectedByUser`, `Disconnecting`, `FatalError`). Auth waits are resolved by submitted codes, the SSE channel, or agent-side auto-responder; token restore re-establishes sessions after agent restart without re-prompting. |
| Persistence | Agent-side: credentials AES-GCM encrypted at `/app/.vapor` (or configured store), access/refresh tokens persisted. CP-side: none — `SessionTracker` is an in-memory `ConcurrentDictionary`, repopulated as agents report. |
| Identity | `AccountName` (case-insensitive). |
| Consistency | Agent-reported, eventually consistent into the CP snapshot. A CP restart loses snapshots until agents re-report; the sessions themselves keep running on agents. |

Code index: `SessionState`, `SessionEventType` (7 kinds) — `src/Vapor.Steam.Core/SessionState.cs`; `BotSession` — `src/Vapor.Steam.Core/BotSession.cs`; `SessionSnapshot` (5 fields), `SessionTracker` — `src/Vapor.ControlPlane/SessionTracker.cs`. See [`session-engine.md`](session-engine.md) for the login flows.

## 3. Agent

A worker machine connected over the outbound WSS tunnel. The CP knows an agent only for as long as its socket is open.

| Facet | Definition |
|-------|-----------|
| Owner | CP holds the registry (`AgentRegistry`) — but it is derived state. The agent process owns its own sessions, plugins and credentials. |
| Lifecycle | Registers on tunnel handshake (bearer key check → WebSocket upgrade → `agentId`/`region` query params → first `hello` frame, else close `PolicyViolation`). Stays registered while connected; heartbeat-driven liveness; deregistered on disconnect. Agent restart = re-hello, fresh registration. |
| Persistence | None, by design. The registry is rebuilt wholesale from agent re-hellos after a CP restart. |
| Identity | `AgentId` — the value in the `hello` frame must equal the query parameter or the socket is rejected. |
| Consistency | Derived, eventual: loss of the socket is the liveness signal, and loss triggers rebalancing of the agent's accounts. Dispatch capacity and pinned routing (`agent:{id}`) read this registry. |

Code index: `AgentHello` (`AgentId`, `Region`, `Capabilities`, `Meta`) — `src/Vapor.Protocol/Models.cs:124`; `ConnectedAgent`, `AgentRegistry` — `src/Vapor.ControlPlane/AgentRegistry.cs`.

## 4. Job

One requested run of an action over a target set. With a `schedule` attached it becomes a recurring template.

| Facet | Definition |
|-------|-----------|
| Owner | Control plane, exclusively — jobs are the CP's primary durable work state. |
| Lifecycle | `JobStatus` 6 states: `Queued` → `Running` → `Finished` / `Failed`, plus `Canceled` and `Scheduled` (recurring templates only). A template with `JobSchedule` stays `Scheduled`; `RecurringJobScheduler` derives a fresh child job per trigger point, honoring `Missed` (`Skip` / `RunOnce`) and `Overlap` (`Skip` / `Allow`) policies. |
| Persistence | SQLite (`SqliteJobStore`) — survives CP restart; queued work resumes. |
| Identity | 32-char lowercase hex from 16 CSPRNG bytes (`Id.New()`), unique per job. |
| Consistency | Single-writer store, per-store serialized writes. Template derivation is atomic on the store; a trigger point fires once or is skipped per policy, never double-fired. |

Code index: `Job`, `JobStatus`, `JobSchedule` (interval or 5-field UTC cron, cron wins) — `src/Vapor.Protocol/Models.cs`; `SqliteJobStore` — `src/Vapor.ControlPlane/SqliteJobStore.cs`; `RecurringJobScheduler` — `src/Vapor.ControlPlane/RecurringJobScheduler.cs`.

## 5. Task

One target's slice of a job — the actual unit of dispatch, execution and result. A job with 5 targets creates 5 tasks; one task maps to at most one agent execution at a time.

| Facet | Definition |
|-------|-----------|
| Owner | Control plane. Agents execute but never store tasks. |
| Lifecycle | `JobTaskStatus` 5 states: `Queued` —claim→ `Running` —result→ `Finished` / `Failed` (terminal, error + output kept), `Canceled`. Claim is atomic (`ClaimNextQueuedTask`); a `Running` task whose lease expires (`TaskLeaseSeconds`, default 300 s, no heartbeat) is requeued (`RequeueStaleRunningTasks`). |
| Persistence | SQLite (`SqliteJobStore`), one row per task; exactly one terminal record per task — attempt fencing makes late duplicates no-ops. |
| Identity | 32-char lowercase hex (`Id.New()`); tasks are also (logically) keyed by `JobId` + `Target`. |
| Consistency | At-least-once delivery with fencing: a requeued task may re-execute (hence the Action safety classes), but result writes are fenced by attempt and a task cannot finish twice. Dispatch picks a connected, capability-matching agent in the task's region (deterministic lowest `agentId`), or pins to `agent:{id}` for host actions. |

Code index: `JobTask` (`Id`, `JobId`, `Target`, `Action`, `Region`, `Payload`, `Status`, `Attempt`, `Error?`, `Output?`), `JobTaskStatus`, `TaskResult`, `TaskHeartbeat`, `TaskCancel` — `src/Vapor.Protocol/Models.cs`; claim/lease/fence SQL — `SqliteJobStore.cs`. The full delivery semantics live in [`consistency.md`](consistency.md) §2.

## 6. Attempt

Not a table or a type of its own: an attempt is the integer redispatch counter on a task, promoted here to a named concept because it carries the delivery guarantees. Attempt n identifies "the nth dispatch of this task"; results and heartbeats carry it and are dropped if stale.

| Facet | Definition |
|-------|-----------|
| Owner | Control plane (the counter lives in the task row; agents echo it back). |
| Lifecycle | Starts at 0/1 on first dispatch, increments on lease-expiry reclaim or dispatch failure. A result whose `Attempt` does not match the current one is ignored (fencing). Redispatch is bounded: `ReadOnly`/`Idempotent` actions use the configured ceiling (`Vapor_TASK_MAX_DISPATCH_ATTEMPTS`); `GuardedWrite`, `NonIdempotent` and unclassified (`Unknown`) actions are hard-capped at 2 — repetition of those can double external side effects. Exhausting the ceiling is terminal `Failed`. |
| Persistence | `JobTask.Attempt` column in SQLite. |
| Identity | The pair `(TaskId, Attempt)` — this is what `SetTaskResult`, `TaskHeartbeat` and `TaskCancel` fence on. |
| Consistency | The fencing property: at most one attempt of a task is "current"; stale writes cannot corrupt the terminal record. |

Code index: `JobTask.Attempt`, `TaskResult.Attempt` — `src/Vapor.Protocol/Models.cs`; `ActionSemantics.MaxDispatchAttempts` (mirror of `ActionMetadata.Safety`, conservative cap 2) — `src/Vapor.ControlPlane/ActionSemantics.cs`; enforcement at the single redispatch decision point `HandleUndispatchableTaskAsync`.

## 7. Action

A named unit of Steam work, declared with metadata and an execution-safety class. 57 in-tree actions across core and plugins.

| Facet | Definition |
|-------|-----------|
| Owner | The agent process. `ActionRegistry` is agent-local; the CP keeps a hand-mirrored semantics table for dispatch decisions. |
| Lifecycle | Core actions register at agent startup; plugin actions register on plugin load and unregister on unload. There is no persistence: an action exists exactly as long as its assembly is loaded. |
| Persistence | None (in-process registry). Durability of the classification lives in source annotations + the `ActionSemantics` mirror. |
| Identity | `IAction.Name`, case-insensitive (`OrdinalIgnoreCase` lookup). A plugin action with a colliding name silently overwrites a core one (last writer wins) — the convention is prefixed names (`market_watch_*`). |
| Consistency | Three-way agreement is a CI gate (`scripts/verify-actions-safety.py`): source annotations ⊆ CP mirror table ⊆ actions catalog, name-for-name and value-for-value. A registered but unclassified (`Unknown`) action logs a warning and is dispatched under the conservative cap. |

Code index: `IAction`, `ActionMetadata` (`RequiresLogin`, `TimeoutSeconds`, init-only `Safety` defaulting to `Unknown`), `ActionSafety` 5-level enum — `src/Vapor.Steam.Core/IAction.cs`, `src/Vapor.Protocol/ActionSafety.cs`; `ActionRegistry` — `src/Vapor.Steam.Core/`; catalog — [`actions.md`](actions.md).

## 8. Capability

An agent's advertisement of which actions it can run: a flat `action → bool` dictionary in the hello frame, used as a routing filter. Not a separate service or directory — just a field with one consumer.

| Facet | Definition |
|-------|-----------|
| Owner | The agent declares it at hello; the CP reads it, never writes it. |
| Lifecycle | Set once per connection (hello), live until disconnect. An agent restart re-advertises (possibly different, e.g. after plugin changes). Plugin installs update the *inventory mirror* (see Plugin) but the connection's capability set itself is fixed until re-hello. |
| Persistence | None — lives in the `ConnectedAgent` entry only. |
| Identity | Keyed by action name (`OrdinalIgnoreCase`); an absent or empty dictionary means "supports everything" (lenient default for minimal agents). |
| Consistency | Best-effort routing hint, not a guarantee: dispatch double-checks capability, and a mismatch fails the dispatch loudly rather than misrouting silently. |

Code index: `AgentHello.Capabilities` — `src/Vapor.Protocol/Models.cs:124`; `ConnectedAgent.SupportsAction` — `src/Vapor.ControlPlane/AgentRegistry.cs:84`; consumers: `AgentRegistry.Pick(region, action)` for task dispatch, `DesiredStateReconciler.PickAgent` for account placement (least-loaded, agent-id tie-break).

## 9. Event

Fire-and-forget notifications on the SSE streams: job/task lifecycle, session state, auth challenges, agent connectivity, crawl and orchestration outcomes.

| Facet | Definition |
|-------|-----------|
| Owner | Control plane (`EventBroker`). |
| Lifecycle | Published in-process, fanned out to subscribed SSE clients. Bounded channels (capacity 256, `DropOldest`): a slow consumer silently loses old events. No replay, no history — for durable history the audit log (SQLite) is the counterpart, not the broker. |
| Persistence | None, deliberately. Audit-worthy outcomes are written to `SqliteAuditStore` independently. |
| Identity | `Id` = `Guid.NewGuid().ToString("N")` for event records; the SSE frame name is the event `type` (`job.created`, `task.dispatched`, `session.<eventType>`, `auth.<challengeType>`, …). |
| Consistency | At-most-once per consumer, best-effort delivery. Consumers must tolerate gaps; ordering within one channel is FIFO, across channels is not guaranteed. |

Code index: `Event`, `SessionEvent`, `AuthChallengeEvent`, `PluginEvent` — `src/Vapor.Protocol/Events.cs`, `src/Vapor.ControlPlane/IEventBroker.cs`; `EventBroker` (3 bounded channels) — `src/Vapor.ControlPlane/EventBroker.cs`. Mechanics in [`api.md`](api.md) §2.

## 10. Plugin

A versioned extension package: a `plugin.json` manifest plus an assembly, loaded into its own `AssemblyLoadContext` on an agent.

| Facet | Definition |
|-------|-----------|
| Owner | The agent owns installation, loading and filesystem state. The CP holds a mirror (`PluginInventory`) of what it believes is installed, rebuilt from host-action outputs. |
| Lifecycle | Install (zip + mandatory SHA-256, manifest validation, hot-load) → loaded (actions registered) → unload (actions unregistered, ALC collected) → uninstall (directory removed; idempotent). SemVer API gate at load: plugin `ApiVersion` must be compatible with the host's contract. |
| Persistence | Agent filesystem (`/app/plugins` or configured dir). CP mirror: in-memory only, drifts on agent restarts/offline edits; the refresh endpoint re-runs `plugin_list` to re-sync. |
| Identity | `PluginManifest.Id` + `Version` (the plugin's own SemVer, separate from `ApiVersion`). |
| Consistency | CP mirror is best-effort and explicitly allowed to drift; the source of truth is always the agent's directory. Trust levels (`official` / `community` / `unknown`) gate permissions, not correctness. |

Code index: `PluginManifest` — `src/Vapor.Plugins.Core/PluginManifest.cs`; `PluginInfo`, `IPlugin` — `src/Vapor.Plugins.Core/Api/IPlugin.cs`; `PluginManager` — `src/Vapor.Plugins.Core/PluginManager.cs`; `PluginInventory` — `src/Vapor.ControlPlane/PluginInventory.cs`; `PluginCatalogService` (catalog source, HTTP JSON index, 60 s cache). Development guide in [`plugins.md`](plugins.md).

## AuthChallenge (document-level definition)

Auth challenges (Steam Guard code / 2FA / QR) are deliberately **not** a persisted domain object in code: `AuthChallengeTracker` is an in-memory pending-challenges map on the CP, and the wire form is the `AuthChallengeEvent` record. This section pins the document-level definition so later codification (convergence plan §23) has a contract to meet — the code stays transient until that item lands.

| Facet | Definition |
|-------|-----------|
| Owner | Control plane. The *answers* come from the operator (admin console) or the agent-side auto-responder; the challenge record itself never holds credentials beyond the short-lived code, and `code` is stripped from admin-facing views. |
| Lifecycle | Raised by an agent when a login needs a code (`auth_code_required` / `2fa_required` / `qr_required`) → pending in the tracker (one per account, upsert semantics) → resolved by a `code_provided_email` / `code_provided_totp` / `code_provided_2fa` event or session-state change → `Clear` removes the entry. A CP restart clears all pending challenges; agents re-raise what still matters. |
| Persistence | None (in-memory `ConcurrentDictionary<string, AuthChallengeEvent>`, keyed by account). Transient by design. |
| Identity | `AccountName` (one pending challenge per account; older ones are replaced). Event records carry a `Guid "N"` id. |
| Consistency | Best-effort mirror of agent login state; the agent's session state machine is the truth. Six `challengeType` values as listed above; `JobId` links the challenge to the dispatching job when one exists. |

Code index: `AuthChallengeEvent` (`Id`, `AccountName`, `ChallengeType`, `Message?`, `Code?`, `Timestamp`, `JobId?`) — `src/Vapor.Protocol/Events.cs:29` and mirrored in `src/Vapor.ControlPlane/IEventBroker.cs:24`; `AuthChallengeTracker` — `src/Vapor.ControlPlane/AuthChallengeTracker.cs`. Redaction rules in [`api.md`](api.md) §2 and [`architecture.md`](architecture.md) § Security.

---

## Cross-check against companion documents

Verified 2026-10-05 against the texts listed; each row is a claim in this document and its source of truth.

| # | Claim here | Checked against | Status |
|---|-----------|-----------------|--------|
| 1 | Ownership and durability of jobs/specs/audit/crawl/settings (durable) vs registry/sessions/challenges/inventory (in-memory, rebuilt) vs credentials (agent-only) | `consistency.md` §1 state-ownership table | consistent, same 11 rows |
| 2 | Task lifecycle: atomic claim, lease reclaim at `TaskLeaseSeconds` (300 s default), attempt-fenced single terminal record | `consistency.md` §2 lifecycle diagram + `SqliteJobStore` anchors | consistent |
| 3 | Session ten-state machine incl. `ConnectingWaitQr`; agent owns sessions, CP mirrors snapshots | `session-engine.md` § SessionState | consistent |
| 4 | Action identity = `Name` (OrdinalIgnoreCase); plugin collision = silent last-writer-wins | `actions.md` § Registry mechanics | consistent |
| 5 | Safety classes and dispatch cap: `GuardedWrite`/`NonIdempotent`/`Unknown` cap 2, `ReadOnly`/`Idempotent` configured ceiling | `actions.md` § Conventions (execution safety), `production.md` § Undispatchable tasks | consistent |
| 6 | Account desired states `Offline`/`Online`/`Idle`/`Farm`/`Boost` | `actions.md` § orchestration payload docs, `getting-started.md` | consistent |
| 7 | Agent registration sequence (bearer → WS upgrade → `agentId`/`region` → hello or `PolicyViolation`) | `api.md` §3 handshake | consistent |
| 8 | SSE mechanics: bounded 256 `DropOldest`, no replay, `code` stripped on admin view | `api.md` §2 | consistent |
| 9 | Reconciler placement: pinned agent first, else region+capability+capacity, least-loaded with agent-id tie-break | `roadmap.md` §2, `DesiredStateReconciler.PickAgent` | consistent |
| 10 | Plugin lifecycle and SemVer API gate; inventory mirror is best-effort | `plugins.md` § lifecycle / trust | consistent |
