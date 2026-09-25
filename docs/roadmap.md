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

## 3. Timeouts, retries, circuit breaking, rate limiting — 🟡 → ✅ this round (P0)

Layer-by-layer inventory:

| Layer | Mechanism | Status |
|-------|-----------|--------|
| agent → Steam Web | retry, differentiated 429 (`Retry-After`, capped) / 5xx backoff | ✅ |
| agent → Steam Web | circuit breaker (Closed/Open/HalfOpen, single probe) | ✅ |
| agent → Steam Web | request pacing (default 1 req/s, configurable) | ✅ |
| action execution | declared per-action `TimeoutSeconds` (bounded, structured `action timeout` result) | 🟡 most actions declare one |
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

## 4. Auth & key management — ✅ (P1 residual)

**Status.** Per-role API keys (admin / agent) guarding REST, SSE and the tunnel
handshake; HMAC-SHA256-signed webhooks; AES-GCM credential store with
KMS-friendly master-key sources (`BASE64` / `FILE` / plain), rotation CLI, and
v1→v2 transparent migration; audit log with redaction-at-rest; output-level log
redaction (messages, scopes, exceptions); proxy credentials redacted end-to-end.

**Gaps.** No OIDC / mTLS (listed as future work in `architecture.md`), no API
key expiry or rotation policy, no per-tenant quotas beyond the rate limiter
added this round.

**Priority rationale.** The single-operator / small-team security model is
complete; OIDC and short-lived agent credentials become P1 when the API surface
opens to multiple tenants.

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

## 6. Distributed tracing & metrics — 🟡 → ✅ this round (P0)

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

**Later (P2).** Exemplars linking traces to metric series; tail-based sampling.

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

## 8. Fault injection & drills — 🟡 (P2)

**Status.** Deterministic test seams throughout (`ProbeOverride`,
`FetchOverride`, fake Redis, injectable clocks, happens-before gates in test
fixtures); E2E drills including kill-the-agent rebalancing and
dispatch-failure paths against stub and real processes.

**Gaps.** No production chaos / fault-injection API.

**Priority rationale.** The seam discipline gives deterministic failure
rehearsal in CI; production fault injection is valuable mainly for the
multi-instance topology that does not exist yet.

## 9. State sync & consistency — 🟡 (P1 to document, P2 to build)

**Status.** The control plane is the single writer and sole state owner
(SQLite: jobs, accounts, audit, crawl). `ConfigVersion` optimistic concurrency
guards spec updates; task claiming is lease-based with at-least-once delivery
and idempotent, attempt-tracked execution; agent session state syncs
eventually into the CP `SessionTracker`; caches are stale-while-revalidate with
single-flight dedup, cross-instance when Redis is enabled.

**Gaps.** Single-instance control plane: no HA, no horizontal write scaling,
restart = brief orchestration pause (agents keep sessions via token restore).

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

## Explicit non-goals (for now)

- gRPC tunnel migration, external message-queue backbone (NATS/Kafka), and
  multi-instance control plane — reevaluate after the P0 items have soaked.
- Mesh-style mTLS between services — revisit together with multi-tenant auth.
