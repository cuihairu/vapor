# Vapor 需求清单

一条一句;完成项保留一行证据(commit + 测试数);过程叙述见 CHANGELOG 与 git log。

## 未完成

- [ ] 41-4 真实环境验收:起 agent 连真实账号拉 730 背包并断言箱子/钥匙,CaseOpening 新增 action 全流程 curl 跑通,再补测试。挂起等操作者提供可用凭据(2026-10-13 三探确认两测试账号被 Steam 侧拒绝 InvalidPassword,非 Vapor 缺陷;本地可跑面已有 E2E 双进程留证)。解封=操作者给可用凭据,先跑裸探针验密码再进全链路。暂停:等用户新账号自测(2026-10-13)。

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
- [x] 41-4 真实环境实测 — 见「未完成」

- [x] 缺陷：文档首页向下滚动出现一大片空白（来源：用户 2026-10-14）——根因：卡片网格 4 卡占 3 列留洞 + 图标短代码缺 emoji 扩展渲染成原文；补满 6 卡成两整行、图标补 material 前缀 — 证据:a4d4b28(mkdocs strict 绿)
- [x] 文档双语化并支持切换（来源：用户 2026-10-14）——README 英文主文档 + README.zh.md 互链；文档站 mkdocs-static-i18n 后缀双语（index/getting-started/running 三页有中文版，余页回退英文），头部语言切换器按页互跳 — 证据:c985d67(全量 4052 绿)

- [x] 后台界面重设计：Steam 风格为默认主题，其余主题可切换（来源：用户 2026-10-14）
  - 现状：后台在 `src/Vapor.ControlPlane/wwwroot/`（`admin.html` / `dashboard.html` / `gamedata.html`），用户认为现有主题**太丑**
  - **默认主题 = Steam 风格**（深蓝灰底、卡片式布局、克制的高亮色）；其它主题作为**可切换项**
  - **本阶段先出原型并放进文档展示**（`docs/design/prototypes/`，与 doggo / awn 同一套做法），**先不改线上页面**
  - 验收：原型覆盖 admin / dashboard / gamedia 三个页面的实际布局（不是泛泛的配色方案），并在文档中展示截图或可打开的静态页
  — 证据:7ff2c42(四主题原型 admin/dashboard/gamedata 齐全,mkdocs strict 绿)


- [x] docs 根目录 23 页实质文档全部双语配对 + 首屏语言切换器（来源：用户 2026-10-16 执行单）——19 篇新译中文孪生 + 1 篇占位（api 920 行/plugins 894 行/architecture/production 等全部真实翻译,命令/路径/代码块/专有名词原样保留;testing.zh.md 为占位标注页）+ 3 篇既有（index/getting-started/running）；6 篇中文占英文槽位的文档（data-dictionary/feature-matrix/performance/session-engine/vnext-convergence-plan/审计-文档一致性）英文槽改写为真英文、中文保留在 .zh.md；testing.md（gitignored CI 镜像,权威源 tests/TESTING.md 为 1093 行中文）按「过长只标注不硬译」口径在源文件标注暂无译版；23 页 EN 首行插 `English | [简体中文](zh/<page>/)`、孪生页首行 `简体中文 | [English](../<page>/)`；verify-api-docs.py MATRIX_RE 改双语锚（负探针 FAIL 验证） — 证据:本 commit(mkdocs strict 绿,7 个 verify 脚本 GREEN,site/zh/ 23 页全中文)
- [ ] docs 子目录双语配对（design/ research/ review/ 共 5 页,2026-10-16 执行单明确下一轮做）
