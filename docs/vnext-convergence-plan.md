# Vapor V-next 收敛改造计划书

> 状态：**待评审**（评审通过后逐包执行，执行前不改代码）。
> 依据：`docs/review/chatgpt-review-2026-10-04.md`（ChatGPT 审核）＋ `docs/review/chatgpt-review-2026-10-04-recheck.md`（逐条复查报告，基线 commit `0419512`）。
> 配套文档：`architecture.md`（组件与权衡）、`consistency.md`（一致性模型）、`roadmap.md`（分布式能力地图）、`feature-matrix.md`（vs ASF 功能矩阵）、`actions.md`（动作目录）、`tests/TESTING.md`（测试纪律）。

## 0. 总体判断与目标

复查结论（30 属实 / 3 已过时 / 0 不成立，详见复查报告）：审核对本仓库的**架构性判断全部属实**——Control Plane / Agent 分离、Desired State 收敛、Task Lease + Attempt fencing、SQLite single-writer、Plugin collectible ALC、Session Engine 五层结构，均为真实且已落地到实现/测试层的能力。审核的**三条已过时条目**（§13 ALC 安全表述、§26 故障注入、§28 性能基准）指向的内容仓库已经完成，无需再投入。

V-next 的收敛方向因此定为：

1. **核心模型固化**：Job/Task/Attempt、Account/Session/Agent、Action/Capability 的 Owner / Lifecycle / Persistence / Identity / Consistency 五要素成文（现散落在 4 份文档，无单一权威源）。
2. **补上真正缺的一层**：Action Execution Semantics（审核 §8/§11 指出的最大真实缺口，与 consistency.md 的 at-least-once 契约直接相关）。
3. **Desired State 收敛面收口**：per-account 的 observed/reason 与一键 Reconcile 呈现（现状已有大半）。
4. **计数与文档同步数字化**：README 过期数字清零（37+ actions / 50 endpoints），动作数/路由数改由脚本守护。
5. **边界重申**：不做分布式 CP、不做进程化 Plugin 沙箱、不再把"堆 Action 数量"当成长指标。

## 1. 决议总表

| 类别 | 对象 | 依据（审核条目） | 处置 |
|---|---|---|---|
| **保留** | CP/Agent 分离、outbound WSS、Job→Task→Agent→Session→Action 链、Desired State + ConfigVersion 收敛、Lease+fencing+at-least-once、SQLite single-writer、Plugin collectible ALC、Session Engine 五层、secret 留 Agent、SSE best-effort、HMAC webhook、系统级性能基线 | §1–§7、§14、§15、§20、§21、§22、§24、§25、§28（主体） | 不动，仅文档化归位 |
| **强化（P0）** | Action Execution Semantics；核心领域模型字典；Desired-State 收敛面 per-account 呈现 | §8、§11；§33-P0；§19、§33-P0 | 见 §2 |
| **强化（P1）** | README/计数同步+verify 脚本；品牌定位句；Plugin manifest 配置 schema 与依赖声明；FakeSteam 确定性测试后端 | §29、§10；§30；§12、§33-P1；§27 | 见 §3 |
| **降级（P2，按信号启动）** | webhook 投递记录、AuthChallenge domain object 代码化、Event 三层分桶、secret version 语义、vapor backup CLI、Placement/Capability matcher、统一 trace 字段约定、Operations Console 增强 | §25（残余）、§23、§24、§22、§21、§16+§17、§18（残余）、§19（残余） | 见 §4，不排期 |
| **删除** | 已过时项不再投入 | §13、§26、§28（主体） | 不做 |
| **明确不做（非目标）** | 分布式 CP（PostgreSQL/Redis/NATS/leader election）、gRPC/mTLS mesh、multi-tenancy/RBAC、进程化 Plugin 沙箱、"Action 数量"成长指标 | §20、§13、§33-P1（沙箱）、§10 | 见 §5，与 `roadmap.md:249-258` 非目标一致 |

## 2. P0 工作包（先做）

### P0-A：Action Execution Semantics

**目标**：给每个 Action 声明执行安全分类，让 Scheduler/重试决策读得到语义，而不是对全部 57 个动作一视同仁。

**采纳来源**：审核 §8（Execution Semantics：ReadOnly/IdempotentWrite/NonIdempotent/Interactive/LongRunning/Compensatable）、§11（Capability 元数据）、§7（at-least-once ≠ exactly-once 的工程化落地）。

**范围**：
- `ActionMetadata` 增加分类字段：`ActionSafety` 枚举 `ReadOnly / Idempotent / GuardedWrite / NonIdempotent`，默认（未声明）为 `Unknown`，按 **NonIdempotent 保守**处理（不可自动重试、不可并行重派）。
- 57 个动作逐一声明分类（登记期机械核对，不允许漏标）。
- 最小闭环：重试策略按分类生效——`ReadOnly/Idempotent` 保留现有 `TaskMaxDispatchAttempts` 重试；`NonIdempotent/GuardedWrite` 最多重试 1 次（仅任务管道失败时），避免重复外部副作用；`GuardedWrite` 需显式幂等键（`idempotency_external_key` 支持）才允许多次尝试。Interactive/LongRunning 不新增枚举——分别由现有 `RequiresLogin`/`TimeoutSeconds` 表达（避免过度设计）。
- `actions.md` 每动作补齐分类列，依赖脚本守护（见 P1-A）。
- Scheduler 侧改动仅限重试决策读取分类；**不动**隧道协议、Job 队列模型、任务状态机。

**边界**：
- 不改 `POST /v1/jobs` 信封与 wire 格式（分类是元数据，不进 payload 契约）；外部 key 幂等支持为 P0-A 的可选项，若评估为侵入性过大则降 P2 单列。
- `IAction`/`ActionMetadata` 属于 Plugin API 面：按 `docs/releasing.md` 的 API 兼容规则处理（新增字段带默认值，需随轮验证 in-tree 六个官方插件的加载/卸载测试不回归）。

**验收标准**：
1. 57 个动作全部显式声明 `ActionSafety`；`ActionRegistry.Register` 拒绝未声明者（至少 warning→后续轮次升级为拒绝）。
2. 重试策略按分类生效，测试覆盖：`NonIdempotent` 动作管道失败不触发二次派发；`Idempotent` 保留重试。
3. `actions.md` 分类列与代码单源，verify 脚本负探针（人为改一处 → EXIT=1）。
4. API 兼容：六个官方插件 load/unload 测试全绿；五 verify + format + 全量串行双百门禁全绿（口径见 §6）。

### P0-B：核心领域模型字典

**目标**：一份权威文档，把平台核心对象定义清楚。审核 §33-P0（Owner/Lifecycle/Persistence/Identity/Consistency）指出这是 P0；复查确认现内容散落在 `consistency.md §1`（状态所有权）、`architecture.md`、`session-engine.md`、`api.md` 四处，而 `data-dictionary.md` 实为 gamedata/crawl 字典（`docs/data-dictionary.md:7-108`），**不覆盖平台核心模型**。

**范围**：
- 新增 `docs/domain-model.md`：为 Account / Session / Agent / Job / Task / Attempt / Action / Capability / Event / Plugin 十对象逐项定义五要素，并以审核 §31 图（已核实与现状一致）为关系骨架。
- AuthChallenge 顺带定义为 domain object（文档级先定：id/account/session/type/createdAt/expiresAt/status/attempt/source），代码保持现状（transient，见 P2-2）。
- 每个对象条目附"代码类型索引"（类型名 + 文件路径），作为文档与实现的映射表。

**边界**：
- **只写文档，不重构代码**。概念分层（JobDefinition/Schedule/Execution 分离，审核 §9）在文档中先行约定义，代码层明确不做拆分（现状 `RecurringJobScheduler` + `SqliteJobStore` 已覆盖该语义）。
- 不与 `consistency.md`、`architecture.md` 重复叙述；三者互链，本文件只持有唯一事实（如对象五要素、AuthChallenge 字段）。

**验收标准**：
1. `docs/domain-model.md` 十对象五要素齐全；每对象均可追溯到具体代码类型（引用存在性核对一遍通过）。
2. 与 `consistency.md §1`、`architecture.md`、`actions.md` 无矛盾（人工核对清单附在文档尾部）。
3. README 文档索引表（`README.md:78-84`）新增该文档一行。

### P0-C：Desired-State 收敛面收口

**目标**：账户的"期望 / 实际 / 原因 / 收敛"在 API 与 UI 上完整呈现。复查确认：reconciler 已产出结构化 reason（`DesiredStateReconciler.cs:471,562,620,664,693`），dashboard 已显示 desired/actual mismatch（`dashboard.html:861-866`）与 overall reasons（`dashboard.html:789-801`）；缺口是 **per-account 的收敛原因聚合呈现 + 一键 Reconcile 动作**。

**范围**：
- API：账户视图带上 observed（实际会话状态/离线上一步）+ reason（reconciler 最近一次该账户收敛决议）字段；字段契约入 `api.md` 并纳入 verify-api-docs 口径。
- UI：dashboard 账户行显示 reason；提供 "Reconcile" 操作（触发一次强制收敛）。
- 不新增收敛引擎逻辑；若实施时发现 reason 未落库/未持久化，仅做"最近决议"级别的内存呈现，持久化为 P2 讨论项。

**边界**：不改变 reconciler 调度语义；UI 改动限 dashboard.html（admin.html 不动）；不引入长连接/通知机制。

**验收标准**：
1. `GET /v1/accounts/{name}`（或现有账户列表口径）返回 observed + reason 字段，契约经 verify-api-docs 守护（正/负探针）。
2. dashboard 账户行渲染 reason；点 "Reconcile" 触发收敛且审计可查（audit log 条目）。
3. E2E 覆盖 reason 传播与 Reconcile 动作（纳入全量门禁）。

## 3. P1 工作包（P0 稳定后）

### P1-A：计数与 README 同步数字化

**范围**：README 过期数字清零（`README.md:22,43` "37+ actions" → 57；`README.md:34` "50 endpoints" → "57 routes / 67 operations"）；新增 `scripts/verify-actions-count.py`（源码动作 `Name` 数 == `actions.md` 合计行，仿 `verify-api-docs.py`，含负探针）；`actions.md` 名目与插件动作计数联动校验。

**验收**：README 无过期数字；负探针 EXIT=1；五 verify 全绿。

### P1-B：品牌定位句调整（需用户拍板）

**推荐默认**：`README.md:5` 改为 `# Vapor — Distributed Steam Automation Control Plane`，第二句保留 "Inspired by ArchiSteamFarm's Bot/Action model"；`feature-matrix.md` 标题维持（vs ASF 对比是功能沟通工具，不构成品牌绑定）。

> **已拍板落地（2026-10-08，轮五十七）**：用户确认按推荐默认执行——README.md:5 改为 `# Vapor — Distributed Steam Automation Control Plane`，首段补 "Inspired by ArchiSteamFarm's Bot/Action model" 出处归属句；`feature-matrix.md` 标题维持。验收达成：README 首屏与 `architecture.md` 定位一致，无代码改动。

**验收**：README 首屏与 `architecture.md` 定位一致；无代码改动。

### P1-C：Plugin 平台补丁包（拆三子项，仅做前二）

- **C1 配置 schema**：manifest 增加可选 `configurationSchema` 字段，插件初始化前按 schema 校验 `configuration`，失败 = 结构化发现错误（含字段级信息）。树内六插件至少一个用上（示例性）。
- **C2 依赖声明**：manifest 增加可选 `dependencies`（pluginId + apiVersion 约束），启用时构建依赖图：缺失依赖、循环依赖、apiVersion 不匹配 → 发现失败。
- **C3 升级/回滚/健康/资源策略**：评估为生态级大件，**降 P3**（见 §4 末）。

**边界**：不做进程化 sandbox（审核 §13/§33 一致，仓库非目标重申）；不引入远程插件源与签名体系（超出收敛目标）。

**验收**：C1/C2 各自：正/负用例测试（缺 schema 字段、坏 schema、缺依赖、循环依赖、版本不匹配全 EXIT≠0 语义）、`plugins.md` 更新、树内官方插件回归全绿。

### P1-D：FakeSteam 确定性测试后端

**范围**：以 BotSession 状态机为边界（登录/断线重连/挑战/超时/错误/受限 六类场景），建立可确定性回放的后端，**不依赖真实 Steam 网络**。（40.7-1 audit 2026-10-07：`ISteamClientManager`/`ISteamTransport` seam 足以表达六场景，无需"模拟器扩展"缩小范围；原点名的 `SteamCallbackSimulator` 为零引用的 NotImplemented 存根，已删除；确定性后端落地为 `tests/Vapor.Steam.Core.Tests/FakeSteam/` 的 `FakeSteamClientManager` 脚本化传输双 + `BotSessionStateMachineTests` 六场景 11 测。）

**边界**：第一周先做 BotSession/SteamClientManager 接口 audit——若传输层抽象不足以支撑确定性双，调整为"模拟器扩展"并知会用户缩小范围；**不做** 1000 账号/10000 任务量级压测（归 P3 容量画像）。

**验收**：≥6 个状态机场景在 FakeSteam 后端上全胜（确定性，无 flake）；纳入全量门禁双百口径。

## 4. P2 降级包（不排期，按信号启动）

| # | 项 | 来源 | 启动信号 | 边界与验收要点 |
|---|---|---|---|---|
| 1 | Webhook 投递记录（持久化 + consumers event 去重约定） | §25 残余 | 出现真实 webhook 丢事件投诉/接入方 >3 | 不做 WebhookSubscription 多订阅模型；验收：投递记录表 + at-least-once 语义文档化 |
| 2 | AuthChallenge domain object 代码化（字段/状态机） | §23 | P0-B 文档约定稳定后 | 保持 transient 语义不变（不落库）；验收：字段对齐 domain-model.md。**已落地（2026-10-10，拉起令批次）**：记录收敛单源 `Vapor.Protocol/Events.cs`（删除 ControlPlane 侧镜像 record），`Attempt` 代数字段（tracker 权威：首抬 1、重抬 +1，SSE 事件同值）；`status`/`source` 有意不设字段（`challengeType` 已编码——`*_required`=pending/agent、`code_provided_*`=answered/operator，设字段即重复状态），`expiresAt` 有意不设（无跨边界 TTL 信号）；语义仍 transient 不落库 |
| 3 | Event 三层分桶（Domain/Operational/Audit）文档化 + SSE 事件名规范化 | §24 | 事件名出现命名混乱时 | SSE 保持 best-effort；验收：约定入文档，SSE 事件名与文档一致 |
| 4 | secret version / rotation 语义成文 | §22 | KeyRotation 增加账号级轮换需求 | 现状 AES-GCM v2 + 轮换工具已覆盖大部分；验收：文档定义版本语义 |
| 5 | `vapor backup/restore/verify` CLI 三件套 | §21 | 备份事故/多机部署需求出现 | 现状 `production.md:348-366` 指引+sqlite `.backup` 可用；验收：CLI 包装现有语义 |
| 6 | Placement / Capability matcher（region+labels+load/health 排序） | §16+§17 | 出现跨区域争抢/负载不均观测 | 不改隧道协议；capability 结构化在 domain-model.md 先行定义 |
| 7 | 统一 Execution ID 字段约定（job/task/attempt/agent/account/session/action/trace_id） | §18 残余 | 排查跨链路问题时 | 现状已有 traceparent 贯通（`TaskSchedulerService.cs:134,147`）；验收：约定成文 + 关键 sink 字段对齐。**已落地（2026-10-10，拉起令批次）**：约定成文（`architecture.md` §Execution ID convention）；审计 sink 对齐——`AuditEntry`/`AuditQuery`/`CreateEntry` 增 `task_id`/`attempt`/`agent_id`/`session_id`/`trace_id`，`SqliteAuditStore` 幂等 `ALTER TABLE` 迁移（旧行回读 null），`/v1/audit/logs` 增 `agentId`/`taskId` 过滤，HTTP 审计路径从 W3C `traceparent` 头解析 `trace_id`，`account.reconciled` 审计带 `agent_id` |
| 8 | Operations Console 增强（Agent/Session 健康卡片、失败原因、audit 视图） | §19 残余 | P0-C 上线后用户反馈 | 验收：dashboard 增卡片视图，无新增后端面 |
| 9 | 容量画像性能场景（100/1k/10k） | §28 残余 | 出现规模化部署前 | 现状系统级基线已可用；验收：performance.md 增场景表 |
| 10 | 残余故障注入场景（SQLite 损坏模拟、CP 全链路重启、跨 attempt 分区） | §26 残余 | 出现相关事故引导 | 验收：新增场景测试 |

## 5. 明确不做（与仓库既有非目标一致，审核亦持同见）

- 分布式 Control Plane（PostgreSQL/Redis/NATS/leader election）——`roadmap.md:249-258` 非目标；审核 §20 明确"不要提前引入"。
- gRPC 隧道迁移、外部消息队列骨干、mTLS mesh——同上。
- Multi-tenancy / RBAC / OIDC——单操作员 self-hosted 威胁模型（`architecture.md` Non-goals）。
- 进程化 Plugin 沙箱——审核 §13/§33 建议暂缓；ALC 在文档中维持"load isolation"表述。
- "Action 数量"不再作为成长指标——新增动作需回答"Capability 化后调度/权限/幂等语义是否成立"，而非仅补 Steam API 面（审核 §10/§34）。

## 6. 执行节奏与门禁

- 按仓库既有轮次纪律执行（轮五十起），每轮一个小包；P0-A→P0-B→P0-C 顺序，P1 在 P0 稳定后启动。
- **每轮门禁**：`./scripts/run-tests.sh` 全量通过；`./scripts/collect-coverage-serial.sh` 覆盖率双百（行+分支，口径 per tests/TESTING.md）；五个 `scripts/verify-*.py`（api-docs / api-auth / testing-docs / coverage-inventory / dependency-versions）ALL GREEN；新增 verify 脚本同样要求负探针（人为破坏 → EXIT=1）；`dotnet format` 干净。
- **提交纪律**：提交面外科化（只含本轮文件，并行会话的 `todo.md`/`coverage.log` 不碰）；commit message 按 `type: 轮N 一句话` 惯例；每轮落地后同步更新 `roadmap.md`（landed 记录）与相关文档。
- **测试纪律**：任何行为变更先补/改测试再实现（仓库一贯 TDD 口径，见 TESTING.md）；E2E 面遵循既有"真实进程覆盖"倾向。

## 7. 验收标准汇总（总门）

1. P0 三包 + P1 四包全部落地，各自验收条目达成（§2/§3）。
2. 复查报告建议清单一对一闭环：30 条属实项中所有缺口项有处置结果，3 条已过时项明确不进计划。
3. 全量门禁稳态：如轮四十九基线（3498 测试 / 双百覆盖率 / 五 verify / format），每轮不出现无披露回退。
4. README/actions.md/api.md/roadmap.md/feature-matrix.md 与代码全量一致（机械守护覆盖计数类）。
5. `docs/domain-model.md` 双月核对一次（挂在维护轮）。

## 8. 启动条件

本计划书经用户拍板后启动执行；拍板粒度建议为"P0 先启动、P1 逐个确认"。启动后首个执行轮次 = P0-A。