# Vapor Plugin Development Guide

Vapor's agent ships with a full plugin system: isolated loading, SemVer-gated API
compatibility, a trust/permission model and a small, explicit API surface. This guide
walks through building, declaring, configuring and debugging a plugin.

Everything described here is exercised by six official plugins and the test suites —
`Vapor.Plugins.TestPlugin` (infrastructure tests), `Vapor.Plugins.MarketWatch` (the
richest example), and the load/unload tests in `Vapor.Plugins.Core.Tests`.

## What a plugin can do

A plugin is a .NET class library loaded by the agent at startup from a plugin directory.
It can contribute any combination of:

| Capability | Interface | What the host does with it |
|------------|-----------|----------------------------|
| Actions | `IActionPlugin` | Registers the contributed `IAction`s in the agent's action registry; they become callable through jobs (`run_action`) |
| Console commands | `ICommandPlugin` | Adds interactive commands to host front-ends |
| Web routes | `IWebApiPlugin` | Serves host-independent `PluginWebRoute`s (e.g. the monitoring endpoint) |
| Session events | `IEventPlugin` | Receives every session event (state changes, auth/2FA prompts, errors) through the `PluginEventDispatcher` |

## Quick start

A minimal plugin directory looks like this:

```
plugins/
└── my-plugin/
    ├── plugin.json
    └── MyPlugin.dll
```

`plugin.json`:

```json
{
  "id": "acme.my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "apiVersion": "1.0",
  "description": "Does something useful.",
  "trust": "community",
  "permissions": ["actions"],
  "entryAssembly": "MyPlugin.dll"
}
```

`MyPlugin.cs`:

```csharp
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

public sealed class MyPlugin : IPlugin, IActionPlugin
{
    public PluginInfo Info { get; } = new(
        Id: "acme.my-plugin",
        Name: "My Plugin",
        Version: new Version(1, 0, 0),
        ApiVersion: PluginApi.Current);

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public IEnumerable<IAction> GetActions()
    {
        yield return new HelloAction();
    }
}
```

If the assembly contains exactly one public `IPlugin` implementation you can omit
`entryType` from the manifest; otherwise declare it explicitly:

```json
{ "entryType": "Acme.MyPlugin.MyPlugin" }
```

Point the agent at the directory with `VAPOR_PLUGINS_DIR` (default `./plugins`) and the
plugin loads at startup.

## The manifest (plugin.json)

| Field | Required | Notes |
|-------|----------|-------|
| `id` | yes | Stable identity used for dedup, logging and registry keys |
| `name` | yes | Display name |
| `version` | yes | SemVer version of the plugin itself |
| `apiVersion` | yes | SemVer version of the plugin API the plugin was built against (use `PluginApi.Current`) |
| `description` | no | Shown in logs and load reports |
| `entryAssembly` | yes | DLL file name, relative to the plugin directory |
| `entryType` | no | Full type name of the `IPlugin` implementation; omit when the assembly has exactly one public implementation |
| `trust` | no | `unknown` (default), `community` or `official` — see [Trust and permissions](#trust-and-permissions) |
| `permissions` | no | Array of `actions` / `commands` / `web` / `events` — see below |
| `configuration` | no | Free-form string key/values handed to the plugin at initialization |

Manifests are parsed leniently (comments and trailing commas allowed) but validated
strictly: an invalid `trust` value or an unknown permission name fails discovery for
that plugin and is reported as a load failure, never a silent skip.

## Lifecycle

1. **Discovery** — the host scans `VAPOR_PLUGINS_DIR`, one subdirectory per plugin, and
   parses each `plugin.json`. Invalid manifests are reported and skipped.
2. **Compatibility check** — `PluginApi.IsCompatible` verifies the major API version
   matches the host and the plugin's minor version does not exceed the host's (a plugin
   built for API 1.2 runs on a 1.4 host but not the reverse). Prerelease/build metadata
   is ignored.
3. **Trust gate** — the host's `PluginManagerOptions.MinimumTrust` is enforced *before
   any plugin code runs*.
4. **Permission evaluation** — capabilities implemented without a declaration are
   stripped (or rejected in strict policy) *before* `InitializeAsync`.
5. **Isolated load** — the entry assembly loads into a collectible
   `AssemblyLoadContext`; the plugin type is instantiated and `InitializeAsync` runs.
6. **Contribution** — the host raises `PluginLoaded`; the agent registers the granted
   actions and hooks granted event subscribers.
7. **Shutdown/unload** — `PluginUnloading` fires first (the host removes contributions),
   then `ShutdownAsync` runs, then the load context is unloaded and collected.

Initialization and shutdown failures are isolated: one broken plugin never prevents
others from loading.

## Trust and permissions

Plugins declare *what they are* and *what they need*; the host decides what they get.

**Trust levels** (manifest `trust` field):

- `unknown` — no declaration (default)
- `community` — reviewed third-party plugin
- `official` — shipped and maintained with the host itself

**Permissions** (manifest `permissions` array):

| Permission | Grants |
|------------|--------|
| `actions` | `IActionPlugin.GetActions()` results are registered |
| `commands` | `ICommandPlugin.GetCommands()` results are registered |
| `web` | `IWebApiPlugin.GetRoutes()` results are served |
| `events` | The plugin is subscribed to session events |

The enforcement model is **minimal trust by default**:

- A capability interface implemented without the matching permission declaration is
  **stripped** — the actions/routes/commands are never registered and the plugin does
  not receive events — and a warning is logged.
- Hosts can harden this with `PluginManagerOptions`:

```csharp
var manager = new PluginManager(hostServices, loggerFactory, new PluginManagerOptions
{
    MinimumTrust = PluginTrust.Community,       // refuse unknown-trust plugins
    RequirePermissionsDeclared = true           // undeclared capability = load failure
});
```

`LoadedPlugin.GrantedPermissions` exposes the intersection the plugin actually received
(useful for diagnostics and for hosts that display plugin capabilities).

Always declare what you implement — the official plugins all do.

## Capabilities

### Actions (`IActionPlugin`)

Actions are the workhorses: named operations the agent executes on a bot session
(through jobs) or standalone. Return `ActionResult` with output fields; errors go in
`Error` with `Success = false`.

```csharp
public sealed class HelloAction : IAction
{
    public string Name => "hello";

    public ActionMetadata Metadata { get; } = new(
        Name: "hello",
        Description: "Greets the payload subject",
        RequiresLogin: false,        // set true only if you need a live session
        TimeoutSeconds: 10);

    public Task<ActionResult> ExecuteAsync(
        BotSession session,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var who = PayloadReader.GetString(payload, "who") ?? "world";
        return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
        {
            ["greeting"] = $"hello {who}"
        }));
    }
}
```

Read payload values through `PayloadReader` (`GetString` / `GetInt32` / `GetBool`) — it
normalizes JSON-sourced values so `5`, `"5"` and `5.0` all work.

`RequiresLogin: false` actions still receive a `session` argument; treat it as nullable
and never assume an authenticated Steam client unless you require login.

### Commands (`ICommandPlugin`)

Interactive commands for host front-ends:

```csharp
public IEnumerable<IPluginCommand> GetCommands()
{
    yield return new MyPingCommand();
}

public sealed class MyPingCommand : IPluginCommand
{
    public string Name => "my-ping";

    public string Description => "Responds with pong";

    public Task<PluginCommandResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => Task.FromResult(PluginCommandResult.Ok("pong"));
}
```

### Web routes (`IWebApiPlugin`)

Host-independent HTTP handlers — useful for dashboards or metrics endpoints that must
not depend on the control plane:

```csharp
public IEnumerable<PluginWebRoute> GetRoutes()
{
    yield return new PluginWebRoute(
        "GET", "/my-plugin/status",
        (_, _) => Task.FromResult(PluginWebResponse.Json("{\"ok\":true}")));
}
```

### Session events (`IEventPlugin`)

Subscribe by implementing the interface *and* declaring the `events` permission. The
host's `PluginEventDispatcher` fans every `SessionEvent` out to all subscribers;
a throwing handler is isolated (logged, never propagated).

```csharp
public sealed class SessionLogger : IEventPlugin
{
    // ... IPlugin members ...

    public Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{sessionEvent.Timestamp:O} {sessionEvent.Type} {sessionEvent.AccountName}");
        return Task.CompletedTask;
    }
}
```

Events include `StateChanged` (with `NewState`), `AuthCodeNeeded`,
`TwoFactorCodeNeeded`, `Connected`, `Disconnected` and `Error` (with `Message`).
Handlers should be fast and non-blocking; long work belongs on your own task.

## Configuration

The manifest's `configuration` section is a string key/value dictionary handed to
`IPluginContext.Configuration`. Read it with the typed extensions from
`Vapor.Plugins.Core`; every reader accepts an environment variable that overrides the
configured value (precedence: **environment > configuration > fallback**), so operators
can adjust behaviour without editing plugin.json:

```csharp
var config = context.Configuration;
var host = config.GetString("metrics.host", "127.0.0.1", "VAPOR_METRICS_HOST");
var port = config.GetInt32("metrics.port", 9700, "VAPOR_METRICS_PORT", min: 0, max: 65535);
var verbose = config.GetBool("metrics.verbose", false, "VAPOR_METRICS_VERBOSE");
var threshold = config.GetDecimal("alert.threshold", 10m, "VAPOR_ALERT_THRESHOLD", min: 0.01m);
```

Unparsable or out-of-range values fall back silently — validate anything critical at
startup and log what you resolved.

## Host services

`IPluginContext.Host` exposes:

- `LoggerFactory` — create loggers under the host's logging pipeline. Always log
  through these loggers rather than `Console`.
- `Services` — `IServiceProvider` for optional host services (`IVaporCache`,
  `ISessionManager`, ...). Treat everything as optional (`GetService`, check null):
  hosts may not register all services, and plugins must degrade gracefully.

## Isolation and unloading rules

Each plugin loads into its own collectible `AssemblyLoadContext`. In practice:

- **Shared contracts are fine**: types from `Vapor.Plugins.Core` and
  `Vapor.Steam.Core` resolve to the host's copies, so `IAction` instances you hand out
  are the same types the host knows.
- **Your private dependencies are yours**: ship your own NuGet DLLs in the plugin
  directory; they load into your context and unload with it.
- **Clean up on shutdown**: cancel your background loops and wait for them in
  `ShutdownAsync` (see `MarketWatchPlugin`), remove observers you registered on host
  services, dispose owned `HttpClient`/handlers. The host removes your registry
  contributions before calling `ShutdownAsync`.
- **Don't leak static state**: statics survive unload and prevent collection. Prefer
  instance fields; use `LoadedPlugin.UnloadTracker` in tests to verify collection.

## Debugging tips

- Startup logs every plugin load with versions, granted permissions and contribution
  counts; failures are logged with reasons (incompatible API, trust gate, missing
  permission in strict mode, initialization exception).
- `PluginManager.LoadAllAsync` returns a `PluginLoadReport` with `Loaded` and
  `Failures` — assert on it in tests, or surface it in host tooling.
- Load failures are isolated: check the report instead of assuming the whole directory
  failed.
- Write tests the way the repo does: `Vapor.Plugins.Core.Tests` stages plugin
  directories on disk (`PluginStaging`) and loads real assemblies through
  `PluginManager`; `Vapor.Plugins.MarketWatch.Tests/PluginHostLoadTests.cs` shows a
  full host load of a real plugin.
- A plugin that implements a capability but forgets to declare the permission will
  load fine but silently lose that capability (minimal trust default) — look for the
  `capability stripped` warning first when something "doesn't show up".

## Official plugins

| Plugin | Manifest id | Demonstrates |
|--------|-------------|--------------|
| Mobile Authenticator | `vapor.mobile-authenticator` | Actions only; TOTP/confirmation logic kept out of the host |
| Monitoring | `vapor.monitoring` | Actions + web routes; self-hosted Prometheus endpoint, background metrics pump |
| Market Watch | `vapor.market-watch` | Actions + background polling + configuration + webhook alerts; full trust/permission declarations |
| Case Opening | `vapor.caseopening` | Actions + web routes + configuration + result recording/archive; the dry-run CS:GO case simulator (see its section above) |
| Game Data | `vapor.game-data` | Actions only; read-only Steam Web API digests (Dota 2 matches/heroes/items, TF2/CS2 schema) with agent-side key handling (see its section below) |
| Game Access | `vapor.game-access` | Actions only; the fourteen game-access actions (farming, licenses & keys, achievements, inventory, loot, points shop) split out of the host with unchanged names/payloads — the wire-compatible extraction precedent (see its section below) |

Per-title plugin coverage — what exists for CS:GO/CS2, Dota 2 and TF2, what is
deliberately a non-goal, and what a future per-game plugin looks like — is
tracked separately in [Valve game plugins](game-plugins.md).

## Packaging checklist

1. Class library targeting the same .NET version as the host, `Vapor.Plugins.Core`
   (and `Vapor.Steam.Core` if you use actions) as `ProjectReference` or package
   references — contract assemblies are shared with the host, do **not** copy them into
   your plugin directory.
2. Emit `plugin.json` next to the entry DLL (`CopyToOutputDirectory`).
3. Declare `trust` and `permissions` honestly; omit nothing you implement.
4. Ship only your own binaries; the host resolves shared contract assemblies itself.
5. Drop the directory into `VAPOR_PLUGINS_DIR` and check the startup log for the
   `granted [...]` line.

## Runtime installation and the PluginStore

Beyond the startup directory scan, plugins can be installed while the agent is
running. The transport is deliberately boring: a zip package downloaded by the
agent itself, verified, unpacked to a staging directory and hot-loaded — the
ControlPlane never brokers the binary.

### Package format

A package is a plain zip whose **root contains `plugin.json`** plus the entry
assembly and any private dependency DLLs (the same layout as a plugin directory).
Every install request must carry the package's SHA-256 hex digest; a mismatch
fails before anything is written under the plugins root.

```
my-plugin.zip
├── plugin.json          # required at the zip root
├── MyPlugin.dll
└── Deps/*.dll
```

### Agent-side job actions

Three host actions (no bot session required, targeted with the `agent:{id}`
task-target prefix) cover the lifecycle:

| Action | Payload | Behaviour |
|--------|---------|-----------|
| `plugin_install` | `url`, `sha256`, optional `pluginId`/`version` | download → checksum → staging unpack → manifest validation → hot-load; reinstalling an installed id replaces it (`replaced: true`) |
| `plugin_uninstall` | `pluginId` | unload + retire the directory (renamed aside then deleted); uninstalling an unknown id is idempotent success (`removed: false`) |
| `plugin_list` | — | report the current inventory |

Every action's output carries the agent's **full installed list** under
`plugins`, so the ControlPlane mirror stays current even for failed installs.
The install pipeline validates before touching the real plugins root: URL
scheme (`http`/`https`/`file`), digest hex, zip-slip entries, manifest-at-root,
and — when `pluginId`/`version` were requested — that the manifest matches
them. A failed install leaves no trace in the plugin directory.

### ControlPlane PluginStore

The ControlPlane adds a plugin **index source** (`Vapor_PLUGIN_INDEX_URL`, a
JSON document of the shape below) and REST endpoints that fan out install
jobs to the named agents:

```json
{ "plugins": [ { "id": "vapor.monitoring", "name": "Monitoring",
  "version": "1.0.0", "apiVersion": "1.0", "description": "...",
  "url": "https://.../vapor.monitoring.zip", "sha256": "<64 hex>",
  "trust": "official", "permissions": ["actions"] } ] }
```

- `GET /v1/plugins/catalog` — the fetched index (60s cache, `error` surfaced inline)
- `GET /v1/plugins/installed` — the last-reported inventory per agent
- `POST /v1/plugins/install` — by `pluginId` (resolved from the catalog) or direct `url`+`sha256`; one targeted job per agent
- `POST /v1/plugins/uninstall/{pluginId}` — uninstall from the named agents
- `POST /v1/plugins/inventory/refresh` — re-run `plugin_list` to re-sync the mirror

The admin console's **插件管理** panel (PluginStore) renders the catalog with
per-agent targeting and one-click batch install.

### Trust boundary, stated plainly

The checksum guarantees *integrity against the digest you pinned*, not the
origin of the package; `trust` remains a manifest-declared label and the
host's `MinimumTrust` policy is unchanged. Point `Vapor_PLUGIN_INDEX_URL` only
at index sources you control, and pin digests you computed yourself.

## Alignment with ASF (ArchiPlugin)

Vapor's plugin system is modeled on ArchiSteamFarm's official plugin mechanism
(`IPlugin` core plus opt-in capability interfaces), adjusted for Vapor's
architecture. This section states the mapping and the deliberate divergences,
so plugin authors coming from ASF know what to expect. (ASF reference:
`ArchiSteamFarm/Plugins/Interfaces/` in
[JustArchiNET/ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm),
the [Plugins / Plugins-development wiki pages](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Plugins),
and community plugins such as FreePackages and ASFEnhance.)

| Concern | ASF (ArchiPlugin) | Vapor | Note |
|---------|-------------------|-------|------|
| Contract shape | Tiny `IPlugin` (`Name`/`Version`/`OnLoaded`) + ~22 opt-in capability interfaces, dispatched via `OfType<T>()` fan-out | `IPlugin` (`Info`/`InitializeAsync`/`ShutdownAsync`) + opt-in `IActionPlugin`/`ICommandPlugin`/`IWebApiPlugin`/`IEventPlugin` | Same additive pattern: new capabilities ship as *new optional interfaces*, old plugins untouched |
| Discovery | Recursive `*.dll` scan of `plugins/` dirs + MEF2 convention catalog; no manifest | `VAPOR_PLUGINS_DIR`, one subdirectory per plugin, explicit `plugin.json` | The manifest carries `trust`, `permissions` and `configuration` — things ASF reads from config leftovers |
| Version compatibility | None for third-party (a mismatch surfaces as `TypeLoadException`, verbose log, 10 s delay, process exit 1); ASF's *official* plugins are exact-version pinned (`HasSameVersion`) | `PluginApi.IsCompatible` handshake: major must match, plugin minor ≤ host minor; **official-trust plugins must target the host's API version exactly** (ASF `HasSameVersion` alignment) | Explicit rejection with a reason beats a crash; the official pin keeps bundled plugins from drifting against the host they ship with |
| Host API exposure | Static singletons (`ASF.*`) + the `Bot` instance passed into each hook | `IPluginContext` → `IPluginHostServices` (`ILoggerFactory`, `IServiceProvider`) | Deliberate divergence: DI instead of global state, for testability and hot-unload hygiene |
| Per-plugin config | `[JsonExtensionData]` leftovers of `GlobalConfig.json` / `BotConfig.json` | `configuration` map in `plugin.json`, handed to `InitializeAsync` | Same idea, declared where the plugin lives |
| Event surface | `IBotConnection` (logged on/off), `IBotCardsFarmerInfo` (farming lifecycle), `IBotCommand2`, chat/trade/friend hooks, PICS changelist stream | `IEventPlugin.OnSessionEventAsync` (state changes, connected/disconnected, auth/2FA/QR prompts, errors) | Vapor has no ASF farming-hook equivalent by design: farm orchestration is control-plane side and flows through the job/audit/event bus, not agent-local state |
| Auto-update | GitHub-release assets keyed by host version (`Plugin-V6-0.zip` convention), opt-in whitelist, applied on restart | PluginStore: index URL + SHA-256-pinned zip + `plugin_install` job, hot-loaded immediately | Vapor installs at runtime without a restart; update *policy* (opt-in, digest-pinned) matches ASF's conservatism |
| Isolation | Default load context, loaded for process lifetime; swap requires restart | Collectible `AssemblyLoadContext` per plugin; `plugin_uninstall` hot-unloads and verifies collection | Beyond ASF |
| Trust & permissions | Custom plugins load with full trust (a `-modded` warning is the only signal) | Manifest-declared `trust` gate + per-capability `permissions`, evaluated *before* any plugin code runs; undeclared capabilities are stripped (or rejected in strict mode) | Beyond ASF; Vapor plugins are untrusted by default |
| Web surface | Plugin assemblies become MVC application parts; `IWebServiceProvider`/`IWebInterface` hooks | Host-agnostic `PluginWebRoute`s mounted by hosts under their plugin route prefix | Smaller, host-independent surface |

**Compatibility policy (adopted from ASF's deprecation discipline):** new
capabilities are new optional interfaces; when a hook's signature must change,
a numbered successor interface ships instead of mutating the old one (ASF's
`IBotCommand2` pattern); behavioral deprecations get a logged warning for at
least one release before removal. The core `IPlugin`/`IPluginContext` pair and
the `PluginApi` SemVer rule above are the only contracts a plugin may rely on —
everything else in the host is free to change.


## Game access plugin (`vapor.game-access`)

The sixth official plugin is a **wire-compatible extraction**: the fourteen
game-access actions that used to be registered by the agent host directly
(`play_games`, `get_card_drops`, `get_playtime`, `add_license`, `redeem_key`,
`get_achievements`, `unlock_achievements`, `reset_achievements`,
`get_inventory`, `loot_inventory`, `find_duplicates`, `swap_duplicates`,
`get_points_shop_summary`, `claim_points_shop_items`) now live in an
independent official plugin assembly, ASF-style.

### Why it exists

ASF ships `ArchiSteamFarm.OfficialPlugins.*` as separate assemblies that ride
with the host; Vapor's game-access surface had grown to half the agent's
action registry, and owning it as a plugin makes the boundary explicit: the
host keeps the session engine, trading/market/store surfaces and
diagnostics; the plugin owns "what the account plays, owns and unlocks".

### The compatibility contract

- **Names and payload schemas are byte-for-byte unchanged.** Deployed jobs,
  schedules and control-plane code paths dispatch by the same strings.
- **The agent Docker image bundles the plugin** into
  `/app/plugins/vapor.game-access` (the Monitoring mechanism, now with a
  second resident), so the default hello capability set is unchanged.
- **Without the plugin** the agent stops advertising the fourteen names; the
  control-plane scheduler only routes actions a connected agent declared in
  hello, so affected jobs fail at dispatch ("no agent declares the action") —
  and a force-routed task would fail with the regular `action not found`
  session error. No silent no-ops either way.
- Source runs stage nothing automatically: point `VAPOR_PLUGINS_DIR` at a
  directory containing the built plugin output to get the full action set.

### Host-service wiring, unchanged

The plugin resolves the same host singletons the deleted host wiring did —
loggers from the host factory, `IVaporCache` for the cached card-drop/playtime
actions and `TradeRateLimiter` for loot/swap from the host service provider.
A host without a rate limiter logs a warning and the throttled actions run
unthrottled (their constructors accept a null limiter) instead of failing the
load. The only core change the split needed: `SendTradeOfferAction.NoopLease`
became public so loot/swap could keep acquiring leases the same way after
leaving the host assembly.

### Tests

The migrated action suites live in `Vapor.Plugins.GameAccess.Tests`, with
`PluginHostLoadTests` loading the compiled plugin through a real
`PluginManager` (discovery, isolated ALC, official-trust gating) and
asserting the fourteen unchanged names — the MarketWatch host-load precedent
applied to an extraction.

## Case opening plugin (`vapor.caseopening`)

The second official plugin doubles as the reference example for the full
capability surface: actions + web routes + configuration + result recording,
with zero Steam-side side effects.

### What it is

A **dry-run case-opening simulator**. It rolls Valve's published CS:GO/CS2
case-opening odds against a local case catalog, records every result, and
exposes catalog/open/results/stats through an action (`case_open`, callable
through the job pipeline) and plugin web routes (`GET cases`, `POST open`,
`GET results`, `GET stats`). It never touches Steam inventory, trade or web
session endpoints — see [ToS boundary](#tos-boundary).

### Algorithm and sources

The odds are **Valve's own published disclosure** — the official Chinese CS:GO
site's 概率公示 page (probability disclosure, 2017-09-11,
[csgo.com.cn](https://www.csgo.com.cn/news/gamebroad/20170911/206155.shtml)),
which is the only primary source for case odds:

| Rarity tier | Published probability | Exact roll window (of 782) |
|-------------|----------------------|----------------------------|
| Mil-Spec (blue) | 79.923% | 625 |
| Restricted (purple) | 15.985% | 125 |
| Classified (pink) | 3.197% | 25 |
| Covert (red) | 0.639% | 5 |
| Rare special item (gold — knife/gloves) | 0.256% | 2 |

The same disclosure fixes the remaining structure, which the engine follows
exactly: each tier is 1:5 against the next-higher tier (2:5 gold:covert),
items of equal rarity are equally likely, and **StatTrak™ is an independent
1:10 roll** for items that have a StatTrak variant. There is **no pity
system** — every open is an independent, identically distributed draw.

Per-open roll order (deterministic, `System.Random`-backed, seed injectable
for tests):

1. **Rarity** — one draw against the 625:125:25:5:2 table above.
2. **Item** — uniform among the case's items at the rolled tier.
3. **StatTrak™** — 1:10 when the picked item allows it.
4. **Float** — uniform U(0,1) mapped linearly into the item's own
   `[min_float, max_float]` range. Wear ranges are per paint kit, not per case
   (e.g. AK-47 | Redline is 0.10–0.70); the CSFloat float/paint-seed analysis
   ([blog.csfloat.com, 2020](https://blog.csfloat.com/analysis-of-float-value-and-paint-seed-distribution-in-cs-go/))
   is the reference for the mapping and the wear thresholds:
   FN < 0.07, MW < 0.15, FT < 0.38, WW < 0.45, BS ≥ 0.45.
5. **Paint seed** — uniform integer 0–1000, independent of float.

Open-source references consulted (approach only, no code copied):
[Desmait/OpenCasePlugin](https://github.com/Desmait/OpenCasePlugin) (C#, same
odds table with a cumulative roll — unlicensed, reference for approach),
[jonese1234/Csgo-Case-Data](https://github.com/jonese1234/Csgo-Case-Data)
(per-case item pools and odds provenance), and
[kratos1812/Case-Opening](https://github.com/kratos1812/Case-Opening)
(GPL-3.0, SourceMod — an alternative weighted-bucket roll).

### Modes and the backend seam

`ICaseOpeningBackend` is the single seam between "decide what was unboxed" and
"how it came to be". The plugin ships exactly one implementation:

- **`SimulationBackend`** (the default, and the only `backend` configuration
  value accepted today): pure local RNG. No network, no Steam calls, no
  account required. `case_open` actions report `mode: "dry-run"` in their
  output.

A real backend is **deliberately not implemented**: opening a case consumes a
key inside the CS2 game client (game coordinator traffic), which the public
Steam Web API cannot do. Requesting any other `backend` value fails plugin
initialization with an explicit error rather than silently simulating.

### Result recording

Every open appends one JSON line (case id, item, rarity, stattrak, float,
wear, paint seed, timestamp) to the configured results file
(`results.path`). Aggregates — opens per case, per-rarity counts and observed
rates — are computed from the same records (`GET stats`). When `results.path`
is omitted, results are kept in a bounded in-memory ring (last 1000) and not
persisted.

### ToS boundary

Stated plainly, so nobody has to guess:

- Case opening in CS2 is a **game-client transaction**; there is no public API
  for it. Automating it (web session scripting, trade-bot unboxing fleets) is
  exactly the "Automation" the
  [Steam Subscriber Agreement §4.C](https://store.steampowered.com/subscriber_agreement/)
  prohibits, and is the pattern Valve targeted in its July 2016 actions against
  unboxing/gambling sites.
- This plugin therefore **only simulates**: local RNG over published odds, no
  inventory reads, no trade offers, no market calls, no web session actions.
  The `ICaseOpeningBackend` seam exists so a future legitimate integration has
  one honest place to live — not as a stub for automation.
- Steam Web API terms
  ([apiterms](https://steamcommunity.com/dev/apiterms)) additionally restrict
  Steam data to personal, non-commercial use; the plugin's output (simulated
  results) involves no Steam data at all.

## Game data plugin (`vapor.game-data`)

The third game plugin (and fifth official one) closes the read-only analytics
candidates from [Valve game plugins](game-plugins.md): public Dota 2 data and
the TF2/CS2 item schemas, straight from the official Steam Web API.

### What it is

Four **read-only actions** — every call is a key-authed GET against
`api.steampowered.com`, projected to a compact digest (never the raw
megabyte-scale payload):

| Action | Web API call | Returns |
|--------|--------------|---------|
| `dota2_match_history` | `IDOTA2Match_570/GetMatchHistory/v1/` | status, counts, compact match list (int64 ids — real match ids exceed int32) |
| `dota2_heroes` | `IEconDOTA2_570/GetHeroes/v1/` | status, count, id/name/localizedName/legs |
| `dota2_game_items` | `IEconDOTA2_570/GetGameItems/v1/` | status, count, id/name/localizedName/cost |
| `econ_item_schema` | `IEconItems_{440,730}/GetSchema/v1/` | status, count, defIndex/name digest, appId |

### Key handling (agent-side, lazy)

The Web API key lives in the plugin configuration (`webapi.key`) or the
`VAPOR_GAME_DATA_WEBAPI_KEY` environment variable — both read by the **agent**
at plugin initialization. It never reaches the control plane, the job
pipeline, or any audit record. With no key configured the plugin still loads
and registers its actions; calling any of them fails with explicit guidance
(`webapi.key` / env var names) instead of faking data — the same
honest-degradation rule as Case Opening's backend seam.

### Boundaries

- **No write path**: no trade offers, no market calls, no inventory mutation,
  no game-client automation — real matches, trading and battle-pass progress
  stay out (SSA §4.C, see [game-plugins.md](game-plugins.md)).
- **`econ_item_schema` whitelists appids** 440 (TF2) and 730 (CS2); anything
  else fails with the whitelist spelled out rather than probing Valve's API.
- Web API availability and coverage are Valve's to change; HTTP and parse
  failures become failed action results with the operation name, never thrown
  into the job pipeline.
