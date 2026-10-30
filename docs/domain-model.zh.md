简体中文 | English

# 领域模型字典

本文档定义 Vapor 领域的十个核心对象 —— Account、Session、Agent、Job、Task、Attempt、Action、Capability、Event、Plugin —— 每个对象五个维度：谁拥有它、如何生灭、持久化在哪、如何被标识、承诺什么一致性。这是对现有代码的解读（2026-10-05 已核对）；每个对象章节末尾给出它所描述的精确类型与文件。关系骨架遵循收敛计划图（评审 §31，已对照当前状态核对）。

一致性措辞与 [`consistency.md`](consistency.md) §1（状态所有权地图）保持一致；本文档补充对象级视图，不改变任何保证。

## 对象地图

| 对象 | 一句话定义 | 代码类型 |
|--------|--------------------|------------|
| Account | 已声明的挂机账号：期望状态加上路由元数据 | `AccountSpec`, `AccountDesiredState`, `AccountStore` |
| Session | 某个账号的一次实时 Steam 登录，由 Agent 拥有 | `BotSession`, `SessionState`, `SessionSnapshot`, `SessionTracker` |
| Agent | 经由出站隧道连接的工作节点 | `AgentHello`, `ConnectedAgent`, `AgentRegistry` |
| Job | 针对一组目标的一次动作运行请求 | `Job`, `JobStatus`, `JobSchedule` |
| Task | 一个任务中某个目标的切片，派发的单位 | `JobTask`, `JobTaskStatus` |
| Attempt | 再派发计数器，用于围栏结果并封顶重试 | `JobTask.Attempt`, `TaskResult.Attempt`, `ActionSemantics` |
| Action | 一个具名、带元数据标注的 Steam 工作单位 | `IAction`, `ActionMetadata`, `ActionSafety`, `ActionRegistry` |
| Capability | Agent 宣告的动作支持，用作路由过滤器 | `AgentHello.Capabilities`, `ConnectedAgent.SupportsAction` |
| Event | SSE 流上的即发即忘通知 | `Event`, `SessionEvent`, `AuthChallengeEvent`, `EventBroker` |
| Plugin | 加载进隔离 ALC 的版本化扩展包 | `PluginManifest`, `PluginInfo`, `PluginManager`, `PluginInventory` |

## 关系骨架

```text
              Control Plane                                Agent
   ┌──────────────────────────────────┐        ┌──────────────────────────┐
   │ AccountSpec ──▶ DesiredState     │        │ BotSession (SessionState)│
   │                  Reconciler      │        │   │ credentials           │
   │                     │            │        │   ▼                     │
   │                     ▼            │ tunnel │ Session ──▶ Action       │
   │   Job ──▶ Task ──▶ Attempt       │◀──────▶│   ▲        (registry +  │
   │              │         │         │  WSS   │   │         capabilities)│
   │              ▼         ▼         │        │ PluginManager ──▶ Plugin │
   │   AgentRegistry ◀── AgentHello   │        │   │ filesystem install   │
   │   SessionTracker ◀─ events       │        │   ▼                     │
   │   PluginInventory ◀─ task output │        │ PluginInventory mirror  │
   │   EventBroker ──▶ SSE            │        │   (source)              │
   └──────────────────────────────────┘        └──────────────────────────┘
```

承载主要负载的路径自上而下读：已声明的 Account 被调和器收敛成 Job，Job 扇出成 Task，Task 携带 Attempt 并派发给具备能力的 Agent，该 Agent 的 Session 执行 Action。其余一切 —— Capability、Event、Plugin —— 要么是该路径上的路由过滤器，要么是对其的观测。

---

## 1. Account

挂机账号是已声明的意图，不是活的东西：`PUT /v1/accounts/{name}` 写入一份规格，编排器把现实收敛到它。凭据刻意不属于该对象 —— 规格只有元数据，密钥存放在钉定 Agent 的加密存储中。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | 控制平面。账号的凭据半边归钉定 Agent 所有，从不跨到 CP。 |
| 生命周期 | 由 `PUT /v1/accounts/{name}` 创建；通过 `Enabled` 和 `DesiredState`（`Offline` / `Online` / `Idle` / `Farm` / `Boost` 之一）切换；由 `DELETE` 删除。调和器按账号驱动期望→实际的收敛。 |
| 持久化 | 读路径在内存（`AccountStore`），写穿到 SQLite（`SqliteConfigStore`、`Vapor_CONFIG_DB_PATH`）；CP 启动时重新水合 —— 重启后存活。 |
| 标识 | `AccountName`，大小写不敏感匹配（`AccountStore` 与 `SessionTracker` 全程使用 `StringComparer.OrdinalIgnoreCase`）。 |
| 一致性 | 单写者 CP 存储；`ConfigVersion` 是调和器消费的单调触发器。期望状态与运行时状态分离：规格是权威的，观测到的会话状态最终收敛。 |

代码索引：`AccountSpec`（14 个字段，含 `IdleApps`、`BoostTargets`、`TradePolicy`、`FarmPolicy`、`MarketListingsEnabled`、`Role`、`SteamId`）—— `src/Vapor.Protocol/Accounts.cs:117`；`AccountDesiredState` —— `Accounts.cs:6`；`AccountStore` —— `src/Vapor.ControlPlane/AccountStore.cs`；收敛运行时（已分配 Agent、登录尝试预算、活跃 Job）按进程驻留内存 —— `DesiredStateReconciler.cs`。

## 2. Session

每个账号一次 Steam 登录，运行在 Agent 内部。Agent 拥有真相；控制平面保留一份最终一致的快照，用于展示与编排决策。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | Agent，端到端：会话状态机、凭据、令牌。CP 的 `SessionTracker` 持有经会话事件上报的快照 —— 它是镜像，不是所有者。 |
| 生命周期 | 十态状态机（`Disconnected`、`Connecting`、`ConnectingWaitAuthCode`、`ConnectingWait2FA`、`ConnectingWaitQr`、`Connected`、`Reconnecting`、`DisconnectedByUser`、`Disconnecting`、`FatalError`）。认证等待由提交的验证码、SSE 通道或 Agent 侧自动应答器解决；令牌恢复在 Agent 重启后重建会话而无需重新提示。 |
| 持久化 | Agent 侧：凭据以 AES-GCM 加密存放在 `/app/.vapor`（或配置存储），访问/刷新令牌被持久化。CP 侧：无 —— `SessionTracker` 是内存 `ConcurrentDictionary`，随 Agent 上报重新填充。 |
| 标识 | `AccountName`（大小写不敏感）。 |
| 一致性 | Agent 上报，最终一致地汇入 CP 快照。CP 重启会丢失快照，直到 Agent 重新上报；会话本身仍在 Agent 上运行。 |

代码索引：`SessionState`、`SessionEventType`（7 种）—— `src/Vapor.Steam.Core/SessionState.cs`；`BotSession` —— `src/Vapor.Steam.Core/BotSession.cs`；`SessionSnapshot`（5 个字段）、`SessionTracker` —— `src/Vapor.ControlPlane/SessionTracker.cs`。登录流程见 [`session-engine.md`](session-engine.md)。

## 3. Agent

一台经由出站 WSS 隧道连接的工作节点。CP 只在其 socket 打开期间知道该 Agent。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | CP 持有注册表（`AgentRegistry`）—— 但它是派生状态。Agent 进程拥有自己的会话、插件与凭据。 |
| 生命周期 | 在隧道握手中注册（bearer key 检查 → WebSocket 升级 → `agentId`/`region` 查询参数 → 首个 `hello` 帧，否则以 `PolicyViolation` 关闭）。连接期间保持注册；心跳驱动存活；断连时注销。Agent 重启 = 重新 hello，全新注册。 |
| 持久化 | 无，刻意为之。CP 重启后注册表从 Agent 的重新 hello 整体重建。 |
| 标识 | `AgentId` —— `hello` 帧中的值必须等于查询参数，否则 socket 被拒绝。 |
| 一致性 | 派生、最终一致：socket 丢失即存活信号，丢失触发该 Agent 账号的再平衡。派发容量与钉定路由（`agent:{id}`）读取此注册表。 |

代码索引：`AgentHello`（`AgentId`、`Region`、`Capabilities`、`Meta`）—— `src/Vapor.Protocol/Models.cs:124`；`ConnectedAgent`、`AgentRegistry` —— `src/Vapor.ControlPlane/AgentRegistry.cs`。

## 4. Job

针对一组目标的一次动作运行请求。附带 `schedule` 时它变成一个循环模板。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | 控制平面，独占 —— Job 是 CP 的主要持久工作状态。 |
| 生命周期 | `JobStatus` 6 个状态：`Queued` → `Running` → `Finished` / `Failed`，外加 `Canceled` 和 `Scheduled`（仅循环模板）。带 `JobSchedule` 的模板保持 `Scheduled`；`RecurringJobScheduler` 按每个触发点派生一份新的子 Job，遵循 `Missed`（`Skip` / `RunOnce`）与 `Overlap`（`Skip` / `Allow`）策略。 |
| 持久化 | SQLite（`SqliteJobStore`）—— CP 重启后存活；排队的作业恢复。 |
| 标识 | 由 16 个 CSPRNG 字节生成的 32 字符小写十六进制（`Id.New()`），每个 Job 唯一。 |
| 一致性 | 单写者存储，按存储串行化写入。模板派生在存储上是原子的；一个触发点要么触发一次、要么按策略跳过，永不双重触发。 |

代码索引：`Job`、`JobStatus`、`JobSchedule`（间隔或 5 字段 UTC cron，cron 优先）—— `src/Vapor.Protocol/Models.cs`；`SqliteJobStore` —— `src/Vapor.ControlPlane/SqliteJobStore.cs`；`RecurringJobScheduler` —— `src/Vapor.ControlPlane/RecurringJobScheduler.cs`。

## 5. Task

一个任务中某个目标的切片 —— 实际派发、执行与结果的单位。一个含 5 个目标的 Job 创建 5 个 Task；一个 Task 同时至多映射到一次 Agent 执行。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | 控制平面。Agent 执行但从不在存储中保存 Task。 |
| 生命周期 | `JobTaskStatus` 5 个状态：`Queued` —claim→ `Running` —result→ `Finished` / `Failed`（终态，保留 error 与 output），`Canceled`。认领是原子的（`ClaimNextQueuedTask`）；租约过期（`TaskLeaseSeconds`，默认 300 s，无心跳）的 `Running` Task 会被重新入队（`RequeueStaleRunningTasks`）。 |
| 持久化 | SQLite（`SqliteJobStore`），每个 Task 一行；每个 Task 恰好一条终态记录 —— 尝试围栏让迟到的重复成为空操作。 |
| 标识 | 32 字符小写十六进制（`Id.New()`）；Task 也（逻辑上）以 `JobId` + `Target` 为键。 |
| 一致性 | 至少一次交付并带围栏：重新入队的 Task 可能再次执行（因此有 Action 安全分级），但结果写入受尝试围栏约束，且一个 Task 不能完成两次。派发给任务所在区域中已连接、能力匹配的 Agent（最空闲优先，Agent id 决胜），或为宿主动作钉定到 `agent:{id}`。 |

代码索引：`JobTask`（`Id`、`JobId`、`Target`、`Action`、`Region`、`Payload`、`Status`、`Attempt`、`CreatedAt`、`UpdatedAt`、`Error?`、`Output?`）、`JobTaskStatus`、`TaskResult`、`TaskHeartbeat`、`TaskCancel` —— `src/Vapor.Protocol/Models.cs`；认领/租约/围栏 SQL —— `SqliteJobStore.cs`。完整交付语义见 [`consistency.md`](consistency.md) §2。

## 6. Attempt

它不是独立的表或类型：attempt 是 Task 上的整型再派发计数器，此处被提升为具名概念，因为它承载交付保证。Attempt n 标识“本任务的第 n 次派发”；结果与心跳携带它，若已过期则被丢弃。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | 控制平面（计数器存在于 Task 行；Agent 回显它）。 |
| 生命周期 | 首次派发起始为 0/1，在租约过期回收或派发失败时递增。`Attempt` 与当前值不匹配的结果被忽略（围栏）。再派发有界：`ReadOnly`/`Idempotent` 动作使用配置的上限（`Vapor_TASK_MAX_DISPATCH_ATTEMPTS`）；`GuardedWrite`、`NonIdempotent` 与未分级（`Unknown`）动作硬封顶为 2 —— 重复这些动作可能使外部副作用翻倍。用尽上限即为终态 `Failed`。 |
| 持久化 | SQLite 中的 `JobTask.Attempt` 列。 |
| 标识 | 二元组 `(TaskId, Attempt)` —— 这是 `SetTaskResult`、`TaskHeartbeat` 与 `TaskCancel` 施加围栏的对象。 |
| 一致性 | 围栏性质：一个 Task 至多有一个 attempt 是“当前的”；过期写入无法损坏终态记录。 |

代码索引：`JobTask.Attempt`、`TaskResult.Attempt` —— `src/Vapor.Protocol/Models.cs`；`ActionSemantics.MaxDispatchAttempts`（`ActionMetadata.Safety` 的镜像，保守封顶 2）—— `src/Vapor.ControlPlane/ActionSemantics.cs`；在唯一的再派发决策点 `HandleUndispatchableTaskAsync` 强制执行。

## 7. Action

一个具名的 Steam 工作单位，以元数据与执行安全分级声明。核心与插件中共 65 个内置动作。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | Agent 进程。`ActionRegistry` 是 Agent 本地的；CP 保留一张人工镜像的语义表用于派发决策。 |
| 生命周期 | 核心动作在 Agent 启动时注册；插件动作在插件加载时注册、卸载时注销。没有持久化：一个动作的存在时间恰好等于其程序集被加载的时间。 |
| 持久化 | 无（进程内注册表）。分级的持久性存在于源码注解 + `ActionSemantics` 镜像中。 |
| 标识 | `IAction.Name`，大小写不敏感（`OrdinalIgnoreCase` 查找）。名称冲突的插件动作会静默覆盖核心动作（后写者胜出）—— 约定是使用带前缀的名称（`market_watch_*`）。 |
| 一致性 | 三方一致是 CI 门禁（`scripts/verify-actions-safety.py`）：源码注解 ⊆ CP 镜像表 ⊆ 动作目录，名称逐一对应、取值逐一相同。已注册但未分级（`Unknown`）的动作会记录一条告警，并在保守上限下派发。 |

代码索引：`IAction`、`ActionMetadata`（`RequiresLogin`、`TimeoutSeconds`、只可初始化的 `Safety`，默认 `Unknown`）、`ActionSafety` 5 级枚举 —— `src/Vapor.Steam.Core/IAction.cs`、`src/Vapor.Protocol/ActionSafety.cs`；`ActionRegistry` —— `src/Vapor.Steam.Core/`；目录 —— [`actions.md`](actions.md)。

## 8. Capability

Agent 对自身可运行哪些动作的宣告：hello 帧中一个扁平的 `action → bool` 字典，用作路由过滤器。它不是独立的服务或目录 —— 只是一个字段，一个消费者。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | Agent 在 hello 时宣告；CP 只读不写。 |
| 生命周期 | 每条连接设置一次（hello），存活至断连。Agent 重启会重新宣告（可能不同，例如插件变更后）。插件安装会更新*库存镜像*（见 Plugin），但连接自身的 capability 集合在重新 hello 前固定不变。 |
| 持久化 | 无 —— 仅存在于 `ConnectedAgent` 条目中。 |
| 标识 | 以动作名称为键（`OrdinalIgnoreCase`）；缺失或空字典意味着“支持一切”（对最小化 Agent 的宽松默认）。 |
| 一致性 | 尽力而为的路由提示，不是保证：派发会二次校验 capability，不匹配时派发会响亮地失败，而不是静默错误路由。 |

### 放置顺序（capability 作为其中一项输入）

两个放置面共享同一套排序契约 —— 先区域匹配，再 capability，再负载，最后以 Agent id 作为全序决胜：

1. **Region** —— 仅 hello 区域与任务/账号区域匹配的 Agent（或宿主机钉定的 `agent:{id}` 目标，完全绕过匹配器）。
2. **Capability** —— `SupportsAction` 过滤到宣告了该动作的 Agent；钉定的 Agent 若不具备则派发响亮地失败，而非回退。
3. **Load** —— 最空闲优先。账号放置按 Agent 统计已分配账号（`DesiredStateReconciler`）；任务派发按 Agent 统计运行中任务，读取自任务存储的 `claimed_by_agent_id` 归属（派发成功时按 attempt 围栏写入，租约重新入队时清除 —— 因此该计数会自愈并在重启后存活）。
4. **Agent id** —— `Ordinal` 升序决胜，负载相等时保持放置确定性。

标签被刻意省略：hello 只携带 `agentId`/`region`/`capabilities`/`meta`，目前也未观察到跨区域或基于标签的放置需求。若将来出现，结构化标签可在本步骤接入，无需改动隧道协议。

代码索引：`AgentHello.Capabilities` —— `src/Vapor.Protocol/Models.cs:124`；`ConnectedAgent.SupportsAction` —— `src/Vapor.ControlPlane/AgentRegistry.cs:84`；消费者：任务派发用 `AgentRegistry.Pick(region, action, load)`，账号放置用 `DesiredStateReconciler.PickAgent`；派发负载信号见 `SqliteJobStore.MarkTaskClaimedBy` / `CountRunningTasksByAgent` —— `src/Vapor.ControlPlane/SqliteJobStore.cs`。

## 9. Event

SSE 流上的即发即忘通知：Job/Task 生命周期、会话状态、认证挑战、Agent 连通性、抓取与编排结果。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | 控制平面（`EventBroker`）。 |
| 生命周期 | 进程内发布，扇出到已订阅的 SSE 客户端。有界通道（容量 256，`DropOldest`）：缓慢的消费者会静默丢失旧事件。无重放、无历史 —— 持久历史由审计日志（SQLite）承担，而非 broker。 |
| 持久化 | 无，刻意为之。值得审计的结果独立写入 `SqliteAuditStore`。 |
| 标识 | 事件记录的 `Id` = `Guid.NewGuid().ToString("N")`；SSE 帧名即事件的 `type`（`job.created`、`task.dispatched`、`session.<eventType>`、`auth.<challengeType>`、…）。 |
| 一致性 | 每个消费者至多一次，尽力而为交付。消费者必须容忍缺口；单通道内顺序为 FIFO，跨通道不保证。 |

代码索引：`Event`、`SessionEvent`、`AuthChallengeEvent`、`PluginEvent` —— `src/Vapor.Protocol/Events.cs`、`src/Vapor.ControlPlane/IEventBroker.cs`；`EventBroker`（3 个有界通道）—— `src/Vapor.ControlPlane/EventBroker.cs`。机制见 [`api.md`](api.md) §2。

### 事件分层（Domain / Operational / Audit）

事件分为三层，各有不同的持久性与消费者契约。分层决定事件**驻留何处**与消费者**可假设什么** —— 切勿混用。

| 层 | 驻留处 | 持久性 | 消费者契约 |
|-------|----------|------------|-------------------|
| **Domain** | `EventBroker` SSE | 无（尽力而为，`DropOldest`） | 至多一次；容忍缺口；无重放 |
| **Operational** | `EventBroker` SSE + 结构化日志 | 无（日志：由运维保留） | 健康/排障信号；负载高时可能丢失 |
| **Audit** | `SqliteAuditStore`（SQLite） | 持久、仅追加 | 精确即所记录的事实；重启后存活；驱动合规 |

- **Domain** —— 核心对象的生命周期：`job.*`、`task.*`、`session.*`、`auth.*`。消费者是实时 UI 与依据当前状态行动的 Agent。
- **Operational** —— 系统健康与进程信号：Agent 连通性、抓取运行进度、收敛决策、插件安装/卸载、webhook 投递尝试。消费者是仪表盘与运维人员。
- **Audit** —— 谁在何时改了什么：`script_*`、`flow_*`、`job_*`、`account_*`、配置与策略变更。独立于 broker 写入（值得审计的结果在其发生的那一刻被持久化到 `SqliteAuditStore`，从不经过 SSE 路由）。消费者是审计 API 与 `audit.db` 本身。

### SSE 事件名

约定为 `<scope>.<snake_case_verb>`：前缀是事件所关于的**主语**（不是层名 —— 层由主语推导），后缀是以小写 snake case 写出的被观测事实。下表的权威表是唯一真相来源；`scripts/verify-sse-event-names.py` 使其与代码中的发布点、以及 [`api.md`](api.md) §2 的枚举保持同步，因此新增一个发布调用却没有表行 —— 或表中有行而代码已不再发出 —— 都会导致构建失败。

| SSE 事件名 | 层 | 发布者 |
|---|---|---|
| `job.created` | Domain | 任务创建端点 |
| `job.canceled` | Domain | 任务取消端点 |
| `task.dispatched` | Domain | 任务调度器 |
| `task.finished` | Domain | Agent 隧道的任务结果路径 |
| `task.failed` | Domain | 任务调度器 |
| `task.dispatch_failed` | Domain | 任务调度器（无法派发） |
| `task.enqueue_failed` | Domain | 任务调度器（无法派发） |
| `job.scheduled_triggered` | Domain | 循环任务调度器 |
| `job.scheduled_skipped` | Domain | 循环任务调度器 |
| `job.scheduled_completed` | Domain | 循环任务调度器 |
| `session.<eventType>` | Domain | 会话事件上报路径（开放家族） |
| `auth.<challengeType>` | Domain | 认证挑战发起/应答路径（开放家族） |
| `agent.connected` | Operational | Agent 隧道 hello |
| `agent.disconnected` | Operational | Agent 隧道断开 |
| `crawl.run_triggered` | Operational | 抓取运行 worker |
| `crawl.run_completed` | Operational | 抓取运行 worker |
| `crawl.run_skipped` | Operational | 抓取运行 worker |
| `account.farm_progress` | Operational | 期望状态调和器 |
| `account.standing_alert` | Operational | 期望状态调和器 |
| `account.standing_released` | Operational | 期望状态调和器 |
| `account.reconciled` | Operational | 期望状态调和器 |
| `trade.auto_accepted` | Operational | 期望状态调和器 |

注意：`crawl.run_failed`（抓取派发异常）只写入审计日志，刻意从不抵达 broker；`session.*` 与 `auth.*` 是开放家族，其成员分别是上报的会话事件类型与六种已文档化的挑战类型，因此无法封闭列举。审计汇聚的动作名即上表的第三列，并在 [`api.md`](api.md) §5 中镜像。SSE 在所有层都保持尽力而为。

## 10. Plugin

一个版本化的扩展包：一份 `plugin.json` 清单加一个程序集，加载进 Agent 上自己的 `AssemblyLoadContext`。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | Agent 拥有安装、加载与文件系统状态。CP 持有它所认为已安装内容的镜像（`PluginInventory`），由宿主动作输出重建。 |
| 生命周期 | 安装（zip + 强制 SHA-256、清单校验、热加载）→ 已加载（动作已注册）→ 卸载（动作注销、ALC 回收）→ 卸载移除（目录删除；幂等）。加载时设 SemVer API 门禁：插件的 `ApiVersion` 必须与宿主的契约兼容。 |
| 持久化 | Agent 文件系统（`/app/plugins` 或配置目录）。CP 镜像：仅内存，在 Agent 重启/离线编辑后漂移；刷新端点重跑 `plugin_list` 重新同步。 |
| 标识 | `PluginManifest.Id` + `Version`（插件自身的 SemVer，与 `ApiVersion` 分离）。 |
| 一致性 | CP 镜像是尽力而为且明确允许漂移；真相来源始终是 Agent 的目录。信任级别（`official` / `community` / `unknown`）门控权限，而非正确性。 |

代码索引：`PluginManifest` —— `src/Vapor.Plugins.Core/PluginManifest.cs`；`PluginInfo`、`IPlugin` —— `src/Vapor.Plugins.Core/Api/IPlugin.cs`；`PluginManager` —— `src/Vapor.Plugins.Core/PluginManager.cs`；`PluginInventory` —— `src/Vapor.ControlPlane/PluginInventory.cs`；`PluginCatalogService`（目录源、HTTP JSON 索引、60 s 缓存）。开发指南见 [`plugins.md`](plugins.md)。

## AuthChallenge

认证挑战（Steam Guard 验证码 / 2FA / QR）刻意**不**是持久化的领域对象：`AuthChallengeTracker` 是 CP 上一个内存中的待处理挑战映射，而线上形态是 `src/Vapor.Protocol/Events.cs` 中唯一的 `AuthChallengeEvent` 记录（原 ControlPlane 侧的镜像记录已移除 —— 一种类型，由 CP/Agent/插件共用）。

| 维度 | 定义 |
|-------|-----------|
| 所有者 | 控制平面。*应答*来自运维人员（管理控制台）或 Agent 侧自动应答器；挑战记录本身除短命的验证码外从不持有凭据，且 `code` 会被从面向管理者的视图中剥离。 |
| 生命周期 | 当登录需要验证码时由 Agent 发起（`auth_code_required` / `2fa_required` / `qr_required`）→ 在 tracker 中待处理（每账号一条，upsert 语义）→ 由 `code_provided_email` / `code_provided_totp` / `code_provided_2fa` 事件或会话状态变化解决 → `Clear` 移除该条目。CP 重启会清空所有待处理挑战；Agent 重新发起仍然重要的那些。 |
| 持久化 | 无（内存 `ConcurrentDictionary<string, AuthChallengeEvent>`，以账号为键）。按设计为瞬时。 |
| 标识 | `AccountName`（每账号一条待处理挑战；旧的被替换）。事件记录携带一个 `Guid "N"` id。 |
| 一致性 | Agent 登录状态的尽力而为镜像；Agent 的会话状态机才是真相。六个 `challengeType` 取值如上所列；`JobId` 将挑战关联到派发它的 Job（若存在）。 |
| Attempt | `Attempt`（int，默认 1）—— tracker 按账号物化重新发起的代数（首次发起 = 1，每次重新发起 = 前一次+1）；tracker 是权威，入站值永不会泄漏进存储的代数。SSE 事件携带同一个数。刻意**缺失**的字段：`status` 与 `source`（二者已由 `challengeType` 编码 —— `*_required` = 待处理且由 Agent 发起，`code_provided_*` = 已应答且由运维发起；单列字段会重复状态），以及 `expiresAt`（没有 TTL 信号跨越 CP↔Agent 边界；过期仍是会话状态的问题，直到有真实信号出现）。 |

代码索引：`AuthChallengeEvent`（`Id`、`AccountName`、`ChallengeType`、`Message?`、`Code?`、`Timestamp`、`JobId?`、`Attempt`）—— `src/Vapor.Protocol/Events.cs`；`AuthChallengeTracker`（`Upsert` 返回已物化 `Attempt` 的存储记录）—— `src/Vapor.ControlPlane/AuthChallengeTracker.cs`。脱敏规则见 [`api.md`](api.md) §2 与 [`architecture.md`](architecture.md) § Security。

---

## 与配套文档的交叉核对

2026-10-05 已对照下列文本核对；每行是本文档的一项论断及其真相来源。

| # | 本文档论断 | 核对对象 | 状态 |
|---|-----------|-----------------|--------|
| 1 | jobs/specs/audit/crawl/settings 的所有权与持久性（持久）vs registry/sessions/challenges/inventory（内存、重建）vs 凭据（仅 Agent） | `consistency.md` §1 状态所有权表 | 一致，同为 11 行 |
| 2 | Task 生命周期：原子认领、`TaskLeaseSeconds`（默认 300 s）处租约回收、尝试围栏的单终态记录 | `consistency.md` §2 生命周期图 + `SqliteJobStore` 锚点 | 一致 |
| 3 | 会话十态状态机，含 `ConnectingWaitQr`；Agent 拥有会话，CP 镜像快照 | `session-engine.md` § SessionState | 一致 |
| 4 | Action 标识 = `Name`（OrdinalIgnoreCase）；插件冲突 = 静默后写者胜出 | `actions.md` § 注册机制 | 一致 |
| 5 | 安全分级与派发上限：`GuardedWrite`/`NonIdempotent`/`Unknown` 封顶 2，`ReadOnly`/`Idempotent` 用配置上限 | `actions.md` § 约定（执行安全）、`production.md` § 无法派发的任务 | 一致 |
| 6 | 账号期望状态 `Offline`/`Online`/`Idle`/`Farm`/`Boost` | `actions.md` § 编排 payload 文档、`getting-started.md` | 一致 |
| 7 | Agent 注册序列（bearer → WS 升级 → `agentId`/`region` → hello 或 `PolicyViolation`） | `api.md` §3 握手 | 一致 |
| 8 | SSE 机制：有界 256 `DropOldest`、无重放、管理者视图上 `code` 被剥离 | `api.md` §2 | 一致 |
| 9 | 调和器放置：先钉定 Agent，否则区域+capability+容量，最空闲并以 Agent id 决胜 | `roadmap.md` §2、`DesiredStateReconciler.PickAgent` | 一致 |
| 10 | 插件生命周期与 SemVer API 门禁；库存镜像是尽力而为 | `plugins.md` § 生命周期 / 信任 | 一致 |
