# Vapor 需求清单

一条一句;完成项保留一行证据(commit + 测试数);过程叙述见 CHANGELOG 与 git log。

## 未完成

- [x] 41-4 mock 链路验收(2026-10-18 用户令「先跑 mock,真实账号后补」)——本地 HttpListener mock Steam 730 背包接口,base url 改经构造参数/VAPOR_STEAM_INVENTORY_BASE_URL 环境变量指定(SteamTradeClient.cs:23,36,53-54),agent 侧 plugin.TradeClientFactory 2 参 ctor 读 env var 指向 mock;测试断言拉到背包、箱子数量与 mock 一致、钥匙数量与 mock 一致、case_open 全流程走通,失败即非零退出(CaseInventoryMockServerTests.cs:44,91,128)。测试 3/3 过,全量 4055 全绿(CI 4562112 macos-Debug/ubuntu-Release 双 job 交叉各 4055)。真实账号验证另列下方独立未完成项。
- [ ] 41-4a 验证真实账号可登录（需用户输入，暂缓）：由操作者提供可用测试账号或自行运行登录探针；验收：获得成功登录的脱敏证据后才解锁 41-4b。沿用 [2026-10-30 决策](docs/review/2026-10-30-decisions.md)，不重复尝试已知无效凭据。 — BLOCKED：需操作者提供真实可登录 Steam 测试账号（含凭据路径）或自行运行登录探针；缺真实账号凭据，2026-11-01 复核，不重复尝试已知无效凭据。
- [ ] 41-4b 验证真实 CS2 背包 / 开箱链路（依赖 41-4a）：操作者显式开启真实动作门控后完成实测；验收：真实背包、箱子 / 钥匙计数、开箱结果与前后库存变化有脱敏证据，mock 记录不能代替。 — BLOCKED：依赖 41-4a 真实账号登录证据 + 操作者显式开启真实动作门控（open.enabled/consume.enabled）；缺真实账号与门控授权，2026-11-01 复核。

## 架构核对（2026-10-30，源码分析后新增）

分析与证据：[架构实现核对](docs/review/architecture-2026-10-30.zh.md)。以下 `[ ]` 是后续工作，本轮未改运行时代码，也未把历史测试数当作本轮测试结果。每项可独立提交、验证和勾选；P0 优先处理秘密边界，P1 处理派发可靠性与内存保留。

- [x] ARCH-01 / P0：在任务写入前隔离秘密字段；依据 `Program.cs` 的 `POST /v1/jobs`、`SqliteJobStore.InsertTasksAsync`、`AgentTaskExecutor`。验收：直接任务、循环模板及其子任务不能将 password/pass、access/refresh token、auth/2FA code 等受支持别名以明文写进控制平面任务库，账号通过 Agent 本地凭据恢复仍可执行；加入跨 API / SQLite 的回归断言。 — 证据:f24de37(f158d1f 先修复 NU1605 依赖降级),SqliteJobStoreTests 49 项、ControlPlane/Account 相关 209 项、全量 3927 全绿
- [x] ARCH-02 / P0：处理历史任务库中的秘密载荷（依赖 ARCH-01）；依据 `jobs.payload_json` 与 `tasks.payload_json`。验收：提供可预览、可备份的清理 / 迁移路径，覆盖循环模板和普通任务，日志不输出秘密；用合成旧库验证清理后 API 查询不再返回旧凭据，并写明 SQLite WAL / 备份副本的处理范围。 — 证据:539df2c,`POST /v1/jobs/secret-payloads/purge`(dry-run 默认/confirm+backupPath 快照),合成旧库 dry-run 计数、scrub 后 API 不再返回旧凭据、备份快照含 scrubbed 前秘密,生产文档写明 WAL/备份副本范围;SqliteJobStore 51+、ControlPlane 相关 69、全量 3933 全绿
- [x] ARCH-03 / P0：为人工验证码 SSE 增加账号所属 Agent 授权；依据 `Program.cs` 的 `/v1/auth/challenges/events` 当前仅检查 Agent 角色。验收：Agent Key 与已认证 Agent 身份绑定，服务端只投递该 Agent 被分配账号的验证码；请求参数不能扩大权限；两个 Agent、跨账号订阅与管理员脱敏投影有回归覆盖。 — 证据:2dbe091(`Vapor_AGENT_KEY_AGENT_IDS` key=agentId 绑定,`/v1/agent/ws` 校验 agentId 匹配,`/v1/auth/challenges/events` 要求 Agent 指定所属账号并只投递该账号 code_provided_*,未绑定 key 视同无效;Agent 侧改并行订阅多账号),ControlPlane 1344 全绿、Agent 198 全绿
- [x] ARCH-04 / P1：为控制平面 Agent 发送队列实现可观测的背压；依据 `AgentRegistry.cs` 的 `ConnectedAgent._send` 使用 `DropOldest`。验收：队列满时不静默丢失已接收的任务 / 取消消息，调度器能感知拒绝并安全重试；容量边界与取消消息场景有确定性测试。 — 证据:9fed6fa,`ConnectedAgent._send` 改 `Wait` 满队拒绝写入不再静默丢最旧,`EnqueueTask`/`EnqueueTaskCancel` 返回 false,`SendQueueRejected` 计数可观测;调度器 `task.enqueue_failed` 安全重试,取消端点发布 `job.cancel_rejected`;AgentRegistryTests 容量边界(1/3 满队拒绝保留顺序)+取消场景,TaskSchedulerServiceTests 满队拒绝重试,ProgramBranchCoverageTests 取消满队发布拒绝;ControlPlane 1351 全绿,非 E2E 全套 4069 全绿
- [x] ARCH-05 / P1：限制 Agent 待执行任务缓冲并向调度器反馈可接收容量；依据 Agent `RunOnce` 的无界 `Channel<WSMessage>` 和串行消费。验收：长任务期间缓冲数量有配置上限、容量耗尽不丢消息，调度不持续向同一 Agent 堆积；压力测试断言队列上限和任务最终状态。 — 证据:11daadd,`AGENT_TASK_BUFFER_CAPACITY` 配置有界 Channel(Wait 满队阻塞不丢消息),hello meta `task_buffer_capacity` 上报,`ConnectedAgent.AcceptsInFlight` 按 in-flight<容量+1 过滤,调度器 `Pick` 带容量谓词满员走 `task.dispatch_failed` 重排;AgentTaskBufferTests 5、AgentRegistryTests+2、TaskSchedulerServiceTests+2;Agent 205、ControlPlane 1355 全绿
- [x] ARCH-06 / P1：补齐已派发但未开跑任务的租约策略（协调 ARCH-05）；依据 `TaskSchedulerService.DispatchOnce` 领取即置 Running，Agent 消费时才启动 `HeartbeatLoop`。验收：首个任务耗时超过租约时，后续排队任务不会在旧派发仍有效时重复执行；覆盖至少两任务、短租约和重连场景，并校验旧 attempt 栅栏。 — 证据:cdd7d29,Agent 接收排队任务即启动 `TaskLeaseRenewer`(每 5s `task_heartbeat`)续租,取出执行时停止续期交给执行心跳,断连 finally 清理;`HeartbeatTask` 只匹配 running+attempt 旧 attempt 栅栏已有;TaskLeaseRenewerTests 3(即时/周期/取消/发送失败静默)+SqliteJobStoreTests `HeartbeatTask_PreventsStaleRequeue`;Agent 208、SqliteJobStore 53 全绿
- [x] ARCH-07 / P1：让取消消息对 Agent 排队任务生效；依据 Agent 接收器只匹配 `currentTaskId/currentAttempt`。验收：取消先于任务开跑时不触发该任务的 Action，正在执行的取消语义保持正确，旧 attempt 的取消不误伤新 attempt；增加排队取消和竞态测试。 — 证据:a102925,`QueuedTaskCanceller` 按 taskId+attempt 围栏记录排队取消,执行循环取出任务先 `TryRemove` 匹配则跳过不执行 Action(取消已由 `CancelJob` 置终态,不发会被拒的 task_result),旧 attempt 不匹配不误伤新 attempt,断连 finally 清理;正在执行取消沿用 `currentTaskCts.Cancel`+`currentTaskCancelledByServer`;QueuedTaskCancellerTests 3(精确匹配/旧 attempt 不误伤/幂等);Agent 211 全绿
- [x] ARCH-08 / P1：移除或实际消费 EventBroker 两条无消费者全局队列；依据 `_globalSessionChannel` / `_globalAuthChannel` 仅写入、通知服务使用另一套 `Subscribe*`。验收：没有订阅者时不会无界保留会话 / 验证码事件，现有订阅扇出与 webhook 脱敏行为通过验证。 — 证据:4042b71,删除两个无 Reader 的 unbounded Channel 字段及其 `Writer.TryWrite`,无订阅者 20k 发布后无任何 Channel 字段残留(反射断言);EventBrokerTests `PublishSessionAndAuthChallenge_WithoutSubscribers_DoesNotRetainGlobalQueues`+1,EventBroker 12、Notification+ControlPlaneApi 49 全绿
- [x] ARCH-09 / P2：为人工验证码交付增加短期确认与过期语义（依赖 ARCH-03）；依据提交端先 `tracker.Clear` 再实时发布，而 SSE 无重放。验收：提交时 Agent 断流不会出现“挑战已清但 Agent 未收到”的静默丢失；只对所属 Agent 重试、过期自动清理、确认后删除，且不扩展为通用 SSE 历史日志。 — 证据:本项由同一 commit 完成,`src/Vapor.ControlPlane/AuthChallengeOutbox.cs:32`(短期内存 outbox:先 `Stage` 再 Clear/发布,所属 Agent SSE 订阅即 `TryGet` 重放未确认代码,`Acknowledge` 确认后删除,`Expire` TTL 过期自动清理;不扩展通用 SSE 历史),`src/Vapor.ControlPlane/Program.cs:3495`(提交端先 `outbox.Stage` 再 `tracker.Clear`+`events.PublishAuthChallenge`),`src/Vapor.ControlPlane/Program.cs:3400`(Agent SSE 订阅先重放 outbox 再进实时流,仅所属 Agent),`src/Vapor.ControlPlane/Config.cs:44`(`VAPOR_AUTH_CHALLENGE_DELIVERY_TTL_SECONDS`,默认 120s,非正回落默认),`src/Vapor.ControlPlane/AuthChallengeOutboxCleaner.cs:10`(30s 周期过期清理),`tests/Vapor.ControlPlane.Tests/ControlPlaneApiTests.cs:312`(提交后无订阅者→重连 agent-one 收到重放且 outbox 确认删除,agent-two 403,admin 不重放),`tests/Vapor.ControlPlane.Tests/AuthChallengeOutboxTests.cs:7` 12 项(Stage/TryGet 空与过期/Expire 部分/Acknowledge 匹配与不匹配/替换/零 TTL/溢出钳制/Trim/空白 ACK/Cleaner),`tests/Vapor.ControlPlane.Tests/ConfigEnvironmentTests.cs:81` +4(0/垃圾/45s 解析+TTL 正向与回落);ControlPlane 全量 1372 全绿,Agent 211 全绿,全量 Release 仅 4 个既有 E2E `InvalidPassword`(真实 Steam 凭据,非本改动);单 commit。
- [ ] ARCH-10 / 条件性延后：实现非协作插件的中毒会话隔离与回收；沿用 `docs/research/process-boundary.zh.md` 的既有决定，仅在第三方插件需求触发后启动。验收：构造忽略取消的插件，证明超时后该会话被隔离、后续任务不永久卡住、监控暴露异常；明确同进程方案无法强制终止任意插件代码，不将 ALC 宣称为进程沙箱。

已在本轮完成的文档项（仅文档验收，不代表上述代码缺口已修复）：

- [x] ARCH-D1：生成中英文 SVG，接入双语文档首页、README 与架构页，并附源码分析入口；证据：`docs/assets/architecture/overview.{en,zh}.svg` 与双语分析报告。
- [x] ARCH-D2：统一当前单控制平面 / SQLite 租约队列口径，移除旧的外部 MQ 集成完成与双活演进承诺；证据：双语 `architecture` 文档。
- [x] ARCH-D3：修正凭据与验证码绝不经过控制平面的错误表述，区分 Agent 通道和 webhook 投影；证据：双语 README、架构与一致性文档。
- [x] ARCH-D4：恢复中文首页标题、卡片、代码块与表格的 Markdown 分行；证据：`docs/index.zh.md`。
- [x] ARCH-D5：将真实账号登录与真实开箱验证从已完成 mock 项拆为独立未勾选项；证据：41-4a / 41-4b。

## GA Exit Criteria(5/5 达成 2026-09-15,2026-09-24 复核维持)

- [x] 主分支 build/test 稳定通过 — 证据:4052 测试全绿(2026-10-13 基线,20b893d..15d7b3c 期间)
- [x] 核心动作闭环:登录、2FA、游戏状态、激活 Key、库存/交易、基础 farming — 证据:actions.md 全量目录(20b893d 同步)
- [x] 安全闭环:凭证加密存储、令牌持久化与轮换、敏感日志脱敏、审计 — 证据:40.8-④ 规则逐条钉入测试(ebbbb81,267ccb3)
- [x] 可运维:OpenAPI、E2E、Docker、CI/CD、可观测性、性能基线 — 证据:api/auth/testing/coverage 四校验脚本接入 CI format job(维护轮十六-十九)
- [x] 文档闭环:架构、运行、运维、发布、故障排查齐备 — 证据:docs/23 页,mkdocs --strict 绿(15d7b3c)

## 里程碑(全部完成)

- [x] P0 恢复可开发状态:修 PlayGamesAction、LoginAction 对接登录流程、测试门禁进 CI — 证据:343a521,2603 测试
- [x] P1 M2 核心能力闭环:PlayGames 全量、RedeemKey 深化、Trading MVP、会话可靠性、登录测试 — 证据:b148892,2408 测试
- [x] P2 M5 安全闭环:FileCredentialStore、AES-GCM、密钥轮换工具、审计日志脱敏 — 证据:2603 测试含安全面全绿(2026-09-21 快照)
- [x] P3 M4 数据与爬虫:SteamWebHandler 产品化、缓存层、游戏数据动作 — 证据:b148892(Redis 缓存后端)
- [x] P4 M3 插件系统:插件基础设施、官方首批插件、插件生态深化 — 证据:a270efc,2549 测试
- [x] P5 规模化运营(A/B/C 三方向)— 证据:c66eec5
- [x] P6 Steam 功能纵深:计划任务、farming 循环、maFile 导入、背包 REST — 证据:baf7efd
- [x] P7 市场闭环:挂单读取、批量撤单、挂单创建(dry-run 默认)— 证据:c05ab53
- [x] P8 积分商店认领 — 证据:2603 测试(2026-09-15 随 GA 盘点收口)
- [x] P9 游戏数据采集(批量动作 + 分片计划器 + 只读页)+ 字段字典 — 证据:c5e06e7 + 76e82ab

## 维护轮(全部完成)

- [x] 覆盖率双 100%:行 22156/22156 + 分支 8494/8494(2026-10-13 实测,门禁 --min 100 --min-branch 100)— 证据:见 tests/TESTING.md 基线表
- [x] ASF 式运营四阶段:每账号代理、异常账号检测、挂卡策略增强、插件生态 — 证据:轮三十八(2549 测试)
- [x] 控制台代理分配、仓库号归集、背包查看面板 — 证据:轮三十九(fe6878c,7370121)
- [x] ASF 风格 CI 收紧:静态分析、NuGet 审计、覆盖率门禁、property 测试 — 证据:轮三十六
- [x] 测试/覆盖率基线刷新、dashboard 扩展、gifts 白名单、成就管理、boost 策略化 — 证据:轮三十四-三十七(2931 测试)
- [x] CI flake 治理:TracingTests 进程全局并发、BrokenTick 等待错位、vstest 收尾失速、MarketWatch 在飞失速、macos SQLite NRE 抖动(rerun 处置)— 证据:轮二十三-二十八
- [x] 剪版 v0.1.0-alpha.2:九个月 Unreleased 收口 + 依赖陈账 — 证据:轮二十六
- [x] 故障排查手册、性能基线、控制页面修复、admin QR 扫码、假异步清理、幻影模型 ItemInfo 移除 — 证据:轮十七-二十二

## V-next 收敛(2026-10-04 立项,全部完成)

- [x] P0-A 执行语义契约:ActionSafety 五级 + 注册表 + 派发 cap 2 — 证据:7d17cec
- [x] P0-B 领域模型字典:docs/domain-model.md 十对象五要素 — 证据:c75f2f6
- [x] P0-C Desired-State 收敛面:账户视图 + 强制收敛端点 — 证据:96a45e2
- [x] P1-A 计数与 README 同步数字化 — 证据:轮五十一
- [x] P1-B 品牌定位句(用户 2026-10-08 拍板按推荐默认执行)— 证据:轮五十七
- [x] P1-C Plugin 平台补丁:configurationSchema + dependencies 依赖图 — 证据:0269423
- [x] P1-D FakeSteam 确定性测试后端 — 证据:轮五十六
- [x] P2 降级包十项(2026-10-10 拉起令激活):webhook 投递记录、AuthChallenge 代码化、Execution ID 字段约定、Event 三层分桶、vapor backup CLI、Operations Console 增强、统一 Execution ID、容量画像、secret version 语义、残余故障注入场景 — 证据:ebbbb81,6db9cf7,c3ec242,20141fd,279c45f,267ccb3,88b7b9f,9584de7,f952dd4
- [x] 控制中心脚本仓库与编排:脚本仓库、脚本执行、编排系统、调度接入 — 证据:803e6ac,10b58a6,91171bf,6ec7027

## CSGO 开箱 + CS2 背包接入(2026-10-11 用户令,41-4 除外全部完成)

- [x] 41-1 真实背包接入:InventoryPaginator 共用化 + case_inventory 动作 — 证据:5478728
- [x] 41-2 开箱与背包关联:CaseLedger 台账 + case_consume 动作 — 证据:5478728
- [x] 41-3 ToS 口径:门控默认关、输出带 mode、docs 同步 — 证据:轮七十四
- [x] 41-5 门禁全绿才 push、commit 英文简短 — 证据:06ce401..5478728
- [x] 41-6 插件改名 vapor.csgo + 门控真开箱(GC 协议对齐操作者自建 Steam-bot)— 证据:f4b41c5,26685b6
- [x] 41-7 Dota2(570)交易插件壳 — 证据:dae07a5
- [ ] 41-4 真实环境实测 — 分项与阻塞条件见「未完成」41-4a / 41-4b；mock 完成不代表真实验收完成。

- [x] 缺陷：文档首页向下滚动出现一大片空白（来源：用户 2026-10-14）——根因：卡片网格 4 卡占 3 列留洞 + 图标短代码缺 emoji 扩展渲染成原文；补满 6 卡成两整行、图标补 material 前缀 — 证据:a4d4b28(mkdocs strict 绿)
- [x] 文档双语化并支持切换（来源：用户 2026-10-14）——README 英文主文档 + README.zh.md 互链；文档站 mkdocs-static-i18n 后缀双语（index/getting-started/running 三页有中文版，余页回退英文），头部语言切换器按页互跳 — 证据:c985d67(全量 4052 绿)

- [x] 后台界面重设计：Steam 风格为默认主题，其余主题可切换（来源：用户 2026-10-14）
  - 现状：后台在 `src/Vapor.ControlPlane/wwwroot/`（`admin.html` / `dashboard.html` / `gamedata.html`），用户认为现有主题**太丑**
  - **默认主题 = Steam 风格**（深蓝灰底、卡片式布局、克制的高亮色）；其它主题作为**可切换项**
  - **本阶段先出原型并放进文档展示**（`docs/design/prototypes/`，与 doggo / awn 同一套做法），**先不改线上页面**
  - 验收：原型覆盖 admin / dashboard / gamedia 三个页面的实际布局（不是泛泛的配色方案），并在文档中展示截图或可打开的静态页
  — 证据:7ff2c42(四主题原型 admin/dashboard/gamedata 齐全,mkdocs strict 绿)


- [x] docs 子目录双语配对（design/ research/ review/ 实盘 6 页,2026-10-16 执行单下一轮项;PATROL1018R2 完成）——3 对真译:admin-ui（EN→中,21 个 prototypes/ 链接逐字保留）、process-boundary（EN→中,全部实测数字逐字保留）、chatgpt-review-recheck（中文占英文槽→英文槽改写真英文+中文留 .zh.md）；2 对双向改写:i18n-diagnosis-2026-10-16、market-survey（英文槽改真英文,中文留 .zh.md,50 条外链引用与截图路径逐字不动）；1 篇逐字稿:chatgpt-review-2026-10-04（1961 行第三方对话逐字稿,按「逐字稿/过长只标注不硬译」口径原文保留+双语占位页互链）；子目录页切换器用 `../zh/<dir>/<page>/` ↔ `../../<dir>/<page>/`（构建实测 href 双向可达） — 证据:本 commit(mkdocs strict 绿,7 verify 脚本 GREEN,site/zh/ 六页全中文)
- [x] 审核通过：b9b1f71dbc06782cf19ca0a7c2a4d631e6720877..HEAD 未发现问题

## 审核发现

- [x] 审核发现：历史秘密清理 `PurgeSecretPayloadsAsync` 逐行 `UPDATE` 且未包事务，进程中断/取消会留下部分已清理、部分未清理的库，与文档「never a partially scrubbed database」表述不符 — 修复:b0ba70f(jobs+tasks scrub 单事务提交,新增 trigger 失败回滚测试),ControlPlane 1338 全绿
- [x] 审核发现：`PurgeSecretPayloads_EndpointScrubsWhenConfirmed` 中 `Assert.DoesNotContain("endpoint-secret", scrubBody)` 为恒真断言——响应体只含 dryRun/jobs/tasks/backupPath 计数，从不回显载荷，无法证明秘密已被实际清除 — 修复:b0ba70f(scrub 后改查 API 投影断言 secret 消失且 keep 保留),ControlPlane 1338 全绿
- [x] 审核发现：purge 端点未校验 `backupPath` 与活动库路径不同，若误传活动库路径，`BackupDatabase` 会以同一文件为源和目标 — 修复:b0ba70f(同一路径抛错并映射 400,新增端点拒绝测试),ControlPlane 1338 全绿
- [x] 审核发现：`AuthChallengeOutbox.Stage` 以未 trim 的 `e.AccountName` 为字典键，而 `TryGet`/`Acknowledge` 均以 `accountName.Trim()` 为键，账号名含首尾空白时重放无法命中（src/Vapor.ControlPlane/AuthChallengeOutbox.cs:40） — 证据:src/Vapor.ControlPlane/AuthChallengeOutbox.cs:40(Stage 改用 `e.AccountName.Trim()` 落键,与 TryGet/Acknowledge 的 trim 查询一致),tests/Vapor.ControlPlane.Tests/AuthChallengeOutboxTests.cs:136(新增 `Stage_TrimsAccountName`:带空白账号名 Stage 后以 trim 键命中);AuthChallengeOutboxTests 13/13 绿,ControlPlane 1376 全绿
- [x] 审核发现：`PollAuthChallengesAsync` 在 `ListSessions()` 为空时 `Task.WhenAll(空列表)` 立即完成且循环内无 await 延迟，外层 while 形成纯 CPU 忙循环（src/Vapor.Agent/Program.cs:786-803） — 证据:src/Vapor.Agent/Program.cs:790(空会话列表改为显式 `await Task.Delay(5s)` 再 `continue`,不再让 `Task.WhenAll(空)` 立即完成形成忙循环),tests/Vapor.Agent.Tests/ProgramPollAuthChallengesTests.cs(新增 `PollAuthChallenges_NoSessions_WaitsBeforePollingAgain`:首次空轮询后 100ms 内取消,验证不会在取消前反复轮询);Agent 212 全绿
- [ ] 审核发现：有界 `Wait` 缓冲使接收循环在 `tasks.Writer.WriteAsync` 阻塞（缓冲满）时无法读取后续 `task_cancel` 帧，排队任务取消可能错过生效窗口（src/Vapor.Agent/Program.cs:409）
- [ ] 审核发现：`Config.LoadFromEnvironment` 解析 `key=value` 绑定仅对 value 做 Trim，key 部分未 Trim，键两侧含空格时与 Bearer token 精确匹配失败（src/Vapor.ControlPlane/Config.cs:139）
