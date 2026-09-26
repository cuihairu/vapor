# Vapor Consistency Model

This document states the consistency and delivery guarantees Vapor actually
gives — task delivery, state ownership, failure and restart semantics — and,
just as importantly, what it does **not** guarantee. Everything here is
verified against source (`src/Vapor.ControlPlane`, `src/Vapor.Agent`,
`src/Vapor.Steam.Core`); file anchors are given per section. Companion
documents: [`architecture.md`](architecture.md) (component design and
trade-offs), [`roadmap.md`](roadmap.md) (capability map).

The model rests on one decision (see
[architecture.md § Control plane](architecture.md)): **the control plane is
the single writer and sole state owner**. One process owns the SQLite stores;
every write to a store is serialized in-process (a per-store mutex/lock). No
cross-instance coordination, no quorums, no leader election — the transactional
story is "one writer, one lock, one file". Everything below follows from that.

---

## 1. State ownership map

| State | Owner | Store | Durability |
|-------|-------|-------|------------|
| Jobs, tasks, schedules | Control plane | SQLite (`SqliteJobStore`) | Survives CP restart |
| Account specs (desired state) | Control plane | In-memory `AccountStore` behind `_gate` | Declared via API; re-declare after restart |
| Audit log | Control plane | SQLite (`SqliteAuditStore`) | Survives CP restart |
| Crawl plans / results | Control plane | SQLite (`SqliteCrawlStore`) | Survives CP restart |
| Global / per-account settings | Control plane | SQLite-backed `ConfigStore` | Survives CP restart |
| Agent registry (who is connected) | Control plane | In-memory `AgentRegistry` | Rebuilt from agent re-hello |
| Session snapshots | Control plane | In-memory `SessionTracker` (agent-reported) | Re-populated by agent session events |
| Auth challenges | Control plane | In-memory `AuthChallengeTracker` | Transient by design |
| Plugin inventory mirror | Control plane | In-memory `PluginInventory` | Rebuilt from task results |
| Steam sessions / credentials | **Agent** | Encrypted local store (AES-GCM) | Never leaves the agent |
| Live Steam connections | Agent | CM/Web per account | Agent-local; token restore after restart |

The split is deliberate: anything that must not be lost lives in the control
plane's SQLite; anything derivable (registry, sessions, inventory) is
in-memory and rebuilt by convergence. Credentials are the exception — they
never reach the control plane at all (`architecture.md` § Security).

## 2. Tasks: state machine, leases, at-least-once delivery

### Lifecycle

```
Queued ──claim──▶ Running ──result ok──▶ Finished
   ▲                 │  │
   │                 │  └─result fail─▶ Failed (terminal; error + output kept)
   │                 │
   │   lease expired (no heartbeat within TaskLeaseSeconds, default 300 s)
   └─────────────────┘
   Queued ──cancel──▶ Canceled        Queued ──attempts exhausted──▶ Failed
```

Anchors: `SqliteJobStore.ClaimNextQueuedTask`, `RequeueStaleRunningTasks`,
`SetTaskResult`, `CancelJob` (`src/Vapor.ControlPlane/SqliteJobStore.cs`).

### Claiming and fencing

- **Claim is atomic.** `ClaimNextQueuedTask` flips one `Queued` task to
  `Running` and increments `attempt` inside the store mutex — two dispatchers
  can never hold the same (task, attempt).
- **Attempts fence execution and reporting.** Heartbeats extend the lease only
  when `status = Running AND attempt = $attempt`
  (`HeartbeatTask`); a heartbeat from a superseded attempt is a no-op that
  returns `false`. Results are written only when the task is still `Running`
  with the matching attempt (`SetTaskResult` throws `NotFoundException` on
  "task not running" / "task attempt mismatch"). A late report from an attempt
  whose lease was reclaimed is **rejected, not applied** — a reclaimed task
  converges to exactly one terminal state, written by whichever attempt the
  control plane currently believes owns it.
- **Delivery is at-least-once.** If a task's lease lapses (agent crash,
  partition, lost process), `RequeueStaleRunningTasks` puts it back to
  `Queued` and the scheduler dispatches it again — possibly to a different
  agent, and possibly while the original agent is still executing it. The
  attempt number is what makes this safe (see the idempotency contract below).

### Retry and backoff

- Dispatch failures (no capable agent, enqueue failure) retry with
  `TaskDispatchRetryDelayMs` (default 2000 ms) as the next-attempt gate
  (`next_attempt_at_ms`), up to `TaskMaxDispatchAttempts` (default 10;
  `<= 0` = unlimited) before the task fails permanently.
- Task-level cancellation flows CP → agent over the tunnel as `task_cancel`
  keyed by `(taskId, attempt)`; the agent suppresses the stale-task report and
  the store has already recorded the cancel.

### The idempotency contract

At-least-once delivery means **an action may execute twice** (original
attempt still alive after lease reclaim). The contract that makes this
safe, per action class:

- **Reads** (`get_inventory`, `get_trade_offers`, `get_achievements`, …) are
  naturally idempotent — re-execution re-reads the same truth.
- **Steam-side state checks** (farm queue, boost schedule, standing checks)
  recompute from Steam's own state, so a duplicate execution converges to the
  same outcome.
- **Writes** go through guarded paths: market listing creation is
  double-gated (account spec opt-in + agent-side switch), trade operations sit
  behind the per-account trade safety layer (sliding-window quota +
  concurrency gate), and the achievement write API demands an explicit
  name list (and `confirm: true` for reset). The gates bound the blast
  radius of a duplicate; they do not make the write exactly-once.
- **Effects visible in the task output are the record of execution** — the
  control plane keeps the output of the terminal attempt, which is the
  operator's audit trail for what actually ran.

### Agent-side watchdog interplay

The agent's task watchdog (`AGENT_TASK_TIMEOUT_SECONDS`, default 900 s)
bounds execution below the CP lease: a hung task is cancelled and reported as
a structured failure *before* the control plane would reclaim the lease, so
the normal path never exercises lease-reclaim redelivery. Lease reclaim
remains the safety net for agent loss (see
[architecture.md § Agent task loop](architecture.md)).

## 3. Account specs: monotonic versions, convergence not CAS

Account desired state (`AccountStore`) is a full-replacement PUT store:

- Writes are serialized by the store lock; there is no compare-and-swap
  rejection at the API boundary. **Every PUT wins** and bumps a monotonic
  `ConfigVersion` (version, timestamp, updated-by).
- The version is the **convergence trigger**: the desired-state reconciler
  compares each account's runtime `SpecVersion` against the spec's current
  version and re-converges on mismatch (`DesiredStateReconciler.cs`). Change
  detection is "version moved", not "field diffed".
- Reads always return the latest committed spec; because there is a single
  writer and in-process serialization, there is no stale-read window on the
  spec store itself.

The reconciler loop is what turns PUTs into convergence: it is
**eventually consistent by design** — a PUT returns immediately, and the
desired state (login, dispatch, trade policy, farm queue) materializes on the
next reconcile pass (`ReconcileIntervalSeconds`, default 15 s). Audit entries
record every spec change with its version.

## 4. Sessions and observability state: eventual, best-effort

- **Session snapshots** (`SessionTracker`) are agent-reported and converge
  asynchronously; the reconciler treats snapshots older than
  `ReconcileSessionStalenessSeconds` (default 120 s) as stale rather than
  authoritative. A missing snapshot is "unknown", never "offline" — the
  agent's tunnel connection is the liveness source.
- **SSE streams** are best-effort event delivery for UIs, not a durable log:
  events published while no subscriber is attached are not replayed (SSE
  subscribers get events from subscription time forward). The durable record
  of what happened is the SQLite stores (tasks, audit), not the event bus.
- **Metrics** are process-lifetime counters; they reset on restart. The
  durable history is in the stores.

## 5. Caches: stale-while-revalidate with single-flight

`IVaporCache` implementations (`MemoryVaporCache`, `RedisVaporCache` in
`src/Vapor.Steam.Core/Caching`) provide TTL caches with an optional stale
window:

- Within TTL: hit. Past TTL but within `staleTtl`: **stale hit** — the old
  value is returned immediately while one single-flight refresh re-reads the
  source (other callers of the same key wait on that one refresh instead of
  stampeding Steam). Past the stale window: miss.
- Redis is opt-in (`VAPOR_REDIS` connection string; in-memory otherwise); with
  it, the stale semantics hold across agent processes sharing the Redis
  instance. Without it, caches are per-agent.
- Cache staleness is a **Steam-data** concern only — none of the control
  plane's own state (jobs, specs, audit) flows through this layer.

## 6. Failure and restart semantics

### Control plane restart

- Survives: jobs, tasks, schedules, audit, crawl state (SQLite).
- Lost and rebuilt: agent registry (agents reconnect with their exponential
  backoff and re-hello), session snapshots (agents re-report), challenge
  prompts, plugin mirror (rebuilt from task results), rate-limiter windows
  (fresh start — a restart resets limiting), API metrics counters.
- In-flight tasks: the executing agents keep running and keep heartbeating;
  on reconnect the heartbeats land on the restored store rows. A task whose
  lease fully lapses across the outage is reclaimed and redelivered
  (at-least-once, as above). Orchestration pauses for the restart duration;
  agents keep their Steam sessions alive via token restore.

### Agent restart

- Credentials/session tokens restore from the encrypted local store; sessions
  re-establish without operator action.
- Any task it held at crash time requeues via CP lease expiry and is
  redispatched (attempt +1).

### Network partition (agent ↔ control plane)

- The agent keeps executing its current task (its cancellation source is
  local; the watchdog still bounds it). The result report, however, is sent
  only on the live socket — there is no agent-side outbox. If the tunnel is
  down at report time, the report is lost and the **control plane's lease
  reclaim is the recovery path**, not the agent.
- The control plane sees heartbeats stop; after `TaskLeaseSeconds` the task
  requeues and may be dispatched elsewhere. If the partitioned agent later
  regains the socket while the task is still `Running` with its attempt, its
  report lands normally; if the lease was reclaimed meanwhile, the attempt
  fence rejects the stale result. **Both agents may do the work once; the
  control plane records one outcome** — this is the visible cost of
  at-least-once, and the reason the write-path gates exist.

## 7. Guarantees and non-guarantees

**Guaranteed:**

1. Single writer for all control-plane state; serialized reads-modify-write
   per store.
2. Atomic task claiming; at most one live (task, attempt) pair.
3. At-least-once task *delivery*, fenced by attempts; exactly-one terminal
   record per task.
4. Full-replacement PUT semantics on account specs with monotonic versions
   and audit entries; convergence on the next reconcile pass.
5. Durable record of every task outcome, dispatch failure and audit action in
   SQLite.

**Not guaranteed (deliberate):**

1. **Exactly-once execution.** At-least-once + idempotency contract instead;
   duplicate execution of a reclaimed task is possible while the original
   attempt is still alive.
2. **Multi-instance control plane.** No HA, no horizontal write scaling, no
   external registry — an explicit trade-off (`architecture.md`); restarting
   the CP pauses orchestration briefly.
3. **Event replay.** SSE is live-tail only; the stores are the history.
4. **Cross-store transactions.** Jobs, specs, audit and crawl are separate
   stores; an audit write failing does not roll back the API response (audit
   persistence failures are logged, never block).
5. **Read-your-writes against Steam itself.** Steam's own state (trade offers,
   inventory, playtime) is eventually observed through the same caches and
   reconcile loops.

---

*Feedback loop:* when a new state owner, delivery path or failure mode lands,
update the relevant section here and the matching row in
[`roadmap.md`](roadmap.md) §9 in the same change.
