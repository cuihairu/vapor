# Plugin process boundary: quantification notes

Conclusion first: the six official plugins cost **≈ 2.3 MB of resident memory in
total** (of which the managed heap is only ≈ 45 KB) — memory is not a reason to
move plugins into separate processes. The real exposure of the current
in-process host is the **runaway-compute fault domain**: a plugin that ignores
its cancellation token turns its session into a zombie (every later action on
that session times out) while the agent itself stays online and keeps its
lease. This page records the mechanism inventory, the fault-domain matrix, and
the measurement method so the follow-up decision (separate process vs. quota +
hard timeout) can be made on data. It answers research §6 candidate 5's
"quantify first, then decide" step; no code was changed.

## 1. What isolation exists today

| Layer | Mechanism | What it actually bounds |
|---|---|---|
| Loading | Per-plugin collectible `PluginLoadContext` (`Vapor.Plugins.Core`) | Version coexistence and hot unload; contract assemblies (`Vapor.Plugins.Core`, `Vapor.Steam.Core`, `Vapor.Protocol`) are shared with the host by type identity |
| Permissions | `plugin.json` `permissions` gate the registration surface (actions/events/web) | What a plugin can *register*, not what it can *do* at runtime |
| Per-action timeout | `BotSession` applies `ActionMetadata.TimeoutSeconds` via `CancelAfter` onto the token handed to `ExecuteAsync` | Well-behaved actions only — cancellation is cooperative; nothing can abort a loop that never observes the token |
| Task watchdog | `TaskTimeoutPolicy` (default 900 s, `AGENT_TASK_TIMEOUT_SECONDS`) cancels the whole task | Unblocks the agent's serial task loop *as a caller* (`ExecuteActionAsync` waits on a `TaskCompletionSource` with a token, so the loop moves on) — but the plugin task itself is still running inside the session |
| Command-loop guards | `BotSession`'s command loop catches every exception per command and reports it as a failed result | Plugin *exceptions* kill one task, not the session or the agent |

The key asymmetry: **exception paths are contained, runaway paths are not.**
.NET has no thread abort; both timeout layers are cooperative by construction
(`TaskTimeoutPolicy`'s own doc comment says a hang "somewhere its token is
never observed" is exactly the case the watchdog cannot recover from).

## 2. Fault-domain matrix (from code, verified against the mechanisms above)

| Failure in a plugin | Blast radius | What the operator sees | Recovery |
|---|---|---|---|
| Action throws | Single task | Task reported failed, session and agent keep serving | None needed |
| Action honors the token and times out | Single task | `"action timeout"` result after `TimeoutSeconds` | None needed |
| Action busy-loops ignoring the token | **Session**: `BotSession`'s `_actionLock` is held forever, so every later action on that session queues and times out; the stuck `HandleExecuteAction` task and its `CancelAfter` CTS leak. **Agent survives**: watchdog reports the task at 900 s, heartbeat/WS are separate loops | Agent stays "online", lease keeps renewing; every action for that account fails with `"action timeout"` — a zombie session that looks healthy | Reconnect the session (agent restart or session teardown) |
| CPU runaway (looping or compute-heavy) | Whole agent process: thread-pool contention hits other sessions' command loops and heartbeat latency | Degraded latency, possible lease misses under load | Same as above, plus process restart if it persists |
| Memory runaway | Whole agent process: no per-plugin quota exists; OOM kills the agent and every session on it | Agent disconnects from the CP | CP reconnects; agent restarts (with session restore) |
| Plugin unload leaks | Contained today: `PluginManager.UnloadAsync` verifies ALC collection (covered by tests) | — | — |

## 3. Memory measurement

Method: a throwaway console probe (not part of `Vapor.sln`, so it does not
enter the coverage denominator) that reuses `PluginManager`/`PluginDiscovery`
exactly as the agent does. It first loads and unloads all six plugins once
(warm-up: pays JIT and file-cache costs), takes a baseline, loads the six
plugins from a directory layout identical to the agent's `plugins/` root, then
reads `/proc/self/statm` (page-level RSS), `GC.GetTotalMemory(true)` and
`Process.PrivateMemorySize64` again after GC.

Environment: Linux 6.x kernel, .NET 10, Release, one run shown (repeated runs
agree to within ~0.1 MB). All six plugins load with 0 failures and register 34
actions: caseopening 1, game-access 16, game-data 4, market-watch 3,
mobile-authenticator 9, monitoring 1.

| Metric | Delta while loading all six plugins |
|---|---|
| Resident set (statm pages × 4 KiB) | **+2,416,640 B ≈ 2.3 MB** |
| Managed heap (`GC.GetTotalMemory(true)`) | **+45,336 B ≈ 45 KB** |
| Private bytes | 0 (assembly images are file-backed and shared; heap growth lands in already-touched GC segments) |
| Plugin self DLLs on disk | ≈ 0.4 MB total (largest: GameAccess 113 KB; the per-directory contract-DLL copies share pages with the host and are never double-loaded) |

Probe (trimmed to the measuring part):

```csharp
var manager = new PluginManager(
    new DefaultPluginHostServices(loggerFactory, emptyServices), loggerFactory);
var report = await manager.LoadAllAsync("/tmp/plugins-dist");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
await Task.Delay(300);
long rssAfter = File.ReadAllText("/proc/self/statm").Split(' ')
    .Select(long.Parse).ToArray()[1] * 4096L;   // resident pages
Console.WriteLine(rssAfter - rssBefore);
```

Reading: the six official plugins are **an order of magnitude below anything
that would justify process isolation on memory grounds** — 2.3 MB against a
~100 MB agent process. GOG Galaxy-style per-plugin processes would multiply the
agent's footprint by roughly the process baseline each, to defend against a
failure mode (memory) that no official plugin exercises.

## 4. Where this leaves the decision

- **Process isolation** (GOG Galaxy model): not justified by memory (§3); its
  real benefit — CPU/loop containment — requires every action to cross a
  process/serialization boundary, a structural rewrite that conflicts with the
  plan's stated non-goal (no process sandbox, see
  `docs/vnext-convergence-plan.md` §5). Rejected for now on the quantified
  basis; revisit only if third-party plugins with real compute/footprint
  appear.
- **Quota + hard timeout** (one notch down): addresses the actual fault domain
  in §2. The gap is not memory — it is *runaway compute inside a session*.
  Concrete shapes if pursued: a hard per-action wall-clock bound that marks the
  session as poisoned and reclaims it (new session instead of a zombie), and
  CPU/wall-clock accounting surfaced in the monitoring plugin. Neither is
  started here; that is a decision to record, not this round's change.
- **Runtime resource declarations** (research §6 candidate 3) remains the
  cheap declarative complement: register what a plugin *may* touch, surface it
  in load reports and audit, no enforcement.

Decision recorded in `todo.md` 40.10: quantification done; the follow-up
(poisoned-session reclamation / quotas) stays pending until there is a
third-party plugin ecosystem that makes the fault domain real.
