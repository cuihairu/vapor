# Vapor 需求清单

一条一句；完成项保留一行证据（commit + 测试数）；过程叙述见 CHANGELOG 与 git log。

## 未完成

- [x] 41-4 mock 链路验收（2026-10-18 用户令「先跑 mock，真实账号后补」）—— 证据:4562112, 4055 测试全绿
- [ ] 41-4a 验证真实账号可登录（BLOCKED：需操作者提供真实可登录 Steam 测试账号或自行运行登录探针；缺真实账号凭据，2026-11-01 复核，不重复尝试已知无效凭据）
- [ ] 41-4b 验证真实 CS2 背包 / 开箱链路（依赖 41-4a；BLOCKED：需操作者显式开启真实动作门控 open.enabled/consume.enabled，mock 记录不能代替，2026-11-01 复核）
- [ ] 41-4 真实环境实测（分项与阻塞条件见 41-4a / 41-4b）
- [ ] ARCH-10 / 条件性延后：实现非协作插件的中毒会话隔离与回收（仅第三方插件需求触发后启动；同进程无法强制终止任意插件代码，不将 ALC 宣称为进程沙箱）

## 架构核对（2026-10-30，源码分析后新增）

分析文档：[架构实现核对](docs/review/architecture-2026-10-30.zh.md)。每项可独立提交、验证和勾选；P0 优先秘密边界，P1 派发可靠性与内存保留。

- [x] ARCH-01 / P0：任务写入前隔离秘密字段 —— 证据:f24de37, 3927 测试全绿
- [x] ARCH-02 / P0：处理历史任务库中的秘密载荷 —— 证据:539df2c, 3933 测试全绿
- [x] ARCH-03 / P0：为人工验证码 SSE 增加账号所属 Agent 授权 —— 证据:2dbe091, ControlPlane 1344 + Agent 198
- [x] ARCH-04 / P1：控制平面 Agent 发送队列实现可观测背压 —— 证据:9fed6fa, 4069 测试全绿
- [x] ARCH-05 / P1：限制 Agent 待执行任务缓冲并向调度器反馈可接收容量 —— 证据:11daadd, Agent 205 + ControlPlane 1355
- [x] ARCH-06 / P1：补齐已派发但未开跑任务的租约策略 —— 证据:cdd7d29, Agent 208 + SqliteJobStore 53
- [x] ARCH-07 / P1：让取消消息对 Agent 排队任务生效 —— 证据:a102925, Agent 211 全绿
- [x] ARCH-08 / P1：移除或实际消费 EventBroker 两条无消费者全局队列 —— 证据:4042b71, EventBroker 12 + Notification/ControlPlaneApi 49
- [x] ARCH-09 / P2：为人工验证码交付增加短期确认与过期语义 —— 证据:单 commit（AuthChallengeOutbox + Program.cs:3495/3400 + Config.cs:44 + Cleaner）, ControlPlane 1372 + Agent 211
- [x] ARCH-D1：生成中英文 SVG 并接入双语文档首页、README 与架构页 —— 证据:`docs/assets/architecture/overview.{en,zh}.svg`
- [x] ARCH-D2：统一单控制平面 / SQLite 租约队列口径，移除外部 MQ 与双活演进承诺 —— 证据:双语 architecture 文档
- [x] ARCH-D3：修正凭据与验证码绝不经过控制平面的错误表述，区分 Agent 通道与 webhook 投影 —— 证据:双语 README、架构与一致性文档
- [x] ARCH-D4：恢复中文首页标题、卡片、代码块与表格的 Markdown 分行 —— 证据:`docs/index.zh.md`
- [x] ARCH-D5：将真实账号登录与真实开箱验证从 mock 项拆为独立未勾选项 —— 证据:41-4a / 41-4b

## GA Exit Criteria（5/5 达成 2026-09-15，2026-09-24 复核维持）

- [x] 主分支 build/test 稳定通过 —— 证据:4052 测试全绿（2026-10-13 基线）
- [x] 核心动作闭环：登录、2FA、游戏状态、激活 Key、库存/交易、基础 farming —— 证据:actions.md 全量目录（20b893d 同步）
- [x] 安全闭环：凭证加密存储、令牌持久化与轮换、敏感日志脱敏、审计 —— 证据:40.8-④ 规则钉入测试（ebbbb81, 267ccb3）
- [x] 可运维：OpenAPI、E2E、Docker、CI/CD、可观测性、性能基线 —— 证据:api/auth/testing/coverage 四校验脚本接入 CI format job
- [x] 文档闭环：架构、运行、运维、发布、故障排查齐备 —— 证据:docs 23 页，mkdocs --strict 绿（15d7b3c）

## 里程碑（全部完成）

- [x] P0 恢复可开发状态：修 PlayGamesAction、LoginAction、测试门禁进 CI —— 证据:343a521, 2603 测试
- [x] P1 M2 核心能力闭环：PlayGames 全量、RedeemKey 深化、Trading MVP、会话可靠性、登录测试 —— 证据:b148892, 2408 测试
- [x] P2 M5 安全闭环：FileCredentialStore、AES-GCM、密钥轮换工具、审计日志脱敏 —— 证据:2603 测试含安全面全绿
- [x] P3 M4 数据与爬虫：SteamWebHandler 产品化、缓存层、游戏数据动作 —— 证据:b148892（Redis 缓存后端）
- [x] P4 M3 插件系统：插件基础设施、官方首批插件、插件生态深化 —— 证据:a270efc, 2549 测试
- [x] P5 规模化运营（A/B/C 三方向）—— 证据:c66eec5
- [x] P6 Steam 功能纵深：计划任务、farming 循环、maFile 导入、背包 REST —— 证据:baf7efd
- [x] P7 市场闭环：挂单读取、批量撤单、挂单创建（dry-run 默认）—— 证据:c05ab53
- [x] P8 积分商店认领 —— 证据:2603 测试（2026-09-15 随 GA 盘点收口）
- [x] P9 游戏数据采集（批量动作 + 分片计划器 + 只读页）+ 字段字典 —— 证据:c5e06e7 + 76e82ab

## 维护轮（全部完成）

- [x] 覆盖率双 100%：行 22156/22156 + 分支 8494/8494（2026-10-13 实测，门禁 --min 100 --min-branch 100）—— 证据:tests/TESTING.md 基线表
- [x] ASF 式运营四阶段：每账号代理、异常账号检测、挂卡策略增强、插件生态 —— 证据:轮三十八（2549 测试）
- [x] 控制台代理分配、仓库号归集、背包查看面板 —— 证据:轮三十九（fe6878c, 7370121）
- [x] ASF 风格 CI 收紧：静态分析、NuGet 审计、覆盖率门禁、property 测试 —— 证据:轮三十六
- [x] 测试/覆盖率基线刷新、dashboard 扩展、gifts 白名单、成就管理、boost 策略化 —— 证据:轮三十四-三十七（2931 测试）
- [x] CI flake 治理：TracingTests 进程全局并发、BrokenTick 等待错位、vstest 收尾失速、MarketWatch 在飞失速、macos SQLite NRE 抖动 —— 证据:轮二十三-二十八
- [x] 剪版 v0.1.0-alpha.2：九个月 Unreleased 收口 + 依赖陈账 —— 证据:轮二十六
- [x] 故障排查手册、性能基线、控制页面修复、admin QR 扫码、假异步清理、幻影模型 ItemInfo 移除 —— 证据:轮十七-二十二

## V-next 收敛（2026-10-04 立项，全部完成）

- [x] P0-A 执行语义契约：ActionSafety 五级 + 注册表 + 派发 cap 2 —— 证据:7d17cec
- [x] P0-B 领域模型字典：docs/domain-model.md 十对象五要素 —— 证据:c75f2f6
- [x] P0-C Desired-State 收敛面：账户视图 + 强制收敛端点 —— 证据:96a45e2
- [x] P1-A 计数与 README 同步数字化 —— 证据:轮五十一
- [x] P1-B 品牌定位句（用户 2026-10-08 拍板按推荐默认执行）—— 证据:轮五十七
- [x] P1-C Plugin 平台补丁：configurationSchema + dependencies 依赖图 —— 证据:0269423
- [x] P1-D FakeSteam 确定性测试后端 —— 证据:轮五十六
- [x] P2 降级包十项（2026-10-10 拉起令激活）—— 证据:ebbbb81, 6db9cf7, c3ec242, 20141fd, 279c45f, 267ccb3, 88b7b9f, 9584de7, f952dd4
- [x] 控制中心脚本仓库与编排：脚本仓库、脚本执行、编排系统、调度接入 —— 证据:803e6ac, 10b58a6, 91171bf, 6ec7027

## CSGO 开箱 + CS2 背包接入（2026-10-11 用户令，41-4 除外全部完成）

- [x] 41-1 真实背包接入：InventoryPaginator 共用化 + case_inventory 动作 —— 证据:5478728
- [x] 41-2 开箱与背包关联：CaseLedger 台账 + case_consume 动作 —— 证据:5478728
- [x] 41-3 ToS 口径：门控默认关、输出带 mode、docs 同步 —— 证据:轮七十四
- [x] 41-5 门禁全绿才 push、commit 英文简短 —— 证据:06ce401..5478728
- [x] 41-6 插件改名 vapor.csgo + 门控真开箱（GC 协议对齐操作者自建 Steam-bot）—— 证据:f4b41c5, 26685b6
- [x] 41-7 Dota2（570）交易插件壳 —— 证据:dae07a5
- [x] 缺陷：文档首页向下滚动出现一大片空白（来源：用户 2026-10-14，卡片网格 4 卡占 3 列留洞 + 图标短代码缺 emoji 扩展）—— 证据:a4d4b28（mkdocs strict 绿）
- [x] 文档双语化并支持切换（来源：用户 2026-10-14，README 互链 + mkdocs-static-i18n 后缀双语，头部语言切换器按页互跳）—— 证据:c985d67（4052 测试全绿）
- [x] 后台界面重设计：Steam 风格为默认主题，其余主题可切换（来源：用户 2026-10-14；本阶段先出原型并放进文档展示，不改线上页面）—— 证据:7ff2c42（四主题原型 admin/dashboard/gamedata 齐全，mkdocs strict 绿）
- [x] docs 子目录双语配对（design/ research/ review/ 实盘 6 页，PATROL1018R2 完成）—— 证据:本 commit（mkdocs strict 绿，7 verify 脚本 GREEN，site/zh/ 六页全中文）
- [x] 审核通过：b9b1f71dbc06782cf19ca0a7c2a4d631e6720877..HEAD 未发现问题

## 审核发现

- [x] 审核发现：历史秘密清理 `PurgeSecretPayloadsAsync` 逐行 `UPDATE` 且未包事务，进程中断/取消会留下部分清理库，与文档「never a partially scrubbed database」不符 —— 修复:b0ba70f（jobs+tasks scrub 单事务提交，新增 trigger 失败回滚测试），ControlPlane 1338 全绿
- [x] 审核发现：`PurgeSecretPayloads_EndpointScrubsWhenConfirmed` 的 `Assert.DoesNotContain("endpoint-secret", scrubBody)` 为恒真断言（响应体从不回显载荷，无法证明秘密已清除）—— 修复:b0ba70f（scrub 后改查 API 投影断言 secret 消失且 keep 保留），ControlPlane 1338 全绿
- [x] 审核发现：purge 端点未校验 `backupPath` 与活动库路径不同，误传活动库路径会以同一文件为源和目标 —— 修复:b0ba70f（同一路径抛错并映射 400，新增端点拒绝测试），ControlPlane 1338 全绿
- [x] 审核发现：`AuthChallengeOutbox.Stage` 以未 trim 的 `e.AccountName` 为字典键，而 `TryGet`/`Acknowledge` 以 trim 键查询，账号名含首尾空白时重放无法命中 —— 证据:AuthChallengeOutbox.cs:40（Stage 改用 `Trim()` 落键）+ AuthChallengeOutboxTests.cs:136（`Stage_TrimsAccountName`），AuthChallengeOutboxTests 13/13 绿，ControlPlane 1376 全绿
- [x] 审核发现：`PollAuthChallengesAsync` 在 `ListSessions()` 为空时 `Task.WhenAll(空)` 立即完成且循环内无 await 延迟，外层 while 形成纯 CPU 忙循环 —— 证据:Program.cs:790（空会话列表改显式 `await Task.Delay(5s)` 再 continue）+ ProgramPollAuthChallengesTests（`PollAuthChallenges_NoSessions_WaitsBeforePollingAgain`），Agent 212 全绿
- [x] 审核发现：有界 `Wait` 缓冲使接收循环在 `tasks.Writer.WriteAsync` 阻塞（缓冲满）时无法读取后续 `task_cancel` 帧，排队任务取消可能错过生效窗口 —— 证据:AgentTaskDispatcher.cs（接收循环改非阻塞 `TryWrite`，满队入有序 overflow；执行循环消费后 `FlushOverflow` 回填，task_cancel 帧可继续读取并经 QueuedTaskCanceller 生效）+ Program.cs:413 + AgentTaskDispatcherTests，Agent 217 全绿
- [x] 审核发现：`Config.LoadFromEnvironment` 解析 `key=value` 绑定仅对 value 做 Trim，key 部分未 Trim，键两侧含空格时与 Bearer token 精确匹配失败（src/Vapor.ControlPlane/Config.cs:139）—— 修复:cbc0471（key 侧 Trim + 回归测试 `AgentKeyAgentIds_TrimsKeySide`）, ControlPlane 1377 全绿

## 代码深审 2026-11-01（requesting-code-review 流程；报告 [docs/review/2026-11-01-code-review.md](docs/review/2026-11-01-code-review.md)）

P0/P1 当场修复（一问题一 commit，各套测试全绿）：
- [x] P1 代理切换在等 disconnect 而非已完成 connect TCS，重连与旧 socket 拆除竞态 —— 修复:f35ec25（`_disconnectTcs` 由 OnDisconnected 落定；Disconnect 移出锁外防同步回调死锁），Steam.Core 1286 全绿
- [x] P1 断开后未清 `_playingGames`，重连后 `PlayGames(desired)` 被 SetEquals 幂等短路、farm 状态静默不上报 —— 修复:efaa760（OnDisconnected 加锁清空），Steam.Core 1286 全绿
- [x] P1 单 `_activeLoginAccountName` 槽被并发 Login 覆盖，LoggedOnCallback 错投另一账号 TCS —— 修复:94085ba（登录门控串行 send→callback 窗口），Steam.Core 1286 全绿
- [x] P1 会话事件直读共享 ingress 通道被多订阅者互抢（2FA 码可落入丢弃方）—— 修复:b7cd244（单广播者扇出至每订阅者通道），Steam.Core 1286 全绿
- [x] P1 编排线程原地改 `AccountRuntime` 队列而 REST 读同一列表实例，序列化抛 InvalidOperationException —— 修复:5cd501a（视图出副本 + 变更侧 copy-on-write），ControlPlane 1377 全绿
- [x] P1 故障的 auth-challenge SSE 流留下同轮兄弟订阅未拆除，重订阅叠读 —— 修复:6bd3296（每轮 linked token 取消全部兄弟），Agent 217 全绿

- [x] 质量门：覆盖率门禁在深审主干是红的（非抖动——同 SHA 两次绿靠部分报告漏报分母，第三次红暴露真缺口，run 41404115679）；本轮闭合：SqliteJobStore 五处缺口（StripSecretPayload 非字典分支、:memory: 早返、OS 条件比较改正向块、畸形 JSON 载荷、同路径抛错）+ Agent 四类（TaskLeaseRenewer 计时器循环排除并登记 TESTING.md、AgentTaskDispatcher Capacity/Dispose、QueuedTaskCanceller Clear、AuthChallengeOutboxCleaner 空区间）—— 证据:70dcdca（coverage-summary 双 100%：22524 行 / 8658 分支，ControlPlane 1386 + Agent 221 + Steam.Core 1289 全绿，format 与 7 个 verify 脚本全绿）

P2 待办（记档，未修）：
- [x] P2 docker-compose 默认 API key 未强制门控：`${VAPOR_ADMIN_API_KEY:-admin-dev-token}` 等兜底默认值，生产忘配环境变量即携已知 dev token 启动 —— 修复:05b1756（`:?` 缺失即 fail-fast + .env.example 模板 + CI 占位；compose config 缺值 rc15/提供 rc0，ControlPlane 1378 全绿，7 verify 脚本全绿）
- [ ] P2 `SetTaskResult` 任务行更新与 `RecomputeJob` 非同一事务，进程中断留下 task=Finished 而 job 仍 Running 中间态 —— 证据:SqliteJobStore.cs:985（UPDATE tasks，无事务）+ :1004（独立 RecomputeJob）
- [ ] P2 `RemoveSessionAsync` 中 `DisconnectAsync` 抛错则 `Dispose` 被跳过，会话泄漏 —— 证据:SessionManager.cs:178-181（应 try/finally 保证 Dispose）
- [ ] P2 任务取消仅投递给 `ListConnected()` 在线 agent：DB 已置 Canceled，离线 agent 复联收不到 task_cancel，跑完结果被 SetTaskResult 以 task not running 丢弃 —— 证据:Program.cs:3190 + AgentRegistry.cs:144（TryWrite 失败）
- [ ] P2 `DisconnectAsync` 以 `Task.Delay(100)` 固定睡眠等断开，竞态余量靠魔法数 —— 证据:SteamClientManager.cs:384（应改等 disconnect 事件，同 `_disconnectTcs` 机制）
- [ ] P2 94085ba 残余：登录被取消后迟到的 LoggedOnCallback 仍可能错投给下一个登录的 TCS —— 证据:SteamClientManager.cs:371-405 + :1596（取消窗口 + 并发登录 + Steam 响应晚到；根治需回调按 SteamID 路由）

P3 记录性（风格/健壮性，未修）：
- [ ] P3 无注释空 `catch {}`：SteamClientManager.cs:380（DisconnectAsync 吞 Disconnect 异常）、:1690（Dispose 同款）；Agent Program.cs:660（websocket 排水）、:462（currentTaskCts.Cancel）
- [ ] P3 魔法数：BotSession.cs:256（回调轮询 100ms）、:604（断开沉降 100ms）；RedisVaporCache.cs:105（`Thread.Sleep(200*attempt)` 库内重试同步阻塞）
- [ ] P3 库内 await 缺 `ConfigureAwait(false)`：SteamWebHandler.cs:174（GetAsync）、:191（PostAsync）；DataActions.cs:440；CsgoPlugin.cs:320；SessionManager.cs:477（_eventCallback.Invoke）
- [ ] P3 `"pending"` 状态字面量 ControlPlane Program.cs:878 重复 19 次，应提共享常量
- [ ] 审核发现：README 英/中首页新增「Steam Guard 验证码与凭据不出 Agent，跨线只有一个布尔值」与实现矛盾——人工验证码实经控制平面 SSE 携明文下发 Agent（Program.cs:3507 发布、:3419-3420 仅管理员剥码），任务 payload 凭据亦入控制平面持久化（secret-payload purge 端点），且同仓 docs/architecture.md:109 已正确描述该路径（README.md:29、README.zh.md:29）
- [ ] 审核发现：docs/architecture.md:116 将挑战流重放写成「still-acknowledged code」，与实现相反——outbox 仅重放未确认（staged）验证码，Acknowledge 即移除（AuthChallengeOutbox.cs:77-91、Program.cs:3404），api.md:85 的「still-unconfirmed」才是正确表述（docs/architecture.md:116）
- [ ] 审核发现：AgentTaskBuffer.CreateChannel 已无生产调用方（RunOnce 改用 AgentTaskDispatcher），仅测试仍引用，属死代码（AgentTaskBuffer.cs:40、Program.cs:372）
- [ ] 审核发现：SetAccountProxyAsync 并发切换时后到者覆盖 _disconnectTcs，先到者持有的 settle TCS 永不落定，只能靠 10s WaitAsync 超时兜底（SteamClientManager.cs:340-365）
- [ ] 审核发现：登录门控引入后，取消的登录迟到 LoggedOnCallback 仍可能经共享 _activeLoginAccountName 错投下一登录 TCS（SteamClientManager.cs:397-438、:1611-1622）；已作为 P2「94085ba 残余」记档，本轮复核确认仍在
- [ ] 审核发现：E2E 超时消息 `{ReconcileTimeout:s}` 仍会抛 FormatException——TimeSpan 单字符 `s` 按标准格式解析、非法即抛，d8dc8ca「fix timespan format」只换了类型未修根因（旧 `TotalSeconds:s`（double）同病）；超时发生时本应给出 TimeoutException+诊断，实际抛 FormatException 遮蔽 lastAgent/栈（已用仓库 SDK net10 实测 `TimeSpan.FromSeconds(180).ToString("s")` 抛 FormatException；应改 `{ReconcileTimeout:g}` 或 `TotalSeconds:F0`）（tests/Vapor.E2E.Tests/AccountOrchestrationE2ETests.cs:159）
- [ ] 审核发现：SubscribeAllEvents 播种「最近 ≤256 条历史」把订阅语义从「仅实时」改成「重放历史+实时」，且历史无时间上界（256 条事件在空闲期可横跨很久）；对启动后才订阅的消费者会重放陈旧 TwoFactorCodeNeeded——TwoFactorAutoResponder 的 TryAnswerAsync 会为新代码烧掉 60s 冷却并虚增 AnsweredCount（Handle2FACode 状态门控挡住真正投码，但冷却已燃），MonitoringPlugin 的 vapor_session_events_total 亦被重放虚增（当前消费者均在启动空历史时订阅一次，影响有限；但任何迟到/重启订阅都会踩中）（src/Vapor.Steam.Core/SessionManager.cs:335-345）
- [ ] 审核发现：TaskLeaseRenewer 的覆盖排除依据失实——「PeriodicTimer 被 dispose 或取消时必抛」只对取消成立；实测 dispose-during-wait / dispose 后调用均返回 false 不抛（net10）。结论（try 闭括号不可达）凑巧仍成立（RunAsync 内 timer 为方法局部 `using`，循环体无人能 dispose 它），但依据被写进 TESTING.md 新增行（`WaitForNextTickAsync 在取消/处置时必抛`），后续读者会据此误判其它 timer 泵（src/Vapor.Agent/TaskLeaseRenewer.cs:31、tests/TESTING.md:122）
- [ ] 审核发现：SubscribeAllEvents 的 `_eventLock` 线性化不变式（事件发布与订阅注册并发交错时「既不丢也不重」）无并发测试——新增测试只覆盖顺序情形（先发布后订阅 `SubscribeAllEvents_AfterEventsPublished_ReceivesSeededHistory`、先订阅后完成通道 `BroadcastEvents_SweepCompletesRegisteredSubscriber`），从未在「注册进行中事件同时发布」的交错下断言每事件恰好到达一次；该锁正是为此竞态而设（注释「never both and never neither」），回归会静默通过（src/Vapor.Steam.Core/SessionManager.cs:335-345、tests/Vapor.Steam.Core.Tests/Unit/SessionManagerTests.cs:370-412）

## 审核覆盖：77773bba..HEAD（2026-11-03 复审，只审不改）

- 复核确认（已记档，未新增）：`d8dc8ca` E2E `{ReconcileTimeout:s}` 单字符标准格式——net10 实测 `TimeSpan.ToString("s")` 抛 `FormatException`，超时路径遮蔽 TimeoutException/诊断（todo.md:130）；`122b22e` SubscribeAllEvents 播种 ≤256 条历史、无时间上界，重放陈旧 TwoFactorCodeNeeded 烧 2FA 冷却并虚增计数（todo.md:131）；`70dcdca` TaskLeaseRenewer 覆盖排除依据失实但结论凑巧成立（todo.md:132）。
- 本轮新发现 1 条：注册-发布并发交错的不变式缺并发测试（见上）。其余变更未见问题：DesiredStateReconciler 空白修正（copy-on-write 语义不变）、SqliteJobStore 比较改正向块（等价）、StripSecretPayload 改 internal（仅测试可见性）、ChannelClosedException 捕获移除（确不可达）、各新增测试断言与实现一致（GetFarmSummaries `?.ToList()` 副本、PurgeSecretPayloads 同路径抛错/畸形 JSON 不动/`:memory:` 早返、AgentTaskDispatcher/QueuedTaskCanceller/AuthChallengeOutboxCleaner 分支）。
