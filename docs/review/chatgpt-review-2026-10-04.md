> 来源：用户 2026-10-04 提供的 ChatGPT 审核分享（https://chatgpt.com/s/t_6ac1d270fa5881919520a45b7d6aebee）。

## 追问

- 有人觉得你可能会想看看这段聊天。
- Croupier  概览  简介  指南  架构  API  开发  SDK  On this page  文档分层 ​  * `guide/`: 上手、配置、部署 * `architecture/`: 架构分层与数据流 * `api/`: REST/API 能力说明 * `analytics/`: 采集、处理、指标与分析链路 * `development/`: 仓库结构、开发命令、约定 * `s...
- 🗓️ 迭代计划（已校准）  SPRINT A (2周) — RBAC 权限系统 ✅ 已完成 (2026-06-20)  * [Input] Role/Permission 模型 + AutoMigrate * [Input] 用户/角色管理 API + PermissionRequired 中间件 * [Input] Dashboard 权限页面 (Admin/Users、Admin/Roles)...
- 规划Vapor未来测试体系中的故障注入测试
- 请帮我设计一个针对Vapor系统的故障注入测试计划，列举关键场景（如Agent断线、任务超时、数据库损坏等），并说明如何验证系统的高可用和一致性。
- 请帮我详细阐述如何给Vapor的Action增加执行语义，包括执行安全性分类、重试策略和幂等性处理，以帮助Scheduler做出更智能的调度决策。
- 请详细解释Vapor插件系统目前的局限性以及如何演进为完整的插件平台，尤其是安全边界和生命周期管理方面的改进方案。

---

## 审核正文

我看了你这个仓库，不是泛泛看 README，而是结合了 `architecture.md`、`consistency.md`、`session-engine.md`、`plugins.md`、`actions.md` 以及当前仓库结构一起看。urlcuihairu/vaporhttps://github.com/cuihairu/vapor

先给结论：

> **Vapor 现在已经不是一个“ASF 的简单 C# 重写”，而是一个比较完整的“Steam 自动化控制平面 + 区域 Agent + Session Runtime + Plugin Runtime”系统。**
>
> 它目前最大的价值已经从“实现多少 Steam Action”转移到了 **分布式任务编排、Agent 管理、Session 生命周期、插件体系、安全边界和一致性模型**。

但它现在也有一个很明显的问题：

> **基础设施已经做得比较深，产品边界反而有些散。**

我认为下一阶段不应该继续无脑增加 Action，而应该把 Vapor 的 **核心模型、任务系统、Agent、Plugin、Session、Security、UI/Observability** 再收紧一次。

---

# 1. Vapor 到底是什么

现在最准确的定位不是：

> Steam Bot Framework

也不只是：

> ArchiSteamFarm alternative

而应该是：

> **A self-hosted control plane for distributed, headless Steam account automation.**

也就是：

```text
                    Vapor Control Plane
                           │
             ┌─────────────┼─────────────┐
             │             │             │
          REST API       Scheduler      UI
             │             │             │
             └─────────────┼─────────────┘
                           │
                    Task / Job Model
                           │
                    Agent Tunnel
                           │
        ┌──────────────────┼──────────────────┐
        │                  │                  │
     Agent CN           Agent JP           Agent EU
        │                  │                  │
   Session Engine     Session Engine     Session Engine
        │                  │                  │
      Steam              Steam              Steam
```

这个架构方向其实是对的。

尤其是你把：

**Control Plane**

和

**Data Plane / Agent**

分开。

这比传统：

```text
一台机器
  ↓
一个 ASF
  ↓
一堆 Bot
```

更容易继续扩展。

你当前架构文档也明确把 Control Plane 定义成状态拥有者，把 Agent 定义成执行面。citeturn1view1turn2view0

---

# 2. 我认为 Vapor 目前最强的地方

## 2.1 Control Plane / Agent 分离是正确的

这是整个项目最值得保留的设计。

Agent 主动向 Control Plane 建立 outbound WebSocket：

```text
Agent ──────── outbound WSS ────────> Control Plane
```

而不是：

```text
Control Plane ────────> Agent
```

这带来几个好处：

- Agent 不需要开放公网端口
- NAT 后面的机器可以工作
- 不同地区可以部署 Agent
- Steam 网络出口可以靠 Agent 决定
- Control Plane 可以统一调度
- Agent 可以随时上下线
- Agent 可以部署在不同云厂商/网络环境

这一点已经非常接近成熟的 distributed worker architecture。

---

# 3. Job → Task → Agent → Session → Action 这个抽象很好

这是 Vapor 目前最漂亮的一层：

```text
Job
 │
 ├── Task(account A)
 │      │
 │      └── Agent
 │            │
 │            └── Session
 │                  │
 │                  └── Action
 │
 ├── Task(account B)
 │
 └── Task(account C)
```

也就是说：

### Job

描述：

> 我要对一批账号执行某个事情。

### Task

描述：

> 对具体账号执行。

### Agent

描述：

> 谁来执行。

### Session

描述：

> 账号当前的 Steam 长连接状态。

### Action

描述：

> 实际干什么。

这个层次非常合理。

当前文档里也已经明确规定了 Job 拆 Task、Task 路由 Agent、Agent 找 Session、Session 执行 Action 的执行链路。citeturn2view0

**这个抽象建议不要推倒重来。**

---

# 4. Desired State 是一个非常值得继续强化的设计

这个我认为甚至比 Job 系统更重要。

现在 Vapor 已经不是单纯：

```text
POST /jobs
执行一次
结束
```

而是：

```text
Account Desired State

        ↓

Reconciler

        ↓

Actual State
```

例如：

```text
Account A

desired:
    enabled = true
    state = farm
    region = jp

actual:
    disconnected
```

然后：

```text
Reconciler
    ↓
找 Agent
    ↓
创建 Session
    ↓
登录
    ↓
进入 farm
```

这实际上已经进入了 **controller/reconciler architecture**。

当前文档明确把 Account Spec 的 `ConfigVersion` 作为 convergence trigger，并通过 reconciler 最终让实际 Session 状态收敛到 desired state。citeturn4view3

### 这是 Vapor 后面最应该发展的核心。

我甚至建议把项目理念明确成：

> **Vapor is a desired-state automation system for Steam accounts.**

而不是：

> Steam bot API.

---

# 5. Consistency 文档做得非常好

这个项目我比较满意的一点，就是你没有停留在：

> “支持重试、支持 HA、支持 Agent。”

而是真的开始描述：

```text
谁拥有状态？
谁写？
什么时候持久化？
失败怎么办？
重复执行怎么办？
Agent 死了怎么办？
Control Plane 重启怎么办？
```

例如现在已经明确：

```text
Control Plane
    ↓
SQLite
    ↓
single writer
```

同时：

```text
Agent Registry
Session Tracker
Auth Challenge
Plugin Inventory
```

这些属于：

```text
ephemeral / reconstructable state
```

而：

```text
Jobs
Tasks
Schedules
Account Specs
Audit
```

属于：

```text
durable state
```

这个分类是正确的。citeturn4view0

---

# 6. Task Lease + Attempt fencing 很专业

这里其实已经不是普通 CRUD 项目了。

现在是：

```text
Task
  ↓
Claim
  ↓
attempt = 1
  ↓
Running
```

如果 Agent 死掉：

```text
lease expired
       ↓
attempt = 2
       ↓
重新执行
```

旧 Agent 如果后来回来：

```text
attempt = 1
      ↓
report result
      ↓
reject
```

因为：

```text
taskId + attempt
```

作为 fencing。

这个设计非常重要。

当前 consistency 文档明确规定了：

- atomic claim
- lease
- heartbeat
- attempt fencing
- stale task reclaim
- at-least-once delivery

这些都已经落到了实现语义里，而不是停留在架构图。citeturn4view1turn4view2

**这是应该保留并继续加强的核心。**

---

# 7. 但这里有一个非常重要的问题

你现在文档说：

> at-least-once delivery

这是正确的。

但是：

> **at-least-once ≠ safe exactly-once**

你自己文档也承认这一点。

例如：

```text
Task attempt 1

Agent A
   ↓
Steam write
   ↓
成功

但 Agent A 没来得及 heartbeat
   ↓
CP 认为死了

Task attempt 2
   ↓
Agent B
   ↓
再次执行
```

所以：

```text
task fencing
```

只能保证：

> **Control Plane 状态不会被旧 attempt 覆盖。**

不能保证：

> **外部 Steam side effect 不会发生两次。**

你当前文档已经非常诚实地说明了这一点。citeturn4view2

---

# 8. 下一步应该建立 Action Semantics

这是我认为 Vapor 当前最值得补的一层。

现在：

```csharp
IAction
```

主要描述：

```text
Name
Metadata
ExecuteAsync()
```

但以后 Action 最好增加：

```text
Execution Semantics
```

例如：

```text
ReadOnly
IdempotentWrite
NonIdempotent
Interactive
LongRunning
Compensatable
```

甚至：

```text
ActionSafety
    ReadOnly
    Idempotent
    GuardedWrite
    NonIdempotent
```

例如：

```text
get_inventory
    → ReadOnly

set_idle
    → Idempotent

create_listing
    → GuardedWrite

send_trade_offer
    → NonIdempotent
```

这样 Scheduler 才知道：

```text
retry = safe?
lease reclaim = safe?
parallel execution = safe?
```

这比简单的：

```text
RequiresLogin
TimeoutSeconds
```

重要得多。

---

# 9. Job 系统现在已经很强，但还可以进一步抽象

目前已经支持：

```text
one-shot job
scheduled job
interval
cron
missed policy
overlap policy
```

这已经不错。citeturn2view1

但我建议下一步把：

```text
Job
Task
Schedule
Execution
Attempt
```

彻底分离概念。

推荐：

```text
JobDefinition
      │
      ├── Schedule
      │
      └── Execution
              │
              ├── Task
              │     ├── Attempt
              │     └── Result
              │
              └── Events
```

这样以后：

```text
Recurring Job
Manual Job
Triggered Job
Webhook Job
Event-triggered Job
```

都可以统一。

---

# 10. 现在最值得改的是“Action Catalog”

你现在已经有 **57 个 shipped actions**，文档甚至已经直接从源码生成/核对 Action catalog。citeturn2view5

这很好。

但我建议：

> **不要再把 Action 数量当作 Vapor 的主要成长指标。**

否则很容易变成：

```text
Vapor
 ├── 57 actions
 ├── 80 actions
 ├── 120 actions
 ├── 200 actions
```

最后变成一个巨大的 Steam API wrapper。

这不是你现在这个架构真正的价值。

---

# 11. Action 应该变成 Capability

建议模型：

```text
Capability
    │
    ├── metadata
    ├── input schema
    ├── output schema
    ├── permission
    ├── execution semantics
    ├── retry policy
    ├── timeout
    ├── required session state
    └── side-effect classification
```

然后：

```text
Action
```

只是 Capability 的一个执行实例。

例如：

```yaml
name: market.create_listing

requires:
  - steam.session
  - inventory

permission:
  - market.write

execution:
  semantics: guarded-write
  idempotency: external-key
  timeout: 30s

safety:
  dry_run: true
```

这样未来 Plugin、UI、API、Scheduler 都可以读取同一份 metadata。

---

# 12. Plugin 系统方向很好，但现在还不够“真正的 Plugin Platform”

你现在已经有：

```text
plugin.json
    ↓
compatibility
    ↓
trust
    ↓
permissions
    ↓
AssemblyLoadContext
    ↓
actions / commands / routes / events
```

甚至是 collectible ALC，支持 unload。citeturn4view4

这是很好的基础。

但是目前 Plugin 更像：

> **Agent extension mechanism**

还不是：

> **完整插件生态。**

下一步建议增加：

```text
Plugin
 ├── Manifest
 ├── Dependencies
 ├── Capabilities
 ├── Permissions
 ├── Configuration schema
 ├── Version
 ├── Compatibility
 ├── Health
 ├── Lifecycle
 ├── Resource limits
 └── Upgrade / rollback
```

---

# 13. Plugin 最大的问题：安全边界实际上没有真正隔离

这个非常重要。

现在：

```text
AssemblyLoadContext
```

主要解决的是：

> assembly isolation / unloading

并不是：

> security sandbox

也就是说 Plugin 本质上还是：

```text
.NET code
    ↓
同一个 Agent 进程
```

恶意插件理论上仍然可能访问：

- 文件
- 网络
- Environment
- Process
- Reflection
- Host services

所以：

> **不要把 ALC 描述成真正的安全隔离。**

应该明确叫：

```text
load isolation
```

而不是：

```text
security sandbox
```

如果未来真的允许第三方 Plugin：

```text
Plugin Process
       ↓
Plugin RPC
       ↓
Agent
```

才是真正更强的安全边界。

不过我建议 **现阶段不要马上做进程化 Plugin**，因为复杂度非常高。

---

# 14. Session Engine 是 Vapor 的真正 Runtime Kernel

这个部分我认为应该进一步提升地位。

现在：

```text
SessionManager
      ↓
BotSession
      ↓
SteamClientManager
      ↓
SteamKit2
```

BotSession 负责：

- 状态机
- command queue
- events
- reconnect
- errors

这个模型是对的。citeturn2view3

我建议以后把它明确成：

```text
Steam Runtime

Session
  │
  ├── Connection
  ├── Authentication
  ├── State
  ├── RateLimit
  ├── CommandQueue
  ├── Capabilities
  ├── Credentials
  ├── Events
  └── Lifecycle
```

然后 Action 只是：

```text
Session.execute(Action)
```

而不是 Action 自己大量处理 Session 生命周期。

---

# 15. Session 与 Account 要继续严格区分

这里建议你特别检查代码。

应该明确：

```text
Account
    =
Steam identity / desired configuration

Session
    =
当前运行时连接

Agent
    =
Session 的运行位置

Task
    =
一次执行请求
```

也就是：

```text
Account
  │
  ├── desired state
  │
  └── may have
          │
          └── Session
                │
                └── hosted by Agent
```

不能变成：

```text
Account == Session
```

否则以后：

- Session migration
- reconnect
- Agent failover
- multi-session
- temporary session

都会变得很麻烦。

---

# 16. Region 的定义也应该更清楚

现在 Region 同时承担：

```text
地理区域
网络出口
Agent grouping
routing hint
```

建议以后明确：

```text
Region
    ↓
Placement Policy
```

例如：

```text
region = jp
network = steam-jp
proxy = xxx
labels:
    steam-market=true
    low-latency=true
```

最终 Scheduler 不应该写死：

```text
region == eu-west
```

而应该：

```text
PlacementPolicy
    ↓
Agent capabilities
    ↓
Agent selection
```

这样更通用。

---

# 17. Agent Registry 可以进一步升级为 Capability Registry

现在 Agent：

```text
id
region
connected
capabilities
lastSeen
```

这个方向是对的。

但是建议能力模型做成：

```text
AgentCapability

steam
    ├── login
    ├── market
    ├── trade
    └── game_access

plugins
    ├── mobile_authenticator
    ├── market_watch
    └── ...
```

然后调度：

```text
Task requirements
       ↓
Capability matcher
       ↓
Eligible agents
       ↓
Load / health / region
       ↓
Select agent
```

这样：

> **Agent 就真正变成一个可调度的 Worker。**

---

# 18. Observability 已经不错，但还需要一个统一的 Execution ID

现在已经有：

- structured logs
- Prometheus
- Grafana
- OpenTelemetry
- SSE
- webhook

这些基础设施已经比较完整。citeturn1view0

但是建议所有链路统一：

```text
job_id
task_id
attempt
agent_id
account_id
session_id
action_id
trace_id
```

形成：

```text
Job
 ↓
Task
 ↓
Attempt
 ↓
Agent
 ↓
Session
 ↓
Action
 ↓
Steam Request
```

然后任何一个错误都可以：

```text
trace_id
```

一路查下来。

---

# 19. UI 不应该只是 Admin Panel

现在：

```text
/admin.html
/dashboard.html
/gamedata.html
```

已经有雏形。citeturn2view1

但我认为 Vapor 真正应该做的是一个：

> **Operations Console**

核心不是“创建 Job”。

而是：

```text
                    Vapor Console

Overview
────────────────────────────────────────
Agents        8/8 healthy
Accounts      132
Sessions      119
Jobs          12 running
Tasks         4,231
Failures      3
Auth          2 pending

Agents
────────────────────────────────────────
JP-01     31 sessions    healthy
JP-02     28 sessions    healthy
EU-01     30 sessions    healthy

Accounts
────────────────────────────────────────
Account
Desired
Actual
Agent
Session
Last Error

Jobs
────────────────────────────────────────
Running
Queued
Failed
Scheduled
```

尤其应该做到：

> **Desired State vs Actual State**

例如：

```text
Account 123

Desired:
    Farm

Actual:
    Disconnected

Reason:
    Agent unavailable

Action:
    Reconcile
```

这会非常有价值。

---

# 20. 现在最大的架构限制：Control Plane 单实例

这是当前最明显的天花板。

你现在明确采用：

```text
single writer
SQLite
one process
```

这个设计对于：

```text
个人
小团队
self-hosted
几十/几百 Agent
```

完全合理。

而且我反而建议：

> **暂时不要为了所谓“分布式”而把它搞复杂。**

当前 consistency 模型就是：

```text
one writer
one lock
one SQLite
```

这个模型简单、可靠、容易备份。citeturn4view0

但是需要明确：

### Vapor 当前不是 HA Control Plane。

而是：

> **Highly available-ish Agent fleet + single durable Control Plane**

未来如果真的需要：

```text
1000+ agents
10000+ accounts
multiple operators
HA control plane
```

再考虑：

```text
PostgreSQL
Redis
NATS JetStream
distributed scheduler
leader election
```

不要提前引入。

---

# 21. SQLite 我反而建议继续保留

这是一个容易误判的地方。

不要因为看到：

```text
SQLite
```

就认为架构不够专业。

你的产品定位是：

> self-hosted automation

SQLite 非常合适。

尤其：

```text
Control Plane
    ↓
SQLite
```

可以直接：

```text
backup file
```

而 consistency 文档已经明确说明整个 CP durable state 都可以通过数据库文件恢复，并重新 convergence。citeturn4view3

这是一个很好的 self-hosted 特性。

建议未来做：

```text
vapor backup
vapor restore
vapor verify-backup
```

而不是急着 PostgreSQL。

---

# 22. Secret Architecture 是正确方向，但还能更进一步

现在：

```text
Credentials
     ↓
Agent encrypted store
```

而不是：

```text
Control Plane
     ↓
password
     ↓
Agent
```

这是非常正确的。

架构文档明确要求 Steam credentials / refresh tokens / 2FA seeds 不通过 API 暴露，并让 Secret 留在 Agent。citeturn2view0

建议进一步定义：

```text
Secret
 ├── ownership
 ├── encryption key
 ├── version
 ├── rotation
 ├── expiration
 ├── scope
 └── audit
```

尤其是：

```text
account secret version
```

这样以后：

```text
password rotated
Steam Guard changed
session invalidated
```

可以有明确的版本语义。

---

# 23. Auth Challenge 应该成为正式的 Domain Object

现在已经支持：

```text
Steam Guard
2FA
TOTP
QR
manual challenge
auto responder
```

而且 challenge 现在主要是 transient state。citeturn2view1

我建议正式抽象：

```text
AuthChallenge
    id
    account
    session
    type
    createdAt
    expiresAt
    status
    attempt
    source
```

状态：

```text
Pending
Answered
Expired
Cancelled
Failed
```

这样 UI / Agent / API 都围绕一个统一模型。

---

# 24. 另一个很值得做的是 Event Model

现在 SSE 是：

> best effort

这一点是正确的。它不应该承担 durable event log。citeturn4view3

但是建议把：

```text
Event
```

正式分成：

### Domain Event

```text
TaskFinished
SessionConnected
AuthChallengeCreated
AccountStateChanged
AgentConnected
```

### Operational Event

```text
AgentHeartbeat
Retry
Timeout
Reconnect
```

### Audit Event

```text
AccountChanged
SecretRotated
TradePolicyChanged
PluginInstalled
```

然后：

```text
Event
   ↓
SSE
Webhook
Log
Audit
Metrics
```

统一出来。

---

# 25. Webhook 很适合继续强化

现在已经有 HMAC webhook。

建议：

```text
WebhookSubscription
    ↓
Event Filter
    ↓
Delivery
    ↓
Retry
    ↓
Backoff
    ↓
Signature
    ↓
Delivery Record
```

并且明确：

```text
at-least-once
```

而不是：

```text
exactly-once
```

Webhook consumer 自己通过：

```text
event_id
```

去重。

---

# 26. 测试体系是 Vapor 下一阶段必须重点投入的

现在已经有相当多测试，而且文档明确提到 Actions、BotSession、SessionManager、SteamClientManager 等都有测试。citeturn2view3

但我建议增加一个非常重要的：

# Failure Injection Test

例如：

```text
Agent disconnect
CP restart
network partition
task timeout
lease expiry
duplicate result
late result
old attempt result
session reconnect
Steam login challenge
plugin unload
plugin crash
scheduler restart
SQLite corruption simulation
```

尤其是：

```text
Task attempt 1
     ↓
network partition
     ↓
lease expire
     ↓
attempt 2
     ↓
attempt 1 reconnect
     ↓
late result
```

这种测试才真正验证你现在 consistency.md 写的东西。

---

# 27. 建议增加一个“Deterministic Fake Steam”

不要所有测试都依赖真实 Steam。

可以做：

```text
FakeSteam
    ├── login
    ├── disconnect
    ├── inventory
    ├── market
    ├── trade
    ├── challenge
    ├── timeout
    └── error
```

然后：

```text
BotSession
       ↓
ISteamTransport
       ↓
FakeSteam
```

这样可以做：

```text
1000 accounts
10000 tasks
100 agents
```

的 deterministic test。

这个对 Vapor 的价值非常高。

---

# 28. Performance 测试应该从“Action benchmark”升级成“System benchmark”

建议定义标准场景：

```text
Scenario A
100 accounts
10 agents

Scenario B
1,000 accounts
20 agents

Scenario C
10,000 accounts
100 agents
```

测：

```text
task throughput
task dispatch latency
scheduler latency
SQLite write latency
agent tunnel throughput
session memory
session CPU
event throughput
SSE fanout
reconcile latency
```

最终 README 给：

```text
Vapor capacity profile
```

比“支持多少 Action”更有价值。

---

# 29. 当前 README 有一个明显的小问题

README 说：

> 37+ actions

但 `actions.md` 现在已经写成：

> 57 shipped actions

这是文档同步问题。citeturn1view0turn2view5

应该改成自动生成：

```text
57 actions
```

或者：

```text
50+ capabilities
```

更好。

因为你未来继续增加 Action，不应该手工维护 README 数字。

---

# 30. Vapor 还有一个定位问题：ASF-inspired 不应该成为品牌核心

现在：

```text
Vapor (ASF-inspired)
```

这个对于熟悉 ASF 的开发者很好。

但是如果长期做开源项目：

> ASF-inspired

会让用户第一眼认为：

> “这是另一个 ASF。”

而实际上你的架构已经明显不同：

```text
ASF

Machine
 └── Bots


Vapor

Control Plane
 ├── Jobs
 ├── Scheduler
 ├── Desired State
 ├── Audit
 └── Agents
       ├── Sessions
       └── Plugins
```

所以我建议 README 第一定位改成：

> **Vapor — Distributed Steam Automation Control Plane**

第二句话再说：

> Inspired by ArchiSteamFarm's Bot/Action model.

这样品牌会更清楚。

---

# 31. Vapor 最值得保留的核心模型

如果让我给你画一个最终版，我会把 Vapor 压缩成下面这张图：

```text
                         Vapor
                           │
                 ┌─────────┴─────────┐
                 │                   │
          Control Plane           Agent
                 │                   │
        ┌────────┼────────┐          │
        │        │        │          │
      API    Scheduler  Reconciler   │
        │        │        │          │
        └────────┼────────┘          │
                 │                   │
              Job / Task             │
                 │                   │
                 └───────┬───────────┘
                         │
                    Agent Tunnel
                         │
                   ┌─────┴─────┐
                   │           │
                Session     Plugin
                   │           │
                   └─────┬─────┘
                         │
                     Action
                         │
                    Steam API
```

其中真正的核心只有：

```text
Desired State
      ↓
Reconciliation
      ↓
Job / Task
      ↓
Agent
      ↓
Session
      ↓
Action
```

其他东西都是围绕这个核心服务。

---

# 32. 我给 Vapor 当前的评价

| 维度 | 当前评价 |
|---|---:|
| 产品定位 | ⭐⭐⭐⭐ |
| Control Plane | ⭐⭐⭐⭐⭐ |
| Agent 架构 | ⭐⭐⭐⭐⭐ |
| Job/Task | ⭐⭐⭐⭐⭐ |
| Desired State | ⭐⭐⭐⭐⭐ |
| Consistency | ⭐⭐⭐⭐⭐ |
| Session Engine | ⭐⭐⭐⭐½ |
| Plugin | ⭐⭐⭐⭐ |
| Security | ⭐⭐⭐⭐ |
| Observability | ⭐⭐⭐⭐½ |
| UI | ⭐⭐⭐½ |
| 测试 | ⭐⭐⭐⭐ |
| 性能体系 | ⭐⭐⭐ |
| 文档 | ⭐⭐⭐⭐½ |
| 长期架构潜力 | ⭐⭐⭐⭐⭐ |

整体我会给：

> **8.5/10**

而且这不是“代码完成度 8.5”。

而是：

> **架构方向已经达到可以继续长期建设的程度。**

---

# 33. 我认为现在最应该做的，不是继续堆功能

按照优先级，我会这样排：

## P0 — 定义核心模型

明确：

```text
Account
Session
Agent
Job
Task
Attempt
Action
Capability
Event
Plugin
```

每个对象：

```text
Owner
Lifecycle
Persistence
Identity
Consistency
```

全部写清楚。

---

## P0 — Action Execution Semantics

给 Action 增加：

```text
ReadOnly
Idempotent
GuardedWrite
NonIdempotent
Interactive
LongRunning
```

以及：

```text
retry policy
timeout
concurrency
idempotency
required capabilities
permission
```

---

## P0 — Desired State 做成真正的核心

重点强化：

```text
desired
actual
observed
reason
reconcile
```

最终 UI 能直接显示：

```text
Desired: Farm
Actual: Disconnected
Reason: Agent unavailable
```

---

## P1 — Agent Capability Scheduler

把：

```text
Region
Agent
Capability
Health
Load
```

统一成：

```text
Placement Engine
```

---

## P1 — Failure Injection

重点测试：

```text
CP restart
Agent crash
Network partition
Lease expiry
Duplicate execution
Late result
Session reconnect
Plugin failure
Scheduler restart
```

---

## P1 — Fake Steam

做 deterministic test backend。

---

## P1 — Plugin Platform

继续补：

```text
dependency
upgrade
rollback
health
configuration schema
resource policy
```

但**不要现在就做进程级 sandbox**。

---

## P2 — Operations Console

重点不是漂亮 UI，而是：

```text
Desired vs Actual
Agent health
Session health
Task timeline
Failure reason
Auth challenge
Audit
```

---

## P2 — Performance Benchmark

形成真正的：

```text
100 / 1k / 10k accounts
```

容量模型。

---

# 34. 最后一个非常重要的判断

你最近在连续梳理自己的这些项目，我觉得 Vapor 和你其他项目最大的不同是：

**Vapor 已经具备“平台”的味道。**

它不是单纯：

```text
library
framework
SDK
server
```

而是：

```text
Control Plane
+
Runtime
+
Agent
+
Scheduler
+
Plugin
+
UI
+
Observability
```

所以我不建议继续按照：

> “还缺哪个 Steam API？”

这种思路开发。

应该转换成：

> **“Vapor 作为一个分布式自动化平台，它的核心抽象是否完整？”**

这会让后面的开发质量提升一个层级。

尤其是你现在已经有非常明确的 **single-writer + desired-state + reconciliation + leased task + attempt fencing + agent runtime**，这些东西实际上比那几十个 Steam Action 更有技术含量。citeturn4view0turn4view2

**如果准备交给 Code Agent 做下一轮改造，我建议不要直接让它“优化 Vapor”，而是让它按照上面的 P0 → P1 → P2 做一次 architecture audit，再逐项落地。**这样最不容易把现在已经比较好的核心架构改坏。

---

If you want, I can:

- 深入解析Vapor的插件系统改进建议
- 扩展Vapor的执行语义设计思路
- 规划Vapor未来测试体系中的故障注入测试