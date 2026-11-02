English | 简体中文

# Code deep review — 2026-11-01

Static analysis of the checked-out `main` (2026-11-01/02), run under the requesting-code-review
flow: security static scan, hidden-bug hunt, and quality gate. Each finding carries `file:line`
evidence. P0/P1 were fixed on the spot (one issue one commit, suite green); P2/P3 are logged in
[`todo.md`](https://github.com/cuihairu/vapor/blob/main/todo.md), section「代码深审 2026-11-01」.

## Summary

| Severity | Found | Fixed | Remaining |
|---|---|---|---|
| P0 | 0 | 0 | 0 |
| P1 | 6 | 6 | 0 |
| P2 | 6 | 0 | 6 |
| P3 | 13 | 0 | 13 |

Quality gate: `dotnet format` whitespace was failing and is fixed (`28cc3c8`); the coverage gate
and one E2E test were already red before this review and remain pre-existing findings (below).

### Fix commits

| Commit | Issue |
|---|---|
| `f35ec25` | Proxy switch awaited the already-completed connect TCS |
| `efaa760` | Playing set not cleared on disconnect |
| `94085ba` | Login callback misrouted through a single account slot |
| `b7cd244` | Session events stolen between concurrent subscribers |
| `5cd501a` | Runtime lists mutated in place during REST reads |
| `6bd3296` | Faulting SSE stream left sibling subscriptions alive |

## P1 — fixed

### 1. Proxy switch raced the dying socket

`src/Vapor.Steam.Core/Steam/SteamClientManager.cs` (SetProxy). The switch settled on
`_connectedTcs.Task`, which `OnConnected` had already completed — so the reconnect proceeded
while the old CM socket was still tearing down and could land on the old exit or wedge.
Fix (`f35ec25`): a `_disconnectTcs` settled by `OnDisconnected` now gates the switch; the
`Disconnect()` call moved outside `_connectLock` so a synchronous callback cannot deadlock.

### 2. Playing set survived a reconnect

`src/Vapor.Steam.Core/Steam/SteamClientManager.cs` (OnDisconnected). `_playingGames` is
connection-scoped but was never cleared, so a post-reconnect `PlayGames(desired)` was skipped by
the `SetEquals` idempotence check and the desired idle/farm state silently never reached Steam.
Fix (`efaa760`): clear the set under `_playingGamesLock` when the CM connection drops.

### 3. Login callback routed to the wrong account

`src/Vapor.Steam.Core/Steam/SteamClientManager.cs` (LoginAsync / OnLoggedOn).
`LoggedOnCallback` carries no account identity, so it was routed through the single
`_activeLoginAccountName` slot. Two overlapping `LoginAsync` calls overwrote that slot and
completed the wrong account's TCS with the other's callback. Fix (`94085ba`): a
`SemaphoreSlim` gate spans send-to-callback so the slot has exactly one owner while a logon
response can arrive.

### 4. Session events stolen between subscribers

`src/Vapor.Steam.Core/SessionManager.cs` (SubscribeAllEvents). A direct multi-reader
`ReadAllAsync` on the shared ingress channel handed each event to exactly one consumer, so
concurrent subscribers (plugin event dispatcher, 2FA auto-responder, monitoring) stole events
from each other — the 2FA code could land in a consumer that drops it. Fix (`b7cd244`): one
broadcaster fans every event out to a per-subscriber channel; a late subscriber sees the ingress
already finished and ends immediately instead of hanging.

### 5. Runtime lists mutated during REST reads

`src/Vapor.ControlPlane/DesiredStateReconciler.cs`. The reconcile thread mutates `AccountRuntime`
queues in place while REST handlers enumerate the same list instances, throwing
`InvalidOperationException` in the serializer. Fix (`5cd501a`): views hand out copies and the
reconcile-side mutations are copy-on-write, so no reader shares the list being changed.

### 6. Faulting SSE stream left sibling subscriptions alive

`src/Vapor.Agent/Program.cs` (PollAuthChallengesAsync). A faulting auth-challenge stream left the
sibling SSE subscriptions from the same iteration alive, so the next subscribe stacked another
reader on channels whose owner had stopped draining. Fix (`6bd3296`): a linked token per
iteration cancels every sibling subscription before the loop re-subscribes.

## P2 — deferred (todo)

1. `docker-compose.yml:18,19,43` — un-gated dev-token API-key defaults
   (`${VAPOR_ADMIN_API_KEY:-admin-dev-token}`); production with the env var unset starts on a
   known token. Fix: drop the defaults and fail fast when unset.
2. `src/Vapor.ControlPlane/SqliteJobStore.cs:985,1004` (SetTaskResult) — the task-row update and
   `RecomputeJob` are separate non-atomic writes; a crash between them leaves task=Finished with
   the job still Running.
3. `src/Vapor.Steam.Core/SessionManager.cs:178-181` (RemoveSessionAsync) — a throwing
   `DisconnectAsync` skips `Dispose()`, leaking the session. Fix: try/finally.
4. `src/Vapor.ControlPlane/Program.cs:3190` + `src/Vapor.ControlPlane/AgentRegistry.cs:144` —
   cancellation is delivered only to `ListConnected()` agents; the DB is already marked
   Canceled, so an offline agent reconnects to a task that will run to completion and have its
   result dropped ("task not running").
5. `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:384` — `DisconnectAsync` sleeps a fixed
   `Task.Delay(100)` to wait out teardown; a magic-number race margin. Fix: wait on the
   disconnect signal (the `_disconnectTcs` mechanism).
6. Residual of `94085ba` — a cancelled login's late `LoggedOnCallback` can still be routed to the
   next login's TCS (cancel window + concurrent login + a slow Steam response). Fix: route by
   SteamID rather than a slot.

## Quality gate

- **`dotnet format`** — `--verify-no-changes` was red (157 whitespace diagnostics in
  `src/Vapor.Agent/Program.cs`); fixed by `28cc3c8` and confirmed 0 diagnostics.
- **Coverage gate** — `scripts/coverage-summary.py --min 100 --min-branch 100` demands 100% line
  and branch coverage. Was pre-existing red (deterministic gaps, not flake: the same SHA passed
  twice only via partial-report omission). Fixed in this recheck; see the recheck section below.
- **Flaky E2E** — `tests/Vapor.E2E.Tests/AccountOrchestrationE2ETests.cs:19,129-156`
  (`DeclaredOnlineAccount_IsAssignedAndRebalancesAfterAgentLoss`) polls with a fixed 60 s
  deadline; under loaded CI runners the reconcile/agent-loss cycle can exceed it and the test
  times out. Test-infrastructure ceiling, not a product defect. Fix: raise the ceiling or make it
  configurable.

## Test baseline

| Suite | Result |
|---|---|
| Vapor.Steam.Core.Tests | 1286 passed |
| Vapor.ControlPlane.Tests | 1377 passed |
| Vapor.Agent.Tests | 217 passed |

## P3 — informational

Stylistic/robustness only; tracked in todo. The compiler runs with `TreatWarningsAsErrors` and
`AnalysisLevel=latest-all`, so unused/dead-code categories came back empty — these are the three
families analyzers do not enforce.

**Uncommented empty `catch { }`**

1. `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:380` — `DisconnectAsync` swallows
   `Disconnect()` errors with no comment.
2. `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:1690` — mirror bare catch in `Dispose`.
3. `src/Vapor.Agent/Program.cs:660` — bare catch swallows websocket `receiver` drain errors.
4. `src/Vapor.Agent/Program.cs:462` — bare catch swallows `currentTaskCts!.Cancel()` errors.

**Magic numbers in library code**

5. `src/Vapor.Steam.Core/BotSession.cs:256` — `Task.Delay(100)` callback-poll interval unnamed.
6. `src/Vapor.Steam.Core/BotSession.cs:604` — `Task.Delay(100)` post-disconnect settle unnamed.
7. `src/Vapor.Steam.Core/Caching/RedisVaporCache.cs:105` — `Thread.Sleep(200 * attempt)` backoff
   blocks inside a library retry loop.

**Library awaits without `ConfigureAwait(false)`**

8. `src/Vapor.Steam.Core/Web/SteamWebHandler.cs:174` (`GetAsync`)
9. `src/Vapor.Steam.Core/Web/SteamWebHandler.cs:191` (`PostAsync`)
10. `src/Vapor.Steam.Core/Actions/DataActions.cs:440` (`SearchGamesAsync`)
11. `src/Vapor.Plugins.Csgo/CsgoPlugin.cs:320` (`ReadBackpackAsync`)
12. `src/Vapor.Steam.Core/SessionManager.cs:477` (`_eventCallback.Invoke`)

**Duplicated literal**

13. `src/Vapor.ControlPlane/Program.cs:878` — the `"pending"` status literal repeats 19 times in
    this file; should be a shared constant.

## Follow-up recheck (2026-11-02)

Added after publication by a second pass; logged in todo. Two are docs-vs-source contradictions,
one is dead code, two are P2s (one a gap in this review's own `f35ec25`).

1. **P2** `README.md:29` / `README.zh.md:29` — the front page claims Steam Guard codes and
   credentials "never leave the agent, only a boolean crosses the line", which the implementation
   contradicts: manual auth codes are published to the control plane and streamed back to the
   agent in plaintext over SSE (`src/Vapor.ControlPlane/Program.cs:3507` publish, `:3419-3420`
   admin-only code stripping), and task payload credentials are persisted in the control plane
   (secret-payload purge endpoint). `docs/architecture.md:109` already describes the real path.
   Fix: correct the README claim.
2. **P2** `docs/architecture.md:116` — describes challenge-stream replay as replaying a
   "still-acknowledged code"; the outbox replays only unacknowledged (staged) codes and
   `Acknowledge` removes them (`src/Vapor.ControlPlane/AuthChallengeOutbox.cs:77-91`,
   `Program.cs:3404`). `docs/api.md:85` ("still-unconfirmed") is the correct wording. Fix: correct
   the architecture doc.
3. **P3** `src/Vapor.Agent/AgentTaskBuffer.cs:40` — `CreateChannel` has no production caller
   (`RunOnce` moved to `AgentTaskDispatcher`; only tests reference it): dead code.
4. **P2** `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:340-365` (SetProxy) — concurrent
   switches overwrite `_disconnectTcs`, so the earlier caller's settle TCS never settles and it
   falls back to the 10 s `WaitAsync` timeout. Coalesce with `??=` so concurrent switches share
   one disconnect wait. (Gap in `f35ec25`.)
5. **P2** `SteamClientManager.cs:397-438,1611-1622` — re-confirmation of the `94085ba` residual:
   a cancelled login's late `LoggedOnCallback` can still route to the next login's TCS via the
   shared `_activeLoginAccountName` slot.

### Coverage gate (pre-existing red, fixed this recheck)

The gate had failed on the reviewed `main` (run 41404115679). The gaps were deterministic — the
same SHA passed twice only by partial-report omission (a report omitting a file's lines removes
them from the denominator). All gaps reproduced locally and are now closed; the gate is green.

| Location | Gap | Fix |
|---|---|---|
| `SqliteJobStore.cs:243` | `StripSecretPayload` non-`Dictionary` branch untested | made `internal`, new test drives it via a `ReadOnlyDictionary` |
| `SqliteJobStore.cs:1110` | `:memory:` early-return in `EnsureBackupPathIsNotLiveDatabase` | new test: in-memory store + backup path purges without throwing |
| `SqliteJobStore.cs:1119` | OS-conditional comparison (ternary left the Windows arm uncovered) | rewritten as a positive `if (!IsWindows())` block per the repo convention (same as `FileCredentialStore.cs:564`) |
| `SqliteJobStore.cs:1164` | malformed-JSON payload row left untouched | new test: `{not valid json}` payload survives purge |
| `SqliteJobStore.cs:1123` | same-path throw arm | new test: `backupPath == dbPath` throws |
| `TaskLeaseRenewer.cs:29` | PeriodicTimer loop's try-closing-brace unreachable | `[ExcludeFromCodeCoverage]` + registration in the TESTING.md exclusion table |
| `AgentTaskDispatcher.cs:41` | `Capacity` getter untested | new test reads `.Capacity` |
| `AgentTaskDispatcher.cs:83` | `Dispose` completes the writer | new test asserts `Reader.Completion` completes |
| `QueuedTaskCanceller.cs:31` | `Clear` untested | new test removes all pending cancels |
| `AuthChallengeOutboxCleaner.cs:18` | null-interval `??` fallback | new test constructs with no interval |
| `TaskLeaseRenewer.cs:22` | null-interval default branch | new test constructs with no interval |

Result: `coverage-summary.py --min 100 --min-branch 100` → 100.0% lines (22524/22524) and 100.0%
branches (8658/8658) across all 16 assemblies; `dotnet format --verify-no-changes` clean; all 7
`verify-*` scripts green; full serial suite green (ControlPlane 1386, Agent 221, Steam.Core 1289).
