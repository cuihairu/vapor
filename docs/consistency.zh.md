简体中文 | English

# Vapor 一致性模型

本文档陈述 Vapor 实际给出的一致性与交付保证 —— 子任务交付、状态所有权、故障与重启语义 —— 以及同样重要的：它**不**保证什么。这里的一切都对照源码（`src/Vapor.ControlPlane`、`src/Vapor.Agent`、`src/Vapor.Steam.Core`）核对；各节附文件锚点。配套文档：[`architecture.md`](architecture.md)（组件设计与权衡）、[`roadmap.md`](roadmap.md)（能力地图）。

本模型建立在一个决策之上（见 [architecture.md § Control plane](architecture.md)）：**控制平面是唯一的写者与唯一的状态所有者**。一个进程拥有这些 SQLite 存储；对存储的每一次写入都在进程内串行化（每存储一个互斥锁/锁）。没有跨实例协调、没有法定人数、没有领导者选举 —— 事务故事就是“一个写者、一把锁、一个文件”。下文的一切都由此推出。

---

## 1. 状态所有权地图

| 状态 | 所有者 | 存储 | 持久性 |
|-------|-------|-------|------------|
| 任务、子任务、计划 | 控制平面 | SQLite（`SqliteJobStore`） | CP 重启后存活 |
| 账号规格（期望状态） | 控制平面 | `AccountStore`（内存读路径）写穿到 SQLite（`SqliteConfigStore`） | CP 重启后存活 |
| 审计日志 | 控制平面 | SQLite（`SqliteAuditStore`） | CP 重启后存活 |
| 抓取计划 / 结果 | 控制平面 | SQLite（`SqliteCrawlStore`） | CP 重启后存活 |
| 脚本仓库 | 控制平面 | SQLite（`SqliteScriptStore`） | CP 重启后存活 |
| Webhook 投递日志 | 控制平面 | SQLite（`SqliteWebhookDeliveryStore`） | CP 重启后存活 |
| 全局 / 每账号设置 | 控制平面 | `ConfigStore`（内存读路径）写穿到 SQLite（`SqliteConfigStore`） | CP 重启后存活 |
| Agent 注册表（谁已连接） | 控制平面 | 内存 `AgentRegistry` | 由 Agent 重新 hello 重建 |
| 会话快照 | 控制平面 | 内存 `SessionTracker`（Agent 上报） | 由 Agent 会话事件重新填充 |
| 认证挑战 | 控制平面 | 内存 `AuthChallengeTracker` | 按设计为瞬时 |
| 插件库存镜像 | 控制平面 | 内存 `PluginInventory` | 由子任务结果重建 |
| Steam 会话 / 凭据 | **Agent** | 加密的本地存储（AES-GCM） | 从不离开 Agent |
| 活跃 Steam 连接 | Agent | 每账号 CM/Web | Agent 本地；重启后令牌恢复 |

这一拆分是刻意的：任何不可丢失的东西都存在控制平面的 SQLite 里；任何可推导的东西（注册表、会话、库存）都在内存中，靠收敛重建。凭据是例外 —— 它们根本从不抵达控制平面（`architecture.md` § Security）。

## 2. 子任务：状态机、租约、至少一次交付

### 生命周期

```
Queued ──claim──▶ Running ──result ok──▶ Finished
   ▲                 │  │
   │                 │  └─result fail─▶ Failed (terminal; error + output kept)
   │                 │
   │   lease expired (no heartbeat within TaskLeaseSeconds, default 300 s)
   └─────────────────┘
   Queued ──cancel──▶ Canceled        Queued ──attempts exhausted──▶ Failed
```

锚点：`SqliteJobStore.ClaimNextQueuedTask`、`RequeueStaleRunningTasks`、`SetTaskResult`、`CancelJob`（`src/Vapor.ControlPlane/SqliteJobStore.cs`）。

### 认领与围栏

- **认领是原子的。** `ClaimNextQueuedTask` 在存储互斥锁内把一个 `Queued` 子任务翻转为 `Running` 并递增 `attempt` —— 两个派发者永远无法持有同一个 (task, attempt)。
- **Attempt 围栏执行与上报。** 心跳仅在 `status = Running AND attempt = $attempt` 时延长租约（`HeartbeatTask`）；来自已被取代的 attempt 的心跳是一个返回 `false` 的空操作。结果仅在子任务仍处于 `Running` 且 attempt 匹配时写入（`SetTaskResult` 在 "task not running" / "task attempt mismatch" 时抛出 `NotFoundException`）。租约已被回收的 attempt 的迟到报告被**拒绝而非应用** —— 被回收的子任务收敛到恰好一个终态，由控制平面当前认定拥有它的那个 attempt 写入。
- **交付是至少一次的。** 若子任务的租约失效（Agent 崩溃、分区、进程丢失），`RequeueStaleRunningTasks` 把它放回 `Queued`，调度器再次派发它 —— 可能派给另一个 Agent，也可能在原 Agent 仍在执行它时。attempt 编号正是让它安全的东西（见下文的幂等契约）。

### 重试与退避

- 派发失败（没有具备能力的 Agent、入队失败）以 `TaskDispatchRetryDelayMs`（默认 2000 ms）作为下一次尝试的门（`next_attempt_at_ms`）重试，最多到 `TaskMaxDispatchAttempts`（默认 10；`<= 0` = 无限）后子任务永久失败。自执行安全分级起，实际上限按动作收口：`GuardedWrite` / `NonIdempotent` 动作 —— 以及任何没有分级的动作（`Unknown`，例如第三方插件动作）—— 无论配置上限如何都封顶共 2 次派发尝试，因此可能使外部副作用翻倍的再投递永远不会在配置上限处空转；`ReadOnly` / `Idempotent` 动作原样使用配置上限。
- 子任务级取消经隧道以 `(taskId, attempt)` 为键的 `task_cancel` 从 CP 流向 Agent；Agent 抑制过期子任务的报告，且存储已记录该取消。

### 幂等契约

至少一次交付意味着**一个动作可能执行两次**（租约回收后原始 attempt 仍存活）。让这变得安全的契约，按动作类别：

- **读**（`get_inventory`、`get_trade_offers`、`get_achievements`、…）天然幂等 —— 重新执行重新读取同一真相。
- **Steam 侧状态检查**（挂机队列、boost 计划、信誉检查）从 Steam 自身状态重新计算，因此重复执行收敛到同一结果。
- **写**走带防护的路径：市场挂单创建是双重门控（账号规格 opt-in + Agent 侧开关），交易操作处于每账号交易安全层（滑动窗口配额 + 并发闸）之后，成就写 API 要求显式的名称清单（重置还要 `confirm: true`）。这些门限制重复的波及面；它们不使写成为恰好一次。
- **子任务输出中可见的效果即执行记录** —— 控制平面保留终态 attempt 的输出，它是运维人员对实际运行内容的审计线索。

### Agent 侧看门狗交互

Agent 的子任务看门狗（`AGENT_TASK_TIMEOUT_SECONDS`，默认 900 s）把执行约束在 CP 租约之下：挂起的子任务会在控制平面将要回收租约*之前*被取消并以结构化失败上报，因此正常路径从不触发租约回收再投递。租约回收仍是 Agent 丢失的安全网（见 [architecture.md § Agent task loop](architecture.md)）。

## 3. 账号规格：单调版本、收敛而非 CAS

账号期望状态（`AccountStore`）是一个全量替换的 PUT 存储：

- 写入由存储锁串行化；API 边界没有比较并交换式的拒绝。**每次 PUT 都胜出**并递增单调的 `ConfigVersion`（版本、时间戳、更新人）。
- 版本是**收敛触发器**：期望状态调和器把每个账号的运行时 `SpecVersion` 与规格的当前版本比较，不匹配则重新收敛（`DesiredStateReconciler.cs`）。变更检测是“版本动了”，不是“字段被 diff”。
- 读总是返回最新已提交的规格；因为存在单写者与进程内串行化，规格存储本身没有过期读窗口。

调和器循环正是把 PUT 变成收敛的东西：它**按设计是最终一致的** —— PUT 立即返回，期望状态（登录、派发、交易策略、挂机队列）在下一次调和趟（`ReconcileIntervalSeconds`，默认 15 s）中物化。审计条目记录每一次规格变更及其版本。

## 4. 会话与可观测性状态：最终一致、尽力而为

- **会话快照**（`SessionTracker`）由 Agent 上报并异步收敛；调和器把早于 `ReconcileSessionStalenessSeconds`（默认 120 s）的快照视为过期而非权威。缺失的快照是“未知”，从不是“离线” —— Agent 的隧道连接才是存活来源。
- **SSE 流**是面向 UI 的尽力而为事件投递，不是持久日志：没有订阅者附着时发布的事件不会被重放（SSE 订阅者从订阅时刻起获取事件）。发生了什么的持久记录在 SQLite 存储（子任务、审计）里，不在事件总线上。
- **指标**是进程生命周期的计数器；重启即清零。持久历史在存储里。

## 5. 缓存：stale-while-revalidate 与 single-flight

`IVaporCache` 实现（`src/Vapor.Steam.Core/Caching` 中的 `MemoryVaporCache`、`RedisVaporCache`）提供带可选过期窗口的 TTL 缓存：

- TTL 之内：命中。超过 TTL 但在 `staleTtl` 之内：**过期命中** —— 立即返回旧值，同时一次 single-flight 刷新重新读取源（同键的其他调用者在那一次刷新上等待，而不是蜂拥冲击 Steam）。超出过期窗口：未命中。
- Redis 是可选的（`VAPOR_REDIS` 连接串；否则内存）；有它时，过期语义在共享该 Redis 实例的各 Agent 进程间成立。没有它时，缓存是每 Agent 一份。
- 缓存过期只关乎 **Steam 数据** —— 控制平面自身的任何状态（任务、规格、审计）都不流经这一层。

## 6. 故障与重启语义

### 控制平面重启

- 存活：任务、子任务、计划、审计、抓取状态，以及已声明的挂机农场 —— 账号规格与设置（全部 SQLite；规格/设置写穿持久化并在启动时重新水合，因此重启不再需要重新声明账号）。这些文件是控制平面的全部持久状态 —— 复制它们（见 production.md，“数据、备份与升级”）就是完整备份，恢复一套稍旧的也安全：期望状态会重新收敛。
- 丢失并重建：Agent 注册表（Agent 以其指数退避重连并重新 hello）、会话快照（Agent 重新上报）、挑战提示、插件镜像（由子任务结果重建）、限流器窗口（全新开始 —— 重启会重置限流）、API 指标计数器。
- 进行中的子任务：执行中的 Agent 继续运行并继续心跳；重连时心跳落在恢复的存储行上。租约在停机期间完全失效的子任务会被回收并再投递（至少一次，如上）。编排暂停于重启期间；Agent 通过令牌恢复保持其 Steam 会话存活。

### Agent 重启

- 凭据/会话令牌从加密的本地存储恢复；会话无需运维动作即重建。
- 崩溃时它持有的任何子任务经 CP 租约到期重新入队并被再派发（attempt +1）。

### 网络分区（Agent ↔ 控制平面）

- Agent 继续执行它当前的子任务（其取消源是本地的；看门狗仍约束它）。但结果报告只在活跃 socket 上发送 —— 没有 Agent 侧的发件箱。若隧道在报告时刻是断的，报告即丢失，**控制平面的租约回收是恢复路径**，而非 Agent。
- 控制平面看到心跳停止；`TaskLeaseSeconds` 之后子任务重新入队，可能被派往别处。若被分区的 Agent 稍后重新拿到 socket，而子任务仍处于 `Running` 且 attempt 未变，其报告正常落账；若期间租约已被回收，attempt 围栏拒绝过期结果。**两个 Agent 都可能把活干一遍；控制平面只记录一个结果** —— 这是至少一次的可见代价，也是写路径门存在的原因。

## 7. 保证与非保证

**保证：**

1. 所有控制平面状态单写者；每存储串行化的读-改-写。
2. 原子的子任务认领；至多一个活跃的 (task, attempt) 对。
3. 至少一次的子任务*交付*，由 attempt 围栏；每个子任务恰好一条终态记录。
4. 账号规格上的全量替换 PUT 语义，带单调版本与审计条目；在下一次调和趟收敛。
5. 每个子任务结果、派发失败与审计动作在 SQLite 中的持久记录。

**不保证（刻意）：**

1. **恰好一次执行。** 代之以至少一次 + 幂等契约；当原始 attempt 仍存活时，被回收的子任务可能重复执行。
2. **多实例控制平面。** 没有 HA、没有水平写扩展、没有外部注册表 —— 一个明确的权衡（`architecture.md`）；重启 CP 会短暂暂停编排。
3. **事件重放。** SSE 只做实时尾随；存储才是历史。
4. **跨存储事务。** 任务、规格、审计与抓取是独立的存储；审计写失败不回滚 API 响应（审计持久化失败只记录日志，从不阻塞）。
5. **对 Steam 本身的读己之写。** Steam 自身的状态（交易报价、库存、游戏时长）经由同样的缓存与调和循环被最终观测到。

---

*反馈回路：*当新的状态所有者、交付路径或故障模式落地时，在同一变更中更新此处相关章节与 [`roadmap.md`](roadmap.md) §9 的对应行。
