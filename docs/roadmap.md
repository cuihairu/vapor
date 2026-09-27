# Vapor Roadmap — Distributed-System Capability Map

Vapor is positioned as an **API-driven distributed system**: a control plane that
owns state and exposes a public REST/SSE surface, and a fleet of agents that
execute jobs close to Steam's regional endpoints over an outbound tunnel. This
map inventories the capabilities such a system needs, marks where Vapor stands
today (surveyed 2026-09-25, all claims verified against source), and orders the
remaining gaps by priority.

Companion documents:

- `feature-matrix.md` — Steam-feature parity vs. ASF (what the platform *does*)
- this file — distributed-systems infrastructure (how the platform *runs*)
- `architecture.md` — component design and trade-offs

Legend: ✅ solid · 🟡 partial · ❌ absent. Priority: **P0** = highest value, do
now · **P1** = next · **P2** = later / reevaluate.

---

## 1. Service registration & discovery — ✅ (P2 residual)

**Status.** Agents self-register over the outbound tunnel (`agent_hello` with
id, region, capability list); the control plane keeps an in-memory
`AgentRegistry` with heartbeat-driven liveness and deregisters on disconnect.
Capabilities double as a routing filter (`SupportsAction`), and the plugin
inventory is mirrored per agent from task results. Discovery is deliberately
CP-centric: agents never discover each other, and the CP endpoint is static
agent-side config.

**Gaps.** No external registry (Consul/etcd/DNS). That only becomes relevant
once there is more than one control-plane instance (see §9).

**Priority rationale.** With a single control plane, the in-memory registry is
the whole truth; an external registry would add a dependency without removing
one.

## 2. Routing & load balancing — 🟡 (P2)

**Status.** Job dispatch is region-scoped with capability matching and random
agent pick; the desired-state reconciler selects the **least-loaded** capable
agent (deterministic tie-break by agent id) under per-agent capacity caps;
`agent:{id}` targets route directly (plugin ops, pinned accounts); agent loss
triggers rebalancing of its accounts.

**Gaps.** Dispatch pick is not latency/health-aware; no client-side load
balancing; no consistent hashing. None of these bind today: accounts are pinned
to agents by the reconciler, and job dispatch is a queue, not a hot path.

**Priority rationale.** Adequate at current scale; revisit if per-agent queue
depth becomes a scheduling constraint.

## 3. Timeouts, retries, circuit breaking, rate limiting — ✅ (complete this round; P1 residual: none material at single-CP scale)

Layer-by-layer inventory:

| Layer | Mechanism | Status |
|-------|-----------|--------|
| agent → Steam Web | retry, differentiated 429 (`Retry-After`, capped) / 5xx backoff | ✅ |
| agent → Steam Web | circuit breaker (Closed/Open/HalfOpen, single probe) | ✅ |
| agent → Steam Web | request pacing (default 1 req/s, configurable) | ✅ |
| action execution | declared per-action `TimeoutSeconds` (bounded, structured `action timeout` result; all in-tree session + host actions declare one, enforced on both paths) | ✅ |
| action execution | agent-level task watchdog (belt over unbraced actions, host actions, session-restore path) | ❌ → landed this round |
| CP → agent dispatch | dispatch attempts + retry delay + lease reclaim + requeue | ✅ |
| agent → CP tunnel | reconnect with exponential backoff (500 ms → 10 s, configurable, unlimited default) | ✅ |
| CP → webhooks | bounded retries with exponential backoff, 10 s HTTP timeout, per-sink isolation | ✅ |
| trade operations | per-account sliding-window quota + concurrency gate | ✅ |
| crawl | per-run hard timeout, per-batch pacing | ✅ |
| **CP inbound REST** | per-key rate limiting | ❌ → landed this round |

**Why these two are P0.** The watchdog closes a real deadlock: a hung action
held the agent's serial task loop forever while its heartbeat kept the CP lease
alive — no layer below the operator would recover it. Inbound rate limiting is
the missing protection plane for an API-first system whose control plane is, by
design, the only ingress.

## 4. Auth & key management — ✅ (complete this round; residual: none material)

**Status.** Per-role API keys (admin / agent) guarding REST, SSE and the tunnel
handshake; HMAC-SHA256-signed webhooks; AES-GCM credential store with
KMS-friendly master-key sources (`BASE64` / `FILE` / plain), rotation CLI, and
v1→v2 transparent migration; audit log with redaction-at-rest; output-level log
redaction (messages, scopes, exceptions); proxy credentials redacted end-to-end.
API keys carry an optional `@<ISO-8601>` expiry suffix (`Vapor_AGENT_API_KEYS`
per key, `Vapor_ADMIN_API_KEY` likewise): a key is valid strictly before its
expiry instant and rejected like an unknown key from that instant on, which
turns rotation into a staged cutover (new key plain, old key with a deadline)
instead of a coordinated dual restart — see `production.md` § Key rotation.

**Gaps.** None material. No OIDC/mTLS by design, not by omission — see
the non-goals below: Vapor is a single-operator self-hosted system, and there
is no tenant concept in the data model.

**Priority rationale.** The single-operator security model is complete,
including the rotation policy; multi-tenancy remains explicitly out of scope
(see `architecture.md` Non-goals).

## 5. Protocol & serialization — ✅ (P2)

**Status.** JSON everywhere: REST (System.Text.Json, camelCase, enum-as-string),
a closed four-message WebSocket envelope for the tunnel, SSE events, webhook
JSON. Protocol records are round-trip property-tested; plugin API compatibility
is SemVer-gated; W3C trace context rides the tunnel (see §6).

**Gaps.** No message-schema version negotiation (the envelope is closed, which
is also its protection); no protobuf/gRPC; no schema registry.

**Priority rationale.** JSON + OpenAPI is the right default for an API-first
system; binary protocols were considered and rejected for now (see
`architecture.md` trade-offs).

## 6. Distributed tracing & metrics — ✅ (exemplars landed this round; residual: tail-based sampling, a collector-side policy)

**Status.**

- *Tracing*: OpenTelemetry on both services (opt-in via `OTEL_EXPORTER_OTLP_ENDPOINT`).
  The full dispatch chain is span-linked across the tunnel in both directions:
  `task.dispatch` (producer, CP) → `task.execute` (consumer, agent, parented by
  the propagated `traceparent`) → `task.result` (consumer, CP, parented by the
  `traceparent` the agent returns). ASP.NET Core instrumentation covers the REST
  surface.
- *Metrics*: CP `/metrics` exports business counters (tasks by status, dispatch
  failures by reason, reconcile actions, schedule outcomes, crawl outcomes,
  notification deliveries); the agent exposes Prometheus on `:9700` via the
  Monitoring plugin (actions, cache, sessions, runtime); a Grafana dashboard
  ships in the compose observability profile.

**Gap → landed this round.** RED metrics for the REST surface itself:
`vapor_controlplane_http_requests_total{method,route,status}` and duration
sum/count — request-level visibility independent of the optional OTel pipeline.

**Gap → landed (second pass).** Exemplars: every request-counter sample
carries the ambient W3C trace id (`# {trace_id="<32 hex>"} 1`), so a
slow/erroring route on a dashboard links straight to a concrete trace;
scrapers that ignore exemplars see identical values, keeping the two
surfaces independent (see `production.md` Monitoring).

**Residual (P2, deliberate).** Tail-based sampling lives in an OTel
Collector between the services and the backend — intentionally not a
service-side knob at single-operator scale (`production.md` Distributed
tracing).

## 7. Config distribution & canary — 🟡 (P2)

**Status.** Desired-state specs are the config-distribution backbone:
declarative, versioned (optimistic concurrency), converged by the reconciler
loop. Plugin behavior is configured via manifest + env overrides; runtime
plugin install is per-agent targetable, which doubles as a coarse canary lever
(install on one agent first). Global dry-run mode previews orchestration
decisions without dispatching.

**Gaps.** No percentage rollouts, no feature flags.

**Priority rationale.** The desired-state model already covers "change config,
watch it converge"; fractional rollout machinery pays off only with much larger
fleets.

## 8. Fault injection & drills — ✅ (complete this round; P2 residual: none material)

**Status.** Deterministic test seams throughout (`ProbeOverride`,
`FetchOverride`, fake Redis, injectable clocks, happens-before gates in test
fixtures); E2E drills including kill-the-agent rebalancing and
dispatch-failure paths against stub and real processes. **Runtime fault
injection API landed this round**: admin-only `/v1/faults` endpoints
(`FaultInjector`) arm error/delay injections on two planes — task dispatch
(after claim, riding the real requeue/retry machinery) and `/v1` API requests
(edge middleware; the fault endpoints, `/healthz` and `/metrics` stay exempt
so a drill is always observable and stoppable). Every fault self-heals via a
budget (auto-remove at exhaustion) and a TTL; a panic button (`DELETE
/v1/faults`) disarms everything; state is in-memory so a CP restart disarms
all — deliberate (outliving the operator's attention is what the bounds exist
to prevent). Observable via `vapor_controlplane_fault_injections_total{kind,mode}`
/ `vapor_controlplane_faults_armed` and the `faults.enable/disable/clear`
audit actions.

**Gaps.** None material at single-CP scale (a multi-instance topology would
want per-plane scoping and a global disarm barrier — revisit with §9 HA).

**Priority rationale.** The seam discipline gives deterministic failure
rehearsal in CI; the runtime API adds production drills against the real
machinery without a chaos framework.

## 9. State sync & consistency — 🟡 (model documented, declared state now durable; P2 to build HA)

**Status.** The control plane is the single writer and sole state owner —
and all of its state now survives a restart: SQLite backs jobs, audit and
crawl directly, while account specs and settings (the declared farm) live in
memory read paths write-through-persisted to SQLite by `SqliteConfigStore`
and rehydrated at startup (landed this round). `ConfigVersion` is a monotonic convergence trigger on a
full-replacement PUT store (single writer makes CAS unnecessary); task
claiming is lease-based with at-least-once delivery, attempt-fenced reporting
and one terminal record per task; agent session state syncs eventually into
the CP `SessionTracker`; caches are stale-while-revalidate with single-flight
dedup, cross-instance when Redis is enabled. **The full model — guarantees,
non-guarantees, failure/restart semantics — is documented in
[`consistency.md`](consistency.md)** (landed this round, §3's per-action
timeout row completed in the same round).

**Gaps.** Single-instance control plane: no HA, no horizontal write scaling,
restart = brief orchestration pause (agents keep sessions via token restore,
and the declared farm is rehydrated from SQLite — no manual re-declaration).

**Priority rationale.** Single-CP is an explicit, documented trade-off (see
`architecture.md`): it buys transactional consistency and zero coordination.
HA (external DB or leader election) is the largest future item on this map —
deliberately sequenced behind the P0 hardening.

---

## Landed this round (P0)

1. **Execution-timeout completeness** (§3): every in-tree action declares a
   `TimeoutSeconds`, and the agent gained a task-level watchdog
   (`AGENT_TASK_TIMEOUT_SECONDS`, default 900 s, `<= 0` disables) that cancels
   the hung task, reports a structured failure to the control plane, and keeps
   the loop serving the next task.
2. **API-surface observability & protection** (§6 + §3): the control plane now
   records RED metrics for every REST request (exposed on `/metrics`) and
   supports per-key sliding-window rate limiting
   (`Vapor_API_RATE_LIMIT_PER_MINUTE`, default off) returning `429` with
   `Retry-After` and a rejection counter.
3. **Consistency model documentation** (§9, P1-to-document): the delivery and
   consistency guarantees — at-least-once with attempt fencing, lease
   reclaim, convergence semantics, failure/restart behavior — are now written
   down in [`consistency.md`](consistency.md).
4. **API key expiry** (§4, P1 residual): every configured API key accepts an
   optional `@<ISO-8601>` expiry suffix; expired keys fail closed with the
   same 401 as unknown keys (admin REST/SSE and the agent tunnel handshake
   alike), giving single-operator key rotation a deadline instead of a
   coordinated restart.
5. **Fault-injection API** (§8, P2): admin-only `/v1/faults` endpoints arm
   bounded error/delay drills on the task-dispatch and api-request planes
   (budget + TTL self-healing, panic button, exempt control surface,
   Prometheus counters and audit trail) — see §8 above.
6. **Metric→trace exemplars** (§6, P2 residual): each
   `vapor_controlplane_http_requests_total` sample carries the ambient W3C
   trace id as a Prometheus text-format exemplar — drilled errors and
   organic ones alike are one click from a concrete trace; tail-based
   sampling documented as collector-side policy (§6).
7. **Declared-state durability** (§9): account specs and settings are
   write-through-persisted to SQLite (`SqliteConfigStore`,
   `Vapor_CONFIG_DB_PATH`) and rehydrated at startup — the control plane's
   last non-persisted state is gone, a restart no longer erases the farm
   declaration, and the consistency.md claim that settings "survive CP
   restart" became true of the code instead of aspirational.

## Explicit non-goals (for now)

- **Multi-tenancy in any form** (per-tenant auth, quotas, namespacing,
  OIDC/RBAC) — out of scope by design, not deferred: Vapor is a
  single-operator self-hosted system and its threat model has exactly one
  admin. See `architecture.md` Non-goals.
- gRPC tunnel migration, external message-queue backbone (NATS/Kafka), and
  multi-instance control plane — reevaluate after the P0 items have soaked.
- Mesh-style mTLS between services — unnecessary at one control plane with
  key-authenticated agents over an outbound-only tunnel.
