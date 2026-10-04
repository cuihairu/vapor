# ChatGPT 审核逐条复查报告（2026-10-04）

> 复查对象：`docs/review/chatgpt-review-2026-10-04.md`（ChatGPT 审核全文，共 34 节）。
> 复查基线：commit `0419512`（审核文档入库，工作区无本会话代码改动）。
> 复查方法：逐节对照当前代码/文档/测试/提交历史，给出【属实 / 已过时 / 不成立】结论并附证据（`文件:行` 或 commit）。
>
> 结论口径：
> - **【属实】**——审核断言与现状一致；若为建议，指其所指缺口当前真实存在。
> - **【已过时】**——审核所指的问题/缺口已被现状解决或不再成立（含"建议已落地"情形）。
> - **【不成立】**——审核断言与现状不符。
> - 观察项——纯评价/方法论，不适用整改判定，不计入三档统计。

## 统计

| 档位 | 条数 | 条目 |
|---|---|---|
| 【属实】 | 30 | §1–§12、§14–§25、§27、§29–§31、§33–§34 |
| 【已过时】 | 3 | §13、§26、§28 |
| 【不成立】 | 0 | — |
| 观察项（不适用） | 1 | §32 |

"属实"中标注 ◐ 者为"建议部分已落地 / 缺口已缩小，残余建议仍需评估"。

---

## 逐条复查表

| # | 审核要点（摘要） | 结论 | 证据 | 处置建议 |
|---|---|---|---|---|
| 1 | Vapor 是分布式 Steam 自动化控制平面，CP/DP 分离方向正确 | 【属实】 | `README.md:15-17`、`docs/architecture.md`（CP 状态拥有者、Agent 执行面）：`README.md:17` "agents reach the control plane, never the other way around" | 维持，写入计划书背景 |
| 2 | Agent 主动建立 outbound WSS，NAT 友好 | 【属实】 | `README.md:17,39`（outbound WSS tunnel, agent key） | 维持 |
| 3 | Job→Task→Agent→Session→Action 抽象合理，不要推倒 | 【属实】 | `docs/actions.md:44-46`（task 派发链）、`docs/consistency.md:29-38`、`docs/architecture.md:566-575` | 维持；概念分层建议并入 P0-B（仅文档化，不做代码重构） |
| 4 | Desired State + ConfigVersion 收敛触发已落地且值得强化 | 【属实】 | `docs/consistency.md:118-130`（PUT 全量替换、monotonic `ConfigVersion` 为 convergence trigger、"version moved 而非 field diffed"）、`docs/architecture.md:247-250,566`（声明式账户 + `DesiredStateReconciler`） | 维持；缺口细化见计划书 P0-C |
| 5 | Consistency 文档把 ephemeral/reconstructable 与 durable 分类正确 | 【属实】 | `docs/consistency.md:29-38`（Agent registry in-memory 由 re-hello 重建；"anything derivable … is in-memory and rebuilt by convergence"）、`consistency.md:13`（single writer sole state owner） | 维持 |
| 6 | Task Lease + Attempt fencing 实现级落地 | 【属实】 | `docs/consistency.md:41-114`（atomic claim、heartbeat 仅 Running+attempt 匹配、`SetTaskResult` 拒绝 attempt 不匹配、lease reclaim、at-least-once）；测试实证：`tests/Vapor.ControlPlane.Tests/TaskSchedulerServiceTests.cs:11`（`HeartbeatTaskReturnsTrueOnlyForMatchingRunningAttempt`）、`:106`（`SetTaskResultRejectsAttemptMismatch`）、`:476,500`（`RequeueStaleRunningTasks*`） | 维持，继续加强（测试已覆盖） |
| 7 | at-least-once ≠ safe exactly-once，文档已诚实承认 | 【属实】 | `docs/consistency.md:218-230`（exactly-one terminal state；"duplicate execution of a reclaimed task is possible while the original attempt is still alive"） | 维持；作为 P0-A 语义分类的正当性依据 |
| 8 | IAction 只有 Name/Metadata/ExecuteAsync，缺 Execution Semantics | 【属实】 | `src/Vapor.Steam.Core/IAction.cs:5-16`；`ActionMetadata` 仅 `Name/Description/RequiresLogin/TimeoutSeconds`（`IAction.cs:18-23`）；57 个动作（见 §10） | **吸收** → 计划书 P0-A（核心缺口） |
| 9 | Job 系统已支持 one-shot/scheduled/interval/cron/missed/overlap | 【属实】 | `docs/actions.md:55-57`（schedule 信封字段）、`src/Vapor.ControlPlane/RecurringJobScheduler.cs`（存在） | 维持；"JobDefinition/Schedule/Execution 分层"建议并入 P0-B 文档化，不做重构 |
| 10 | 57 shipped actions；不要把 Action 数量当成长指标 | 【属实】 | `docs/actions.md:3,466`（57 shipped actions、合计行 57）；源码独立核数：动作 `Name` 声明恰 57（Steam.Core 20 + GameData 4 + GameAccess 16 + MobileAuthenticator 9 + MarketWatch 3 + Monitoring 1 + HostActions 4） | 维持；采纳为开发准则（新增动作须过 Capability 化决策，见计划书 §5） |
| 11 | Action 应演进为 Capability（metadata/input/output/permission/execution semantics/retry/timeout/required state） | 【属实】 | 现状 `ActionMetadata` 仅 4 字段（`IAction.cs:18-23`），无 input/output schema、无权限/重试声明；capability = 动作名列表（`AgentRegistry.cs:33-36` 的 `SupportsAction`） | **吸收** → 计划书 P0-A（与 §8 合并，最小先行） |
| 12 | Plugin 基础好：plugin.json→compatibility→trust→permissions→collectible ALC | 【属实】 | `docs/plugins.md:4,43-44,97-98,113-122`（manifest/trust gate/permission evaluation）、`:292-305`（collectible ALC 隔离与卸载规则）、`:381`（`plugin_uninstall` 热卸载）、`:436-440`（vs ASF）、`:504`（PluginManager） | 维持；后续建议（依赖/升级/回滚/健康/配置 schema）按计划书 P1-C 拆分吸收 |
| 13 | ALC 只是 load isolation，不要把 Plugin 描述成真正的安全隔离 | 【已过时】 | 全仓检索无 "sandbox/沙箱/安全隔离" 表述；`docs/plugins.md:146-148` 已是 "minimal trust by default"（无 permission 声明即拒绝对应能力），`README.md:25` 用词即 "ALC-isolated"——审核告诫已被现状满足，无整改对象 | 不需要动作；计划书非目标重申"进程化 sandbox 不做" |
| 14 | Session Engine 是真正 Runtime Kernel（SessionManager→BotSession→SteamClientManager→SteamKit2） | 【属实】 | `docs/session-engine.md:5,9-14,38-44,99-101`（BotSession 状态机/命令队列/事件/重连，命令队列见 `:11`） | 维持；"提升地位"并入 P0-B 文档化 |
| 15 | Account 与 Session 必须严格区分（已核实） | 【属实】 | `docs/architecture.md:80`（Account=id/regionHint/labels/enabled，secret 分离存储）、`consistency.md §1`（Account spec durable vs Session in-memory）、`src/Vapor.ControlPlane/SessionTracker.cs` 与 `AccountStore.cs` 分离 | 维持 |
| 16 | Region 应演进为 Placement Policy（含 network/proxy/labels） | 【属实】 | 现状 region + labels `architecture.md:80`，调度按 region 硬匹配 + capability（`AgentRegistry.cs:33-36`） | **吸收** → 计划书 P2（placement/capability 增强） |
| 17 | Agent Registry 升级为 Capability Registry（结构化能力） | 【属实】 | `AgentRegistry.cs`（`ConnectedAgent` ↔ `AgentHello`，capability 即动作名列表） | **吸收** → 计划书 P2（与 §16 合并） |
| 18 | Observability 已完整（logs/Prometheus/Grafana/OTel/SSE/webhook），建议统一 Execution ID 链路 | 【属实】◐ | `README.md:26`、`src/Vapor.ControlPlane/Program.cs:100-101`（OTLP 可选注册）、`TaskSchedulerService.cs:147`（`task.dispatch` Activity）、`:134`（traceparent 随隧道注入）、`ApiRequestMetrics.cs`、`deploy/grafana`+`deploy/prometheus`、SSE `EventBroker.cs`；统一字段约定未成文（SSE/webhook/审计字段分散） | 维持；残余（trace_id 约定成文）→ 计划书 P2 |
| 19 | UI 应成为 Operations Console；Desired vs Actual + Reason + Reconcile | 【属实】◐ | 三控制台存在：`README.md:74`、`wwwroot/admin.html|dashboard.html|gamedata.html`；desired/actual 对照已落地：`dashboard.html:861-866`（mismatch 行），overall reasons 渲染 `dashboard.html:789-801`，reconciler 已产出结构化 reason（`DesiredStateReconciler.cs:471,562,620,664,693`）；缺"per-account 收敛原因 + 一键 Reconcile"呈现 | 残余缺口 → 计划书 P0-C；全貌 app console → P2 |
| 20 | CP 单实例是当前架构的刻意选择；不要为"分布式"提前复杂化 | 【属实】 | `consistency.md:12-19`（one writer/one lock/one file，无领导选举）、`roadmap.md:249-258` 非目标（多实例 CP/NATS 等留待 P0 浸泡后重估） | 维持；计划书非目标重申 |
| 21 | SQLite 继续保留；建议 vapor backup/restore/verify CLI | 【属实】◐ | SQLite 现状（`consistency.md:12-19`）；备份已有运维指引+脚本：`docs/production.md:348-366`（四库加入备份集，`sqlite3 .backup` 命令样例）；`vapor backup` CLI 不存在 | 保留 SQLite；CLI 三件套 → P2 可选 |
| 22 | Secret 架构正确（凭证留 Agent）；建议 secret version/rotation 语义 | 【属实】◐ | `README.md:27`（凭证永不出 Agent）、`architecture.md:94-101`（API 不暴露 raw secrets、region-scoped 拉取）、`:451-458`（v2 AES-GCM + `VAPOR_ENCRYPTION_KEY_FILE`）、`tests/Vapor.KeyRotation.Tests/`（轮换工具） | "account secret version" 字段 → P2 文档化/字段（低优先） |
| 23 | Auth Challenge 应正式化 domain object（type/status/attempt/source/expiresAt） | 【属实】 | 现状为 per-account 内存快照：`AuthChallengeTracker.cs:5-35`（`ConcurrentDictionary<string, AuthChallengeEvent>`，Upsert/Clear/List/Get）；类型齐全（README:21：SteamGuard/2FA/TOTP/QR/manual/auto-responder） | 代码化 → P2；文档约定先行（P0-B 顺带定义） |
| 24 | Event 应分 Domain / Operational / Audit 三层，统一 sink | 【属实】 | SSE 为 best-effort 非 durable log：`consistency.md:146-151`；audit log 存在（`IAuditStore.cs`）但三层分桶无成文模型 | 分层约定 → P2 文档化（SSE 不改 durable 语义，与审核意见一致） |
| 25 | Webhook 强化（filter/retry/backoff/signature/delivery record, at-least-once） | 【属实】◐ | HMAC：`src/Vapor.ControlPlane/NotificationSinks.cs:207-216`（HMACSHA256, `X-Vapor-Signature`）；filter+retry+backoff 已配：`Config.cs:24-28`（URL/secret/events/maxRetries=3/retryBaseDelayMs=500）；残余=投递记录/持久化队列、消费者 event 去重约定 | 残余 → P2 |
| 26 | 应增加 Failure Injection 测试（列表场景） | 【已过时】 | 核心已落地：运行时故障注入 API `/v1/faults`（`FaultInjector.cs`、`roadmap.md:158-173` ✅、budget/TTL/自愈/豁免面/计数器）、测试 `FaultInjectorTests.cs` + `FaultApiTests.cs`；审核所列 task 侧场景（lease expiry/duplicate/late result/reclaim/attempt mismatch）已有既有测试覆盖（§6 证据）；E2E 真实进程覆盖（commit `1cc63bb` 等） | 主体不再做；残余可选场景（SQLite 损坏模拟、CP 全链路重启、跨 attempt 网络分区）→ P3 观察 |
| 27 | 建议 Deterministic FakeSteam 测试后端 | 【属实】 | 无 FakeSteam 后端；雏形 = `tests/Vapor.Steam.Core.Tests/RealSteam/SteamCallbackSimulator.cs` + `Vapor.Plugins.TestFixtures`；BotSession/SessionManager/SteamClientManager 测试依赖模拟器而非全量 fake transport | **吸收** → 计划书 P1-D（范围限定 BotSession 层确定性） |
| 28 | 性能测试应从 Action benchmark 升级为 System benchmark | 【已过时】 | 现状已是系统级基线：`docs/performance.md:11-46`（API 时延 p50/p95/max/吞吐 8 并发×200 请求、分配量 GC 差值口径、资源占用基准）——实测项目 `ApiLatencyBenchmarks.cs`/`AllocationBenchmarkCollection.cs`/`ResourceFootprintBenchmarks.cs`/`CacheBenchmarks.cs`/`ConcurrencyTests.cs`；"当前只是 Action benchmark"的前提不成立 | 主体剔除；容量画像（100/1k/10k 场景）作为可选 → P3 |
| 29 | README "37+ actions" 与 actions.md "57 shipped actions" 不同步 | 【属实】 | `README.md:22,43` 仍写 "37+ actions"、`README.md:34` 仍写 "50 endpoints"（真值 57 路由/67 操作，`docs/api.md:3`）；actions.md 本身准确（`docs/actions.md:3,466`，源码动作数=57，见 §10）；"自动生成"部分已落地：`scripts/verify-api-docs.py` 机械守护 api.md，actions 计数无守护 | **吸收** → 计划书 P1-A（README 修正 + actions 计数 verify 脚本） |
| 30 | ASF-inspired 不应成为品牌核心 | 【属实】 | `README.md:5` `# Vapor (ASF-inspired)`；功能上 feature-matrix 仍以 ASF 对比为纲（合理） | **吸收** → 计划书 P1-B（品牌定位句调整，用户拍板） |
| 31 | 最值得保留的核心模型图（Desired State→Reconciliation→Job/Task→Agent→Session→Action） | 【属实】 | 与 `architecture.md:566-575`、`consistency.md §1` 现状一致 | 维持；该图可直接用作 P0-B 文档骨架 |
| 32 | 评分 8.5/10 与分维度评价 | 观察项 | 主观评价，无整改对象 | 不适用 |
| 33 | P0/P1/P2 优先级清单 | 【属实】◐ | 逐项核对见计划书：P0 核心模型（§33-P0→P0-B，data-dictionary.md 实为 crawl/gamedata 字典，不覆盖平台核心模型，证据 `docs/data-dictionary.md:7-108` 各节）；P0 action semantics（→P0-A）；P0 desired state（→P0-C，大半已落地）；P1 placement（→P2）；P1 failure injection（已落地，剔除）；P1 FakeSteam（→P1-D）；P1 plugin platform（→P1-C 拆分） | 大部分吸收，剔除已过时子项 |
| 34 | 平台化判断；交给 Code Agent 前先做 architecture audit 再落地 | 【属实】 | 本任务即为该方法论的执行实例 | 采纳 |

---

## 吸收 / 剔除清单一览

**吸收（进计划书）**：§8/§11 → P0-A；§33-P0(核心模型)、§5/§14/§15/§9(概念分层) → P0-B；§19/§33-P0(desired state) → P0-C；§29/§10 → P1-A；§30 → P1-B；§12/§33-P1(plugin) → P1-C；§27 → P1-D；§16/§17/§18(字段约定)/§21(CLI)/§22(version)/§23/§24/§25(残余) → P2/P3。

**剔除（已过时或明确不做）**：§13（告诫已满足）、§26（故障注入已落地）、§28 主体（已是系统级基准）、§20 分布式化（与仓库非目标一致）、进程化 Plugin 沙箱（审核自身建议暂缓）。