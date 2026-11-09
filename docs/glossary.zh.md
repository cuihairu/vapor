简体中文 | English

# 术语表

下表按源码运行时抽象归纳；它描述的是职责边界，不是目录名。

| 术语 | 英文 | 定义 | 证据 |
|---|---|---|---|
| 第 1 层：接入与契约 | Access & contract | 共享线上契约，以及控制面的 REST、SSE、WebSocket、OpenAPI、指标、健康检查和管理入口。 | `src/Vapor.Protocol/Models.cs:51`、`src/Vapor.ControlPlane/Program.cs:16`、`src/Vapor.ControlPlane/Endpoints/SystemEndpoints.cs:10` |
| 第 2 层：控制与编排 | Control & orchestration | 把声明的账号状态、定时/手动任务变成持久子任务；负责路由、重试、取消，并把结果转成事件。 | `src/Vapor.ControlPlane/Program.cs:39`、`src/Vapor.ControlPlane/TaskSchedulerService.cs:8`、`src/Vapor.ControlPlane/DesiredStateReconciler.cs:27` |
| 第 3 层：Agent 隧道 | Agent tunnel | 每个 Agent 一条认证出站 WebSocket，通过有限发送队列承载 `hello`、任务派发/结果/取消/心跳和追踪头。 | `src/Vapor.Agent/Program.cs:386`、`src/Vapor.ControlPlane/Endpoints/JobEndpoints.cs:270`、`src/Vapor.Protocol/Models.cs:131` |
| 第 4 层：运行时执行 | Runtime execution | 通过本地有限入口执行脚本、插件、代理等宿主任务和账号动作；每个任务尝试只回报一个被围栏的结果。 | `src/Vapor.Agent/AgentTaskBuffer.cs:4`、`src/Vapor.Agent/Program.cs:575`、`src/Vapor.Agent/AgentTaskExecutor.cs:16` |
| 第 5 层：Steam 域 | Steam domain | 逐账号的 `BotSession`、SteamKit 传输和 Steam Web 客户端；以需要登录的动作复用，并持有本地加密凭据。 | `src/Vapor.Steam.Core/BotSession.cs`、`src/Vapor.Steam.Core/Steam/SteamClientManager.cs:17`、`src/Vapor.Steam.Core/Security/FileCredentialStore.cs:14` |
| 第 6 层：插件域 | Plugin domain | 从清单发现插件并装入隔离上下文；校验 API/配置/信任约束，贡献动作、路由、事件和宿主安装能力。 | `src/Vapor.Plugins.Core/Api/IPlugin.cs:17`、`src/Vapor.Plugins.Core/PluginManager.cs:53`、`src/Vapor.Agent/HostActions/PluginPackageInstaller.cs:16` |
| 第 7 层：状态归属 | State ownership | 明确各聚合归属 SQLite、Agent 文件、可选 Redis 或短生命周期内存；在线 Agent 注册表与发送队列保持瞬态。 | `src/Vapor.ControlPlane/Program.cs:31`、`src/Vapor.Steam.Core/Security/FileCredentialStore.cs:32`、`src/Vapor.ControlPlane/AgentRegistry.cs:58` |
| 第 8 层：平台服务 | Platform services | 提供周期调度、webhook、审计、密钥限流、有界故障注入以及指标/追踪/日志。 | `src/Vapor.ControlPlane/NotificationService.cs:13`、`src/Vapor.ControlPlane/ApiKeyRateLimiter.cs`、`src/Vapor.ControlPlane/FaultInjector.cs:54` |
| Agent 注册表 | Agent registry | 控制面内存中从已接受 `AgentHello` 到活跃 WebSocket 和发送队列的映射。 | `src/Vapor.ControlPlane/AgentRegistry.cs:58` |
| 任务 | Job | 持久的操作者意图单元；一次性或周期，会拆成子任务。 | `src/Vapor.Protocol/Models.cs:51`、`src/Vapor.Protocol/Models.cs:62` |
| 子任务 | Task | 任务内的单目标尝试载体；持久状态由 `taskId` + `attempt` 围栏保护。 | `src/Vapor.Protocol/Models.cs:75`、`src/Vapor.Protocol/Models.cs:90` |
| 动作 | Action | 有元数据、由会话调用的命名操作；也是公开任务动作名。 | `src/Vapor.Steam.Core/IAction.cs:6`、`src/Vapor.Protocol/Models.cs:51` |
| 会话 | Session | Agent 管理的 Steam 账号运行时状态机与动作执行面。 | `src/Vapor.Steam.Core/SessionManager.cs:13`、`src/Vapor.Steam.Core/BotSession.cs:309` |
| 凭据存储 | Credential store | Agent 本地加密且原子写入的凭据文件，用于恢复 Steam 会话。 | `src/Vapor.Steam.Core/Security/FileCredentialStore.cs:8`、`src/Vapor.Steam.Core/Security/FileCredentialStore.cs:14` |
| 期望状态 | Desired state | 持久账号规格，调和循环用它和活跃编排状态比对。 | `src/Vapor.ControlPlane/AccountStore.cs:5`、`src/Vapor.ControlPlane/DesiredStateReconciler.cs:66` |
| 事件 | Event | 控制面对任务、会话与认证挑战的非持久通知扇出。 | `src/Vapor.ControlPlane/EventBroker.cs:7`、`src/Vapor.Protocol/Models.cs:116` |
| WebSocket 消息 | WebSocket message | 控制面/Agent 每帧共用的 JSON 隧道信封，非空成员统一 JSON 默认值。 | `src/Vapor.Protocol/Models.cs:131`、`src/Vapor.Protocol/JsonDefaults.cs:6` |
| 插件清单 | Plugin manifest | 插件发现与兼容契约：身份、API 版本、信任、权限、入口程序集和配置。 | `src/Vapor.Plugins.Core/PluginManifest.cs:9`、`src/Vapor.Plugins.Core/PluginManifest.cs:92`、`src/Vapor.Plugins.GameAccess/plugin.json:1` |
| 宿主动作 | Host action | 与账号动作分离的 Agent 机器操作，目标必须是 `agent:<id>`。 | `src/Vapor.Agent/HostActions/IHostAction.cs:6`、`src/Vapor.ControlPlane/HostTaskTarget.cs:9` |
| SQLite 存储 | SQLite stores | 控制面分别为任务队列、配置/账号、审计、采集、脚本/编排和 webhook 投递建立数据库。 | `src/Vapor.ControlPlane/Program.cs:39`、`src/Vapor.ControlPlane/Program.cs:63`、`src/Vapor.ControlPlane/Program.cs:64` |
| 缓存后端 | Cache backend | 配置了 Redis 则使用 Redis，否则使用进程内有界缓存。 | `src/Vapor.Agent/Program.cs:114`、`src/Vapor.Steam.Core/Caching/RedisVaporCache.cs:77`、`src/Vapor.Steam.Core/Caching/MemoryVaporCache.cs:58` |

## 相关文档

- [架构](architecture.zh.md#source-abstraction-layers)
- [插件开发](plugins.zh.md)
