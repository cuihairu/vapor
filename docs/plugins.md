# Vapor Plugin Development Guide

Vapor's agent ships with a full plugin system: isolated loading, SemVer-gated API
compatibility, a trust/permission model and a small, explicit API surface. This guide
walks through building, declaring, configuring and debugging a plugin.

Everything described here is exercised by three official plugins and the test suites —
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
