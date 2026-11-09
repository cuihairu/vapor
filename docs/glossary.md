English | 简体中文

# Glossary

This table describes the runtime abstraction layers found by reading the source, not the repository layout alone.

| Term | English term | Definition | Evidence |
|---|---|---|---|
| Layer 1: Access and contract | Access & contract | A shared wire contract and the control plane's REST, SSE, WebSocket, OpenAPI, metrics, health and admin entry points. | `src/Vapor.Protocol/Models.cs:51`, `src/Vapor.ControlPlane/Program.cs:16`, `src/Vapor.ControlPlane/Endpoints/SystemEndpoints.cs:10` |
| Layer 2: Control and orchestration | Control & orchestration | Turns declared account state and scheduled/manual jobs into durable tasks, routes them, retries/cancels them, and turns results into events. | `src/Vapor.ControlPlane/Program.cs:39`, `src/Vapor.ControlPlane/TaskSchedulerService.cs:8`, `src/Vapor.ControlPlane/DesiredStateReconciler.cs:27` |
| Layer 3: Agent tunnel | Agent tunnel | An authenticated, outbound, single WebSocket per agent that carries `hello`, task dispatch/result/cancel/heartbeat and trace headers over bounded send queues. | `src/Vapor.Agent/Program.cs:386`, `src/Vapor.ControlPlane/Endpoints/JobEndpoints.cs:270`, `src/Vapor.Protocol/Models.cs:131` |
| Layer 4: Runtime execution | Runtime execution | Runs host operations such as script/plugin/proxy tasks and account actions through a bounded local intake; reports one fenced result per task attempt. | `src/Vapor.Agent/AgentTaskBuffer.cs:4`, `src/Vapor.Agent/Program.cs:575`, `src/Vapor.Agent/AgentTaskExecutor.cs:16` |
| Layer 5: Steam domain | Steam domain | A per-account `BotSession`, SteamKit transport and Steam Web clients, wrapped in reusable login-gated actions and local encrypted credentials. | `src/Vapor.Steam.Core/BotSession.cs`, `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:120`, `src/Vapor.Steam.Core/Security/FileCredentialStore.cs:14` |
| Layer 6: Plugin domain | Plugin domain | Discovers manifest-described plugins in isolated load contexts, checks API/configuration/trust constraints, contributes actions/routes/events, and installs host operations. | `src/Vapor.Plugins.Core/Api/IPlugin.cs:17`, `src/Vapor.Plugins.Core/PluginManager.cs:53`, `src/Vapor.Agent/HostActions/PluginPackageInstaller.cs:16` |
| Layer 7: State ownership | State ownership | Assigns each aggregate to SQLite, agent files, optional Redis, or short-lived memory; keeps the agent registry and in-flight send queues ephemeral. | `src/Vapor.ControlPlane/Program.cs:31`, `src/Vapor.Steam.Core/Security/FileCredentialStore.cs:32`, `src/Vapor.ControlPlane/AgentRegistry.cs:58` |
| Layer 8: Platform services | Platform services | Schedules recurrence, emits webhooks, audits actions, limits keys, injects bounded faults, and publishes metrics/tracing/logs. | `src/Vapor.ControlPlane/NotificationService.cs:13`, `src/Vapor.ControlPlane/ApiKeyRateLimiter.cs`, `src/Vapor.ControlPlane/FaultInjector.cs:54` |
| Agent registry | Agent registry | The control plane's in-memory map from an accepted `AgentHello` to a live WebSocket and send queue. | `src/Vapor.ControlPlane/AgentRegistry.cs:58` |
| Job | Job | The durable unit of operator intent; one-shot or recurring, split into tasks. | `src/Vapor.Protocol/Models.cs:51`, `src/Vapor.Protocol/Models.cs:62` |
| Task | Task | A single target attempt carrier inside a job; its durable state is fenced by `taskId` + `attempt`. | `src/Vapor.Protocol/Models.cs:75`, `src/Vapor.Protocol/Models.cs:90` |
| Action | Action | A named, metadata-described operation invoked by a session; also the public job action name. | `src/Vapor.Steam.Core/IAction.cs:6`, `src/Vapor.Protocol/Models.cs:51` |
| Session | Session | The Steam account runtime state machine and action execution surface managed by the agent. | `src/Vapor.Steam.Core/SessionManager.cs:13`, `src/Vapor.Steam.Core/BotSession.cs:309` |
| Credential store | Credential store | The agent-local, encrypted, atomically written credential file used to restore Steam sessions. | `src/Vapor.Steam.Core/Security/FileCredentialStore.cs:8`, `src/Vapor.Steam.Core/Security/FileCredentialStore.cs:14` |
| Desired state | Desired state | A persisted account specification that the reconciler compares with live orchestration state. | `src/Vapor.ControlPlane/AccountStore.cs:5`, `src/Vapor.ControlPlane/DesiredStateReconciler.cs:66` |
| Event | Event | A non-durable control plane notification fan-out for jobs, sessions and auth challenges. | `src/Vapor.ControlPlane/EventBroker.cs:7`, `src/Vapor.Protocol/Models.cs:116` |
| WebSocket message | WebSocket message | The JSON tunnel envelope for every control-plane/agent frame; all non-null members use shared JSON defaults. | `src/Vapor.Protocol/Models.cs:131`, `src/Vapor.Protocol/JsonDefaults.cs:6` |
| Plugin manifest | Plugin manifest | The plugin discovery and compatibility contract: identity, API version, trust, permissions, entry assembly and configuration. | `src/Vapor.Plugins.Core/PluginManifest.cs:9`, `src/Vapor.Plugins.Core/PluginManifest.cs:92`, `src/Vapor.Plugins.GameAccess/plugin.json:1` |
| Host action | Host action | An agent-machine operation separate from account actions, targeted by `agent:<id>`. | `src/Vapor.Agent/HostActions/IHostAction.cs:6`, `src/Vapor.ControlPlane/HostTaskTarget.cs:9` |
| SQLite stores | SQLite stores | Separate control plane databases for job queue, config/accounts, audit, crawl, scripts/flows and webhook deliveries. | `src/Vapor.ControlPlane/Program.cs:39`, `src/Vapor.ControlPlane/Program.cs:63`, `src/Vapor.ControlPlane/Program.cs:64` |
| Cache backend | Cache backend | Optional Redis when configured, otherwise an in-process bounded cache. | `src/Vapor.Agent/Program.cs:114`, `src/Vapor.Steam.Core/Caching/RedisVaporCache.cs:77`, `src/Vapor.Steam.Core/Caching/MemoryVaporCache.cs:58` |

## See also

- [Architecture](architecture.md#source-abstraction-layers)
- [Plugin development](plugins.md)
