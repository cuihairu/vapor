English | [简体中文](zh/vnext-convergence-plan/)

# Vapor V-next Convergence Plan

> Status: **Pending review** (after review approval, execute package by package; no code changes before execution).
> Basis: `docs/review/chatgpt-review-2026-10-04.md` (ChatGPT review) + `docs/review/chatgpt-review-2026-10-04-recheck.md` (item-by-item recheck report, baseline commit `0419512`).
> Companion documents: `architecture.md` (components and trade-offs), `consistency.md` (consistency model), `roadmap.md` (distributed capability map), `feature-matrix.md` (vs ASF feature matrix), `actions.md` (action catalog), `tests/TESTING.md` (testing discipline).

## 0. Overall judgment and goals

The recheck conclusion (30 confirmed / 3 outdated / 0 unfounded, see the recheck report): the review's **architectural judgments about this repository are all confirmed** — the Control Plane / Agent separation, Desired State convergence, Task Lease + Attempt fencing, SQLite single-writer, Plugin collectible ALC, and the five-layer Session Engine structure are all real capabilities already landed at the implementation/test level. The review's **three outdated items** (§13 ALC security wording, §26 fault injection, §28 performance benchmarks) point to work this repository has already completed, requiring no further investment.

The V-next convergence direction is therefore set as:

1. **Core model consolidation**: write down the five elements — Owner / Lifecycle / Persistence / Identity / Consistency — for Job/Task/Attempt, Account/Session/Agent, and Action/Capability (currently scattered across 4 documents with no single authoritative source).
2. **Add the genuinely missing layer**: Action Execution Semantics (the largest real gap flagged by review §8/§11, directly tied to the at-least-once contract in consistency.md).
3. **Close out the Desired State convergence surface**: per-account observed/reason presentation plus one-click Reconcile (most of this already exists today).
4. **Digitize count and documentation synchronization**: zero out stale README numbers (37+ actions / 50 endpoints); put action and route counts under script guard.
5. **Restate the boundaries**: no distributed CP, no process-level Plugin sandbox, and no more treating "piling up Action count" as a growth metric.

## 1. Decision summary table

| Category | Item | Basis (review items) | Disposition |
|---|---|---|---|
| **Keep** | CP/Agent separation, outbound WSS, Job→Task→Agent→Session→Action chain, Desired State + ConfigVersion convergence, Lease+fencing+at-least-once, SQLite single-writer, Plugin collectible ALC, Session Engine five layers, secrets stay on the Agent, SSE best-effort, HMAC webhook, system-level performance baseline | §1–§7, §14, §15, §20, §21, §22, §24, §25, §28 (main body) | Unchanged; documentation placement only |
| **Strengthen (P0)** | Action Execution Semantics; core domain model dictionary; Desired-State convergence surface per-account presentation | §8, §11; §33-P0; §19, §33-P0 | See §2 |
| **Strengthen (P1)** | README/count synchronization + verify scripts; brand positioning sentence; Plugin manifest configuration schema and dependency declaration; FakeSteam deterministic test backend | §29, §10; §30; §12, §33-P1; §27 | See §3 |
| **Demote (P2, signal-gated)** | webhook delivery records, AuthChallenge domain object codification, Event three-tier bucketing, secret version semantics, vapor backup CLI, Placement/Capability matcher, unified trace field convention, Operations Console enhancements | §25 (residual), §23, §24, §22, §21, §16+§17, §18 (residual), §19 (residual) | See §4; not scheduled |
| **Drop** | No further investment in outdated items | §13, §26, §28 (main body) | Not doing |
| **Explicitly not doing (non-goals)** | Distributed CP (PostgreSQL/Redis/NATS/leader election), gRPC/mTLS mesh, multi-tenancy/RBAC, process-level Plugin sandbox, "Action count" growth metric | §20, §13, §33-P1 (sandbox), §10 | See §5; consistent with the non-goals at `roadmap.md:249-258` |

## 2. P0 work packages (do first)

### P0-A: Action Execution Semantics

**Goal**: declare an execution safety classification for every Action so that Scheduler and retry decisions can read the semantics instead of treating all actions alike (57 at project inception, grown to 65 after landing).

**Adopted from**: review §8 (Execution Semantics: ReadOnly/IdempotentWrite/NonIdempotent/Interactive/LongRunning/Compensatable), §11 (Capability metadata), §7 (the engineering realization of at-least-once ≠ exactly-once).

**Scope**:
- Add a classification field to `ActionMetadata`: an `ActionSafety` enum of `ReadOnly / Idempotent / GuardedWrite / NonIdempotent`, defaulting (when undeclared) to `Unknown` and handled **conservatively as NonIdempotent** (no automatic retry, no parallel re-dispatch).
- Each of the 65 actions declares its classification individually (mechanically checked at registration; no missed labels allowed).
- Minimal closed loop: the retry policy takes effect by classification — `ReadOnly/Idempotent` keep the existing `TaskMaxDispatchAttempts` retry; `NonIdempotent/GuardedWrite` retry at most once (only on task-pipeline failure) to avoid duplicating external side effects; `GuardedWrite` requires an explicit idempotency key (`idempotency_external_key` support) before multiple attempts are allowed. Interactive/LongRunning get no new enums — they are expressed by the existing `RequiresLogin`/`TimeoutSeconds` respectively (avoiding over-engineering).
- Fill in the classification column for every action in `actions.md`, guarded by script (see P1-A).
- Scheduler-side changes are limited to retry decisions reading the classification; **do not touch** the tunnel protocol, the Job queue model, or the task state machine.

**Boundaries**:
- Do not change the `POST /v1/jobs` envelope or wire format (classification is metadata and does not enter the payload contract); external-key idempotency support is an optional part of P0-A — if assessed as too invasive, it is demoted to a separate P2 item.
- `IAction`/`ActionMetadata` are part of the Plugin API surface: handle them per the API compatibility rules in `docs/releasing.md` (new fields carry defaults; each round must verify that the load/unload tests of the six in-tree official plugins do not regress).

**Acceptance criteria**:
1. All 65 actions explicitly declare `ActionSafety`; `ActionRegistry.Register` rejects undeclared ones (at least a warning → escalated to rejection in later rounds).
2. The retry policy takes effect by classification, with test coverage: a `NonIdempotent` action failing in the pipeline does not trigger a second dispatch; `Idempotent` keeps its retry.
3. The `actions.md` classification column and the code stay single-source; verify script negative probe (manually change one spot → EXIT=1).
4. API compatibility: the six official plugins' load/unload tests all green; five verify + format + the full serial double-100 gates all green (criteria in §6).

### P0-B: Core domain model dictionary

**Goal**: one authoritative document that defines the platform's core objects clearly. Review §33-P0 (Owner/Lifecycle/Persistence/Identity/Consistency) flags this as P0; the recheck confirms the content is currently scattered across four places — `consistency.md §1` (state ownership), `architecture.md`, `session-engine.md`, and `api.md` — while `data-dictionary.md` is actually a gamedata/crawl dictionary (`docs/data-dictionary.md:7-108`) that **does not cover the platform core model**.

**Scope**:
- Add `docs/domain-model.md`: define the five elements for each of the ten objects — Account / Session / Agent / Job / Task / Attempt / Action / Capability / Event / Plugin — using the review §31 diagram (verified consistent with the current state) as the relationship skeleton.
- Define AuthChallenge along the way as a domain object (settled at the document level first: id/account/session/type/createdAt/expiresAt/status/attempt/source); the code stays as-is (transient, see P2-2).
- Each object entry carries a "code type index" (type name + file path) serving as a mapping between documentation and implementation.

**Boundaries**:
- **Documentation only, no code refactoring**. The conceptual layering (JobDefinition/Schedule/Execution separation, review §9) is provisionally defined in the document first; no split is made at the code level (the current `RecurringJobScheduler` + `SqliteJobStore` already covers that semantics).
- Do not duplicate what `consistency.md` and `architecture.md` say; the three cross-link, and the new document holds only the unique facts (e.g. the object five-elements, the AuthChallenge fields).

**Acceptance criteria**:
1. `docs/domain-model.md` covers all five elements for all ten objects; every object traces back to a concrete code type (the reference-existence check passes in one pass).
2. No contradictions with `consistency.md §1`, `architecture.md`, or `actions.md` (manual check list appended at the end of the document).
3. The README document index table (`README.md:78-84`) gains a row for the new document.

### P0-C: Desired-State convergence surface close-out

**Goal**: fully present each account's "desired / actual / reason / convergence" across the API and UI. The recheck confirms: the reconciler already produces structured reason (`DesiredStateReconciler.cs:471,562,620,664,693`), and the dashboard already shows desired/actual mismatch (`dashboard.html:861-866`) and overall reasons (`dashboard.html:789-801`); the gap is **per-account aggregated presentation of convergence reasons + a one-click Reconcile action**.

**Scope**:
- API: the account view carries observed (actual session state / next step when offline) + reason (the reconciler's most recent convergence decision for that account) fields; the field contract goes into `api.md` and under the verify-api-docs criteria.
- UI: the dashboard account row shows reason; provide a "Reconcile" action (triggers one forced convergence).
- No new convergence-engine logic; if implementation finds reason is not stored/persisted, do only an in-memory "latest decision" presentation, with persistence as a P2 discussion item.

**Boundaries**: do not change the reconciler scheduling semantics; UI changes are limited to dashboard.html (admin.html untouched); no long-lived connection/notification mechanism is introduced.

**Acceptance criteria**:
1. `GET /v1/accounts/{name}` (or the existing account-list criteria) returns observed + reason fields, with the contract guarded by verify-api-docs (positive/negative probes).
2. The dashboard account row renders reason; clicking "Reconcile" triggers convergence and is auditable (audit log entry).
3. E2E covers reason propagation and the Reconcile action (included in the full gate).

## 3. P1 work packages (after P0 stabilizes)

### P1-A: Count and README synchronization digitization

**Scope**: zero out stale README numbers (`README.md:22,43` "37+ actions" → 57; `README.md:34` "50 endpoints" → "57 routes / 67 operations"); add `scripts/verify-actions-count.py` (the count of action `Name`s in source == the total row in `actions.md`, modeled on `verify-api-docs.py`, with a negative probe); cross-check `actions.md` entries against plugin action counts.

**Acceptance**: no stale numbers in README; negative probe EXIT=1; five verify all green.

### P1-B: Brand positioning sentence adjustment (needs user sign-off)

**Recommended default**: change `README.md:5` to `# Vapor — Distributed Steam Automation Control Plane`, keep the second sentence "Inspired by ArchiSteamFarm's Bot/Action model"; keep the `feature-matrix.md` title (the vs ASF comparison is a feature-communication tool, not a brand binding).

> **Signed off and landed (2026-10-08, round 57)**: the user confirmed executing per the recommended default — README.md:5 changed to `# Vapor — Distributed Steam Automation Control Plane`, the first paragraph gains the "Inspired by ArchiSteamFarm's Bot/Action model" attribution sentence; the `feature-matrix.md` title stays. Acceptance met: the README front page aligns with the `architecture.md` positioning; no code changes.

**Acceptance**: the README front page is consistent with the `architecture.md` positioning; no code changes.

### P1-C: Plugin platform patch package (split into three sub-items; only the first two are done)

- **C1 configuration schema**: add an optional `configurationSchema` field to the manifest; validate `configuration` against the schema before plugin initialization; failure = a structured discovery error (with field-level detail). At least one of the six in-tree plugins uses it (by way of example).
- **C2 dependency declaration**: add optional `dependencies` to the manifest (pluginId + apiVersion constraint); build a dependency graph at enable time: missing dependency, circular dependency, or apiVersion mismatch → discovery failure.
- **C3 upgrade/rollback/health/resource policy**: assessed as ecosystem-scale work, **demoted to P3 and not scheduled** (disposition recorded in git history).

**Boundaries**: no process-level sandbox (review §13/§33 agree; the repo non-goal is reiterated); no remote plugin sources or signing system (beyond the convergence goal).

**Acceptance**: for each of C1/C2: positive/negative case tests (missing schema field, bad schema, missing dependency, circular dependency, version mismatch — all with EXIT≠0 semantics), `plugins.md` updated, in-tree official plugin regressions all green.

### P1-D: FakeSteam deterministic test backend

**Scope**: with the BotSession state machine as the boundary (six scenario classes: login / disconnect-reconnect / challenge / timeout / error / restricted), build a deterministically replayable backend that **does not depend on the real Steam network**. (40.7-1 audit 2026-10-07: the `ISteamClientManager`/`ISteamTransport` seam is sufficient to express the six scenarios, so the scope did not need narrowing via a "simulator extension"; the originally cited `SteamCallbackSimulator` was a zero-reference NotImplemented stub and has been deleted; the deterministic backend landed as the `FakeSteamClientManager` scripted transport double in `tests/Vapor.Steam.Core.Tests/FakeSteam/` plus `BotSessionStateMachineTests` with 11 tests across the six scenarios.)

**Boundaries**: week one runs a BotSession/SteamClientManager interface audit first — if the transport-layer abstraction cannot support the deterministic double, adjust to a "simulator extension" and notify the user of the narrowed scope; **do not do** 1000-account/10000-task-scale stress testing (that belongs to the P3 capacity profile).

**Acceptance**: ≥6 state machine scenarios all pass on the FakeSteam backend (deterministic, no flake); included in the full gate double-100 criteria.

## 4. P2 demoted packages (not scheduled; started on signal)

| # | Item | Source | Start signal | Boundaries and acceptance highlights |
|---|---|---|---|---|
| 1 | Webhook delivery records (persistence + consumers event dedup convention) | §25 residual | Real webhook lost-event complaints / more than 3 integrators | No WebhookSubscription multi-subscription model; acceptance: a delivery-record table + at-least-once semantics documented. **Landed (2026-10-10, kickoff-order batch)**: one append-only record row per attempt (`WebhookDeliveryRecord`: notificationId/category/type/jobId/accountName/attempt/outcome/statusCode/error/attemptedAtMs), `SqliteWebhookDeliveryStore` (`Vapor_WEBHOOK_DB_PATH`, default `data/webhook.db`); the webhook sink records delivered/failed per attempt (including HTTP status code and error), and record failures are swallowed so they never block delivery (an audit trail, not a replay queue — undelivered events are lost on restart); `GET /v1/notifications/deliveries` filters by notificationId/outcome with limit/offset paging, returned newest-first |
| 2 | AuthChallenge domain object codification (fields/state machine) | §23 | After the P0-B documented convention stabilizes | Keep the transient semantics unchanged (not persisted); acceptance: fields align with domain-model.md. **Landed (2026-10-10, kickoff-order batch)**: the record converged on the single source `Vapor.Protocol/Events.cs` (the ControlPlane-side mirror record deleted); an `Attempt` ordinal field (the tracker is authoritative: first raise = 1, re-raise = +1, SSE events carry the same value); `status`/`source` deliberately have no fields (`challengeType` already encodes them — `*_required` = pending/agent, `code_provided_*` = answered/operator; adding fields would duplicate state), `expiresAt` deliberately absent (no cross-boundary TTL signal); the semantics stay transient and unpersisted |
| 3 | Event three-tier bucketing (Domain/Operational/Audit) documentation + SSE event-name normalization | §24 | When event-name chaos appears | SSE stays best-effort; acceptance: the convention goes into the docs and SSE event names match them | **Landed (2026-10-11, round 72)**: the documentation half landed with ebbbb81 (a Domain/Operational/Audit three-tier table + per-tier persistence and consumption contracts); the residual half is SSE event-name normalization — the canonical `domain-model.md` "SSE event names" table (20 exact names + the two open families session./auth.) + `scripts/verify-sse-event-names.py` three-way checking (code literals ∪ explicitly registered variable forwarding ⇄ canonical table ⇄ api.md §2, with audit-only names going through an explicit exemption set), hooked into the ci format job; the convention is `<scope>.<snake_case_verb>` (the prefix names the subject being described, not the tier), and the initial wrong example plugin.installed was corrected (plugin install/uninstall writes audit only) |
| 4 | secret version / rotation semantics written up | §22 | KeyRotation adds account-level rotation needs | The current AES-GCM v2 + rotation tooling already covers most of it; acceptance: the docs define the version semantics | **Landed (2026-10-11, round 72)**: the documentation half landed with ebbbb81 (the v1 plaintext → v2 AES-GCM evolution, reads stay compatible with old CBC, new writes are always `gcm:`, rotate re-encrypts the whole file); the residual half is "every rule pinned by tests" — a per-rule test-anchor section after the rules in the architecture.md "Secret version semantics" section (two tests in FileCredentialStoreTests / three in VaporCryptoHelperEncryptionTests / two in CredentialStoreRotatorTests, all five anchors grep-verified) + a maintenance note: `CurrentFormatVersion` is a private const in both FileCredentialStore.cs and CredentialStoreRotator.cs, so a version bump must change both in the same commit; zero code changes |
| 5 | The `vapor backup/restore/verify` CLI trio | §21 | Backup incidents / multi-machine deployment needs appear | The current `production.md:348-366` guidance + sqlite `.backup` works; acceptance: a CLI wrapping the existing semantics. **Landed (2026-10-10, kickoff-order batch)**: `tools/Vapor.Backup` (`backup`/`restore`/`verify`, exit codes 0/1/2) — backup goes through the SQLite online backup API (snapshot-consistent while the control plane keeps writing); restore runs `PRAGMA integrity_check` up front, corrupted backups are refused and an existing target requires `--force`; verify reports every non-"ok" row; production.md gained a CLI section alongside the container-native path |
| 6 | Placement / Capability matcher (region+labels+load/health ordering) | §16+§17 | Cross-region contention / load imbalance is observed | No tunnel-protocol change; capability structuring is defined first in domain-model.md. **Landed (2026-10-11, kickoff-order batch)**: both agent-selection paths, account and task, share one placement order — region match (host-pinned `agent:{id}` directed tasks bypass region selection, but capability is still checked) → capability filter (`SupportsAction`) → **least load** (the account side counts assigned accounts; the task side counts running tasks owned by `claimed_by_agent_id`) → agent-id lexicographic tie-break; task ownership is written after successful enqueue (`SqliteJobStore.MarkTaskClaimedBy`, fenced by attempt — the claim→pick order is kept so fault-injection semantics stay untouched), lease requeue clears ownership so the load signal self-heals, and it lives in the task table so it survives restarts; one snapshot per tick + local accumulation spreads up to 25 dispatches within the same tick; health is implicit (the registry keeps only connected agents); **labels deliberately absent** (no tenant/environment-dimension signal source; the decision is recorded in domain-model §8). `IJobStore` gains `MarkTaskClaimedBy`/`CountRunningTasksByAgent`, and `AgentRegistry.Pick(region, action?, load?)` carries the payload order |
| 7 | Unified Execution ID field convention (job/task/attempt/agent/account/session/action/trace_id) | §18 residual | When debugging across the chain | traceparent already threads through (`TaskSchedulerService.cs:134,147`); acceptance: the convention written up + key sink fields aligned. **Landed (2026-10-10, kickoff-order batch)**: the convention is written up (`architecture.md` §Execution ID convention); the audit sink is aligned — `AuditEntry`/`AuditQuery`/`CreateEntry` gain `task_id`/`attempt`/`agent_id`/`session_id`/`trace_id`, `SqliteAuditStore` gets an idempotent `ALTER TABLE` migration (old rows read back null), `/v1/audit/logs` gains `agentId`/`taskId` filters, the HTTP audit path parses `trace_id` from the W3C `traceparent` header, and the `account.reconciled` audit carries `agent_id` |
| 8 | Operations Console enhancements (Agent/Session health cards, failure reasons, audit view) | §19 residual | User feedback after P0-C ships | Acceptance: the dashboard gains card views with no new backend surface. **Landed (2026-10-10, kickoff-order batch)**: zero new backend surface, everything queries existing GETs — the job list lazily fetches failure reasons for failed jobs via the existing `GET /v1/jobs/{id}` (cached by job id, at most 5 fetches per refresh, rendering a `失败原因:` line); Agent cards gained connection duration (`connectedAt` relative time); the session panel gained a status-distribution badge summary at the top (client-side aggregation); audit rows render the round 65 execution-ID fields (agent/task/attempt/session/trace chips, omitted when absent); the write-verb contract is unchanged (PUT/DELETE forever forbidden, `method:"POST"` exactly once); four panel-anchor tests went into DashboardStaticTests |
| 9 | Capacity-profile performance scenarios (100/1k/10k) | §28 residual | Before scaled deployment happens | The current system-level baseline is usable; acceptance: performance.md gains a scenario table | **Landed (2026-10-11, round 72)**: `CapacityProfileBenchmarks`, three benchmarks of the same shape (job stockpile ListJobs(100)+GetJob+claim, audit QueryAsync, EventBroker burst-delivery floor `min(256, burst)`, each looping 100/1k/10k); the numbers were transcribed into the "Capacity profile" section of performance.md (SQLite reads do not degrade with stockpile: ListJobs p95 ≤21ms across all three scales, audit p50 ≤1ms); assertions set only wide order-of-magnitude ceilings to avoid CI jitter; `run-benchmarks.sh -f` multi-filter merge fixed |
| 10 | Residual fault-injection scenarios (SQLite corruption simulation, full-chain CP restart, cross-attempt partition) | §26 residual | When related incidents call for it | Acceptance: new scenario tests. **Landed (2026-10-10, kickoff-order batch)**: `FaultScenarioTests`, three tests — a corrupted database file fails typed at construction (bytes kept as-is with no schema rebuild, and the failed construction releases the handle so repair-by-replacement can happen immediately); after a lease partition, stale attempt signals cannot corrupt the new attempt (zero-lease requeue → new attempt takes the lead → stale heartbeat rejected + stale settlement throws `NotFoundException`); eight workers take the lead concurrently with each task claimed exactly once; `RestartResilienceE2ETests` full-chain restart — a task-dispatch delay fault creates a deterministic in-flight window → the CP + Agent processes are killed and restarted (same DB, same port) → after restart `/v1/faults` is empty, proving in-memory faults do not survive → the re-dispatched task's attempt≥2 echo output arrives and the audit history survives |

## 5. Explicitly not doing (consistent with the repo's existing non-goals; the review concurs)

- Distributed Control Plane (PostgreSQL/Redis/NATS/leader election) — a non-goal at `roadmap.md:249-258`; review §20 explicitly says "do not introduce prematurely".
- gRPC tunnel migration, external message-queue backbone, mTLS mesh — same as above.
- Multi-tenancy / RBAC / OIDC — single-operator self-hosted threat model (`architecture.md` Non-goals).
- Process-level Plugin sandbox — review §13/§33 recommend deferring; the docs keep the "load isolation" wording for ALC.
- "Action count" is no longer a growth metric — a new action must answer "do the scheduling/permission/idempotency semantics hold once it is Capability-ized", not merely fill in another Steam API surface (review §10/§34).

## 6. Execution cadence and gates

- Execute per the repo's existing round discipline (from round 50 on), one small package per round; order P0-A→P0-B→P0-C; P1 starts after P0 stabilizes.
- **Per-round gate**: `./scripts/run-tests.sh` passes in full; `./scripts/collect-coverage-serial.sh` double-100 coverage (line + branch, criteria per tests/TESTING.md); the five `scripts/verify-*.py` (api-docs / api-auth / testing-docs / coverage-inventory / dependency-versions) ALL GREEN; new verify scripts likewise require a negative probe (manual breakage → EXIT=1); `dotnet format` clean.
- **Commit discipline**: surgical commit surface (only the current round's files; parallel sessions' `todo.md`/`coverage.log` untouched); commit messages follow the `type: round N one-liner` convention; after each round lands, update `roadmap.md` (landed records) and related docs in sync.
- **Testing discipline**: for any behavior change, add/adjust tests before implementing (the repo's consistent TDD line, see TESTING.md); the E2E surface follows the existing "real-process coverage" leaning.

## 7. Acceptance criteria summary (overall gate)

1. All three P0 packages plus all four P1 packages land, each meeting its acceptance items (§2/§3).
2. The recheck report's recommendation list closes one-to-one: every gap item among the 30 confirmed items has a disposition, and the 3 outdated items are explicitly kept out of the plan.
3. Full-gate steady state: as in the round 49 baseline (3498 tests / double-100 coverage / five verify / format), no undisclosed regressions in any round.
4. README/actions.md/api.md/roadmap.md/feature-matrix.md fully consistent with the code (mechanical guards cover the count categories).
5. `docs/domain-model.md` is re-checked once every two months (attached to a maintenance round).

## 8. Start conditions

This plan starts executing after the user signs off; the suggested sign-off granularity is "start P0 first, confirm P1 items one by one". The first execution round after start = P0-A.
