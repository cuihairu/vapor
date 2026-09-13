# Vapor 完成交付计划（从当前 Alpha 到可用 GA）

> 目标：把当前"骨架完整、能力未闭环"的状态推进到"可稳定运行、可运维、可扩展"的生产可用版本。

## 0. 现状与完成定义

### 当前状态快照（2025-03-11）

- **构建**: `dotnet build` 通过，0 错误 0 警告。
- **测试**: 530 个测试全部通过（41 个测试文件，覆盖 Unit / Integration / Performance）。
- **CI**: 多平台（Ubuntu / Windows / macOS）构建 + 测试门禁已就绪。
- **已实现 Actions（15 个）**: Ping, Echo, Login, Idle, PlayGames, RedeemKey, GetInventory, SendTradeOffer, AcceptTradeOffer, DeclineTradeOffer, CancelTradeOffer, GetGameInfo, SearchGames, GetPrice, GetMarketListings。
- **已实现基础设施**: ControlPlane（15+ API 端点 + SSE 事件流 + SQLite 持久化 + 审计日志持久化/查询 + Admin UI）、Agent（WebSocket 隧道 + 全部 Action 注册）、SessionEngine（BotSession 状态机 + SessionManager + SteamClientManager）、SteamWebHandler、SteamTradeClient、FileCredentialStore（v2 加密存储 + 备份恢复 + 版本迁移）+ AES-GCM 加密（兼容历史 AES-CBC 数据）。
- **整体完成度**: ~72%。

### GA Exit Criteria

1. `dotnet build` / `dotnet test` 在主分支稳定通过。
2. 核心动作闭环：登录、2FA、游戏状态、激活 Key、库存/交易、基础 farming。
3. 安全闭环：凭证加密存储、令牌持久化与轮换、敏感日志脱敏、审计。
4. 可运维：OpenAPI、E2E、Docker、CI/CD、可观测性、性能基线。
5. 文档闭环：架构、运行、运维、发布、故障排查齐备。

---

## 1. P0 阶段：恢复可开发状态 ✅ 已完成

- [x] 修复 `PlayGamesAction` 重复类型定义与静态/实例混用。
- [x] 补 `PlayGamesAction` 单测（解析输入、play/stop/idle 分支、异常输入）。
- [x] `dotnet build Vapor.sln` 通过。
- [x] `LoginAction` 改为真正触发 session 登录流程，对接 `BotSession` 登录命令路径。
- [x] 校准测试文档统计，CI 增加编译 + 单测必过门禁。

---

## 2. P1 阶段：M2 核心能力闭环（✅ 100% 完成）

### 2.1 PlayGames 全量实现 ✅ 已完成

- [x] 在 `SteamClientManager` 中实现真实 Play/Stop（含多 AppID）。
- [x] 支持输入格式：`123`、`123,456`、`id/123`，统一规范化（`PlayGamesPayloadParser`）。
- [x] play/stop/idle 分支完整实现。

### 2.2 RedeemKey 深化（⚠️ 部分完成）

- [x] 基础错误映射（AlreadyOwned、DuplicateRequest、RateLimitExceeded、InvalidParam、Timeout）。
- [x] Key masking 安全日志。
- [x] 解析 Steam 回包中 app/package/receipt 明细，丰富输出结构。
- [x] 增加可观测字段（请求 ID、耗时、结果码）。
- [x] 增加重试策略（瞬时失败自动重试）。

### 2.3 Trading MVP ✅ 已完成

- [x] `GetInventoryAction`（支持分页，50k+ 物品）。
- [x] `SendTradeOfferAction`（Trade URL 解析、物品资产解析）。
- [x] `AcceptTradeOfferAction` / `DeclineTradeOfferAction` / `CancelTradeOfferAction`。
- [x] `SteamTradeClient` 完整实现（库存、报价 CRUD、IEconService API）。
- [x] `TradeModels` 数据模型（InventoryItem、TradeOffer、TradeOfferState 等）。
- [x] 交易流程校验增强（资产归属验证、报价状态机、风控节流）：
  - `TradeOfferStateMachine`：合法转换校验（accept 仅限收到的 Active 未过期报价 + partner 匹配；decline/cancel 方向校验）。
  - `TradeAssetValidator`：发送前验证资产存在、可交易、无冷却、数量充足（重复引用聚合）。
  - `TradeRateLimiter`：单账户滑动窗口限额 + 并发互斥（默认 5 次/5 分钟，可配置、可注入时钟）。
  - `ISteamTradeClient` 接口提取 + `GetOwnSteamId`（steamlogin cookie 解析），Trade URL 支持 64 位 partner。
  - Action 层默认开启校验（`skip_verification` / `verify_state=false` 可显式跳过），Agent DI 注册共享限流器。

### 2.4 会话可靠性增强（⚠️ 部分完成）

- [x] Token 持久化（FileCredentialStore 存储 RefreshToken / AccessToken）。
- [x] Agent 断线重连（指数退避 500ms → 10s）。
- [x] AccessToken 自动刷新（过期前主动续期）。
- [x] RefreshToken 续期策略。
- [x] 断线重连策略可配置化（退避上限、最大重试次数）。
- [x] 进程重启后会话恢复（从持久化 Token 自动重建会话）。

### 2.5 登录流程测试补全

- [x] 登录成功/失败/需验证码/需 2FA 的单测。
- [x] 登录流程集成测试（模拟 Steam 响应）。

---

## 3. P2 阶段：M5 安全闭环（✅ 100% 完成）

### 3.1 凭证体系（✅ 完成）

- [x] `ICredentialStore` 接口定义（Save/Get RefreshToken、AccessToken、Revoke、HasCredentials）。
- [x] `FileCredentialStore` 生产级实现（`~/.vapor/credentials.json`，SemaphoreSlim 并发安全，懒加载）。
- [x] 支持多密码来源：明文、AES、环境变量、文件（`VaporCryptoHelper`）。
- [x] 凭证文件损坏恢复（备份 + 回滚，`.bak` 自动恢复）。
- [x] 凭证版本化与迁移策略（v1 明文 → v2 加密格式，加载时自动迁移）。

### 3.2 加密与密钥管理（✅ 完成）

- [x] AES-256-CBC 加密实现（随机 IV、Base64 编码）。
- [x] 自定义密钥支持（`SetEncryptionKey()`，一次性设置）。
- [x] 升级为 AES-GCM + 随机 nonce + 完整性校验。
- [x] 禁止默认密钥 "Vapor" 用于生产（启动检查 + 告警）。
- [x] 引入主密钥配置规范（`VAPOR_ENCRYPTION_KEY` / `VAPOR_ENCRYPTION_KEY_BASE64` / `VAPOR_ENCRYPTION_KEY_FILE`，适配 KMS / Docker secrets）。
- [x] 密钥轮换工具（`tools/Vapor.KeyRotation` CLI，支持 base64/file/env 密钥格式、dry-run、失败中止）。

### 3.3 安全审计与脱敏（✅ 完成）

- [x] RedeemKey / SteamClientManager 中的 Key masking。
- [x] 统一敏感文本/JSON 脱敏工具（密码、令牌、验证码、key 字段）并接入 Agent 高风险日志入口。
- [x] ControlPlane 关键入口结构化审计日志（配置修改、任务创建/取消、验证码提交、会话事件上报）。
- [x] 审计日志持久化与查询（`SqliteAuditStore` + `GET /v1/audit/logs` 分页/过滤 API，存储前统一脱敏）。
- [x] 审计日志覆盖登录（session.login）、交易/激活码任务结果（task.result.reported）、验证码提交、配置修改。
- [x] 凭证文件权限检查与启动告警（Unix 下自动收紧为 600，过宽权限告警并修复）。
- [x] 全链路日志脱敏（`RedactingLoggerProvider` 输出层统一拦截：消息、结构化状态、scope、异常均脱敏；Agent 已接入 `AddRedactingConsole`）。

---

## 4. P3 阶段：M4 数据与爬虫能力（✅ 100% 完成）

### 4.1 Steam Web API 客户端产品化（✅ 完成）

- [x] `SteamWebHandler`：重试（指数退避，最多 5 次）、限流（1 req/s，可配置）、Cookie 管理。
- [x] 请求头规范（User-Agent、Accept、Referer、Origin）。
- [x] 429/5xx 退避策略增强（区分限流 vs 服务异常：429 尊重 Retry-After（有上限），5xx 指数退避）。
- [x] 统一 HTTP 中间件（`HttpCircuitBreaker` 熔断：Closed/Open/HalfOpen + 单探针；`WebRequestMetrics` 指标采集：成功率/429/5xx/网络失败/重试/熔断拒绝）。

### 4.2 数据模型与缓存（✅ 基本完成）

- [x] `GameInfo` / `ItemInfo` / `PriceOverview` / `GameSearchResult` 数据模型定义（含缓存 key 生成与 FetchedAt 新鲜度标记）。
- [x] 缓存层落地（`IVaporCache` 接口 + `MemoryVaporCache`：TTL、LRU 淘汰、hit/miss 统计、单飞行防击穿、可注入时钟）。
- [x] 增量更新策略与缓存失效策略（`SteamCacheTtl` 分级新鲜度：search 1h/6h、game 30min/2h、market 5min/30min、price 3min/15min；`GetOrSetStaleWhileRevalidateAsync` 过期宽限窗口内即时返回旧值 + 后台单飞行刷新；`force_refresh` payload 强制拉取回填；`RemoveByPrefix` 前缀失效 + `cache_invalidate` Action（prefix/clear_all）；Monitoring 增 `vapor_cache_stale_hits_total`）。
- [x] 可选 Redis 后端实现（`RedisVaporCache`：`VAPOR_REDIS` 启用，JSON 信封 + 绝对新鲜/陈旧过期时间戳，跨实例 SWR 单飞行锁（SET NX PX + token 释放），SCAN 前缀失效，仅清理自身索引键不 FLUSHDB；`RedisCacheEntryTests` 纯逻辑测试 + `VAPOR_TEST_REDIS` 门控集成测试，CI 有专属 Redis service job）。

### 4.3 新动作（✅ 完成）

- [x] `GetGameInfoAction`（获取游戏详情，appdetails API，缓存接入）。
- [x] `SearchGamesAction`（搜索游戏，storesearch API，limit 1-50，缓存接入）。
- [x] `GetPriceAction`（获取价格信息，price_overview 提取，缓存接入）。
- [x] `GetMarketListingsAction`（获取市场列表，market search render API，分页 + 缓存接入）。
- [x] `SteamStoreApiClient`（三个数据源统一客户端）+ `MarketListing`/`MarketListingsPage` 模型。
- [x] 缓存策略：payload `cache_ttl_seconds` 覆盖（0 = 禁用），Agent 共享 4096 容量 / 10 分钟默认 TTL 缓存。

---

## 5. P4 阶段：M3 插件系统（✅ 100% 完成）

### 5.1 插件基础设施（✅ 完成）

- [x] `Vapor.Plugins.Core`（发现 `PluginDiscovery` + plugin.json 清单、加载 `PluginLoader`、隔离 `PluginLoadContext` 可卸载 ALC（Vapor 契约程序集与宿主共享类型标识）、卸载 `PluginManager.UnloadAsync` + ALC 回收验证）。
- [x] 插件 API：`IPlugin` / `IPluginContext` / `IPluginHostServices` + 能力接口 `IActionPlugin`（贡献 Steam.Core `IAction`）/ `ICommandPlugin`（`IPluginCommand`）/ `IWebApiPlugin`（宿主无关的 `PluginWebRoute`）。
- [x] 版本兼容策略（`PluginApi` SemVer：major 必须一致，插件 minor ≤ 宿主 minor，prerelease/build 忽略）。
- [x] `PluginManager` 生命周期（LoadAll/Load/Unload/重载、失败隔离报告 `PluginLoadReport`、`PluginLoaded`/`PluginUnloading` 事件）。
- [x] `ActionRegistry.Unregister`（插件卸载时移除其贡献的 Action）+ Agent 集成（`VAPOR_PLUGINS_DIR` 或 `./plugins`，加载插件并注册 Action，退出时卸载）。
- [x] 测试：`Vapor.Plugins.Core.Tests`（46 个测试）+ `Vapor.Plugins.TestPlugin` 示例插件程序集。

### 5.2 官方插件首批

- [x] MobileAuthenticatorPlugin（TOTP、确认哈希、时间同步、交易确认列表/响应，59 个测试）。
- [x] MonitoringPlugin（Prometheus 文本端点 + get_metrics Action + 插件 Web 路由；动作/会话/缓存/运行时指标；Grafana 面板模板 + Prometheus 抓取配置，21 个测试）。

### 5.3 P4 深化（进行中，四个方向）

- [x] 方向 1：事件订阅 + 配置 API 正式化。
	- [x] `IEventPlugin` 能力接口（`OnSessionEventAsync(SessionEvent, CancellationToken)`，复用 SessionManager 既有事件流）。
	- [x] `PluginEventDispatcher`（订阅注册/移除引用幂等、Start 幂等、单插件 handler 异常隔离并日志、DisposeAsync 停泵）。
	- [x] `PluginConfigurationExtensions`（GetString/GetInt32/GetBool 扩展方法；优先级 env > config > fallback；bool 接受 true/false/1/0/yes/no；int 支持 min/max 范围校验）。
	- [x] MonitoringPlugin 配置读取重构为扩展方法（env 覆盖：VAPOR_METRICS_HOST/PORT/PATH）。
	- [x] Agent 接线：PluginLoaded/PluginUnloading 事件挂接 dispatcher 注册/移除，退出时 DisposeAsync。
	- [x] 测试：PluginEventDispatcherTests（6）+ PluginConfigurationExtensionsTests（12），Plugins.Core.Tests 共 67 个。
- [x] 方向 2：插件权限/信任模型。
	- [x] manifest 新字段：`trust`（unknown/community/official，解析时规范化小写、非法值报错）与 `permissions`（actions/commands/web/events，去重规范化、未知权限报错）。
	- [x] 宿主策略 `PluginManagerOptions`：`MinimumTrust`（低于门槛的插件在任何插件代码执行前拒绝）+ `RequirePermissionsDeclared`（严格模式：实现能力接口但未声明权限 → 加载失败）。
	- [x] 最小权限默认：宽松模式下未声明的能力被剥离（Actions/Commands/Routes 不注册、事件订阅不挂 dispatcher）并告警。
	- [x] `LoadedPlugin.GrantedPermissions` 暴露实际授予的权限；Agent 事件订阅挂接需 granted `events`。
	- [x] 官方插件清单补 trust=official + permissions 声明（MobileAuthenticator: actions；Monitoring: actions+web）。
	- [x] 测试：PluginTrustTests（18 个：清单解析规范化/信任门禁/能力剥离/严格策略），Plugins.Core.Tests 共 85 个。
- [x] 方向 3：第三个官方插件 MarketWatch（价格阈值监控 + webhook 推送，实战检验插件 API）。
	- [x] `Vapor.Plugins.MarketWatch`：market_watch_add/remove/list 三个 Action（app_id + per-watch threshold/cc 覆盖）+ 后台轮询（匿名 SteamWebHandler 拉公开 price overview）。
	- [x] 移动基线阈值模型：首次取价记基线；涨跌幅 ≥ 阈值触发日志 + webhook JSON 推送并重置基线（稳价不重复告警）。
	- [x] 配置（manifest configuration + env 覆盖）：check_interval_seconds（默认 300，下限 10）/ threshold_percent（默认 10）/ country / webhook_url；`PluginConfigurationExtensions` 补 `GetDecimal`。
	- [x] 干净 Shutdown（停轮询、释放 WebHandler）+ `IAsyncDisposable`；清单声明 trust=official + permissions=[actions]。
	- [x] 测试 24 个：store/阈值评估、action 全流程、轮询→告警→webhook 集成（fake client + handler）、**经真实 PluginManager/ALC 实战加载验证权限门禁与 Action 注册**。
- [x] 方向 4：插件开发指南文档。
	- [x] `docs/plugins.md`：快速上手、清单全字段（含 trust/permissions/configuration）、生命周期七步、SemVer 兼容规则、信任与权限模型（最小权限默认 + 宿主策略）、四个能力接口示例、配置扩展方法与 env 覆盖、宿主服务、隔离与卸载规则（共享契约/私有依赖/Shutdown 清理/静态泄漏）、调试技巧、官方插件清单、打包清单。
	- [x] README docs 索引加链接；production.md 新增 "Plugins" 小节（加载失败隔离、宿主策略、MarketWatch 配置矩阵）；architecture.md 可扩展性描述更新为真实插件系统。

---

## 6. 横向工程化（✅ 100% 完成，随各阶段并行推进）

### 6.1 文档与接口

- [x] OpenAPI / Swagger 基础配置。
- [x] 架构文档（`docs/architecture.md`）。
- [x] 会话引擎文档（`docs/session-engine.md`）。
- [x] 测试文档（`tests/TESTING.md`）。
- [x] OpenAPI 完整化（全部 22 个端点补齐 tags/summary/响应码与 ErrorResponse schema 声明；bearer 鉴权文档级声明验证生效）。
- [x] 生产部署指南（`docs/production.md`：拓扑、配置矩阵、安全加固、备份/升级、扩容、监控）。
- [x] 故障排查手册（`docs/troubleshooting.md`：诊断工具箱 + 症状→诊断→处置清单）。

### 6.2 测试体系

- [x] Steam.Core 单测（15 个测试文件，289 个测试方法）。
- [x] 集成测试（SessionWorkflowTests）。
- [x] 性能测试（ConcurrencyTests）。
- [x] ControlPlane 单测（API / 审计 / 存储 / 调度，38 个测试）。
- [x] Agent 单元测试（`Vapor.Agent.Tests`，41 个测试：重连策略默认值/环境变量解析/指数退避封顶/重试上限、任务执行器凭证分支（password/pass 别名、refreshToken/snake_case、无凭证回退存储会话、取消与异常映射）、WebSocket URL 构建）。
- [x] E2E 测试（`Vapor.E2E.Tests`，5 个测试：spawn 真实 ControlPlane + Agent 进程 + 临时 SQLite/审计库，stub 会话模式隔离 Steam 依赖；覆盖 echo 任务全链路闭环（创建→调度→WS 派发→执行→结果落库）、未知 action 的调度行为（保持 queued + 持续重试 + 取消清理）、job.created 审计断言、未认证/agent key 越权拒绝、healthz）。
- [x] 性能基准扩展（`Vapor.ControlPlane.Tests/Performance/ControlPlaneBenchmarks.cs`，4 个基准：队列吞吐 500 job 创建 ~5.2k/s + claim/finish ~2k/s、4 并发 claimer 200 任务恰好一次派发、EventBroker 600 订阅者 × 100 事件扇出、50 并发 SSE 连接全收；宽松上限断言防 CI 抖动，实测数字见 `tests/TESTING.md`）。

### 6.3 发布与运维

- [x] ControlPlane Prometheus /metrics 端点（`vapor_controlplane_tasks_by_status` gauge + `vapor_controlplane_agents_connected`，compose 抓取已接入）。
- [x] Prometheus 告警规则（`deploy/prometheus/alerts.yml`：agent 侧 Down/TargetMissing/ScrapeSlow + 控制面 Down/QueuedBacklog/StuckRunning，compose 自动挂载加载）。
- [x] CI/CD 多平台构建（Ubuntu / Windows / macOS，Debug / Release）。
- [x] Codecov 覆盖率上报。
- [x] Docker 镜像（ControlPlane + Agent，含 Monitoring 插件、非 root 用户、CI 构建门禁）。
- [x] docker-compose 本地编排（含 observability profile：Prometheus + Grafana 自动 provisioning）。
- [x] 自动发布流水线（tag 触发：5 RID zip + GHCR 镜像，正式版更新 :latest；回滚即固定上一镜像 tag，见 `docs/production.md`）。
- [x] 可观测性：结构化日志（脱敏）、指标（MonitoringPlugin Prometheus 端点 + Grafana 面板 + compose observability profile）。
- [x] 可观测性增强：分布式追踪（OpenTelemetry，`Vapor.ControlPlane`/`Vapor.Agent` ActivitySource，WSMessage 携带 W3C traceparent 跨隧道传播，`OTEL_EXPORTER_OTLP_ENDPOINT` 启用 OTLP 导出，默认关闭零开销）；dispatch 失败计数器（`vapor_controlplane_dispatch_failures_total{reason=...}`）已进 /metrics。

---

## 7. 建议里程碑排期

| 阶段 | 周期 | 完成度 | 说明 |
|------|------|--------|------|
| P0 构建恢复 | Week 1 | ✅ 100% | 已完成 |
| P1 M2 核心能力 | Week 2-4 | ✅ 100% | 交易校验增强已完成 |
| P2 安全闭环 | Week 5-7 | ✅ 100% | 日志 Provider 级脱敏已完成 |
| P3 数据能力 | Week 6-9 | ✅ 100% | 数据动作 + 分级缓存（内存/Redis）+ 失效闭环已完成 |
| P4 插件系统 | Week 8-12 | ✅ 100% | 基础设施 + MobileAuthenticator + Monitoring 官方插件已完成 |
| GA 收口 | Week 10-12 | ✅ 100% | Docker/compose/可观测性/E2E/发布流水线/部署与排障手册/OpenAPI 完整化/追踪与 Redis 缓存均已就绪 |
| P5 规模化运营 | Week 13-16 | ✅ 完成 | 账户农场编排 + 通知/自动化闭环 + 质量与协议韧性（见第 10 节） |
| P6 功能纵深 | Week 17+ | 📋 已立项 | 卡牌 farming 闭环 + 交易与确认闭环 + 互操作与认领（见第 11 节，对标矩阵 `docs/feature-matrix.md`） |

---

## 8. 风险清单与缓解

| 风险 | 缓解措施 |
|------|----------|
| Steam 协议/接口变化导致行为不稳定 | 建立协议适配层，隔离 SteamKit2 变化 |
| 交易与认证流程边界复杂，回归成本高 | 高风险流程先做 contract tests + replay tests |
| 安全改造（加密/密钥管理）易引入兼容问题 | 灰度开关与数据迁移脚本，逐步切换 |

---

## 9. 下一步优先事项

1. ~~P3 收尾: 4 个数据 Action + 缓存接入~~（已完成）。
2. ~~P2 推进: 429/5xx 退避增强 + 熔断/指标~~（已完成）。
3. ~~P4 推进: 插件基础设施（Vapor.Plugins.Core：发现、加载、隔离、卸载 + IPlugin API）~~（已完成）。
4. ~~P4 推进: MobileAuthenticatorPlugin（TOTP、确认哈希、时间同步）~~（已完成，已注册进 Vapor.sln）。
5. ~~P4 推进: MonitoringPlugin（指标导出、Grafana 面板模板）~~（已完成：Prometheus 端点 + 面板模板 + compose observability profile）。
6. ~~横向: Docker 镜像与 compose 编排、可观测性（Prometheus 指标导出）~~（已完成）。
7. ~~横向: Agent 单测与集成测试、E2E 测试（控制面 + Agent + SQLite + 模拟 Steam 依赖）~~（已完成：`Vapor.Agent.Tests` 41 个 + `Vapor.E2E.Tests` 5 个）；**剩余: 自动发布流水线**。
8. ~~横向: 生产部署指南、故障排查手册、OpenAPI 完整化、自动发布流水线与回滚~~（全部完成：`docs/production.md` + `docs/troubleshooting.md` + OpenAPI 22 端点注解 + release workflow 补齐 GHCR 镜像发布与打包文档）。
9. P5 推进（2026-09-12 定案，实施顺序 A → C → B）: ~~**方向 A 账户农场编排**~~（✅ 已完成）→ ~~**方向 C 通知与自动化闭环**~~（✅ 已完成）→ ~~**方向 B 质量与协议韧性**~~（✅ 已完成：统计修正 + contract tests 抓到真实上游漂移 + WS 协议 replay tests + ISteamTransport 协议适配层 + 覆盖率管道修复与 74.3% 真实基线）。**P5 三个方向全部完成。**
10. P6 推进（2026-09-12 立项）: **Steam 功能纵深**——实施顺序 P6-1 卡牌 farming 闭环 → P6-2 交易与确认闭环（前两组均为 GA 出口条件 #2 未闭环项）→ P6-3 互操作与认领；对标矩阵与取舍依据见 `docs/feature-matrix.md`，完整清单见第 11 节。

---

## 10. P5 阶段：规模化运营（✅ 100% 完成：A ✅ / C ✅ / B ✅，2026-09-12）

### 10.1 方向 A：账户农场编排（✅ 已完成，2026-09-12）

- [x] `/v1/accounts` 资源 CRUD：账户名、期望状态（`offline`/`online`/`idle` + 挂机 appid 列表）、期望 region/agent 标签、备注；凭证不入 CP（沿用 agent 侧 FileCredentialStore 与现有 config/account 体系，CP 只存元数据与期望状态）。
- [x] 期望状态编排器 `DesiredStateReconciler`：周期对账（期望 vs `SessionTracker` 实际），为偏离账户挑选 capable agent → 下发 login job → 掉线自动重登录；agent 下线时按标签重平衡到其它 agent。
- [x] 编排策略可配置：每 agent 并发账户上限、对账间隔、重试上限与冷静期（防登录风暴）、dry-run 模式（只报告偏差不执行）。
- [x] 账户生命周期动作：enable / disable / remove（disable 停会话不清配置，remove 清理）。
- [x] 账户聚合视图：`GET /v1/accounts/{name}`（期望状态、当前 agent、实时会话状态、最近任务结果、待处理验证码）+ `GET /v1/accounts` 列表过滤（按状态/region/agent）。
- [x] jobs/sessions API 补按账户过滤；编排决策与账户变更接入审计日志。
- [x] E2E 测试：声明账户 → 自动分配 stub agent 登录 → kill agent → 重平衡到备用 agent。

> 实现要点：`AccountSpec`/`AccountStore`（ConfigVersion 乐观并发，凭证不入 CP）；`DesiredStateReconciler` 8 步对账循环（login 派发 / idle 派发 / 失败快照计数 / 指数退避冷静期 `60s × 2^(n-1)` 封顶 15min / 连续失败节流 / spec 版本 bump 重置预算 / dry-run 偏差报告 / agent 丢失重平衡）；编排指标 `vapor_controlplane_reconcile_actions_total` + `accounts_by_desired_state`；审计 action `account.spec.updated/enabled/disabled/removed` + `account.reconciled`。前置修复：agent 端全量会话状态上报（此前仅验证码挑战事件进 CP）。E2E：双 stub agent + kill 首选 agent → 重平衡到备用 agent。

### 10.2 方向 C：通知与自动化闭环（✅ 已完成，2026-09-12）

- [x] 核心通知系统：`INotificationSink` 抽象 + webhook 实现（HMAC 签名、指数退避重试、失败隔离），订阅 EventBroker 事件流（job/session/auth/风控）；规则化过滤（事件类型、账户、region）。（✅ 2026-09-12：`NotificationSinks.cs` 规则模型 + `WebhookNotificationSink`（`X-Vapor-Signature: sha256=HMAC("{ts}.{body}")`、`base×2^attempt` 退避）；`NotificationService` 三泵订阅 EventBroker，逐 sink 规则过滤 + try/catch 失败隔离，auth challenge 只外发 `codeSupplied` bool 绝不外发验证码；`Vapor_WEBHOOK_NOTIFICATIONS_*` 5 参数配置 + `/metrics` 派发指标。15 个新测试。）
- [x] 2FA/验证码自动提交闭环：`AuthCodeNeeded`/`TwoFactorCodeNeeded` 事件 → 优先由 MobileAuthenticator 插件（本地 TOTP + 确认哈希）自动应答 → 无本地密钥时回落人工 SSE 通道；全自动模式需显式开启。（✅ 2026-09-12：`SteamTotp`/`SteamTimeSynchronizer` 移入 Steam.Core（宿主零插件编译依赖，`DecodeSecret` 转 public）；`ICredentialStore`/`FileCredentialStore` 扩展加密 `SharedSecret`（复用 AES-GCM v2 路径）；`TwoFactorAutoResponder` 订阅会话事件 → 查本地 secret → 60s/账户冷却 → `Provide2FACode`，无 secret 不占冷却、自然回落人工通道；插件新增 `save_shared_secret` action（经 `Host.Services` 取 `ICredentialStore`）；agent 端 `AGENT_2FA_AUTO_SUBMIT=true` 显式开启（默认关）+ Steam 服务器时间同步循环（启动 + 每小时）。SteamTotp/TimeSynchronizer/新 responder 19 个测试迁移或新增。email 验证码无法本地生成，天然走人工通道。）
- [x] 计划任务：job 增加 schedule 字段（interval 或 cron 表达式）创建周期任务；处理 missed/overlap 策略（跳过或排队）；持久化下次触发时间（复用 tasks 表迁移模式）。（✅ 2026-09-12：`POST /v1/jobs` 接受 `schedule{intervalSeconds|cron, missed, overlap}` 创建模板 job（`JobStatus.Scheduled`、无 tasks、持久化下次触发点）；`RecurringJobScheduler` 每秒扫描到期模板，原子创建派生 job（复制 action/targets/payload，`meta.scheduledFrom`，`parent_job_id` 列支持 overlap 查询）并推进模板；missed=skip 丢弃停机期间触发点 / run_once 补跑一次（标记 `scheduledMissedCount`）；overlap=skip 上一次未完成则顺延 / allow 并行；cancel 模板即停止周期；cron 永不匹配自动退役；cron 用 Cronos（5 字段 UTC），`ScheduleClock` 隔离；jobs 表自动迁移 + 到期/父 job 索引；metrics `vapor_controlplane_schedule_triggers_total{outcome}`。27 个新测试（ControlPlane 113→140），E2E 6 全过。）
- [x] ControlPlane /metrics 补通知派发指标（sent/failed/retried）与编排指标（accounts_by_state、reconcile_actions_total）。（✅ 编排指标随方向 A 落地：`vapor_controlplane_reconcile_actions_total` + `accounts_by_desired_state`；通知派发指标随 10.2 提交 1 落地：`vapor_controlplane_notifications_total{sink,outcome}` + `vapor_controlplane_notification_retries_total{sink}`。）

### 10.3 方向 B：质量与协议韧性（✅ 已完成，2026-09-12）

- [x] 统计修正（可先行）：TESTING.md 测试统计刷新（当前停更于 268，实际 822）；P1/P2/横向过时标题修正（已完成）。（✅ 2026-09-12：刷新为全解决方案 919 基线（8 个测试项目逐类明细），覆盖率章节改为 coverlet.collector/cobertura 管道 + 真实 44.6% 基线表，修正 net8.0 过时提示。）
- [x] SteamKit2 协议适配层（风险清单承诺）：提取 `ISteamTransport` 类隔离接口，SteamKit2 类型不外泄出 Core 内部，协议升级只动适配层。（✅ 2026-09-12：`ISteamTransport` 协议无关操作面（连接/登录/验证码/动作/回调泵/令牌刷新）+ `ISteamClientManager : ISteamTransport`（保留既有 DI/消费面名字）；新增协议无关模型 `TransportLogOnDetails`、`SteamResult`（数值精确镜像 Valve EResult 线上编码，任务 output 里已持久化的 resultCode 语义不变）、`RedeemKeyResult` 迁移至适配层文件；`SteamClientManager` 成为 SteamKit2 适配器实现——**对公共面零 SteamKit2 类型**：删除 `GetClient()`（src 零消费方），`GetLogOnDetailsAsync` 返回 `TransportLogOnDetails`，`RedeemKeyResult.Result` 改 `SteamResult`（`MapResult` 显式映射，未知码归 `Other=0`）；SteamKit2 `using` 仅存于实现文件与 internal 反射适配器 `SteamAuthTokenProvider`。`RedeemKeyAction` 全部走 `SteamResult`。13 个契约测试（SteamResult 12 线上编码镜像 Theory + 接口可替换性）。Steam.Core 562→573，E2E 6 全过，format OK。）
- [x] contract tests（风险清单承诺）：Steam Web API 响应用录制 JSON fixture（GetGameInfo/GetPrice/GetMarketListings/SearchGames）离线回放，防上游响应结构漂移导致解析静默失败。（✅ 2026-09-12：录制 3 个真实响应 fixture（appdetails 620 / storesearch "portal" / market search render 730，精简截断存 `tests/Vapor.Steam.Core.Tests/TestData/`，csproj 复制到输出目录）+ 4 个契约回放测试。**当场抓到真实契约漂移**：market search render 响应已从 `listinginfo`/`total_rowcount` 切换为 `results[]`（`hash_name`/`sell_price` 分/`asset_description.classid`）+ `total_count`，旧解析对线上响应静默返回 null；修复为双路径解析（优先新契约、回落旧契约），`MarketListing` 扩展聚合语义字段 `Name`/`HashName`/`SellListings`（逐挂单字段聚合结果置 0/null），旧契约以内联合成 fixture 保持回归覆盖。Steam.Core 558→562 全过，format OK。）
- [x] replay tests（风险清单承诺）：ControlPlane↔Agent WS 任务派发协议录制回放（消息序列快照测试）。（✅ 2026-09-12：`WsProtocolReplayTests` 7 个测试——录制 5 类隧道帧的确切 JSON 快照（hello / task 派发含 traceHeaders / task_heartbeat / task_result 含 output / task_cancel），双向锁定：序列化方向快照文本精确相等（camelCase、枚举字符串、ISO 8601 时间戳、`WhenWritingNull` 省略全固化），反序列化方向逐字段还原断言；另覆盖完整派发会话序列（5 帧按线序逐帧按两端 handler 路由语义分派）与前向兼容（未知字段/未知 type 不破坏解析，显式 JSON null 不变值）。协议任何漂移（字段改名/大小写/枚举/时间格式）都会破坏快照而非静默失败。ControlPlane 140→147 全过，format OK。）
- [x] 覆盖率 44.6% → 60%+：优先 Agent（25.1%：TaskExecutor/会话泵/WS 客户端分支）、Protocol（44.4%）；补齐后更新 CI 门禁与 TESTING.md 基线数字。（✅ 2026-09-12：新建 `Vapor.Protocol.Tests` 专项项目（20 测试：JsonDefaults 序列化契约 7 + 协议模型逐字段往返 13），Protocol 44.4%→**89.1%**；重算时发现旧 44.6% 基线被系统性压低——不同 testhost 报告里同一源文件的 `filename` 前缀写法不一致（`src/<项目>/…`、`<项目>/…`、裸文件名并存），合并未归一化导致同一行重复计入分母；新增 `scripts/coverage-summary.py` 归一化合并脚本，重算真实整体 **74.3%**，远超 60% 目标。新增 `codecov.yml` 门禁（project 70% ±2 / patch 60%）。Agent 22.6% 为结构性：除 `Program.cs`（顶层组装，E2E 子进程覆盖但插桩测不到）外全部 100%；Steam.Core 67.9% 的未覆盖大头是需真实网络/Redis 的集成壳（SteamTradeClient / RedisVaporCache，后者接口的内存实现已 100%）。TESTING.md 基线表与统计（941→961）同步刷新。）

---

## 11. P6 阶段：Steam 功能纵深（📋 已立项 2026-09-12，对标矩阵见 `docs/feature-matrix.md`）

> 对标五个同类产品（ASF / Watt Toolkit / Steam Game Idler / steamguard-cli / Idle Master Extended）逐能力域对照立项。矩阵结论：平台层（多节点舰队编排 / 任务系统 / 插件 / 可观测性）为 Vapor 独有，差距集中在 Steam 功能纵深——恰为 GA 出口条件 #2 的"库存/交易、基础 farming"未闭环项。取舍三原则：契合平台定位（不做单机工具箱功能）、GA 出口条件优先、复用既有编排/通知/插件底座。

### 11.1 P6-1 卡牌 farming 闭环（GA 出口条件，平台杠杆最大）

- [x] 徽章页解析：拉取玩家徽章/卡牌掉落页，得出各 app 剩余掉落张数（✅ 2026-09-12 `SteamBadgesClient` + `get_card_drops` 动作。徽章页 HTML 是剩余掉落唯一来源且登录门控、不可匿名录制——fixture 按三方解析器互证构造（ASF CardsFarmer.cs / steam-game-idler scraper.rs / Greasy Fork userscript：`badge_row` 行切分、appid 双载体 `card_drop_info_dialog_{id}` 与 `steam://run/{id}`、`progress_info_bold` 掉落文案、`pagelink` 分页、`l=english` 强制语言），来源已在 fixture 头注明，结构漂移时按契约测试失败重录真实页。动作默认 SWR 缓存 10min，`force_refresh`/`cache_ttl_seconds` 可控；解析变体单测 + 契约回放 + 分页/失败语义 + 缓存共 24 测）。
- [x] smart farming 调度：剩余掉落 > 0 的 app 进 idle 队列，掉完自动切换下一个；与 DesiredStateReconciler 整合——`Idle` 期望状态从"指定 appIds"升级为 farm 策略模式（调度在 CP 编排层，动作面只加"查剩余掉落"action）（✅ 2026-09-12 见下方日志）。
- [x] 挂机排除名单：IdleApps 补充排除（黑名单）语义，对齐 ASF `Blacklist`（✅ 2026-09-12 随 smart farming 一并落地：Farm 模式下 IdleApps 解释为排除名单，Idle 模式仍为白名单）。

### 11.2 P6-2 交易与确认闭环（GA 出口条件，安全底座已备）

- [x] 交易报价读取：incoming/outgoing 报价列表拉取并入 CP（REST 化），复用既有脱敏与审计（✅ 2026-09-12 见下方日志）。
- [x] 报价接受/拒绝：基于 MobileAuthenticator 既有确认哈希/响应能力；**自动接受必须按账户显式策略开启**（默认人工 SSE 通知，对齐验证码红线）（✅ 2026-09-12/13 人工路径 REST 化 + mobile 确认闭环完成，见下方日志；编排器侧自动策略待 §11.2 ④ loot 之后评估）。
- [x] 批量确认 action：交易/市场确认批量处理（对标 Watt 批量确认）（✅ 2026-09-13 `confirm_all_confirmations`：identity secret 仅存 agent 侧凭证库、payload 零 secret；`type` 过滤（Steam type 枚举归一化 trade/market/generic）+ `operation` allow/cancel，逐条响应单项失败不中断并如实汇总；CP `POST /v1/accounts/{name}/confirmations/accept-all` 同步端点，审计 `trade_confirmations.accept_all`；1050 测试全过）。
- [x] 报价发送（loot）：向指定好友转移库存；优先级低于前三项。（✅ 2026-09-13 `loot_inventory` + CP `POST /v1/accounts/{name}/loot`，见下方日志）。
- [ ] （后置）1:1 换卡（STM/TradeMatcher 等价）：依赖报价读取 + 接受闭环（两者已就绪，可随时启动）。

### 11.3 P6-3 互操作与认领（降低迁移/使用成本）

- [ ] .maFile 导入：解析 SDA/steamguard-cli 的 maFile（shared_secret / identity_secret / 设备 ID），入库走既有加密存储——存量 2FA 用户零成本迁移。
- [ ] 免费 license 认领：addlicense 等价 action（sub/add 页解析）；免费游戏提醒可由 MarketWatch 模式扩展 watch 类型。
- [ ] 库存 REST 化：GetInventoryAction 深化为 `/v1/accounts/{name}/inventory`（按 app/类型过滤），为交易/市场功能供数。

### 11.4 P6-4 竞品对齐但后置（记录待决，不承诺）

- [ ] Web Dashboard（对标 ASF-ui）：CP 已有 REST+SSE，可先做只读面板；自研面大，P6-1~3 落地后评估。
- [ ] 市场挂单创建/批量撤单（对标 SGI）：ToS 灰区 + 需库存/定价前置。
- [ ] QR 扫码登录（对标 SGI/steamguard-cli）；成就解锁/管理（对标 SGI）：便利性/需求弱，后置。

### 11.5 明确不采用（定位外）

网络加速（Watt 品类不同）、本地账号切换（客户端概念）、通用 TOTP 保险箱（偏离核心）、游戏内脚本/成就数值编辑（高风险灰区）。

> 2026-09-11：全解决方案已从 net8.0 迁移到 net10.0（SDK 10.x，CI 同步）。
> 2026-09-11：MonitoringPlugin + Docker/compose + Prometheus/Grafana 可观测性栈落地；660 个测试全部通过。
> 2026-09-11：Agent 单元测试（41 个）+ E2E 测试（5 个，真实双进程闭环）落地；全解决方案 706 个测试通过。
> 2026-09-12：任务派发终态机制落地（`Vapor_TASK_MAX_DISPATCH_ATTEMPTS` / `Vapor_TASK_DISPATCH_RETRY_DELAY_MS`）：无 capable agent 的任务带延迟重试，达到上限后置 Failed 并记录 error（tasks 表新增 error / next_attempt_at_ms 列，自动迁移旧库）；修复 SubscribeAllEvents 测试线程池竞态 flake。
> 2026-09-12：覆盖率体系修复：7 个测试项目统一接入 coverlet.collector（此前 CI 的 codecov 上传指向不存在的文件，从未真正上报）；`run-tests.sh/.ps1` 覆盖率模式扩展到整个解决方案并在收集前清理历史残留报告；CI 新增 Ubuntu Release 覆盖率收集步骤并修正 Codecov glob 为 `**/TestResults/*/coverage.cobertura.xml`。首次取得真实全解决方案基线：**行覆盖约 44.6%**（ControlPlane 74.6%、Monitoring 89.3%、MobileAuthenticator 75.6%、Steam.Core 68.6%、Plugins.Core 67.0%、Protocol 44.4%、Agent 25.1%——Agent 实际另有 41 单测 + 5 E2E 集成覆盖，E2E 跑在子进程中测不到）。
> 2026-09-12：修复 PluginUnloadTests 的 ALC 回收断言在覆盖率运行下必失败的问题——coverlet instrumentation 持有被插桩 collectible 程序集的强引用导致 ALC 永不可回收（非产品缺陷）；测试现于检测到 coverlet 注入时跳过回收断言，非 coverage 运行仍完整验证（30 秒时间预算轮询）。
> 2026-09-12：覆盖率管道修复两 bug（`run-tests.sh`/.ps1 同修）：① 并行限流 `-- RunConfiguration.MaxCpuCount=2` 曾误拼在 `--collect` 之前——`--` 之后所有 token 都被解析为 runsettings 参数，导致收集静默失效；限流参数移到命令最末。限流本身修复满核 testhost 并行下 E2E（真实子进程）/CP 调度器（真实时钟）偶发 flake（实测复现过），并同步到 CI 两个测试步骤。② 历史覆盖率清理原在 eval **之后**执行，删掉的是本次刚生成的文件——本地 `-c` 模式永远拿不到报告；清理移到测试之前。③ 新增 `scripts/coverage-summary.py`（跨报告 filename 前缀归一化 + 按行去重取最大命中）与 `codecov.yml` 门禁（70%/60%），CI codecov 上传自此真正有文件可传、有门禁可守。
> 2026-09-12：任务 output 持久化落地：tasks 表新增 `output_json` 列（自动迁移旧库），`SetTaskResult` 同时持久化 output 与 error（此前两者均随会话丢弃）；`JobTask` 新增 `Output` 字段并出现在 REST `/v1/jobs/{id}` 响应中；E2E 恢复 output 断言，全链路（Agent 回报 → 存储 → REST）验证通过。
> 2026-09-12：P5 方向 A 账户农场编排完成（5 个提交）：`/v1/accounts` CRUD + `DesiredStateReconciler` 期望状态编排（周期对账/login 派发/指数退避冷静期/连续失败节流/agent 丢失重平衡/dry-run）+ enable/disable/remove 生命周期 + 聚合视图与列表过滤 + jobs/sessions 按账户过滤 + 编排审计与 metrics + E2E 重平衡测试（E2E 套件 5→6 个）。前置修复：agent 全量会话状态上报（此前普通状态变化不进 CP SessionTracker，编排器无数据可用）。ControlPlane 99 单测 + E2E 6 全过。
> 2026-09-12：10.2 通知系统落地：`INotificationSink` + `WebhookNotificationSink`（HMAC 签名 + 指数退避）+ `NotificationService` 三泵订阅 EventBroker（规则过滤 + 失败隔离，验证码永不外发）+ webhook 配置 5 参数 + 派发 metrics，15 个新测试。
> 2026-09-12：10.2 2FA 自动提交闭环落地：SteamTotp/SteamTimeSynchronizer 移入 Steam.Core、`ICredentialStore` 扩展加密 SharedSecret、`TwoFactorAutoResponder`（本地 TOTP 自动应答，60s/账户冷却，无 secret 回落人工 SSE）、插件 `save_shared_secret` action、agent 端 `AGENT_2FA_AUTO_SUBMIT` 显式开启 + Steam 时间同步循环；19 个测试迁移或新增（Steam.Core 558 / MobileAuthenticator 44 / Agent 41 全过）。
> 2026-09-12：10.2 周期任务落地：`POST /v1/jobs` 支持 `schedule`（interval ≥ 5s 或 5 字段 UTC cron/Cronos，missed=skip|run_once、overlap=skip|allow）创建模板 job，`RecurringJobScheduler` 每秒触发派生 job（`meta.scheduledFrom` + `parent_job_id`），停机补跑防风暴、cancel 即停、cron 用尽自动退役、`/metrics` 新增 `schedule_triggers_total{outcome}`；jobs 表自动迁移 4 列 + 2 索引。**方向 C（通知与自动化闭环）至此全部完成**（10.2 剩余 metrics 子项早已随方向 A 与通知提交落地）。
> 2026-09-12：P5 方向 B 开工（统计修正 + contract tests）：TESTING.md 刷新为 919 真实基线；contract tests 录制 3 个真实 Steam 响应 fixture 离线回放，**当场抓到真实上游漂移**——market search render 已从 `listinginfo`/`total_rowcount` 切换为 `results[]`/`total_count`，旧 `GetMarketListingsAsync` 对线上响应静默返回 null；解析器改双路径（新契约优先、旧契约回落），`MarketListing` 扩展聚合字段（`Name`/`HashName`/`SellListings`）。Steam.Core 558→562。
> 2026-09-12：replay tests 落地：`WsProtocolReplayTests` 7 个测试录制回放 CP↔Agent WS 隧道 5 类帧（hello/task/heartbeat/result/cancel）——序列化方向快照精确相等、反序列化方向逐字段断言、完整会话序列按两端 handler 语义路由、未知字段/type 前向兼容。ControlPlane 140→147。
> 2026-09-12：ISteamTransport 协议适配层落地：`ISteamTransport` 协议无关操作面 + `TransportLogOnDetails`/`SteamResult`/`RedeemKeyResult` 协议无关模型，`SteamClientManager` 收敛为 SteamKit2 适配器（公共面零 SteamKit2 类型，`GetClient()` 删除，`SteamResult` 数值镜像线上编码保持任务 output 兼容）。协议升级从此只动适配层。Steam.Core 562→573。
> 2026-09-12：**P5 全部完成（方向 A/C/B 三方向收官）**。方向 B 五子项收口：统计修正 / contract tests（抓到真实上游漂移并双路径修复）/ WS 协议 replay tests / ISteamTransport 协议适配层 / 覆盖率管道修复 + 真实基线（整体行覆盖 74.3%，Protocol 89.1%，测试总数 961，全部过；codecov 门禁 70% 上线）。
> 2026-09-12：P6 立项：对标五个同类产品（ASF / Watt Toolkit / Steam Game Idler / steamguard-cli / Idle Master Extended）完成功能矩阵（`docs/feature-matrix.md`，7 能力域 30+ 项）。结论：平台层（多节点舰队编排/任务系统/插件/可观测性）全场独有；差距集中在 Steam 功能纵深，恰为 GA 出口条件 #2 未闭环项。P6 四组候选立项（§11）：卡牌 farming 闭环 → 交易与确认闭环 → 互操作与认领 → 后置待决（Dashboard/市场挂单/QR 登录/成就）；明确不采用网络加速/账号切换/通用 TOTP/游戏内脚本。
> 2026-09-12：P6-1 ①徽章页解析落地：`SteamBadgesClient`（解析徽章页 HTML 得各 app 剩余卡牌掉落——该页是掉落数唯一来源，Web API 无对应字段）+ `get_card_drops` 动作（SWR 缓存 10min + force_refresh/cache_ttl_seconds，输出按剩余张数降序）。徽章页登录门控不可匿名录制，fixture 按三方解析器互证构造（ASF CardsFarmer.cs 的 `card_drop_info_dialog_{id}` appid 载体 / steam-game-idler scraper.rs 的 `steam://run/{id}` 载体与 `progress_info_bold`/`pagelink` 文案 / Greasy Fork userscript 的 DOM 层级），`l=english` 强制语言防本地化漂移；分页逐页抓取（后续页失败跳过、首页失败抛错）。24 个新测试（解析变体 13 + 契约回放 3 + 动作 8），Steam.Core 573→597，总 961→985。P6-1 剩余：②smart farming 调度、③IdleApps 排除名单。
> 2026-09-12：**P6-1 全部完成（①+②+③，卡牌 farming 闭环 GA 出口条件达成）**。②smart farming 调度：`AccountDesiredState.Farm = 3` 新期望状态，`DesiredStateReconciler` 编排 farm 循环——周期派发 `get_card_drops`（CP 只知 accountName，`steam_id` 缺省经 `SteamWebHandler.TryResolveOwnSteamId()` 从会话 cookie 反解，四 cookie 名变体 + 最小值校验），从任务 output（SQLite JSON 往返后 drops 为 JsonElement，解析双形态兼容 Dictionary/JsonElement）构建 `FarmQueue`，`play_games` 挂队首（按剩余张数降序），刷新周期 `Vapor_RECONCILE_FARM_REFRESH_SECONDS`（默认 300s）重查，队首掉完自动切换下一个，全空 stop 保留在线；farm 查询失败只记 LastDeviation，绝不消耗 login 失败预算。③排除名单：Farm 模式下 `IdleApps` 语义变为排除名单（黑名单，对齐 ASF Blacklist），Idle 模式仍为白名单；spec 变更重置 farm 队列。7 个编排器新测试（初始派发/排除过滤/轮换/停机/刷新间隔/JSON 往返/Farm→Online 切换）+ Protocol Farm 枚举往返 2 + AccountApi farm 1 + 动作 cookie 反解 2。ControlPlane 147→155，Protocol 20→22，Steam.Core 597→599，总 985→997 全过。
> 2026-09-12：P6-2 ①交易报价读取落地（REST 化）：`get_trade_offers` 动作（IEconService `GetTradeOffers/v1`，payload `active_only` 默认 true，输出 sent/received 双列表——offer 级 id/partner/state/计数/message/时间戳 + item 级 app/context/asset/class/instance，ulong 大值一律字符串防 JS 精度丢失）；CP 新增同步查询端点 `GET /v1/accounts/{name}/trade-offers`——`TradeOffersReader` 创建单 target job 并有界轮询（30s 窗口/200ms 间隔），Finished→200 返回 offers、Failed/Canceled→502、窗口内未回报→202+job_id 降级客户端轮询 `/v1/jobs/{id}`；写入 `trade_offers.read` 审计（复用既有脱敏）。测试：动作 8（fake trade client）+ API 5（TestFactory 移除后台调度器 + ClaimNextQueuedTask 模拟 agent 认领回报），Steam.Core 599→607、ControlPlane 155→160，总 997→1010 全过。
> 2026-09-12：P6-2 ②人工路径落地（REST 化接受/拒绝）：`TradeOffersReader` 泛化为 `AccountTaskRunner`（有界等待单账户任务派发，WaitWindow/PollInterval 测试可调），新增同步端点 `POST /v1/accounts/{name}/trade-offers/{offerId}/accept`（body 需 `partner_steam_id`，从报价列表输出取得）与 `/decline`——Finished→200、Failed/Canceled→502、窗口未回报→202+job_id，`trade_offer.accept`/`trade_offer.decline` 审计（复用脱敏）。测试：AccountApi +6（鉴权/400 校验/accept+decline 成功/失败/202），ControlPlane 160→166，总 1010→1016 全过。②剩余：自动接受显式策略 + mobile 确认闭环（identity_secret 须留在 agent 侧，同 shared secret 红线）。
> 2026-09-13：P6-2 ② mobile 确认闭环落地（identity_secret 全程不出 agent）：`ICredentialStore` 扩展 `Save/GetIdentitySecretAsync`（FileCredentialStore 加密存储同 shared secret，磁盘密文）；插件新增 `save_identity_secret` 与 `confirm_trade_offer` 复合动作——后者从 agent 侧凭证库读 secret（payload 显式传 secret 一律不收，任务记录零接触），列确认→按 CreatorId 匹配 trade offer id（Steam 接受后确认有数秒延迟，内置轮询 6×250ms 可测）→ allow/cancel；CP accept 端点默认 `auto_confirm`——accept 任务回报 `requires_mobile_confirmation=true` 时自动续派 confirm 任务并审计 `trade_offer.confirm`，响应体 `mobile_confirmation{attempted,confirmed,job_id,error?}` 如实分段报告（确认失败不影响 accept 已成功的事实，200 而非 502）。测试：插件 +10（存储 3 + 确认动作 7，fake client 支持列表队列）、凭证库 +4（往返/密文/缺失/覆盖）、CP +3（自动确认成功/失败/无需确认），总 1016→1033 全过。②全部完成。
> 2026-09-13：P6-2 ③批量确认落地（对标 Watt 批量确认）：`TradeConfirmation` 提取确认类型并归一化（Steam mobileconf `type` 为整数枚举 1=generic/2=trade/3=market → 小写名称字符串，未知码透传）；插件新增 `confirm_all_confirmations` 复合动作——identity secret 仍只从 agent 侧凭证库读取（payload 零 secret 红线延续），`type` 过滤（all/trade/market）+ `operation`（allow 默认/cancel），逐条响应、单项失败不中断，输出 `{operation,total,succeeded,failed,results[]}` 汇总如实报告；CP 新增同步端点 `POST /v1/accounts/{name}/confirmations/accept-all`（operation/type 校验 400、Finished→200、Failed/Canceled→502、窗口未回报→202+job_id），审计 `trade_confirmations.accept_all`，派发 payload 仅含 operation/type。测试：插件 +11（批量动作 9 + 解析 type 归一化 2）、CP +6（成功含 payload 零 secret 断言/type 过滤透传/502/400×2/202），总 1033→1050 全过。
> 2026-09-13：P6-2 ④报价发送（loot）落地（对标 ASF/Watt loot）：`loot_inventory` 复合动作——扫描指定 app 库存（`app_ids` 默认 [753] 社区库存：卡牌/宝珠等挂机主要产出；753→context 6、其余→context 2）分页拉取（每 app 上限 50 页、最多 5 个 app），只保留当下可交易物品（Tradable 且无未来 TradabilityDate），一次拉取即完成所有权事实（不再重复 send 的二次校验），目标 `partner_steam_id` 或带 token 的 `trade_url`（可发非好友），经 TradeRateLimiter 限流后整单发出；输出 `{trade_offer_id,partner_steam_id,item_count,apps_scanned,requires_mobile_confirmation}`，无可交易物品如实失败。CP 端点 `POST /v1/accounts/{name}/loot`：partner 校验（两者缺一 400）、loot output 的 `requires_mobile_confirmation=true` 时自动续派 confirm_trade_offer（offer id 取自 loot output，identity secret 仍不出 agent），审计 `account.loot` + `trade_offer.confirm`；三态 200/202/502 与 accept/decline 对称。测试：动作 +15（过滤/默认 app/覆盖/分页/确认标志/空库存/失败/上限/无 web handler）、CP +5（自动确认含 payload 断言/无需确认/502/400/202），总 1050→1070 全过。P6-2 主体完成。
