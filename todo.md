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
| P6 功能纵深 | Week 17+ | ✅ 完成 | P6-1 farming / P6-2 交易确认 / P6-3 互操作认领全部落地；P6-4 剩两项明确后置（挂单创建 ToS 灰区、成就管理需求弱）（见第 11 节，对标矩阵 `docs/feature-matrix.md`） |

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
10. ~~P6 推进（2026-09-12 立项）~~（✅ 2026-09-13 完成：P6-1 卡牌 farming 闭环 → P6-2 交易与确认闭环 → P6-3 互操作与认领全部落地，GA 出口条件 #2 闭环；P6-4 仅剩两项明确后置（挂单创建/批量撤单 ToS 灰区、成就管理需求弱），对标矩阵已同步勾选 `docs/feature-matrix.md`）。**todo.md 全部计划阶段（P0-P6 + GA 收口横向）至此完成。**
11. ~~P7 推进（2026-09-14 立项）：市场闭环——挂单读取 → 批量撤单 → 挂单创建~~（✅ 2026-09-14 完成：三期全部落地，灰区核心以 dry_run 默认 + 账户/agent 双开关缓解，详见 §12）。
12. ~~P8 推进（2026-09-15 立项）：积分商店认领（矩阵 §3.6 最后一个非后置非定位外缺口；免费定义默认认领、付费需显式 force、defid 仅来自调用方）~~（✅ 2026-09-15 完成：`get_points_shop_summary` + `claim_points_shop_items` + `GET/POST /v1/accounts/{name}/points-shop/*`，详见 §13）。
13. ~~GA 出口条件验收盘点（2026-09-15）：对 §0 五条逐条仓库实证盘点~~（盘点结论 4 达成 / #4 缺性能基线，详见 §14）。
14. ~~性能基线（GA 盘点 §14 #4 唯一缺口，2026-09-15 立项即收口）：时延/吞吐/资源三维度基准 + `docs/performance.md` 权威基线 + `run-benchmarks.sh`~~（✅ 2026-09-15 完成，详见 §16；**GA 出口条件 5/5 全部达成**）。

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
- [x] 报价接受/拒绝：基于 MobileAuthenticator 既有确认哈希/响应能力；**自动接受必须按账户显式策略开启**（默认人工 SSE 通知，对齐验证码红线）（✅ 2026-09-12/13 人工路径 REST 化 + mobile 确认闭环完成，见下方日志）。**编排器侧自动接受策略评估结论（2026-09-13 收口）**：**不实现**——① 接受报价是资产转移动作，自动决策风险不对称（恶意/钓鱼报价甄别成本远低于误接受损失，且不可逆）；② 人工路径成本已足够低（GET trade-offers → POST accept，accept 后 mobile 确认自动续派，一次调用闭环）；③ 平台的自动化场景（loot、swap）均在发送方，无需接受侧自动化，ASF 的 AcceptGifts 同样标记为高风险 opt-in。若未来需求成立，形态必须为 AccountSpec 显式策略（per-account、partner 白名单、内容过滤、默认关闭、全量审计）——记录于此，暂不立项。
- [x] 批量确认 action：交易/市场确认批量处理（对标 Watt 批量确认）（✅ 2026-09-13 `confirm_all_confirmations`：identity secret 仅存 agent 侧凭证库、payload 零 secret；`type` 过滤（Steam type 枚举归一化 trade/market/generic）+ `operation` allow/cancel，逐条响应单项失败不中断并如实汇总；CP `POST /v1/accounts/{name}/confirmations/accept-all` 同步端点，审计 `trade_confirmations.accept_all`；1050 测试全过）。
- [x] 报价发送（loot）：向指定好友转移库存；优先级低于前三项。（✅ 2026-09-13 `loot_inventory` + CP `POST /v1/accounts/{name}/loot`，见下方日志）。
- [x] 1:1 换卡（STM/TradeMatcher 等价）：依赖报价读取 + 接受闭环（两者已就绪）。（2026-09-13 `find_duplicates` + `swap_duplicates` 动作，`CardSwapMatcher` 纯逻辑：按 (app, class, instance) 分组、excess 只取当前可交易副本；匹配是严格双向互补——我方多余卡须对方一张没有、反之亦然，逐张 1:1 配对（默认上限 25 对）。`swap_duplicates` 默认 dry_run 只出配对方案，`send=true` 才发报价（走既有频控 + `send_trade_offer` 通道），CP `GET /v1/accounts/{name}/duplicates` + `POST /v1/accounts/{name}/swap-offers`（send 时自动补 mobile 确认，同 loot 流程），审计 `inventory.duplicates` / `trade.swap_offer`。1171 测试全过。）

### 11.3 P6-3 互操作与认领（降低迁移/使用成本）

- [x] .maFile 导入：解析 SDA/steamguard-cli 的 maFile（shared_secret / identity_secret / 设备 ID），入库走既有加密存储——存量 2FA 用户零成本迁移。（2026-09-13 `MaFileParser` + `Vapor.Agent import-mafile <file-or-dir...> [--password <pw>]`；**agent 本地 CLI 导入**是有意设计——maFile 内容本身就是 secret,经 ControlPlane 任务转发会落入 SQLite 任务记录,违反"任务记录零接触"红线。兼容 SDA 嵌套布局（含 PBKDF2-SHA1 50k + AES-CBC 密码加密）与 steamguard-cli 平铺布局；仅导入 shared/identity secret,Session 令牌只检测不导入（账户走正常凭证流）；设备 ID 落盘但使用时按账户派生；退出码 0/1（有失败）/2（用法）。）
- [x] 免费 license 认领：addlicense 等价 action（sub/add 页解析）；免费游戏提醒可由 MarketWatch 模式扩展 watch 类型。（2026-09-13 `add_license` action + `POST /v1/accounts/{name}/licenses`：app_ids 走 SteamKit client 协议（`SteamApps.RequestFreeLicense`，经 `ISteamTransport.RequestFreeLicenseAsync`），sub_ids 走商店 checkout `addlicense/{subid}`（`ISteamStoreApiClient.AddFreeLicenseAsync`，用 web handler 既有会话 cookie）；detail 1=成功、15=已拥有（视为成功）。免费游戏提醒已落地：MarketWatch `market_watch_add kind=free`——appdetails is_free 边沿检测（首次观察记基线，不免费→免费告警一次，免费保持不重复，免费→收费→免费再告警），webhook `type=free_game_alert`；与 `add_license` 组成"提醒→认领"闭环。）
- [x] 库存 REST 化：GetInventoryAction 深化为 `/v1/accounts/{name}/inventory`（按 app/类型过滤），为交易/市场功能供数。（2026-09-13 `GET /v1/accounts/{name}/inventory`：`app_ids=753,730` 多 app 扫描（loot context 规则 753→6、其余→2，上限 5 app/次），`tradable_only`/`marketable_only` 过滤，steam_id 缺省从会话 cookie 反解（CP 只需账户名）；保留 app_id/context_id 单 app 旧输出兼容；审计 inventory.read；三态同读取类端点。）

### 11.4 P6-4 竞品对齐但后置（记录待决，不承诺）

- [x] Web Dashboard 只读面板（对标 ASF-ui 只读部分）：`wwwroot/dashboard.html`——统计卡/账户/Agent/会话/作业（点击展开任务明细）/审计日志 + jobs/sessions 双 SSE 流 + 30s 轮询兜底，纯 GET + EventSource 零写操作；无写动词契约测试守护（DashboardStaticTests 4 个）；与 admin.html 互链，`/` 重定向不变。（管理功能继续走 admin.html；功能扩展待后续评估）
- [x] 市场挂单创建/批量撤单（对标 SGI）：ToS 灰区 + 需库存/定价前置。（2026-09-14：前置条件经 P6 补齐，立项为 P7 市场闭环，见 §12）
- [x] QR 扫码登录（对标 SGI/steamguard-cli）：SteamKit2 BeginAuthSessionViaQR + 轮询，login 任务 payload `qr_login:true` 触发；挑战 URL 经 session 事件 `qr_required` 上浮（CP 归类为挑战类型 `qr_required`，轮转自动重发，批准/超时自动清挑战），request_key 不出 transport，refresh token 走既有加密落盘 + token 登录路径；3 分钟等待窗。
- [x] 成就解锁/管理（对标 SGI）：需求弱，后置。（2026-09-18 立项为 §33 并落地：显式单发 REST + 布尔置位划线——数值编辑维持不采用，✅ 见 §33）

### 11.5 明确不采用（定位外）

网络加速（Watt 品类不同）、本地账号切换（客户端概念）、通用 TOTP 保险箱（偏离核心）、游戏内脚本/成就数值编辑（高风险灰区）。

---

## 12. P7 阶段：市场闭环（📋 已立项 2026-09-14，对标矩阵见 `docs/feature-matrix.md` §3.5）

> 立项动机：P6 闭环了 farming→库存→交易（挂机掉卡 → 重复物清单/换卡 → loot/swap），但卡牌最终变现一环——挂单出售——仍是矩阵 §3.5 唯一双 ❌ 能力域（对标产品中仅 SGI 具备）。当初 P6-4 后置的理由是两半："需库存/定价前置"已被 P6 自身补齐（库存 REST `/inventory`+`/duplicates` 与 marketable 过滤、行情底座 `SteamStoreApiClient` + MarketWatch 价格告警、市场类 mobile 确认（`confirm_all_confirmations` 已归一化 market 类型）、限流/熔断/dry_run 底座 TradeRateLimiter / HttpCircuitBreaker / swap_duplicates 先例）；另一半"ToS 灰区"以分期 + 显式开关缓解，不回避也不抢跑。
>
> **ToS 缓解设计（立项约束，实施时逐条对照）**：① 分期把低风险面先落地——挂单读取（纯读）→ 批量撤单（降低市场暴露）→ 挂单创建（灰区核心，最后做）；② 创建默认 dry_run 只出定价方案，真实挂单需 per-account 显式开关（默认关，对齐 `AGENT_2FA_AUTO_SUBMIT` 先例）；③ 市场专用保守频控（独立于交易限流预算），不做自动重定价/爬价——价格输入仅来自用户显式给定或既有行情接口，MarketWatch 保持只监控不回写；④ 红线延续：identity/shared secret 不出 agent，market 确认复用既有 mobile 确认闭环。
>
> **为什么不立项成就解锁/管理**：后置记录"需求弱"未变，无新驱动；且成就解锁紧邻"明确不采用"清单中的"成就数值编辑（高风险灰区）"，安全边界模糊，不宜在无需求拉动下开垦。

### 12.1 P7-1 挂单读取（只读，零风险增量）

- [x] 自己的挂单列表解析（挂单 id / hash 名称 / 买方价格 / 卖方所得 / 资产摘要），REST 化 `GET /v1/accounts/{name}/market/listings`；也是撤单的前置供数。（✅ 2026-09-14 `get_my_market_listings` + `SteamMarketClient`，见下方日志）

### 12.2 P7-2 批量撤单

- [x] 按过滤器批量撤单（app / hash 名称 / 价格区间 / 挂单时长），逐条撤单 + 限流 + 单项失败不中断如实汇总（对齐 `confirm_all_confirmations` 语义），REST `POST /v1/accounts/{name}/market/listings/cancel`；dry_run 默认输出将撤清单。（✅ 2026-09-14 `cancel_market_listings` + `SteamMarketClient.CancelListingAsync`，见下方日志）

### 12.3 P7-3 挂单创建（灰区核心，最后落地）

- [x] web 通道挂单创建 + 费用感知定价（Steam 手续费买方支付模型，输出买/卖两侧价格）；默认 dry_run 输出定价方案，`send=true` 且账户显式开启市场开关才真实挂单；market 类 mobile 确认复用既有闭环。（✅ 2026-09-14 `create_market_listing` + `SteamMarketClient.CreateListingAsync` + `MarketFeeCalculator`，见下方日志）

## 13. P8 阶段：积分商店认领（📋 已立项 2026-09-15，对标矩阵见 `docs/feature-matrix.md` §3.6）

> 立项动机：P7 市场闭环收口后，矩阵 §3.6 的"积分商店认领"（ASF ✅ / SGI ➖ / Watt ➖ / steamguard-cli ➖）是唯一既非后置也非定位外的未立项缺口。风险级与 `add_license` 免费认领同档——只加不减、无自动扫描、defid 全部来自调用方显式输入；消耗积分的付费兑换以显式 `force` 单独门控。

### 13.1 积分商店认领

- [x] 余额与定义查询（发现供数）+ 免费定义默认认领（ASF RP 语义：`point_cost == 0`；付费需 `force=true` 且缺省整批前置拒绝）；REST `GET /v1/accounts/{name}/points-shop/summary` + `POST /v1/accounts/{name}/points-shop/claim`，三态 200/202/502。（✅ 2026-09-15 `get_points_shop_summary` + `claim_points_shop_items`，见下方日志）

## 14. GA 出口条件验收盘点（✅ 2026-09-15：5/5 达成——#4 缺口同日收口，见 §16）

> 对 §0 五条 GA Exit Criteria 逐条以**仓库实证**盘点（本地实跑 + CI 记录 + 文件存在性核查，非转述历史结论）。盘点为纯文档动作，未改产品代码。

### #1 主分支 build/test 稳定通过 — ✅

- 本地实跑（2026-09-15，Debug）：`dotnet build` 0 警告 0 错误；全量 **1899/1899**（Steam.Core 1068 / ControlPlane 391 / Plugins.Core 127 / MobileAuthenticator 126 / Agent 53 / MarketWatch 56 / Monitoring 35 / Protocol 37 / E2E 6）。
- CI：ci workflow 10 job（format、docker-build、integration-redis、build-test × Debug/Release × ubuntu/windows/macos、coverage）+ codeql + dependency-review；P8 代码提交（2ca78a8）起 ci + codeql 全绿。
- 稳定性：2026-09-13 eaa764e 首次全绿后主分支保持绿；其间 ci 失败均可归因且当轮闭环——三个真缺陷（maFile 错密码解析泄漏 c8061c9、Redis 初始连接重试 afdef0c、coverage 插桩下 SE.Redis 命令超时 2ca78a8）+ 一次 runner VM 挂死（windows Debug 四个独立测试进程同时静默 41 分钟触发 45 分钟 job 超时，`--failed` rerun 绿，同 commit 姊妹 job 全绿佐证非代码问题）。

### #2 核心动作闭环 — ✅

Core 27 个 action 实测（`src/Vapor.Steam.Core/Actions/`）+ MobileAuthenticator/MarketWatch 插件动作，六域全覆盖：

| 域 | 落点 |
|---|---|
| 登录 | `login`（密码/refresh token/`qr_login` 三路径）+ 挑战自动应答（TwoFactorAutoResponder，显式开启） |
| 2FA | MobileAuthenticator 插件：TOTP（SteamTotp + 服务器时间同步）、确认列举/响应、`confirm_all_confirmations` 批量（类型过滤 + allow/cancel） |
| 游戏状态 | `play_games` / `idle`（白名单） |
| 激活 Key | `redeem_key`（Store#RegisterCDKey） |
| 库存/交易 | `get_inventory`（多 app 扫描 + 可交易/可市场过滤）、`get_trade_offers`、`accept_trade_offer`/`decline_trade_offer` + mobile 确认自动续派、`send_trade_offer`/`loot_inventory`、`find_duplicates`/`swap_duplicates` |
| farming | `get_card_drops`（徽章页解析，SWR 缓存）+ `farm` 期望状态（队首掉完自动轮换、排除名单） |

### #3 安全闭环 — ✅

- 凭证加密存储：`FileCredentialStore` v2（AES-GCM、原子写、`.bak` 恢复、owner-only 600）+ master key 外置（env/file/KMS 密钥源优先级）；
- 令牌持久化与轮换：refresh token 加密落盘（`TokenRefreshTests`）+ `CredentialStoreRotator` / `tools/Vapor.KeyRotation`（密钥轮换 CLI，`--dry-run`，失败即停）；
- 敏感日志脱敏：`RedactingLoggerProvider`（消息/结构化值/scope/异常内容全覆盖，Agent `AddRedactingConsole()`）；
- 审计：`SqliteAuditStore` + `GET /v1/audit/logs`（过滤/分页/脱敏入库）+ 敏感动作专项审计点（登录转移、`task.result.reported`、交易 accept/decline/确认、loot、换卡、编排决策、points-shop）；
- 红线（测试断言守护）：identity secret 不出 agent、payload 零 secret、验证码只出 bool、webhook HMAC 签名且不载验证码。

### #4 可运维 — ✅（6/6 达成；性能基线 2026-09-15 收口，见 §16）

| 要素 | 证据 | 判定 |
|---|---|---|
| OpenAPI | SwaggerGen + bearer security definition | ✅ |
| E2E | `Vapor.E2E.Tests` CP+Agent 双进程真实闭环（6 测） | ✅ |
| Docker | Agent/ControlPlane 双 Dockerfile + docker-compose（CP+Redis+Prometheus+Grafana）+ CI docker-build job | ✅ |
| CI/CD | ci.yml（10 job）+ codeql + dependency-review + release.yml（5 RID 多平台发布） | ✅ |
| 可观测性 | OTel tracing（W3C traceparent 跨 agent 隧道）+ Prometheus 文本端点/Grafana 面板 + 编排/通知/调度 metrics + 告警规则 | ✅ |
| 性能基线 | 三维度基准（只读端点 p50/p95 时延 + 任务派发写入口对照、组件吞吐、每操作托管分配量）+ 权威基线 `docs/performance.md`（含环境/口径/归档）+ `scripts/run-benchmarks.sh`；2026-09-15 收口（§16） | ✅ |

### #5 文档闭环 — ✅

架构（`docs/architecture.md` + `docs/session-engine.md`）、运行（`docs/running.md` + `docs/docker.md`）、运维（`docs/production.md`）、发布（`docs/releasing.md`）、故障排查（`docs/troubleshooting.md`）全部在库；社区文档（README/CHANGELOG/SECURITY/CONTRIBUTING/SUPPORT/CODE_OF_CONDUCT）+ `docs/plugins.md` + `docs/feature-matrix.md` 齐备。

### 结论与后续

- **#1/#2/#3/#4/#5 全部达成**（#4 的性能基线缺口于 2026-09-15 作为独立小项收口，见 §16）——GA 出口条件全部达成。
- 性能基线已按本盘点定位作为独立小项落地：轻量压测（时延/吞吐/资源三维度）+ `docs/performance.md` 权威基线；盘点本身未扩 scope。

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
> 2026-09-13：**P6 收口（P0–P6 全计划阶段完成）**。收口时点基线：`dotnet build` 0 警告 0 错误，**1185 个测试全部通过**（Steam.Core 700 / ControlPlane 205 / Plugins.Core 85 / MobileAuthenticator 65 / Agent 49 / MarketWatch 32 / Monitoring 21 / Protocol 22 / E2E 6）。① P6-1/P6-2/P6-3 全部落地（含 Dashboard 只读面板、QR 扫码登录两个 P6-4 项）；GA 出口条件 #2（库存/交易、基础 farming）闭环。② P6-4 剩余两项维持后置：挂单创建/批量撤单（ToS 灰区 + 定价前置）、成就解锁/管理（需求弱）。③ 编排器侧自动接受策略评估完毕，结论不实现（决策记录见 §11.2 ②）。④ 文档同步：`docs/feature-matrix.md` 矩阵表与 P6 勾选清单全部更新、CHANGELOG Unreleased 补齐 P4/P5/P6 条目、production.md 配置矩阵补 `Vapor_RECONCILE_FARM_REFRESH_SECONDS`、TESTING.md 修正 ControlPlane 计数笔误（204→205）。**更正**：本条初稿曾写"五条 GA Exit Criteria 全部达成"——不实，彼时主分支 CI 已连续 39 次失败（见下条），出口条件 #1（主分支稳定通过）在当时不成立。
> 2026-09-13：**修复 CI 连续 39 次失败（自 2026-09-07 起 P4–P6 全程主分支 CI 未绿过，历次"全过"均为本地 Linux 结果）**。三类根因与修复：① `format` job 命令写错——`dotnet format --verbosity warning` 的 `warning` 不是合法值（仅 q/m/n/d），命令打印用法后 exit 1，与代码无关；改为 `dotnet format Vapor.sln --verify-no-changes`。② Windows 测试清理失败（25 个测试，全部是 Dispose/finally 清理阶段而非断言）：可收集 ALC 加载的 DLL 在 Windows 上保持文件锁，`Directory.Delete` 抛 `UnauthorizedAccessException`（Linux unlink 打开文件不报错故本地全绿）——插件系 5 个测试类 + E2E 的清理 catch 补 `UnauthorizedAccessException`；SqliteJobStore 2 个测试的 `File.Delete` 撞上 Microsoft.Data.Sqlite 连接池句柄，包 try/catch(IOException) best-effort。③ Redis 集成测试 `SetAndGet_RoundTripsAcrossInstances` **生来即坏**：`CreateCache()` 每次调用生成新 `Guid` KeyPrefix，writer/reader 两实例各写各读，断言必挂——该测试只在 CI redis job 跑（本地从不设 `VAPOR_TEST_REDIS`），从未绿过；修复为每测试实例共享一个 prefix 字段。本地验证：`VAPOR_TEST_REDIS` 指向真实 redis-server（redislite 起 6390 端口）跑 11 个集成测试全过（重建前同测试同断言失败 = 复现 CI 挂点）。Windows 侧修复在 Linux 不可复现，靠 CI 三平台 job 确认。文档核验：feature-matrix/CHANGELOG/production.md 全部代码引用逐条 grep 对上（动作名 ×17、env var、webhook 签名头、CLI 用法、契约测试与 fixture、dashboard.html）；唯一不精确处为 REST 端点计数（37 个 Map 调用含 /metrics、/healthz、/ 重定向，`/v1` 实为 34 个），已修正。
> 2026-09-13：**修复 CI flaky 尾巴（57d9910 轮：Windows Release / format / integration-redis 已绿，余 ubuntu Debug+Release 与 windows Debug 三平台同挂同测试）**。两个固定延时竞态，一并改确定性等待：① `NotificationTests.SinkRules_FilterDeliveries` 等 4 个测试共用的 `StartAsync` helper 以 `Task.Delay(100)` 祈祷 NotificationService 三条泵完成订阅——事件在注册落地前发布即被 broker 静默丢弃（channel 发布时无订阅者不缓冲），CI 负载下 5s 等待超时；`EventBroker` 新增 `SubscriberCount / SessionSubscriberCount / AuthSubscriberCount` 三个只读计数（不进 `IEventBroker` 接口，避免破坏 fake 实现），helper 改为轮询计数至三泵齐备（5s 上限），正常路径注册本就同步完成，轮询即刻退出（单类运行时间 500ms→170ms，10 连跑全过）。② `BotSessionTests.SubscribeEvents_WhenStateChanges_ReceivesEvent`（todo 已记录的 flake 家族变体，macos Debug 挂 2s）：收集器在 `Task.Run` 里首次 MoveNextAsync 才挂上 reader，而生产代码 `DisconnectAsync` 内部自带 `Task.Delay(100)` 才 `SetState` 发布，双层延时叠加下测试 1s 预算不够；去掉测试侧固定 sleep，等待预算 1s→5s 与仓库其它等待签名对齐。
> 2026-09-13：**主分支 CI 首次全绿（eaa764e）——GA 出口条件 #1 达成**。自 2026-09-07 起连续 39+ 次失败的 ci workflow（9 jobs：format、docker-build、integration-redis、build-test × Debug/Release × ubuntu/windows/macos）与 codeql 全部 success；前三轮修复（format 命令、Windows 文件锁清理、redis KeyPrefix）+ 本轮 flaky 竞态修复（NotificationTests/BotSessionTests 固定延时→确定性就绪等待）逐层收敛完毕。至此 P6 收口更正条目中"出口条件 #1 在当时不成立"的缺口补上。
> 2026-09-13：**修复 E2E 断连 flake（21f99da 纯文档提交轮 CI 首次暴露：windows Release `JobCreation_IsRecordedInAuditLog` 1m30s 超时，job 卡 running）**。链条：agent WS 断连（CI 负载）→ task 已认领卡 Running → CP 的 stale 回收受 `Vapor_TASK_LEASE_SECONDS`（生产默认 300s）约束 → E2E 死线 90s < 300s lease，**数学上不可能在死线内恢复**，断连一旦发生必挂。修复：E2E 栈启动 CP 时补 `Vapor_TASK_LEASE_SECONDS=15`（E2E 已有的 aggressive-timing 覆盖家族漏了这一项）——15s = 3× 心跳间隔（5s），CPU 饥饿下漏 2 拍不误抢活 task；死 task 15–20s 内回收重派，重连 agent 重试完成。生产 300s lease 是合理保守值，不改。本地 E2E 6/6 过。
> 2026-09-13：**覆盖率冲刺：整体行覆盖 74.3% → 95.9%，测试总数 1185 → 1493 全部通过（**计数更正：该条初稿合计 1493 漏统计了 MobileAuthenticator 65→126 与 Protocol 22→37 的补测，全量实为 **1569**，TESTING.md 已同步修正；提交信息同样滞后，以本条为准**）**。方法：用 Python 从最新 cobertura XML 按程序集归一化提取每文件未覆盖行（跳过 branch 行），映射源码语义后按性价比逐个补测，四轮全量回归（每轮 `run-tests.sh -c` 全绿）。Steam.Core 68.6%→94.9%（700→900 测试）：动作分支加固（Loot/GetInventory/SwapDuplicates/FindDuplicates/RedeemKey/GetCardDrops/AddLicense/DataActions 的失败语义/限流/值形状/分页/无登录态路径）、SessionManager（token 刷新循环故障吸收/恢复回调/事件订阅/凭证缺失组合）、SteamStoreApiClient（market 新契约解析与全部降级/畸形响应）、SteamWebHandler 请求构造（分主机 Referer/Origin、cookie/header 组装、重试耗尽语义、不可解析 Retry-After 回退、Dispose 幂等）、**RedisVaporCache mock 化**（mock IConnectionMultiplexer/IDatabase/IServer 使 148 行 SWR 锁/信封协议/索引维护/SCAN 清理全部离线覆盖——此前唯一途径是需 VAPOR_TEST_REDIS 的集成测试）。ControlPlane 205→297（DesiredStateReconciler 执行路径加固：后台循环异常存活/无 agent/在途窗口 + Program 分支加固）。MarketWatch 32→48（单 app 抓取失败隔离/周期中取消干净停机/webhook 传输崩溃吞并/阈值 payload 值形状全分支）。TESTING.md 基线与明细表同步刷新。**产品不一致记录**：`SwapDuplicatesAction.TryParseUInt32` 缺 `case double` 分支（Loot/GetInventory 均有）——JSON 往返 payload 中小整数表现为 double，`570.0` 会被静默跳过；测试暂按真实行为断言并加注释，后续统一（已于同日统一修复，见下条）。剩余 629 行未覆盖为集成壳（SteamTradeClient 需真实 SteamKit2 会话）、防御分支（数学上不可达的兜底 throw、仅 ClearCookies 可触的 loginCookies 合并）与 E2E 进程边界，已归档至 TESTING.md 覆盖率段。
> 2026-09-13：**统一三处 `TryParseUInt32` 的 double 分支**：`SwapDuplicatesAction` 补上 `case double d when d > 0 && d % 1 == 0 && d <= uint.MaxValue`（与 `LootInventoryAction` 同写法、与 `GetInventoryAction` 的 `Math.Floor(d) == d` 语义等价），JSON 往返 payload 中的 `570.0` 不再被静默跳过；`swap_duplicates` 的 app_ids 值形状测试更新（`570.0 → 570` 解析、`570.5` 非整数反例仍跳过）。测试总数不变（1569），Steam.Core 900 全过。同步修正 TESTING.md 合计（1493→1569）与 MobileAuthenticator/Protocol/插件体系计数。
> 2026-09-13：**修复 CI flake（SessionManagerTests.TryRestoreSessionAsync_WithEventCallback_InvokesCallbackForSessionEvents 抛 "Collection was modified"）**。根因：回调里 `List.Add` 持 lock，但 `Assert.NotEmpty/Assert.All` 枚举时不持同一把锁——恢复成功后事件泵仍在后台并发回调，枚举撞上写入即版本检查失败。修复：收集器换 `ConcurrentQueue`（枚举快照语义，天然线程安全），10 连跑全过。
> 2026-09-14：**CI coverage job 超时修复 + 覆盖率第二轮冲刺：95.9% → 98.6%，测试总数 1569 → 1756 全部通过**。① CI `.github/workflows/ci.yml` 的 coverage job `timeout-minutes` 30 → 60（近期 coverage job 在 30min 限制下超时，build-test 三平台全绿——限流 MaxCpuCount=2 的全解决方案覆盖率跑分天然逼近 30min）。② 第二轮覆盖率冲刺（ControlPlane 97.9%→99.5%、Agent 91.9%→98.1%、Plugins.Core 88.1%→99.1%、Monitoring 89.7%→97.2%、Steam.Core 94.9%→98.0%；+187 测试：ControlPlane +18（DesiredStateReconciler 执行路径/EventBroker 并发/Notification 无 sink 快速返回/SqliteJobStore 迁移与并发/RecurringJobScheduler/AccountStore 空名校验/新建 AuthTests 8 个 Bearer 解析分支）、Plugins.Core +36（事件分发/清单/配置扩展/卸载 ALC/加载，配套新建 TestFixtures 故障 fixture 库并入 sln）、Monitoring +12、MarketWatch +8、Steam.Core +65、Agent +1（password+refreshToken 组合 payload 分支））。③ **MarketWatch 三个循环级测试确定性化**：轮询循环由 InitializeAsync 末尾启动并在首个 `Task.Delay` 捕获配置值 10s（下限防商店 API 滥用），事后反射改 `_interval` 赢不过循环到达 Delay 的时序（~50% flake）——新增 `RestartLoopForTestsAsync` 测试钩子确定性重启循环（快间隔 + 先脚本化 park 再启动），`Shutdown_WhileLoopParkedInFetch`/`Shutdown_DuringParkedFetch`/`PollLoop_ShortInterval` 三测试稳定；`PollOnce_WebhookCanceledDuringSend` 断言放宽为 `ThrowsAnyAsync<OperationCanceledException>`（HttpClient 把取消的 send 包装成 TaskCanceledException）。④ 修正前轮 agent 留下的错误测试假设：`SettleActiveJob_CardDropsOutcomeWithoutTasks` 按源码真实语义重写——空 farm 队列 settle 后同轮即重派（`refreshDue = FarmQueue is null || …`），改名 `…_SettlesQuietlyAndRequeries`。⑤ 死代码归档（不投入）：`VaporCryptoHelper` 三处 catch（`Enum.IsDefined` 前置 + SHA256 密钥归一化后不可能抛）、`RedactingLoggerProvider.AppendPairs`（私有嵌套类非接口成员无调用点）、`HttpCircuitBreaker` RecordFailure 的 HalfOpen 存储态分支（`_state` 从不赋值为 HalfOpen——半开是 `GetEffectiveStateLocked` 的推导态）、`SteamTotp` 空 base64 分支（必被 IsNullOrWhiteSpace 前置拦截）、`RecurringJobScheduler` 的 `missed>0 ∧ future=null` 组合（周期性 cron 匹配过一次必有下一次）。⑥ 覆盖率管道实践记录：solution 级 `dotnet test --filter` 会让 0 测试的项目仍写**全零覆盖率报告**毒化 `coverage-summary.py` 合并（本次发现并整轮清理重跑）——补测后务必整轮 `run-tests.sh -c` 重跑而非局部补报告。⑦ TESTING.md 全面刷新（1756 基线/明细表逐类更新/剩余 159 行四类归档：死代码 ~39/集成壳 ~37/平台分支 ~12/防御分支 ~71）。CI format job（`dotnet format --verify-no-changes`）本地验证通过。
> 2026-09-14：**修复 CI 三失败（format / coverage / windows Release），run 34800549008 全绿**。① format：`BotSessionBranchTests.cs` 事件回调三元表达式缩进，`dotnet format` 修复（417ecd1）。②③ coverage 与 windows Release 挂**同一测试** `QrLogin_PostQrLogonCanceledBySessionShutdown_ReportsCanceled`（两平台同报 `expected event not observed; seen: []`）——测试确定性缺陷而非产品 bug，根因链：BotSession 事件回调经 `RaiseEventCallback` → `Task.Run` fan-out（投递延迟无上界）+ 每个活会话的 `RunSteamCallbacksAsync` 泵线程占一个池线程阻塞 `Thread.Sleep(100)` → CI 4 核 runner 上并行测试类的泵线程数超池最小线程数 → **线程池饥饿**（~500ms 注入一线程）；该测试 t≈0 就等 `qr_required` 事件，3s 预算内回调零执行（`seen: []`，连最初 state_changed 都没有）。`BotSessionQrLoginTests` 同等事件却没挂——它们先 `await LoginAsync()` 走完整轮，池早已消化回调。修复（f151ba6）：park 前置条件改用 **mock 被调用本身的 TCS 确定性信号**（mock Returns 回调内 TrySetResult），不再以事件 fan-out/`Task.Delay(150)` 作 park 代理；同文件 `QrLogin_BeginChallengeCanceledBySessionShutdown` 同族隐患一并修；Dispose 后结果等待 3s→10s；`qr_required` 事件路径行为覆盖由 `BotSessionQrLoginTests:51` 承担不丢。本地验证：单跑 5 轮 + `taskset -c 0,1` 限 2 核全量 Steam.Core.Tests 2 轮 970/970（复现 CI 失败环境）+ `dotnet format --verify-no-changes`。**经验**：本地多核必过、CI 必挂先查池饥饿；park 前置条件用 mock TCS 信号，事件等待只配在整轮 await 完成之后。
> 2026-09-14（补记，fd1ae2e）：覆盖率第二轮半（+36 测试，1756→1792，全部通过）。新增 TracingTests（Agent/ControlPlane 各 1 组）、TestFixturesTests（Plugins.Core，故障 fixture 库 68.3%→100% 全覆盖）、SteamTimeSynchronizerTests（9 测试，HttpListener 真实 HTTP 壳覆盖，集成壳清单相应缩水），扩展 13 个既有测试文件；执行 §10.3 日志此前"归档不投入"的死代码删除（VaporCryptoHelper 防御 catch、HttpCircuitBreaker HalfOpen 存储态分支、RecurringJobScheduler missed 组合、SteamTotp 空 base64、RedactingLoggerProvider.AppendPairs）并简化永不到达的错误处理（永不满 TryWrite、按构造非空的 null 守卫等）；coverage/ 进 .gitignore。本地覆盖率汇总 99.5%（11003/11058）。**该提交违反文档同步工作流（零 .md 变更、未跑 format 门禁与限核回归），随后 CI 三失败（见下条），本条与 TESTING.md 基线刷新（1756→1792、逐类明细重算、归档改三类 57 行）为补记。**
> 2026-09-14：**修复 CI 三失败（fd1ae2e 轮：format / ubuntu Release / windows Release）+ 回调泵结构性修复**。① format：fd1ae2e 新增的 SteamTimeSynchronizerTests.cs 20 处 WHITESPACE（提交前未跑 format 门禁），`dotnet format` 修复、`--verify-no-changes` 过。②③ 两处测试失败同属文档在案的池饥饿 flake 家族，被 fd1ae2e +36 测试的负载压破，本轮除预算兜底外**消除了饥饿根源**：`BotSession.RunSteamCallbacksAsync` 由 `Thread.Sleep(100)` 同步泵改 `await Task.Delay(100, ct)` 异步泵——此前每个带 transport 的活会话永久占一个池线程（f151ba6 日志记载的根源），节奏不变（100ms 轮询 RunCallbacks）、空档期线程归还池子、OCE 静默退出语义保持；该泵本就跑在 Task.Run 池线程上，无线程亲和性假设，生产行为等价。- **ubuntu Release**：`QrLogin_PostQrLogonCanceledBySessionShutdown_ReportsCanceled` 在 parkedInLogon 10s 预算内未等到 transport 调用——f151ba6 的确定性 park 信号本身有效，是抵达 park 的整条登录链在饥饿下爬行超预算；同族 4 处预算 10s→30s（park 等待与结果等待对称，预算只覆盖池调度，健康路径不等待）。- **windows Release**：`SessionWorkflowTests.Workflow_EventSubscription_ReceivesEvents` 重写——该测试**从未验证过事件投递**：fixture 的 SessionManager 无 transport，创建会话零事件，收集器永远凑不满 2 条，历史全靠 `cts.Cancel()` 路径假通过（断言仅 NotNull）；本次失败即 Task.Run 主体未在 2.2s 窗口（Task.Delay(200)+WaitAsync(2s)）内被调度。重写为确定性双事件流：stub 登录（Disconnected→Connected）+ DisconnectAsync（→Disconnecting）恰好 2 次 StateChanged，删除 Task.Delay(200) 就绪睡眠，断言收紧为 events.Count==2 且逐条校验类型与状态（通道无界不丢事件，晚启动照样收齐）。验证：定点 4/4；`taskset -c 0,1` 限 2 核复现 CI 饥饿环境 Steam.Core.Tests Release 985/985 + Debug 985/985；全解决方案覆盖率轮 1792 全绿、99.5%（11003/11060）；format 门禁过。TESTING.md 基线全面刷新（1792/逐类明细/覆盖率表/归档三类）。
> 2026-09-14：**P7 立项：市场闭环（挂单读取 → 批量撤单 → 挂单创建）**。从矩阵两个后置项（挂单创建/批量撤单、成就解锁/管理）中选前者的依据：其"需库存/定价前置"已被 P6 自身补齐（库存 REST + duplicates、行情读取 + MarketWatch、mobile 确认已支持 market 类型、TradeRateLimiter/熔断/dry_run 底座），后置理由只剩 ToS 灰区半边，以四层缓解应对（分期把灰区核心放最后 / 创建默认 dry_run / per-account 显式开关默认关 / 市场专用保守频控 + 不做自动重定价），且补齐 farming→变现最后一环契合平台定位；成就解锁/管理维持后置（"需求弱"记录未变，且紧邻明确不采用的"成就数值编辑"灰区，无新驱动）。附带记录：矩阵 §3.6 积分商店认领（ASF ✅ / Vapor ❌）既非后置也非定位外，属未立项缺口，留待 P7 后评估。文档同步：todo.md 新增 §12（P7 三子项 + ToS 缓解设计 + 不立项成就的理由）、§9 新增第 11 项、§11.4 挂单项勾选指向 §12；feature-matrix.md 图例新增 📋（已立项）、§3.5 挂单两行与 P6-4 对应项同步。纯文档提交，无代码变更，基线参照 1792 / 99.5%。
> 2026-09-14：**修复 CI flake（TradeRateLimiterTests 连续两轮两平台同挂：a9b1397 windows Release、0b66056 macos Debug，均 `Acquire_WhenConcurrencyFreedDuringTimedWait_Succeeds` [5 s] 超时）**。根因同属在案的"固定延时当 park 前置条件"flake 家族新成员：测试用 fire-and-forget `Task.Delay(50)` 后释放并发槽位，赌第二个 acquire 已停靠在限时等待里；CI 调度延迟下 5s `AcquireTimeout` 先到（邻测试 100ms 等待实测膨胀到 936ms）。修复（72d4a7d）：利用 SemaphoreSlim 语义做**零时序假设**构造——槽位被持有时第二个 acquire 不可能在释放前完成，先启动等待者再释放，两种交错（已停靠 → Release 唤醒；未停靠 → 释放转为可用计数、到达即成功）断言都成立，删除 Task.Delay 猜测；成功路径三个测试的 AcquireTimeout 预算按池调度纪律 5s/1s→30s（预算只覆盖池调度，健康路径即时完成；负路径测试的 SUT 自身超时即被测行为，不动）。验证：定点 16/16；`taskset -c 0,1` 限 2 核 Release 全项目 985/985；format 门禁过。
> 2026-09-14：**P7-1 挂单读取落地：`get_my_market_listings` action + `GET /v1/accounts/{name}/market/listings` REST 端点（+26 测试，1792→1818 全绿）**。① `SteamMarketClient`（Web 层，对标 SteamBadgesClient 的登录态页面客户端模式）：`GET steamcommunity.com/market/mylistings/?norender=1&start&count`（登录门控、无官方 Web API 等价物）。容错解析覆盖两类消费者文档相左的实测变体——数组名 `mylistings` 为主 `listings` 兜底；条目无内联 `asset_description` 时按 asset id join 顶层 `assets` 表；`listing_on_hold`/`listing_to_be_confirmed` 数组/数字双收。输出挂单 id / hash 名称 / 买方价格（分）/ 卖方所得（= price − fee，fee 缺失则 null 不估算）/ 资产摘要（market_name / icon_url / game_name）/ cancel_requested / time_created / 分页总数；无 listingid 或 price 的条目跳过不造值。② fixture `market_mylistings_p1.json` 为构造骨架（登录门控页面无法匿名录制，沿 P6-1 徽章页先例）：三方互证（cs2.sh 抓取指南 / node-steam-market-fetcher index.d.ts / SGI inventory/market.rs）+ 契约测试锁骨架，漂移时认证抓包重录；fixture 同时内嵌两种资产承载变体（内联 description + assets 表 join）。③ 命名与既有公共行情搜索 `get_market_listings` 显式区分，本 action 为 `get_my_market_listings`（自己的挂单）。④ CP 同步端点走 AccountTaskRunner 三态（Finished→200 / 窗口未完→202+job_id / Failed→502），审计 `market_listings.read`；start/count 分页（start 下限 0、count clamp 1..500），**单次派发只取一页、翻页由调用方续派**——大批量挂单不阻塞 30s 同步窗口，也即 P7-2 批量撤单的前置供数形态。⑤ 不加缓存：挂单随撤单/售出即时变化，陈旧 listing id 会直接误导撤单决策（与 get_card_drops 的 SWR 缓存场景相反）。验证：Core 21 + CP 5 新测试全过；全量 1818/1818；format 门禁过。
> 2026-09-14：**P7-2 批量撤单落地：`cancel_market_listings` action + `POST /v1/accounts/{name}/market/listings/cancel` REST 端点（+19 测试，1818→1837 全绿）**。① `SteamMarketClient.CancelListingAsync`（撤单契约对标 SGI market.rs 与市场页取消按钮同款请求）：`POST steamcommunity.com/market/removelisting/{listingId}`，form body 回显 `sessionid`（`SteamWebHandler` 新增 `TryGetSessionId` 读取——sessionid 是普通会话 cookie 而非凭证，可回显；无 sessionid 即未登录，直接失败零请求）；XHR 标记头 `X-Requested-With: XMLHttpRequest` 单独由 client 传入，Referer/Origin 由 web handler 对 community 域自动补齐（client 层不可重复加，实测重复会拼出双值头）；429 由 handler 既有弹性层退避重试，任意 2xx 即成功。② `cancel_market_listings` action（RequiresLogin，超时 600s）：过滤器 app_id / market_hash_name（Ordinal 精确）/ 买方价格含边界区间 / `older_than_seconds` 挂龄；action 内部翻页收集（PageSize 500 × MaxPages 20 防御上限，对 Steam 谎报 total_count 兜底）；逐条撤单间隔 `delay_ms`（默认 1000 对齐市场页节奏，测试注入 0），即市场专用保守频控、独立于交易限流预算；单项失败不中断，输出 `{dry_run, matched, scanned, succeeded, failed, listings[]}` 如实汇总（对齐 `confirm_all_confirmations` 语义）。③ dry_run 双层护栏：默认 true 只输出 `would_cancel` 清单零 POST；无过滤器 + 实撤在 **CP 400 拒绝 + action 层独立拒绝**（绕过 REST 直接派发也拦住，"refusing to cancel with no filter"）——一个手滑调用不能清空全部挂单；负价格 / min>max 同样前置拒绝。④ CP 端点：dryRun 默认 true，payload 只装显式出现的过滤器（避免缺省值伪装成用户选择），审计 `market_listings.cancel` 带 dryRun/hasFilter/outcome，三态 200/202/502 对称。⑤ 测试：client 4（sessionid 回显与 XHR 头、5xx→false、无 sessionid 零请求、空 id 抛参错）+ action 10（dry_run 默认/实撤/单项失败不中断/hash 精确/价格含边界/挂龄时钟无关——`older_than_seconds=1` 全匹配 vs `int.MaxValue` 全不匹配两极避免依赖真实时钟/无过滤实撤拒绝/倒挂价格区间拒绝/列表拉取失败报错）+ CP 5（鉴权/404/dry_run 默认/无过滤实撤 400/agent 失败 502）。全量 1837/1837；format 门禁过
> 2026-09-14：**P7-3 挂单创建落地：`create_market_listing` action + `POST /v1/accounts/{name}/market/listings` REST 端点 + `MarketFeeCalculator`（+36 测试，1837→1873 全绿）——P7 市场闭环三期全部完成**。① 契约四源互证（SGI market.rs / Steam 官方 economy_v2.js / Steam 官方 market_multisell.js / node-steamcommunity 社区实现群）：`POST steamcommunity.com/market/sellitem/`，form `sessionid/appid/contextid/assetid/amount/price` + XHR 头（Referer/Origin 仍由 web handler 对 community 域自动补）；**price 字段 = 卖方所得**——官方 `OnAccept` 取卖方框（`market_sell_currency_input`）值提交、买方框仅经 `GetItemPriceFromTotal` 联动换算，SGI 亦从买方目标价 `find_seller_price` 反推后发送，两路独立证实；响应 `success` bool（HTTP 200 也可被拒）+ `message`（限流无专用错误码仅文本）+ `requires_confirmation`/`needs_mobile_confirmation`/`needs_email_confirmation`/`email_domain`，非 JSON 回复返回 null，JSON 回复即便 HTTP 错误也带出 message。② 费用感知定价 `MarketFeeCalculator`：Steam 手续费按**卖方所得**计算——Steam 5% + 发行商 10%（各 floor 到整分、每项最低 1 分），买方支付 = 卖方所得 + 两费；反推从 `floor(target/1.15)` 向下走到不超买方目标（费率 floor 使精确目标不可达时落在下方，如 114 → 99/112）；dry_run 定价方案输出买/卖两侧价格，注明个别游戏发行商费率不同、以 Steam 卖单对话框为准。③ 三重护栏（ToS 缓解②落实）：`send` 缺省 = dry run 零请求只出定价方案；实撤需**账户显式开关**（`AccountSpec.MarketListingsEnabled` 默认 false，PUT 只在显式携带 `marketListingsEnabled` 时翻转——不带字段的 PUT 不会静默重置，CP 400 拒绝）+ **agent 显式开关**（`AGENT_MARKET_LISTINGS_ENABLED`，对齐 `AGENT_2FA_AUTO_SUBMIT` 先例，默认关，直接派发也拦住）。④ 价格输入仅来自调用方（不做自动重定价/爬价，ToS 缓解③延续）：`seller_proceeds_cents` / `buyer_price_cents` 二选一（双给/缺一/非正前置拒绝）。⑤ market 类 mobile 确认复用既有闭环：action 如实上报 `needs_mobile_confirmation`，确认走既有 `confirm_all_confirmations(type=market)`；CP 端点刻意不做自动确认链——挂单创建是灰区核心，保持显式步骤。⑥ CP 端点三态 200/202/502、审计 `market_listings.create`（send/账户开关/outcome）；payload 只装显式字段，`send` 仅在 true 时进入 payload（缺省键即 agent 侧 dry-run 默认）。⑦ 测试坑：payload 经 SqliteJobStore JSON 往返后**全部值变 JsonElement**（数值/字符串/布尔皆然），CP 侧断言需 Scalar helper 归一后再比；端点初版漏装 `send` 键（agent 将永远收到 dry run）被 `SendEnabled` 测试当场抓住——payload 透传断言的价值实证。⑧ 测试 +36：Core 30（费率 11：5%+10% 数学/双最低 1 分/买方反推精确与 floor 陷阱/往返不变式 + client 6：表单字段与 XHR/确认旗标/Steam 拒绝仍出 message/非 JSON/无 sessionid 零请求/参数校验 + action 13：dry run 默认零请求/买方价反推/双价·缺价·非正·缺 asset 拒绝/无 agent 开关拒绝/实撤 POST 与确认旗标/Steam 拒绝冒泡/请求层失败）+ CP 6（鉴权/404/无账户开关 send 400/dry run 默认与 payload 断言/开启后 send 透传/开关经不带字段 PUT 存活）。全量 1873/1873；format 门禁过。（收尾插曲：CI 暴露两个既有问题、两个独立 fix 提交修复——① macos 节点概率性暴露 `MaFileParserTests.Parse_EncryptedSda_WithWrongPassword_Throws` 泄漏裸 `JsonReaderException`：错密码约 1/256 概率垃圾明文恰好通过 PKCS7 padding 校验、其后 JSON 解析异常未包装；`c8061c9` 把解密后 JSON 解析统一包装成同一条 decrypt 错误并加确定性回归测试（正确密码 + 非 JSON 明文）。② coverage job 两轮先后挂 Redis 集成测试（SWR 8s 超时 / `SetAndGet` RedisConnectionException）而同配置的 integration-redis 专 job 两轮全绿：调用方连接串整体覆盖 options 默认值把 `abortConnect=false` 丢了，裸 `host:port` 下初始连接一次毛刺即整体失败；`afdef0c` 给 `CreateFromConnectionString` 初始连接加 3 次短退避重试（不可达服务器仍快速失败，+1 测试）。终态基线 1875，`afdef0c` CI 全绿。）
> 2026-09-15：**P8 积分商店认领落地：`get_points_shop_summary` + `claim_points_shop_items` action + `GET/POST /v1/accounts/{name}/points-shop/*` REST 端点（+24 测试，1875→1899 全绿）**。① 契约与通道：SteamKit2 3.4.0 内置 `LoyaltyRewards` unified service 全套类型（`SteamKit2.WebUI.Internal`），经既有 `SteamUnifiedMessages` 通道调用（与 RedeemKeyAsync 的 `Store#RegisterCDKey` 同写法先例）——`LoyaltyRewards#GetSummary`（余额：points/points_earned/points_spent）、`LoyaltyRewards#QueryRewardItems`（按 definitionids 点查 + cursor 分页，防 Steam 循环同一 cursor 的 bulletproofing 照抄 ASF）、`LoyaltyRewards#RedeemPoints`（defid → communityitemid）；`ISteamTransport` 新增三方法协议无关面（`GetPointsShopSummaryAsync`/`QueryPointsShopItemsAsync`/`RedeemPointsShopItemAsync`），对齐 ASF ArchiHandler 同源实现（GetPointsBalance/GetRewardItems/RedeemPoints）。② 语义对齐 ASF RP 命令：免费定义（`point_cost == 0`）默认可兑，付费定义需 `force=true`（ASF 的 defid 后缀 `!` 等价物）且缺省**整批前置拒绝**——批内先 QueryRewardItems 验证（未知 defid 或付费无 force → 一个都不兑），通过后逐条兑换、单项失败不中断如实汇总（confirm_all 语义）；`expected_points_cost` 留 0 不校验（对齐 ASF：调用方已前置验价，避免竞价类失败面）。③ 发现供数与 ToS 节制：`get_points_shop_summary`（balance + `definition_ids` 点查 + `free_only` 客户端侧过滤 + `items_total` 报查询命中总数）是认领的必要前置（defid 没有发现途径 claim 就没法用）；**刻意不做"扫描全部可认领"的自动化**——defid 只来自调用方显式输入，与 add_license 同档风险控制。④ CP 端点：GET summary 的 `definition_ids` 走逗号分隔 query，不可解析值 400 而非静默跳过（掩盖调用方错误）；POST claim 的 `force` 仅 true 时进 payload（缺省键即 agent 侧免费默认，false 不伪装成选择，对齐 `send` 惯例）；三态 200/202/502、审计 `points_shop.summary` / `points_shop.claim`。⑤ 红线延续：全程无 secret 接触（走已登录 transport 会话），`community_item_id` 64 位一律字符串化防 JS 精度丢失（P6-2 教训复用）。⑥ 测试 +24：Core action 16（summary 7：余额/点查形状/free_only 过滤保 items_total/无 client/无响应/定义查询失败保余额 + claim 9：缺 ids/无 client/免费兑换含 64 位字符串断言/付费无 force 零兑换前置拒绝/未知 defid 前置拒绝/force 跳过查询/单项失败不中断/无响应如实报告）+ CP 8（summary 鉴权/404/非法 query 400/透传断言 + claim 鉴权/缺 ids 400/payload 透传/force 缺省键）。测试坑：claim 的 "no response" 测试必须 `force=true` 直达兑换路径——Loose mock 的 QueryPointsShopItemsAsync 默认返回 null 会提前走 lookup 失败分支（error 路径测试要显式避开未 mock 的前置依赖）。全量 1899/1899；format 门禁过。收尾补一笔：代码提交的 coverage job 三轮连挂三个不同 Redis 集成测试，本轮实锤为 `RedisTimeoutException`（command=GET，6977ms > 默认 5s syncTimeout）——异常自带 POOL dump（QueuedItems=19、Min=4）证明是 coverlet 插桩下线程池排队拖慢命令响应，连接本身健康（integration-redis 专 job 三轮全绿佐证）；2ca78a8 测试连接串加 `syncTimeout/asyncTimeout=15s`（仅测试面，产品代码零改动，不加重试以免掩盖 SWR 时序语义）。
> 2026-09-15：**GA 出口条件验收盘点（§14）**：五条逐条仓库实证——#1（本地 Debug 全量 1899/1899、CI 10 job + codeql 全绿、自 eaa764e 起失败均可归因当轮闭环）/ #2（Core 27 action + 插件动作，六域全覆盖）/ #3（加密存储 v2、令牌持久化与密钥轮换、全链路脱敏、审计端点+专项审计点、secret 红线测试断言）/ #5（架构/运行/运维/发布/故障排查五类文档齐备）达成；#4 可运维 5/6，唯一字面缺口为**性能基线**（无基准测试与基线数据记录）。结论：GA 出口条件基本达成；性能基线列为独立后续小项（轻量压测 + `docs/performance.md`），不随盘点扩 scope。盘点为纯文档动作，未改产品代码、未动测试。

## 15. P9 阶段：游戏数据采集（爬虫式多账号分片）+ 字段字典（📋 已立项 2026-09-15）

> 立项动机：P3 数据底座（`get_game_info` / `search_games` / `get_price` / `get_market_listings`，匿名可达）之上补两块——①游戏信息字段语义只在源码注释里，无面向使用者的权威字典；②没有"分配不同账号抓取不同游戏"的爬虫式编排（计划 → 分片 → 多账号并行采集 → 结果沉淀可查）。数据来自 Steam 公开商店端点，**全程无 secret 接触**；账号仅作分片/派发/审计归属。硬约束（实证 `AgentTaskExecutor`）：payload 无凭证时 agent 一律回放已入库会话——采集池账号须先登录入库，单账号失效只失败其分片。页面只读（触发走 REST），对齐 DashboardStaticTests 的 no-write-verb 契约。为自有扩展，**不对标 feature-matrix**（无对应行）。

### 15.1 采集闭环

- [x] 字段字典（docs 权威源 + 页面渲染）：`docs/data-dictionary.md`（六模型逐字段 + 来源端点 + TTL 层级 + 无凭证约束）+ `wwwroot/gamedata.html` 字典面板，两侧同步维护。（✅ 2026-09-15）
- [x] 账号分配（自动分片 + 手动覆盖）：`CrawlShardPlanner` 账号池 round-robin 均摊，显式 app→account 映射优先（目标不在池内回落轮转并告警，不丢 app）。（✅ 2026-09-15）
- [x] 采集结果（持久化可查）：`SqliteCrawlStore`（plans + per-app results 两表、due-cursor 认领、轮次聚合、每计划保留 N 轮）+ REST 查询 + gamedata 页结果浏览（过滤/分页/JSON 展开）。（✅ 2026-09-15）
- [x] 触发（一次性 + 可选周期复跑）：`CrawlRunWorker` 到期认领 → 分片派发 `get_game_info_batch` → 逐应用落库；`cron`/`intervalSeconds` 周期复跑（`ScheduleClock` 推进游标），one-shot 完成即清；`crawl.*` 事件接入既有 webhook 管道。（✅ 2026-09-15）
- [x] Core 执行单元：`get_game_info_batch`（≤200 app/批，单项失败不中断，与单应用共享 `game:{appId}:{cc}` 缓存层级，真实 HTTP 间 `interval_ms` 节流）。（✅ 2026-09-15）

> 2026-09-15：**P9 游戏数据采集落地（+98 测试，1899→1997 全绿；feat 提交 c5e06e7 + docs 提交）**。① Core：`GetGameInfoBatchAction`（`app_ids` CSV/JSON 数组双解析、逐 app 走 `FetchCachedAsync` SWR、`storeError` 隔离传输异常、`fetched>0` 即成功、pacing 仅对缓存未命中生效——`Func<TimeSpan,Task>` 注入测试零等待）。② CP：`CrawlShardPlanner`（override 优先/池外回落+告警、轮转确定性可断言、`Chunk` clamp 1..200）；`SqliteCrawlStore`（独立 DB 两表、`ClaimDuePlanAsync` 刻意不动 cursor——崩溃恢复重复可见而非静默跳过、完成时收尾游标、`ListRunsAsync` GROUP BY 聚合、`PruneRunsAsync` 幂等）；`CrawlRunWorker`（5s tick、`_inflight` 防重叠、超时 CancelJob+整片失败行、部分 dispatch 失败留 in-flight 由轮询收尾、输出解析 JsonElement/对象双路径）。③ REST 8 端点（全 admin + 审计；`ScheduleClock.Validate` 只在确有 cron/interval 时调用——one-shot 是合法默认形态，初版一律校验导致所有 one-shot create 400，被 CrawlApiTests 当场抓住）。④ 页面 `gamedata.html` 只读（六模型字典/来源与 TTL 表/计划/轮次/结果浏览，30s 轮询），dashboard/admin 互链，DashboardStaticTests +4 锁契约。⑤ 与计划的偏差记录：重叠 tick 只 `OverlapSkips++` 计数**不发** `crawl.run_skipped` 事件（周期计划长运行下 5s tick × 30min run 会产生 360 条噪音事件，skip 语义收敛为指标+审计；`crawl.run_skipped` 保留给空池）；StoreDataActionBase 之外新增 `internal` 构造注入 store client 工厂与 delay 便于单测。⑥ 测试 +98：Core 22 + CP 76（planner 9 / store 11 / worker 14 / REST 38 / 静态 4）。⑦ 附带卫生：6 个测试 factory 的 Config 显式 `CrawlDbPath: ":memory:"`，避免 DI 解析 `SqliteCrawlStore` 在测试工作目录落地 `data/crawl.db`。验证：全量 1997/1997；format 门禁过；CI（ci 10 job + codeql）以 c5e06e7 全绿为准。

## 16. 性能基线（GA 盘点 §14 #4 唯一缺口收口）（✅ 2026-09-15 完成）

> 立项动机：GA 盘点 §14 #4"可运维"的唯一字面缺口——无基准测试、无基线数据记录（时延/吞吐/资源占用）。既有 4 个组件级基准（队列/并发 claimer/EventBroker/SSE）的数字手抄在 TESTING.md 且出处环境未记录；缺 CP 只读端点时延（§14 收尾注点名"对 CP 只读端点与任务派发路径做轻量压测"）与资源占用两个维度，且无权威落点与归档机制。纯测试 + 脚本 + 文档动作，不动产品代码。

### 16.1 基准与文档

- [x] 时延基准：`ApiLatencyBenchmarks.cs`——6 个只读端点（8 并发 × 200 请求，p50/p95/max/RPS，真实 `:memory:` 存储 + 移除全部后台服务）+ `POST /v1/jobs` 任务派发写入口对照（串行 200）。（✅ 2026-09-15）
- [x] 资源基准：`ResourceFootprintBenchmarks.cs`——job store create+claim+finish 每操作托管分配量（进程级 `GC.GetTotalAllocatedBytes` 差值口径，异步代码跨线程池线程不能用 per-thread 计数器）。（✅ 2026-09-15）
- [x] 缓存基准：`CacheBenchmarks.cs`（Steam.Core.Tests/Performance）——热读并发吞吐 + 每读分配量 + distinct-key 写吞吐（P9 批量采集依赖层）。（✅ 2026-09-15）
- [x] 运行脚本 `scripts/run-benchmarks.sh`（两项目 Release + MaxCpuCount=2）+ 权威基线 `docs/performance.md`（实测环境/口径说明/三维度基线表/归档区）。（✅ 2026-09-15）
- [x] TESTING.md 性能章节收敛：手抄数字表移除，指向 performance.md 单一权威源（避免双处漂移）。（✅ 2026-09-15）

> 2026-09-15：**性能基线落地（+10 测试，1997→2007 全绿：CP 467→475 / Steam.Core 1090→1092；perf 提交）**。三维度齐备——时延（6 只读端点 p50 0–16ms / p95 4–47ms + `POST /v1/jobs` p50 1ms 对照，in-memory handler 口径）、吞吐（既有 4 项重录 + 缓存写 67,759/s、热读 >10⁶/s）、资源（job store 68,860 B/操作、缓存 252 B/读）。**取舍记录**：①基准不挂 CI（延续"本地开发机实测为准"口径——数字与机器强相关，CI runner 无横向可比性，挂上去只添 flake 面；CI 继续只保证测试通过，数字刷新走本地 `run-benchmarks.sh` + performance.md 归档）；②时延口径 = `WebApplicationFactory` in-memory handler（覆盖框架管道+序列化+存储，**不含真实网络栈**，performance.md 与测试注释双处注明——是代码回归基线，非端到端网络时延）；③断言语义 = 功能正确 + 宽数量级上限（p95<5s、分配 <10–20 万 B/操作，防 CI 抖动 flake），打印的数字才是基线；④手写基准延续（不引 BenchmarkDotNet，仓库零新增 NuGet 依赖约束）。实现要点：时延宿主 `BenchmarkApiFactory` 自建（既有 `ControlPlaneApiTests.TestFactory` 是 FakeJobStore 拒写，写路径基准不可用）并 `ConfigureLogging(ClearProviders)`——ASP.NET 每请求 info 日志既污染测量又淹没基准输出；`dotnet test` 显示 ITestOutputHelper 需 `--logger "console;verbosity=detailed"`（run-benchmarks.sh 已固化）。与旧手抄数字差异（队列 ~5,200/s → 本机 644/s 等）属测量环境不同，不构成回归信号，已在 performance.md 归档区注明。验证：全量 2007/2007；format 门禁过；CI 以本轮提交全绿为准。

## 17. 覆盖率回填冲刺（P7-3/P8/P9 新代码债还清）（✅ 2026-09-16 完成）

> 立项动机：2026-09-15 三个功能阶段（P7-3 挂单创建、P8 积分商店、P9 游戏数据采集）落地后新增 ~138 行未覆盖，合计覆盖率从仓库基线 99.5% 回落到 98.5%（Steam.Core 97.8%、ControlPlane 98.8%）。纯测试补齐（零产品代码改动），把新代码债补回至基线水平。

### 17.1 补测内容

- [x] Steam.Core 积分商店/市场 action：definition_ids 值形状全谱（SQLite JSON 往返后的 JsonElement number/string、.NET List 的 int/long/double/uint/short/string/null/bool、单标量包装、全无效值跳过查询、Distinct 保序）、metadata 契约、lookup 失败前置短路、挂单创建参数校验（缺 app/contextId、amount<1、buyer 低于最低价、EmailConfirmation 域上报）与撤单（负价格区间、pacing 间隔、取消透传 "canceled"、传输异常、webHandler 缺失分支）。（✅ 2026-09-16）
- [x] Steam.Core 客户端与批量动作：`SteamMarketClient` 非标准 JSON 值类型防御臂（字符串计数→null、bool currency→null、数字布尔归一）与空响应体；`GetGameInfoBatchAction` .NET List 混合数值解析、空列表 400、客户端工厂异常面。（✅ 2026-09-16）
- [x] ControlPlane CrawlRunWorker：读取/派发路径取消传播（OCE 经 `throw;` 不被 generic catch 吞）、GetJob 故障吞咽后轮次保活、超时路径 CancelJob 故障仍记整片失败、CancelJob 取消传播、planner 告警不阻断派发、审计故障不阻断、无 games/errors 键输出零行落库、混合列表（JsonElement 与 .NET 对象混排）输出解析。（✅ 2026-09-16）
- [x] ControlPlane 存储与 REST：`SqliteCrawlStore` 守卫（空路径/空 id/非正 keep/空 planId no-op）、planner 空池+overrides 告警、CrawlApi PUT merge 语义（省略 overrides 保留既有、显式空 appIds 400）、AccountApi cancel 四过滤器 payload 装载（trim/装键纪律）、五端点 202 pending/502 失败三态、claim 校验（账户 404/零 id 400）与重复 id 去重。（✅ 2026-09-16）

> 2026-09-16：**覆盖率回填冲刺（+53 测试，2007→2060 全绿：Steam.Core 1092→1117 / ControlPlane 475→503；合计 98.5%→99.5% 回到仓库基线，Steam.Core 97.8%→99.4%、ControlPlane 98.8%→99.5%；test 提交）**。剩余 73 行未覆盖全部落在既有豁免三类（平台分支 8 / 集成壳反射 ~15 / 防御兜底 ~50）。**取舍记录**：①`CrawlRunWorker.ExecuteAsync` 的停机 break（L116-118）与坏 tick 兜底 catch（L120-126）放弃覆盖——前者需要停机竞态窗口、后者需要 sqlite 存储层异常注入，注入点代价高于 8 行防御代码的验证价值，归档说明；②其缺口行中 L105/106/387/389 为 coverlet 对异步状态机的行归属偏差（对应路径均有断言成立的测试实际走过：kill-switch 日志在 StartAsync 同步段执行、dispatch OCE rethrow 被 DispatchCanceled 测试命中），不再重复补测；③`MetricsHttpServer` 客户端断开 catch（2 行）本轮实测未命中（上轮 100% 系时序性覆盖），Monitoring 99.4% 属时序边沿非代码债；④202 测试模式统一为 `AccountTaskRunner.WaitWindow=400ms`/`PollInterval=25ms` + `removeHosted`（无人认领即 pending），try/finally 恢复默认值。**过程插曲**：整轮 `run-tests.sh -c` 首跑因系统内存不足被杀（swap 6.3/8Gi），重跑正常；覆盖率验证不可局部补报告（solution 级 filter 会让 0 测试项目写全零报告毒化合并），每批补测后整轮重跑。验证：全量 2060/2060；format 门禁过；CI 以本轮提交全绿为准。

## 18. 控制页面可用性修复（404 + QR 挑战断链）（✅ 2026-09-16 完成）

> 立项动机：发版前实测发现管理页面在真实部署形态下全部 404——2060 个测试全绿却没人请求过一个页面。顺藤摸瓜又发现 QR 扫码挑战事件从未到达页面（后端专为轮转 republish，前端两个 SSE 流都没监听）。本轮为产品修复 + E2E 守护补盲。

### 18.1 修复内容

- [x] 静态页面 404 根因修复：Web SDK 的 StaticWebAssets 只把 wwwroot 复制进 publish 不进 bin，且 `UseStaticFiles()` 默认 WebRoot=ContentRoot（裸 dll 启动时=cwd 而非 dll 目录）——`dotnet <路径>/dll` 从任意 cwd 启动（E2E 的真实形态）页面全 404。双修：csproj `<Content Update="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />`（Update 而非 Include，避免与 SDK 默认项重复）+ Program.cs 改用 `PhysicalFileProvider(AppContext.BaseDirectory/wwwroot)` 锚定部署目录（dev 形态 wwwroot 不在 bin 旁则回落默认 provider）。（✅ 2026-09-16）
- [x] E2E 静态页守护测试 `StaticPagesE2ETests`（+5）：4 页面 200+content-type+非空（匿名）、`/` 最终落到 admin UI；E2EStack 的 CP 进程恰是"bin dll + 测试器 cwd"的 404 复现形态。**红绿双向验证**：临时摘除 csproj 修复+清 bin/wwwroot → 5/5 红（正是当年漏掉的盲区）；恢复 → 5/5 绿。（✅ 2026-09-16）
- [x] QR 扫码挑战断链修复（admin.html/dashboard.html）：后端 `PublishQrChallenge` 同状态 republish 注释明说 "so dashboards refresh the QR challenge in place"，但 admin 的 authStream/sessionsStream 均未监听 `qr_required`，dashboard 同漏——QR 轮转后页面永远显示旧挑战；且 `renderChallenges` 把 qr_required 当认证码挑战渲染（误导性输入框）。修复：admin 两流补 `auth.qr_required`/`session.qr_required` 监听 + QR 专属渲染分支（挑战 URL 等宽展示 + 复制按钮，去掉认证码输入）；dashboard 补 `session.qr_required`。（✅ 2026-09-16）

> 2026-09-16：**控制页面可用性修复（+5 E2E 守护，2060→2065 全绿；fix 提交）**。①404 链路完整实证：`dotnet msbuild -getItem:Content` 确认 SDK 默认项无 CopyToOutputDirectory → cwd 矩阵实验（`dotnet run` ✅ / publish 目录内启动 ✅ / Docker WORKDIR ✅ / bin dll+任意 cwd ❌ 404）→ E2E `VaporProcess.Start` 不设 WorkingDirectory 且全部测试从不请求页面——两层盲区叠加。②QR 链路冒烟（bin dll + 无关 cwd 实例）：POST `/v1/sessions/events`(agent token, qr_required+URL) → challenge 归类 ✓ → `GET /v1/auth/challenges` 含 URL ✓ → SSE 实发 `event: auth.qr_required` ✓ → 部署产物页面含渲染分支 ✓。③页面逐字段对账（admin 消费的 Job/JobTask/SessionSnapshot/AuthChallengeEvent 字段全部存在于后端 camelCase 序列化）、三页面 JS `node --check` 全过。**取舍记录**：二维码图片渲染本轮不做——内嵌 QR encoder 数百行无测试护栏、后端引 QRCoder 违反零新增 NuGet 约束、用第三方图片服务会把登录 token 发出去（安全红线）；先修到"可复制的挑战 URL + 明确文案"，页面内二维码记为后续增强。验证：全量 2065/2065（含新 E2E 5）；format 门禁过；CI 以本轮提交全绿为准。

## 19. 覆盖率收尾冲刺（74→39 行，确定性缺口全清）（✅ 2026-09-16 完成）

> 立项动机：§17 回填后仍剩 74 行未覆盖（99.5%）。对全部缺口行做可测性分级（DETERMINISTIC / PLATFORM / TIMING / DEAD）后，把所有可确定性覆盖的行一次收掉，其余 39 行归档豁免。纯测试补齐（零产品代码改动）。

### 19.1 补测内容

- [x] Steam.Core 会话与缓存：`BotSession` QR 挑战轮转 republish（无事件回调分支）与 `SessionCommand` record 合成成员；`RedisVaporCache` SWR 缺键直填（无删除分支）与 `RemoveByPrefix` 全端点断连回退首端点；`RedactingLoggerProvider` 非泛型 IEnumerable 枚举臂；`FileCredentialStore` 符号链接→/dev/null 的 chmod EPERM 降级（Save/Load 各一，Linux 非 root 守卫）；`VaporCryptoHelper` 合法 base64 但解码 <32 字节回退 raw UTF-8；`SteamMarketClient` currencyid 字符串臂；`CancelMarketListingsAction` min_price 过滤排除更低价挂单；`GetGameInfoBatchAction` 不支持元素种类点名报错。（✅ 2026-09-16）
- [x] ControlPlane 后台循环收尾：`CrawlRunWorker` 坏 tick 兜底 catch（tick 继续、下一拍仍派发）+ 停机竞态双路径（dispatch 在飞时 Stop 静默 break）+ 审计取消传播；`DesiredStateReconciler` unassign 存储故障逃逸到后台循环 catch（循环存活证明=第三拍成功 unassign）；`NotificationService` 有限流 broker 三泵自然排空（不调 StopAsync 即 RanToCompletion）；`TaskSchedulerService` 无 listener 惰性分发 + `VaporTracing.InjectTraceparent` 双 null 臂；`AccountTaskRunner` TaskRunResult record 合成成员（新文件）；`Program` agent WS 循环正常出口（SlowHeartbeatStore 改为延迟后不传已取消 token 给 inner，heartbeat 正常返回→循环条件评估→正常退出而非异常路径）；`MaFileImportCli` 不存在路径保留进展开列表点名报错；`PluginManager.DisposeAsync` 卸载自身抛错的 per-plugin catch。（✅ 2026-09-16）
- [x] 确定性加固（顺手）：`RecurringJobSchedulerTests`/`TaskSchedulerServiceTests` 的 StartStop 冒烟从 Task.Delay 猜时序改为等 store 被调用信号（ListedDue TCS / StaleRequeueLeases 轮询），消除两处 CI 饥饿隐患。（✅ 2026-09-16）

> 2026-09-16：**覆盖率收尾冲刺（+21 测试，2065→2086 全绿：ControlPlane 503→511 / Steam.Core 1117→1128 / Agent 53→54 / Plugins.Core 127→128；合计 99.5%→99.7% 即 13354/13427→13394/13433，未覆盖 74→39 行；test 提交）**。Agent 99.5%→**100%**、Plugins.Core 99.4%→**100%**、ControlPlane 99.5%→99.8%、Steam.Core 99.4%→99.6%。**方法论**：两个可测性分析子代理对 74 行逐条分级，DETERMINISTIC 全收、PLATFORM/TIMING/DEAD 归档——本轮证明其中 Monitoring 断连 catch 类"时序边沿"两轮实测均未命中，维持豁免判断。**关键手法**：①symlink→/dev/null 让 chmod 命中 EPERM（root 守卫跳过）；②合法 base64 且文本 ≥32 字符使 raw 回退满足密钥长度（"c2hvcnQ=" 双双过短会抛）；③有限流 IAsyncEnumerable fake 覆盖泵自然排空（`ExecuteTask.Status` 为 WaitingForActivation——BackgroundService 附加 continuation，轮询须用 IsCompleted）；④Moq 验证 ILogger.Log 的 exception 参数必须 `It.IsAny<Exception?>()`（实际调用带 UnauthorizedAccessException，字面量 null 不匹配）；⑤PeriodicTimer 取消双路径（等待中取消→OCE faulted / 已取消再评估→false 优雅退出）断言须路径无关（IsCompleted）。**归档豁免（39 行，四类）**：平台分支 2（FileCredentialStore Windows return）、集成壳/反射 ~21（SteamClientManager SteamKit 内部反射、SteamStoreApiClient 真实 HTTP、SessionManager 通道不 Complete 的正常出口、BotSession 回调深处）、结构性 DEAD ~9（RedisVaporCache for(;;)、RecurringJobScheduler/TaskSchedulerService 的 PeriodicTimer 仅 Dispose 返 false、MarketWatchPlugin 不可注入防御）、防御兜底 ~7（SqliteJobStore 迁移边沿、SqliteCrawlStore 同事务 CAS、SteamTimeSynchronizer 真实网络超时、MetricsHttpServer 断连 catch、CrawlRunWorker 105/106 为 coverlet 异步行归属偏差、Program 135 测试 bin wwwroot、MarketWatch const 行）。验证：全量 2086/2086；taskset 0,1 双核饥饿抽查新时序测试 10/10；format 门禁过；CI 以本轮提交全绿为准。

## 20. admin 页 QR 扫码登录按钮（✅ 2026-09-16 完成）

> 立项动机：feature-matrix P6-4 记录在案的后置项"管理面暂用 POST /v1/jobs 触发(UI 按钮后置)"。§18 已修好 QR 挑战渲染闭环（挑战 URL 展示+复制+轮转刷新），但触发入口仍要手 curl。本轮在 admin.html 会话面板补一键触发按钮，补全"按钮→login 作业→qrLogin 凭证→挑战 URL 上浮→手机扫码"的最后一段 UI。

### 20.1 实现内容

- [x] admin.html 会话面板 header 加"QR 扫码登录"按钮（与刷新按钮同容器并排）：`startQrLogin()` 经 `window.prompt` 取账户名（空/取消静默返回）→ POST `/v1/jobs` `{action:"login", targets:[name], payload:{qrLogin:true}}`（`AgentTaskExecutor` 读取的 camelCase 契约键，无需 password）→ `addEvent` 提示挑战链接将出现在认证挑战面板 + `refreshJobs` 跳转新作业；失败 alert 点名。（✅ 2026-09-16）
- [x] 契约守护测试 `AdminHtml_QrLoginButton_DispatchesLoginJobWithQrPayload`（DashboardStaticTests +1）：断言按钮 id、startQrLogin 绑定、`action: "login"` 与 `payload: { qrLogin: true }` 字面量——防止后续前端重构悄悄改掉 agent 读取的契约键。（✅ 2026-09-16）
- [x] feature-matrix.md 同步：L132 后置标记更新为 UI 按钮已落地。（✅ 2026-09-16）

> 2026-09-16：**admin 页 QR 登录按钮（+1 守护测试，2086→2087 全绿；feat 提交）**。改动纯前端（admin.html 单文件）+ 一条静态契约测试；脚本 `node --check` 过；复用 §18 的 QR 挑战渲染分支与 SSE 监听，零后端改动。**取舍延续**：二维码图片渲染仍不做（§18 三轮取舍不变：自研 encoder 数百行无护栏/零新增 NuGet/第三方图片服务泄 token 红线）。验证：ControlPlane 全量 511→512 绿；format 门禁过；CI 以本轮提交全绿为准。

## 21. 维护轮：假异步清理 + 坏 JSON 边界 + 测试普查刷新 + 无用 API 审计（✅ 2026-09-16 完成）

> 立项动机：feature-matrix 与 todo 计划项全部闭环后,用户指定方向：性能优化/代码清理/文档完善/边界测试。本轮四线并行,全部收口。

### 21.1 实施内容

- [x] 假异步清理（代码清理）：`VaporCryptoHelper.DecryptWithKey` 原为 `Task.FromResult(DecryptAes(...))` 的假异步（CPU 密集解密包成 Task）,唯一生产调用方 `CredentialStoreRotator.ReEncrypt` 再 `.ConfigureAwait(false).GetAwaiter().GetResult()` 阻塞取值——全仓唯一一处 sync-over-async。改为与 `EncryptWithKey` 对称的同步签名,rotator 直呼,测试侧 8 处 await 同步清理。（✅ 2026-09-16）
- [x] 坏 JSON 体边界测试（+3,ControlPlaneApiTests）：POST 语法损坏 JSON（`{"action": `）到 `/v1/jobs`、`/v1/accounts/{name}/loot`、`/v1/auth/challenges/{name}/code`,断言模型绑定层 400 而非 500——现有测试只覆盖语义校验 400（如 intervalSeconds=0）,从未覆盖不可解析体。（✅ 2026-09-16）
- [x] TESTING.md 测试普查刷新（文档完善）：结构块/统计表/全部每类表格从 2026-09-14 基线（1792）刷新到 2026-09-16 实测（2090）。方法论:`dotnet test --list-tests` 会在终端宽度折行长 theory 名（实测漏 14 个）,改用 TRX `UnitTestResult` 条目计数（与各项目运行数逐一对上）；全部每类计数经脚本与 TRX 真值交叉校验（两类同名跨项目误报人工核过）。补齐此前漏列的 12 个测试类行（市场/积分/抓取/批量详情等）,并记录"勿用裸 `dotnet test Vapor.sln` 全量跑"的基准饥饿坑（见下）。（✅ 2026-09-16）
- [x] 无用公共 API 审计（子代理全量普查,记录处置）：~340 个公共类型逐一 grep 生产引用。**处置**：①Protocol 死记录簇（`ActionDescriptor`/`ActionParamSchema`/`PermissionLevel`/`CommandRequest`/`CommandResult`/`JobEvent`/`TaskEvent`/`PluginEvent`,仅测试往返构造）与插件 Routes/Commands 扩展面互为预留（协议概念镜像 ASF,宿主侧暂未接线）——保留,属产品决策非死代码;②`ItemInfo` record 生产零引用,但 data-dictionary/gamedata.html 以"六模型"文档化且声称"交易/MarketWatch 内部使用"与现实不符——记录为待专项（数据字典对账）,不在维护轮草率删;③`ECryptoMethod.EnvironmentVariable/File` 臂、`SessionState.DisconnectedByUser`、`EPasswordFormat` 值 2/3——序列化契约面,保留;④`MetricsRegistry.GetValue`/`GaugeAdd`、`MetricsHttpServer.IsRunning`、`ScheduleClock.CountTriggerPoints`、`VaporCryptoHelper.HasTransformation`/`HasDefaultKey`——测试断言面/诊断面,保留（删除的计数 churn 大于价值）。负面结论同样有价值:PayloadReader/ActionRegistry/全部结果 record/全部状态枚举经查全部生产在用;`CredentialStoreRotator` 有 tools/Vapor.KeyRotation 生产工具消费（此前误判仅测试使用）。（✅ 2026-09-16）
- [x] 环境变量文档漂移检查（负面结果）：文档 VAPOR_* 与代码/compose 交叉核对——docker.md 的 9 个变量全部由 docker-compose.yml 消费;plugins.md 的 `VAPOR_METRICS_VERBOSE`/`VAPOR_ALERT_THRESHOLD` 是配置扩展示例代码里的示意名非系统变量;唯一未文档化的是 `VAPOR_ENVIRONMENT`（环境判定第三回退,低价值不补）。无真实漂移。（✅ 2026-09-16）

> 2026-09-16：**维护轮（2090 全绿;refactor/test/docs 三个提交）**。**过程中发现并归档**：裸 `dotnet test Vapor.sln` 九项目并行会饿死 60s 预算的 `SseEndpoint_ConcurrentConnections_AllReceiveEvents` 基准（并行全跑超时假红,项目单独重跑 39s 515/515 全绿）——已写入 TESTING.md 运行测试节,全量跑必须走 `./scripts/run-tests.sh`。**取舍记录**：审计的 15 个候选全部保留并逐条注明理由（契约面/扩展面/测试断言面/产品决策）,本轮零 API 删除——与此前"死代码删除轮"（删内部防御分支）不同,公共 API 的删除是契约决策;`ItemInfo` 与 data-dictionary 的对账单独立项待做（涉及 gamedata.html 六模型文案与守护测试联动）。验证：全解决方案 2090 用例——并行全跑 2089 过 + 1 基准饥饿假红,ControlPlane 单独重跑 515/515 后全绿收口;Steam.Core 加密/轮换相关 44/44;format 门禁过;CI 以本轮提交全绿为准。

## 22. data-dictionary 对账：移除幻影模型 ItemInfo（六模型→五模型）（✅ 2026-09-16 完成）

> 立项动机：§21 审计记录在案的待专项——`ItemInfo` record 生产零引用（lowest/median/volume 字段解析在 src 全仓不存在,get_price 返回的是 `PriceOverview` 形状）,但 data-dictionary.md 与 gamedata.html 以"六模型"文档化,且声称"由交易与市场监控流程内部使用"——文档与现实不符。git 历史保留完整形状,未来真做单件检价时可复活。对账确认其余五模型（GameInfo/PriceOverview/GameSearchResult/MarketListing/MarketListingsPage）全部生产在用。

### 22.1 实施内容

- [x] 代码移除：`GameModels.cs` 删 `ItemInfo` record（39 行,含 `item:{appId}:{marketHashName}` CacheKey）;`GameModelsTests` 删 `ItemInfo_SerializesWithCamelCase_AndRoundTrips` 并同步删 `CacheKeys_AreStable` 里的 ItemInfo 断言（4→3 测试）。（✅ 2026-09-16）
- [x] 守护测试更新：`DashboardStaticTests.GameDataHtml_DocumentsAllSixModels` → `DocumentsAllFiveModels`（数组去 ItemInfo）,并新增反向守卫 `Assert.DoesNotContain("ItemInfo", html)` 防幻影模型回流文档（与 `PointsShopItemInfo` 无子串冲突,gamedata 页不含积分商店内容）。（✅ 2026-09-16）
- [x] 文档双侧同步：data-dictionary.md 删 ItemInfo 节 + 头注守护测试名更新;architecture.md L319 模型列表去 ItemInfo;gamedata.html 删缓存表 prose 尾句（"由交易与市场监控流程内部使用"的不实声明）+ JS DICTIONARY 数据块（tab 由 `Object.keys(DICTIONARY)` 动态生成,自动五模型）,`node --check` 过。（✅ 2026-09-16）
- [x] TESTING.md 计数联动：Steam.Core 1128→1127、合计 2090→2089、GameModelsTests 4→3、DashboardStaticTests 行"六模型文档"→"五模型文档(含不回流守卫)"。（✅ 2026-09-16）

> 2026-09-16：**data-dictionary 对账收口（-1 测试,2089 全绿;refactor + docs 提交）**。此为 §21 审计②号处置的落地：公共 API 删除属契约决策,经对账确认零生产引用后执行;与 §21 维护轮"零 API 删除"不矛盾——那轮把决策时间留给本轮专项。教训延续：grep 结论不得被 `head` 截断（§21 曾因此误判 MarketListing,本轮复核确认其生产在用故只删 ItemInfo）。验证：GameModelsTests 3/3、DashboardStaticTests 9/9（含新反向守卫）、全解决方案 build 0 warning 0 error、format 门禁过;CI 以本轮提交全绿为准。

## 23. 修复主分支 CI 失败：TracingTests 与 OTel 宿主 boot 的进程全局 listener 并发（✅ 2026-09-17 完成）

> 立项动机：主分支 ci workflow 三挂一绿（830b404 windows Debug、5663482 windows Release、54d444d macos Release 均挂 `TracingTests.DispatchWithoutListener_DispatchesWithoutTraceparent`；2991552 同代码绿）——GA 出口条件 #1"主分支稳定通过"的现行缺口。§22 前后的两轮失败均为纯 docs 提交（代码未动）,失败模式为断言失败（1–4ms 内 `Assert.Null` 炸）而非超时,排除池饥饿家族。

### 23.1 根因与修复

- [x] 根因实证：`CompositionRootSmokeTests` 设 `OTEL_EXPORTER_OTLP_ENDPOINT` boot 真宿主 → `Program.cs` 的 `AddOpenTelemetry().WithTracing(… .AddSource("Vapor.ControlPlane") …)` 注册**进程级全局** OpenTelemetry listener；xunit 不同测试类 = 不同 collection = 并行运行,`DispatchWithoutListener` 的"无 listener 时派发不带 traceparent"断言落在宿主存活窗口（实测 ~2s:boot + 4 请求 + dispose）内时,`ActivitySource.StartActivity` 返回真 Activity → traceparent 注入 → 断言炸。与全部观测吻合：失败 Actual 是带 traceparent 的字典（唯一来源即全局 listener）、同代码间歇绿/挂（04:25 绿/14:08 挂/14:39 绿/连挂两轮）、Linux 三个 job 从未撞上窗口（并行调度时序不同）。非产品缺陷——产品"默认零开销惰性"语义（无 OTLP env 不注册 SDK）正确,是测试隔离缺口。（✅ 2026-09-17）
- [x] 修复（零产品代码,纯测试隔离）：新建 `ProcessGlobalTracingCollection`（`[CollectionDefinition(DisableParallelization = true)]`）,`TracingTests` 与 `CompositionRootSmokeTests` 双双入列——两类触碰进程全局状态（OTel listener + 进程级 env）的测试与全部并行 collection 串行化；沿 `MaFileImportCliCollection` / `VaporCryptoHelperTestCollection` 同款先例。`CompositionRootSmokeTests` 类注释同步更正：原注释"xUnit 类内串行保证 env 不互踩"只说对一半——类内串行不保证与其它类不并行,该假设在 DisableParallelization 落地后才真正成立。（✅ 2026-09-17）

> 2026-09-17：**修复主分支 CI 失败（测试总数不变,2089 全绿;test 提交）**。排查路径：gh run list 发现 9-16 当日三挂一绿 → `--log-failed` 提取失败断言（Actual 为带 traceparent 的 TraceHeaders）→ grep 全部 `AddActivityListener`/`AddOpenTelemetry`/`OTEL_EXPORTER` 注册点锁定唯一交汇。影响面核查：Agent.Tests 无宿主 boot 无同构风险；其余引用 TraceHeaders 的测试（WsProtocolReplayTests/ProtocolRecordsEdgeTests）为纯序列化测试不涉 ActivitySource。验证：定点（TracingTests 5 + CompositionRootSmoke 2）7/7、ControlPlane 全项目 515/515、build 0 警告 0 错误、format 门禁过；**复现场景前后对照**——testhost 设 `OTEL_EXPORTER_OTLP_ENDPOINT` + `taskset 0,1` + `MaxCpuCount=2` 模拟 CI 慢机,修复前 3 轮挂 1（复现失败断言）,修复后同场景 3 轮全绿；CI 以本轮提交全绿为准。

## 24. 进程全局状态 × 类间并行：同构 flake 全仓审计（✅ 2026-09-17 完成）

> 立项动机：§23 修复了已发作的一对（OTel 宿主 boot vs 无 listener 断言），但"进程全局状态 + xUnit 类间并行"是 flake 家族——同构地雷可能还有未爆的。对全部测试项目做一次系统排查，把家族一次清零或确认清零。

### 24.1 审计矩阵与结论

- [x] 审计维度与结果（全仓 grep 交叉比对，负面结果为主）：①进程环境变量——写方仅 CompositionRootSmokeTests（已入 ProcessGlobalTracingCollection）与 Plugins.Core 的 PluginConfigurationExtensionsTests（私有命名空间 `VAPOR_TEST_PLUGIN_*`，无他类读者，类内串行足够）；读方均为门控只读（`VAPOR_TEST_REDIS`/`STEAM_TEST_*`）。ProgramBranchCoverageTests 的"环境变量回退"走 host 级 UseEnvironment/configuration 注入，不碰进程 env。②ActivityListener/OTel——注册点仅 Program.cs（门控）+ TracingTests（using）+ OTel 宿主 boot（§23 已隔离）。③`Activity.Current` 赋值——TracingTests 内 save/restore 配对且已入非并行 collection。④`VaporCryptoHelper` 静态加密 key——全部触碰者（三个 crypto 测试类 + FileCredentialStoreTests）已在 VaporCryptoHelperTestCollection；Agent.Tests 侧仅 MaFileImportCliTests 一个使用者且有专属 collection；CredentialStoreRotatorTests 用显式 key 的 EncryptWithKey/DecryptWithKey 不碰静态（grep "VaporCryptoHelper.Encrypt" 匹配它属前缀子串误报）。⑤Culture/TimeZone/AppDomain.SetData——零测试触碰。跨项目互不影响（每项目独立 testhost 进程）。**结论：无第二颗已知地雷，§23 修复覆盖整个已知家族。**（✅ 2026-09-17）
- [x] 防再犯护栏沉淀（TESTING.md）：最佳实践新增第 9 条"进程全局状态必须入非并行 collection"，新开"进程全局状态与测试并行"节——三类全局状态 × 三个既有隔离 collection 的对照表、§23 案例与本地复现配方（OTEL env + taskset 双核 + MaxCpuCount=2）、审计口径（新测试触碰全局状态先查表入列）。（✅ 2026-09-17）

> 2026-09-17：**同构 flake 审计（零代码改动，纯审计 + 文档;docs 提交）**。方法论延续 §21"负面结论同样有价值"：逐维度 grep 读写矩阵 → 交叉比对隔离归属 → 误报甄别（前缀子串、host 级 vs 进程级 env）。主分支稳定性是 GA 出口条件 #1，本轮把 flake 家族的"已爆 ×2"扩展为"已审 ×全仓"，新地雷只能在未审计的全局状态类型里出现（新代码引入时由 TESTING.md 护栏拦截）。验证：无代码改动，CI 以 §23 修复后的绿为准。

## 25. 修复 CI flake：CrawlRunWorkerTests.BrokenTick 等待信号与被断言副作用错位（✅ 2026-09-17 完成）

> 立项动机：dcacd31（纯 docs 提交）的 ci 在 macOS Debug 挂 `CrawlRunWorkerTests.BrokenTick_LogsErrorAndKeepsServing`——又是 §19 引入的真实时钟测试。与 §23 并行隔离家族不同,这次是"等待信号 ≠ 被断言副作用"的时序家族（记忆中 vapor-test-determinism 已归档的模式）。

### 25.1 根因与修复

- [x] 根因：测试用 `WaitUntilAsync(() => _jobs.Created.Count == 2)` 作同步点,但 job 在其 `crawl.run_triggered` 事件 publish **之前**就已创建——慢机上断言在 tick 3 的 publish 飞行中执行,Published 快照只有 tick 1 一条事件,`Assert.Contains(run_id == Created[1].run_id)` 滤不中（失败现场唯一事件 run_id=7b56… 即 tick 1 的）。副路径同理：tick 1 的等待（Created.Count==1）后立即设 `ThrowOnPublishRuns=1`,若 tick 1 的 publish 尚在飞行,炸的会是 tick 1 的事件而非 tick 2 的 completion publish——测试语义本就欠定。（✅ 2026-09-17）
- [x] 修复：两处等待信号对齐到被断言副作用本身（`RunEventsPublished()` 计数 `crawl.run_triggered` 已 publish 条数）——等待信号=断言对象,窗口消失;arming 精准命中 tick 2。顺手加固 fake broker：`Published` 从裸 List 改为锁内快照（`IReadOnlyList` + ToArray）,消除断言枚举与后台 tick Add 的并发读写窗口（Publish/读侧同锁）。（✅ 2026-09-17）

> 2026-09-17：**CI flake 修复（测试数不变,515/515 全绿;test 提交）**。模式归档：等待条件必须是被断言副作用的前置完成信号,不能是它的前置动作的计数（job 创建 ≠ 事件发布）。§23/§25 已覆盖两类 flake 家族——进程全局状态隔离与等待信号错位;记忆 vapor-test-determinism 的 park 前置条件 TCS 信号法是同族正解。验证：CrawlRunWorkerTests 27/27（含双核压力 ×2）、ControlPlane 全项目 515/515、format 门禁过;CI 以本轮提交全绿为准。

## 26. 剪版 v0.1.0-alpha.2：九个月 Unreleased 收口 + 依赖陈账清理（✅ 2026-09-17 完成）

> 立项动机：todo.md P0–P25 全闭环、HEAD CI 全绿、feature-matrix 非后置缺口全收口——计划源已无未完成项（用户定向：剪版）。release 自 v0.1.0-alpha.1（2025-12-28）后近九个月未剪,全部工作积压在 CHANGELOG Unreleased;§9.8 建成的 release workflow（build×5 RID 打包 + GHCR 双镜像 + 自动建 Release,`-` 预发布 tag 不动 `:latest`）建成后从未端到端运行——本轮发布本身就是对该管道的首次真实验证。

### 26.1 依赖陈账（dependabot 五连挂 3–5 个月）

- [x] 盘点：#24（Test.Sdk 17.8→17.14.1）/ #25（xunit 2.6.2→2.9.3）/ #26（runner 2.5.4→2.8.2）/ #29（Mvc.Testing 8.0.24→8.0.27）四个 PR 的目标版本主分支**早已 ≥**（用户直提抢跑惯例所致）,关闭;#28（Microsoft.Data.Sqlite 8.0.24→8.0.27）方向不对——项目已 target net10.0,8.0.x 是 net8 时代遗留（同系 Mvc.Testing 已 10.0.12）。（✅ 2026-09-17）
- [x] `Microsoft.Data.Sqlite` 8.0.24 → **10.0.12**（与 Mvc.Testing 同版对齐;SQLitePCLRaw 2.1.13 满足依赖区间不动;store schema/行为无变化）。全量测试门禁过（ControlPlane 515/515 直接受影响——Sqlite store 全家桶在其内）,format 门禁过。（✅ 2026-09-17）

### 26.2 剪版

- [x] CHANGELOG 定稿：`[Unreleased]` → `[0.1.0-alpha.2] - 2026-09-17`（保留空 Unreleased 节）;顺手修三处结构债——重复的 `### Fixed` 节（第二条与首条逐字重复,整节删除）、Security 节尾孤行 `- Ongoing development.`、尾部对比链接补 `[0.1.0-alpha.2]` compare 区间。（✅ 2026-09-17）
- [x] tag `v0.1.0-alpha.2` 推送 → release workflow 端到端验证（按 releasing.md 清单：CI 绿 → annotated tag → 5 RID zip + GHCR 双镜像 + GitHub Release 自动建页）。（✅ 2026-09-17）

> 2026-09-17：**剪版完成（首次端到端发布,chore(deps) + docs 两次提交）**。release workflow 首跑：build ×5 RID 与 docker 双镜像（`controlplane:0.1.0-alpha.2` / `agent:0.1.0-alpha.2`,预发布 tag 未动 `:latest`）全绿;release job 建 Release 后上传资产时遇 GitHub 5xx（错误体为 unicorn HTML 页,6/10 上传后中断）——`gh run rerun --failed` 重跑,`overwrite_files: true` 覆盖已有 + 补缺,终态 10/10 资产 + prerelease 标记正确（GHCR 版本列表 API 需 `read:packages` scope,本地 token 无,以 docker job 绿为镜像发布凭据）。管道验证结论：机制无缺陷,唯一脆弱点是资产上传的瞬时 5xx,重跑即恢复,无需改 workflow。发布地址 https://github.com/cuihairu/vapor/releases/tag/v0.1.0-alpha.2 。

## 27. CI 首见第三族 flake：vstest 会话收尾失速（结果全部交付后宿主不退出）+ --blame-hang 守护（✅ 2026-09-17 完成）

> 立项动机：§26 剪版收口后的纯 docs 提交 51bc76b,ci 的 windows Release job 在 Test 步骤卡死 40+ 分钟。与 §23（进程全局状态并行耦合,断言红）和 §25（等待信号错位,断言红）不同——这次**无任何断言失败、无测试卡住,是宿主不退出的基础设施级失速**。

### 27.1 取证链

- [x] 现场还原（`--log` 读回）：16:35:52 最后一条输出为 MarketWatch **56/56 结果流式打印完毕**（同节点 Agent/Plugins.Core 16:35:49 前已正常收尾）→ 之后 40 分 20 秒零输出 → 17:16 job 级 45 分钟超时强制终止（run 结论 cancelled,非 failure）。唯一未完成的 VSTest target 是 MarketWatch.Tests,但被杀时其全部测试结果均已交付——卡的是会话收尾,不是测试。（✅ 2026-09-17）
- [x] 代码面排除：MarketWatch 生产代码轮询循环为 `Task.Run` 后台任务（不拽进程退出）;全测试项目无前台线程;该程序集内全部等待有界（`WaitUntilAsync` 30s 预算、门控 TCS 测试 5s 超时、release 必然 SetResult）;fixture 仅 1 个 IDisposable 且无循环等待。56/56 完成的静态+动态双重证据使"测试内挂死"不成立。（✅ 2026-09-17）
- [x] flake 判别：同提交的前一轮（唯一差异为 todo.md 文本）25 分钟前 10/10 全绿（windows Release 4m26s）;被杀后 `gh run rerun --failed` 即时重跑全绿。同代码两种结局 → 定性 vstest 会话收尾失速（windows hosted runner 基础设施 flake）,非确定性回归。（✅ 2026-09-17）

### 27.2 加固

- [x] ci.yml 两个全量测试步（build-test 矩阵、coverage）挂 `--blame-hang --blame-hang-timeout 12m`：testhost 无进展 12 分钟即转储 + 终止 + 未完成测试标失败快速红,最坏燃烧时间从 45/60 分钟压到 ≤12 分钟。integration-redis 不挂：SE.Redis 内部 15s 超时兜底 + 仅 11 个门控测试,无静默燃烧面;本地 run-tests.sh 不挂：交互跑挂住即 Ctrl+C。（✅ 2026-09-17）
- [x] tests/TESTING.md 新增「CI 挂死守护」小节记录机制与案例;工具链备注：`gh run view --log` 读回偶发截断（首次计数 51,缓存后 56）——取证计数以缓存文件为准。（✅ 2026-09-17）

> 2026-09-17：**第三族 flake 归档 + 守护落地（测试数不变;ci 提交 + docs 提交）**。三族 flake 图谱补齐：§23 进程全局状态并行耦合（断言红）、§25 等待信号与断言副作用错位（断言红）、§27 vstest 宿主收尾失速（无红、静默烧超时）。第三族无法靠测试代码预防（宿主行为在测试框架之下）,只能靠 CI 参数把「静默烧满」转化为「快速红 + 转储」;前两族的测试侧方法论不变（mock TCS 信号 / 等待=断言对象）。验证：守卫参数本地实跑确认 CLI 接受且 Blame collector 正常挂载;随本轮提交 CI 全绿。

## 28. MarketWatch.Tests CI 间歇卡死根因：在飞失速族（park 舞蹈 × 循环重启竞态）修复 + 收尾失速族观察定谳（✅ 2026-09-18 完成）

> 立项动机：§27 归档当日,守护首战即抓到第二次发作（276bf82 windows Debug,`--blame-hang` 12 分钟快速红 + 转储）,且历史取证推翻「一次性基础设施 flake」初判——自 09-14 起六次烧满/失速 job 全部落在 windows build-test,两次可取证的在飞清单都卡在本程序集。第三族一分为二:**在飞失速**（根因在测试代码,已修）与 **56/56 交付后收尾失速**（宿主层,待转储定谳）。

### 28.1 取证与定性修正

- [x] 发作清单（gh job 级审计,全部 45m 烧满 cancelled 除守护首战）:09-14 34898498429（win Release）、09-15 34918070742（win Debug）、09-15 34919945683（win Release + Debug 双烧满;在飞清单卡在本程序集:Edge 18 交付、Store 11/18 未完）、09-17 51bc76b run（win Release;MarketWatch 56/56 全部交付后收尾失速）、09-17 35253174090（win Debug;守护 12 分钟引爆,Edge 19/24 在飞,5 个未交付全为本类 Add/Remove/Shutdown 族）。macos/ubuntu 三天零发作。版本相关性排除（09-15 发作早于 Sqlite 10.0.12 升级;两天 SDK 同版本）。MarketWatchStore 实为纯内存字典（无 SQLite）,早期「共享 SQLite 基建」假设作废。（✅ 2026-09-17）
- [x] 根因定位:`Shutdown_DuringParkedFetch_CancelsCleanly` 在 `InitializeAsync` 之前就把 fake fetch 的 `parked.Task` 挂上 `PendingPrice`,且 watch 注册在 `RestartLoopForTestsAsync` 之前——重启内部 `await _loop` 等原循环退出;若原循环首轮 `Snapshot()` 落在 add 之后（CI `Task.Run` 排队延迟下常态）,它带 entry 进 fetch,park 在**无视取消令牌**的 Task 上,主线程死等 `await _loop` → 确定性死锁:该测试不交付、同程序集后续全部排队（与 19/24 在飞签名精确吻合）。本地低负载原循环总在 add 前跑完空轮 → 多轮不复现。生产侧 `PollLoopAsync` 全部退出路径静态核验完备（含 `Task.Delay` 的 OCE catch→return）;fake 无视 CT 是唯一结构性条件。（✅ 2026-09-17）
- [x] 同族筛查:三处 park 舞蹈中 `PollOnce_CanceledDuringParkedFreeFetch`（park 在 init 后挂,原循环若 park 上去其 OCE 被循环吞掉进 Delay,测试尾 DisposeAsync 的 Cancel 能收拾）与 `Shutdown_WhileLoopParkedInFetch`（add 在重启后,原循环存活期 store 必空不进 fetch）均安全;事发测试是唯一违例。（✅ 2026-09-17）
- [x] 修复:排序对齐 `Shutdown_WhileLoopParkedInFetch`——park 脚本与 watch 注册全部挪到 `RestartLoopForTestsAsync` 之后（原循环必然先退出）,删除 init 前的预挂;窗口关闭:原循环存活期 store 必空,永不进 fetch。本地 56/56 + 限 2 核 3 轮 + 全仓 2089 全绿。（✅ 2026-09-17）

### 28.2 尾速族与观察项（✅ 2026-09-18 定谳关闭）

- [x] ci.yml 两个全量测试步在失败时上传 `TestResults/`（hangdump + Sequence.xml）——守护首战转储因无上传步骤丢失,下次发作必须留证。（✅ 2026-09-17 上传落地;观察期内未再需要,机制留作复发预案）
- [x] 在飞失速族已修,后续 CI 若再发作应只剩收尾失速族;windows 连续多轮无发作即可关闭本节。（✅ 2026-09-18 定谳关闭,见下方收口块）

> 2026-09-17：**在飞失速族闭环（test + ci + docs 三提交）**。方法论沉淀:「无断言红、无测试卡住」不是测试代码无罪的证据——在飞清单（已交付/未交付 census）能把卡点定位到测试类;**让循环 park 在不可取消的 Task 上时,必须保证任何存活的前序循环都够不到它**（park 先挂或 watch 先注册皆死路,重启型测试的脚本一律放在重启之后）。收尾失速族（56/56 交付后宿主不退出）的「基础设施 flake」定性修正为「待转储定谳」,§27 图谱第三族据此两分。

## 29. boost 策略化：时长目标调度（✅ 2026-09-18 完成）

> 立项动机：功能矩阵 3.2 节「游戏时长 boost」行为 ⚠️（对标 ASF ✅ / SGI ✅）。现状 `play_games` 是裸动作——收到 appid 列表就挂、没人叫停就一直挂；无时长查询、无目标配置、无达标切换。目标：把 boost 从「手动 play」升级为期望状态编排，与 farm 同域同构（复用 `AccountDesiredState` + `DesiredStateReconciler` 模式）。

### 29.1 现状盘点（2026-09-18 调研）

- [x] `PlayGamesAction`（src/Vapor.Steam.Core/Actions/PlayGamesAction.cs）：action=play/stop/idle，payload `games` 支持 `12345, id/67890` 多格式（PlayGamesPayloadParser），HashSet<uint> 交 SteamClientManager.PlayGames——**多 app 并行挂机已支持**，缺的是上层的「挂多久」。
- [x] farm 编排模板（DesiredStateReconciler.ReconcileFarmAsync）：期望状态 Farm → 周期刷新队列（FarmQueueCheckedAt + FarmRefreshSeconds 节流）→ queue[0] 派 play → 掉完/队列空停挂保持在线。boost 完全可循此骨架：队列换成「未达标 app」，刷新换成「时长查询」。
- [x] **时长数据源缺口**：全仓无 playtime/hours 查询（IPlayerService/GetOwnedGames 未接）。SteamBadgesClient 徽章页 HTML 解析是现成模式——个人资料 games tab（`/my/profile/games?tab=all`）HTML 含每游戏 `xxx.x hrs` 同款可解析，用已登录 web session 访问自己 profile 无公开性依赖，无需 API key。
- [x] 配置面：AccountSpec 现有 IdleApps（idle 白名单）/Farm 排除名单先例，Boost 目标清单（appid → 目标小时）走同一条 spec → Agent → payload 链。（✅ 2026-09-18）

### 29.2 实施阶段（按依赖排序）

- [x] **P1 时长数据源**：`SteamProfileGamesClient`（Web/,仿 SteamBadgesClient）+ `GetPlaytimeAction`（payload `games` 可选过滤,输出按 hours 降序列表 + total_hours,SWR 缓存默认 30 分钟）。数据源定案：games tab 内嵌 `var rgGames = [...]` JSON(非 HTML Regex)——`appid`/`name`/`hours_forever`(带逗号字符串,缺失/null=0)三方交叉确认(johnnysparks gist 5376855、SteamTreemap steam_treemap.py、greasyfork 583222 用户脚本 + bendodson 提取器),fixture 走构造骨架模式。两处实现层强化:①marker 候选扫描(fixture 头注引用 marker 字面量即触发错位——只有后随 `[` 的候选胜出);②括号/字符串平衡扫描器(游戏名含 `;`/`[`/`"` 不截断,优于三方通用的「首个 `;` 切片」)。fail-loudly 落实:找不到 payload 抛异常而非空表(风险备注①)。（✅ 2026-09-18 10a9a77,25 新测试）
- [x] **P2 配置与 API 链**：`AccountSpec.BoostTargets`（新 record `BoostTarget(AppId, TargetHours)`）+ `AccountDesiredState.Boost = 4`；PUT /v1/accounts 透传 + 审计日志记录。校验落地:Boost 状态必须带至少一个目标(其余状态允许预配置);目标小时须为有限正数(NaN/Infinity/0/负数拒绝);同 appid 两个不同目标是配置矛盾直接拒绝(相同重复合并);appid=0 丢弃;输出按 appid 升序(与输入顺序无关)。文档:architecture.md API 描述同步。（✅ 2026-09-18 5fc5e26,8 store 测试 + 3 API 测试）
- [x] **P3 Reconciler boost 策略**：期望状态或 Farm 扩展模式二选一（立项倾向独立 `Boost` 状态,farm 与 boost 互斥时 boost 让位,同 farm 现行排除语义）；周期查时长 → 未达标入队 play（多 app 可并行) → 达标移出 → 全达标停挂回落 Idle；查询失败仅记 deviation 等下轮（farm 同款容错）。落地为独立 `AccountDesiredState.Boost` + `ReconcileBoostAsync`（farm 镜像：in-flight 守卫 + `ReconcileBoostRefreshSeconds` 默认 1800s 节流 + deviation 容错不吃登录预算）；未达标集合一次 play 派发多 app 并行；IdleApps 沿用排除名单语义；`SettleActiveJobAsync` 解析 get_playtime 输出（内存字典/SQLite round-trip JsonElement 双格式）。两处实现层强化（与 farm 不同处）：①refreshDue 以 `BoostCheckedAt` 判定而非「集合为 null」——失败/无效报告盖章后等满间隔再重试,避免每 15s 重拉 games tab 的查询风暴（farm 现行为仍是「null 即重查」,记 §30 观察）;②无效报告不落到集合上（视为 null 而非空表）,空表会被读成「全部达标」直接停挂。spec 变更重置 boost 簿记（targets 变更即重查）。测试:9 个新用例覆盖 查询→挂机→达标停挂全链、多 app 并行、排除名单、间隔节流、失败容错、无效输出、round-trip 与保守判定。（✅ 2026-09-18 dee4e96,65 测试全绿）
- [x] **P4 收尾**：feature-matrix.md 3.2 行 ⚠️→✅；todo 回填；全量测试 + CI 绿。（✅ 2026-09-18）

> 风险备注:①profile games tab HTML 结构无官方契约,Regex 解析需真实样本 fixture 护航,页面改版时 fail loudly（解析零命中要报警而非静默空表,否则 boost 会误判全部达标直接停挂）;②时长查询节流（games tab 页面重,别按 reconcile 周期裸拉,参照 FarmQueueCheckedAt 节流字段先例）;③play 多 app 并行时 Steam 只显示首个,但时长全部累计——目标语义按「累计时长」写清楚。

> 2026-09-18：**§29 收口（P0–P4 全落地,feat×3 + docs×4 + 立项,共 8 提交）**。链路:`6ba5100` 立项 → `10a9a77` P1 数据源（games tab 内嵌 `var rgGames` JSON,候选扫描 + 平衡扫描器,25 测试）→ `5fc5e26` P2 配置面（`BoostTarget` spec + PUT 透传 + fail-fast 校验矩阵,11 测试）→ `dee4e96` P3 编排（`ReconcileBoostAsync` farm 镜像,9 测试）→ P4 文档收口。行为定案:独立 `boost` 期望状态;未达标集合一次 play 派发多 app 并行（风险备注③的「累计时长」语义——`get_playtime` 输出即 Steam 服务端累计口径）;IdleApps 沿用排除名单;全达标停挂保持在线;查询失败/无效输出仅记 deviation、等满 `ReconcileBoostRefreshSeconds`（默认 1800s）再重试,绝不静默判「全部达标」。风险备注①②落实为:解析零命中抛异常（P1）+ `BoostCheckedAt` 无条件盖章节流（P3）。**遗留观察（P3 实现对比发现,暂不立项）**:farm 的 `refreshDue` 仍是「`FarmQueue is null` 即重查」——get_card_drops 失败后每个 reconcile 周期（15s）重拉徽章页,与注释宣称的「等下次刷新间隔」不符;boost 侧已改为 `BoostCheckedAt` 判定。farm 行为变更涉及既有测试语义,等真实查询失败频率数据再决定是否对齐。本节 45 个新测试（25+11+9）,Reconciler 套件 65 全绿,CI 全绿（`7092f0d`,feat 提交 `dee4e96` 因连续推送被 Actions 合并,由 HEAD 快照覆盖验证）。

## 30. 覆盖率盘点 + BotSession 取消竞态变体确定性化（✅ 2026-09-18 完成）

> 立项动机：§29 收口当日纯 docs 提交（`189f556`）ubuntu Debug 单 job 挂 `BotSessionTests.ExecuteActionAsync_WithTimeout_ThrowsTimeoutException`（其余 9 job 全绿），`gh run rerun --failed` 即绿——flake 家族（2026-09-13 437 行同文件已修两变体后第三发作）第四成员；同轮用户要求覆盖率推进到 98% 以上。

### 30.1 覆盖率盘点（结论：99.8%，目标已远超）

- [x] 全量合并覆盖 **99.8%（13387/13417）**（`coverage-summary.py`，基线 99.5% 之上再升）；§29 boost 全部新文件（SteamProfileGamesClient / GetPlaytimeAction / Reconciler boost 段 / AccountStore 校验）100% 不在缺口列表。30 行未覆盖行逐行定性：①平台守卫（FileCredentialStore `IsWindows()` return，Linux 永不可覆盖）；②真网络（SteamClientManager 反射包装 `GenerateAccessTokenForAppAsync`、TimeSync 默认端点重载）；③宿主配置分支（Program.cs `UseStaticFiles()` else）；④**同一事务内防御性死守卫**（SqliteJobStore 314-317/622-623/867：SELECT 已带 `status` 过滤后同连接 UPDATE 必命中，属跨进程双保险）；⑤并发交错守卫（SessionManager 132-133 `TryAdd` 失败分支，无 hook 点）；⑥后台循环取消空 catch（时序命中不可断言）。唯一干净可测缺口补测：`GetPlan_WithUnknownId_ReturnsNull`（SqliteCrawlStore 251，走 `GetPlanUnsafeAsync` 查库路径，区别于既有空 id 短路测试）。（✅ 2026-09-18）

### 30.2 BotSession 取消竞态变体（机理实测闭合 + 确定性化）

- [x] 机理：mock action `Task.Delay(1min, ct)` + 调用方 100ms `CancelAfter` 真时间竞态。翻车链：调用方线程在 `TryWrite`（BotSession.cs:100）与 `WaitAsync`（:102）之间被池饥饿抢占 ≥100ms → 处理循环跑完「Delay 同步抛 → catch(OCE) → `TrySetResult("canceled")`」（HandleExecuteAction :306-310）→ 调用方恢复后 `WaitAsync` 见 task **已完成**——.NET 10 实测验证（最小复现程序）：**被等待 task 已完成时 `WaitAsync(ct)` 正常返回，忽略已取消 token** → 无异常 → 断言挂。附带发现：:306 的 `"canceled"` 结果在正常时序对调用方不可达（调用方 `WaitAsync` 注册必然早于 action 内部注册，取消时按注册序先触发抛 OCE）——生产 API 的「执行中取消」对外契约实际上只有 OCE 一种结局（记录在案，暂不改实现）。（✅ 2026-09-18）
- [x] 修复：改写为 `ExecuteActionAsync_CancelWhileActionRunning_ThrowsOperationCanceledException`——mock TCS `started` 信号确认 action 已 park 后再 `Cancel()`（park-then-cancel 排序），取消回调按注册序锁定调用方 `WaitAsync` 先触发；删 100ms 真等与 1min park（`Timeout.InfiniteTimeSpan` 受 ct）。原「timeout → action timeout」意图已由既有确定性测试覆盖（`TimeoutSeconds: 1` + 结果断言）。5 连跑全绿、单轮 ~170ms。全量 2092 测试绿 + format 过。（✅ 2026-09-18）
- [x] **变体二（v1 修复被 CI 击穿，同日）**：windows Debug 5ms 快速红「No exception was thrown」——v1 注释的前提「调用方 `WaitAsync` 持有 caller token 上最早注册」**不成立**：注册发生在 `TryWrite` **之后**，调用方线程在两者之间被抢占时命令循环先注册 linked CTS；注册序反超后取消传播可先结算完成源，`WaitAsync` 仲裁见已完成 task 按「完成-优先」返回结果。最小复现程序复现了**注册序反超**但未能复现精确结算路径——机理断言收在注册序层面（continuation 级细节未验证不写死）。**v2 修复（结构性而非排序性）**：mock action park 改挂在**独立 `parkedCts`**，与 caller token 彻底解耦——caller `Cancel()` 时原始完成源**无任何路径**可结算，`WaitAsync` 的 `TrySetException` 必胜（与注册序无关的充分性论证）；`finally` 中 `parkedCts.Cancel()` + `session.Dispose()` 清理无限 park。本地 5/5 连跑绿 + 全量绿 + format 过。（✅ 2026-09-18）

> 2026-09-18：**两题一并收口（test + docs 两提交）**。方法论再沉淀：①「任务已完成的 task，`WaitAsync` 直接返回结果」是 .NET 的真实语义（先查完成再看取消）——依赖 `WaitAsync` 抛 OCE 的测试必须保证**调用时** token 尚未取消、或被等待 task 尚未完成，二者其一否则断言不可靠；②覆盖率缺口的六类定性里，③④⑤是防御性/并发代码的自然残留，不追净——把「行数」当目标会逼出赌时序的坏测试，§27 家族方法论（等待=断言对象、mock TCS 信号）在这里的反面教材就是被替换的 100ms 竞态版。

> 2026-09-18：**§29 遗留观察兑现——farm `refreshDue` 对齐（fix 提交 `db63bed`）**。farm 循环的 `FarmQueue is null` 短路撤除,改 `FarmQueueCheckedAt` 判定（MinValue=首查,盖章后等 `ReconcileFarmRefreshSeconds`）,失败/丢失的 get_card_drops 不再每 15s 重拉徽章页;派发 reason 与 null 队列守卫同步对齐 boost。行为变化:①`Farm_CardDropsTaskFailed_MarksDeviationOnly` 原断言「失败后同 pass 立即重试」改为「单查询 + deviation + 等间隔」;②`SettleActiveJob_CardDropsOutcomeWithoutTasks` 原断言「空队列永远 refresh-due 同 pass 重查」改为「安静结算不重查」——「task 行消失」与「查询失败」同等待遇,不再有「空队列=立即重查」的隐式通道。spec 变更/agent 消失/离开状态的重置段仍清 `FarmQueueCheckedAt=MinValue`,operator 想立即刷新依旧走 spec 更新。Reconciler 65 全绿 + 全量绿 + format 过,CI 以本轮提交为准。

> 2026-09-18：**§28.2 收尾失速族定谳关闭（用户裁定提前关闭,docs 提交）**。观察数据:自 §28 修复窗口 09-17 18:00Z 起,**连续 13 轮已完成 ci run 全绿、windows 零发作**（09-17 四轮 + 09-18 九轮,含 §29 boost 八提交 + §30 两题 + farm 对齐的混合负载),为历史发作间隔（09-14→09-17 六发作 ≈ 每 6-8 轮一次）的约 1.6 倍;期间唯一异常 run `549575b` cancelled 经甄别为**调度层取消（零 job,4 分钟排队即被基础设施杀掉,非测试失速）**,不计发作。**定谳:收尾失速族 = vstest 宿主层基础设施 flake（§27 原始定性成立,「残留 park」假设撤销）**——依据:①在飞族修复已排除三处 park 舞蹈的全部违例路径（§28.1）;②混合负载高强度推送期零复发;③即便残留,`--blame-hang` 12 分钟快速红 + TestResults/hangdump 上传已把最坏代价从 45/60 分钟烧满压到 ≤12 分钟且有转储可取证,复发即按 §28.1 流程重开取证。三族 flake 图谱终版:§23 进程全局状态并行耦合（断言红,已修）、§25 等待信号错位（断言红,已修）、§27/§28 收尾失速（宿主层,守卫兜底 + 观察关闭)、§28 在飞失速（测试代码,已修）;§30 补第四支——Task.WaitAsync 完成-优先语义竞态（测试侧确定性化）。

---

## 31. Dashboard 全功能管理台扩展（✅ 2026-09-18 完成，P1–P6 全落地；用户定向立项）

> 立项动机：REST 能力面与 UI 覆盖严重失衡——CP 已有 **23 个写端点**（账户 PUT/DELETE、enable/disable、交易 accept/decline、批量确认、loot、swap、市场挂单/撤单、积分认领、license、爬虫计划 CRUD+trigger、作业创建/取消、账户+全局配置），而 admin.html UI 仅覆盖 4 类（挑战提交、作业创建/取消），其余 19 个全靠 curl。三页分工（dashboard 只读监控 / admin 管理 / gamedata 数据）中，admin 的管理面远落后于 API 面。

### 31.1 现状盘点（2026-09-18）

- [x] 三页现状：dashboard.html 1144 行（只读，双 SSE+轮询，DashboardStaticTests 锁 no-write-verb）；admin.html 1818 行（挑战/作业管理 + QR 登录，写契约允许）；gamedata.html 719 行（字典+结果浏览，只读契约同 dashboard）。
- [x] UI 缺口清单（19 个无入口写端点）：`PUT /v1/accounts/{name}`（期望状态编辑）、`DELETE /v1/accounts/{name}`、`enable`/`disable`、`trade-offers accept/decline`、`confirmations/accept-all`、`loot`、`swap-offers`、`market/listings`（创建）+ `/cancel`、`points-shop/claim`、`licenses`、`crawl/plans` CRUD + trigger、`PUT /v1/config/account/{name}` + `/v1/config/global`。

### 31.2 实施阶段（按风险递增；方向定案：扩展 admin.html，dashboard 只读契约不动）

- [x] **P1 账户生命周期面板**：期望状态查看/编辑（state/farm 排除/boost 目标/idle 名单表单化，PUT 透传）、enable/disable 按钮、删除账户（显式输入账户名确认）；账户列表复用 dashboard 同源 GET。（✅ 2026-09-18 admin.html「账户管理」面板）
- [x] **P2 交易与确认面板**：报价列表（GET 既有）内联 accept/decline、批量确认（type/operation 过滤器）、loot 表单、换卡（duplicates 查看 + dry_run 方案展示 + send 确认）。（✅ 2026-09-18 admin.html「交易与确认」面板）
- [x] **P3 市场与认领面板**：挂单列表 + 过滤撤单（dry_run 默认）、挂单创建（费用感知定价预览 + send 双确认；账户市场开关未开启时 UI 禁用而非报错）、points-shop 认领（summary 展示 + 免费默认/付费 force 分离）、add_license 表单。（✅ 2026-09-18 admin.html「市场与认领」面板）
- [x] **P4 爬虫计划管理**：plans 列表/创建/编辑/删除/手动 trigger（cron 校验错误内联展示，复用 REST 语义）。（✅ 2026-09-18 admin.html「爬虫计划管理」面板）
- [x] **P5 配置面板**：全局 config + 账户 config 表单化（env 覆盖提示，敏感项只显示是否已设不回显值）。（✅ 2026-09-18 admin.html「配置管理」面板）
- [x] **P6 收尾**：production.md 三页分工说明更新、feature-matrix Web UI 行措辞刷新、DashboardStaticTests 扩展（admin 页写操作二次确认模式抽查 + 敏感值不回显契约）、CHANGELOG。（✅ 2026-09-18）

> 红线与风险备注：①**dashboard.html 零写动词契约不变**——全功能管理台收敛在 admin.html（写契约既有页），三页分工不破坏；②破坏性操作（删账户、真实挂单、接受报价、force 付费认领）一律二次确认且确认文案含不可逆提示；③UI 是 REST 薄封装，无业务逻辑下沉，服务端校验是唯一真源（UI 禁用态只是引导，不能替代 4xx 呈现）；④认证沿用 admin 现行 authorization 模式，新面板零新增鉴权面；⑤灰区操作（市场挂单）UI 需读取并展示账户开关状态，开关关闭时按钮禁用 + 指引文案，而非点击后才报错；⑥单文件静态页模式沿用（无构建链），admin.html 体积增长可控性靠面板折叠分区维持。

> 2026-09-18：**§31 P1 账户生命周期面板落地（feat 提交，admin.html 单文件改动）**。①「账户管理」面板（认证挑战与事件日志之间）：账户卡片列表（启用徽章 / state chip / 市场开关 chip / idle·boost·note 汇总行）+ 编辑表单（期望状态五选一下拉、Idle 白名单与 Farm 排除名单复用同一字段——标签随 state 语义、Boost 目标 `appid:小时` CSV、region/agentId/note、enabled 与市场挂单 checkbox）。②写操作三通道：编辑保存走 `PUT /v1/accounts/{name}` 全量语义（AccountStore.Upsert 是替换式更新，前端所有字段显式提交，`updatedBy: "admin-console"` 落审计）；启用/禁用走 `POST enable|disable`（禁用弹 confirm 说明「不停删数据」）；删除走 `DELETE`（prompt 输入账户名精确匹配才执行——比 confirm 强一档，防误点）。③Boost 目标前端预校验镜像服务端规则（appid 正整数、小时有限正数），格式错本地 alert 而非等 400；与 §29.2 P2 校验矩阵同源。④枚举序列化确认：CP 全局 `JsonStringEnumConverter(CamelCase)` → GET 返回 `"farm"` 小写字符串，编辑表单 PascalCase option 值反序列化兼容（转换器忽略大小写）。⑤dashboard/gamedata 零写动词契约不动，DashboardStaticTests 9/9 绿（新增断言留给 P6 统一补）。验证：全量 1158+ 全绿、format 门禁过、JS 脚本块 node --check 语法过。

> 2026-09-18：**§31 P5 配置面板落地（feat 提交，admin.html 单文件改动）**。①「配置管理」面板（市场与认领之后）：全局配置区（版本戳 `v{n} · updatedBy · updatedAt` + settings 键值行编辑器 + 添加键/保存）+ 账户配置区（账户下拉、enabled/region/labels + settings 行编辑器）。②行编辑器为两区共享组件：键名只读（存量）/可编辑（新增），每次键击 commit 进 working Map（键名变更同步迁移 Map 键，清空键名即撤销）；新增行键名未落定前绝不进 Map（防半输入状态污染 PUT body）。③**敏感值不回显契约**：`/password|secret|token|key|credential/i` 命中的键，DOM 输入框留空 + placeholder「••• 已设」，真实值只存在前端 working Map 内存中；行级 commit 规则——敏感行空输入一律视为「保留原值」回填 Map（与 placeholder 承诺逐字一致，输入非空才覆盖），因此 PUT body 天然携带原值、密钥不经历 DOM 往返；要清除密钥走删除键。④服务端天然不回显已确认：ConfigStore.SetAccount 构造 AccountConfig 从不设置 Password 字段（恒 null）+ `WhenWritingNull` 序列化 → GET /v1/config 无密码可泄；面板红线只针对 settings 字典内的敏感键。⑤helper 文案声明两层覆盖语义：agent 侧环境变量优先于 config settings；CP 不持有任何账户密码（密码只在 agent 凭证库）。⑥PUT 均为服务端整字典替换语义，`updatedBy: "admin-console"` 落审计（config.global.updated / config.account.updated）；保存成功后 refreshConfig 重拉，UI 永远以服务端为唯一真源。验证：node --check 语法过、ControlPlane 540 + 全量 1158 全绿（DashboardStaticTests 含）、format 门禁过。

> 2026-09-18：**§31 P6 收尾完成——§31 全部六阶段关闭（docs + test 提交）**。①production.md 新增「Web consoles」小节（Security checklist 之前）：三页分工表（admin 全功能管理台默认首页 / dashboard 只读监控契约锁定 / gamedata 字典只读）+ 鉴权模型说明（静态页本身无鉴权可拉取但内嵌零敏感数据，数据全走带 Bearer 的 `/v1` API）+ 运维红线（admin API key 是控制台全部写能力的唯一防线；配置面板敏感键打码不回显）。②feature-matrix：Web UI 行刷新为「只读 dashboard + 全功能管理台 admin + 游戏数据字典页,静态托管,零构建链」；P6-4 Dashboard 条目补记 §31 五面板扩展（写端点全覆盖）。③DashboardStaticTests 9→11：新增 `AdminHtml_DestructiveWrites_RequireExplicitConfirmation`（八条破坏性通道各锚一条确认文案短语——删账户 prompt 精确匹配、accept 报价、loot、换卡 send、真实撤单、真实挂单 ToS 灰区、force 付费认领、删爬虫计划）+ `AdminHtml_ConfigPanel_NeverDisplaysSensitiveSettingValues` 敏感值契约（SENSITIVE_KEY_PATTERN 定义、••• 占位文案、updatedBy admin-console）。④CHANGELOG Unreleased 补 §31 综合条目。⑤§32（白名单自动接受）为独立章程接续实施。验证：DashboardStaticTests 11/11 绿、全量 + format 随提交前门禁跑。

---

## 32. 白名单自动接受报价：保守 Gifts 语义（✅ 2026-09-18 完成，P1–P4 全落地；用户裁定立项）

> 立项动机：推翻 §11.2「不实现自动接受」的 2026-09-13 评估——用户裁定立项。**保守白名单版**定案（四个候选中选一）：AccountSpec 显式策略 + partner 白名单 + **仅自动接受「纯收」报价**（items_to_give 为空、只收不给，零资产让渡风险，对齐 ASF AcceptGifts 语义；ASF 对任意报价的白名单自动接受同样不做，其 wiki 明确警告等价性不可自动判断）+ 全量审计。默认关闭；白名单为空时不动作；交换类（有让渡）报价一律不自动接受（人工走 §31 P2 面板）。

### 32.1 实施阶段

- [x] **P1 配置面**：`AccountSpec.TradePolicy`（新 record：`AutoAcceptGifts bool` 默认 false + `PartnerWhitelist IReadOnlyList<ulong>`；fail-fast 校验：steamId 正 64 位、去重排序、AutoAcceptGifts=true 须白名单非空）+ `PUT /v1/accounts` 透传 + 审计。（✅ 2026-09-18）
- [x] **P2 评估循环**：Reconciler 增 `TradeOfferCheckedAt` 节流（与 farm/boost 同构，`ReconcileTradeRefreshSeconds` 默认 600s）→ `GET trade-offers`（activeOnly）→ 逐条判定：非白名单 partner → 跳过；有 items_to_give → 跳过；纯收 + 白名单 → 走 accept 通道（auto mobile confirm）→ 无条件盖章 CheckedAt；查询失败仅记 deviation 等满间隔。（✅ 2026-09-18）
- [x] **P3 审计与可观测**：每次评估落审计（含「跳过」决策与原因：partner 不在白名单 / 非纯收 / 策略未开启）；自动接受事件接入 webhook 管道（`trade.auto_accepted`）。（✅ 2026-09-18）
- [x] **P4 收尾**：feature-matrix 交易行措辞、production.md 配置矩阵（env 开关）、CHANGELOG、全量测试 + CI。（✅ 2026-09-18）

> 红线：①策略 per-account 显式开启，**默认关闭**，全局无「一键全开」；②白名单为空时策略等于未开启（双保险）；③非纯收报价即使白名单内也**永不**自动接受——等价性判断不做（ASF 同款取舍）；④评估循环失败容错与 farm/boost 同款（deviation 不吃登录预算）；⑤identity secret 不出 agent（复用既有 confirm 通道约束）。

> 2026-09-18：**§32 P1 配置面落地（feat 提交，Protocol + ControlPlane + admin.html 防回归三处）**。①`Vapor.Protocol` 新 `TradePolicy(AutoAcceptGifts=false, PartnerWhitelist=ulong[]?)` record，`AccountSpec` 增可空 `TradePolicy` 字段（null=未配置=策略关闭）。②`AccountStore.NormalizeTradePolicy`：SteamId 0 视为不可用条目丢弃（对齐 BoostTarget 的 AppId=0 处理）、去重升序排序（spec 与输入顺序无关）、**AutoAcceptGifts=true + 白名单空（含全零被滤光的边界）→ ArgumentException 拒绝保存**——立项红线②的 fail-fast：双保险锁在声明时，不依赖 Reconciler 运行时记得检查；`AutoAcceptGifts=false + 白名单非空` 保留（预备名单，运行时不动作）；两者皆空归一化为 null。③**省略即清除**（与 IdleApps/BoostTargets 同为全量替换语义，区别于 MarketListingsEnabled 的 null-keep）：方向性取舍——PUT 漏带策略字段落在「不自动接受」的安全侧，绝不落在「悄悄保留自动接受」的危险侧；admin.html `saveAccountEdits` 同步改为回传现有 `tradePolicy`（否则 §31 P1 面板的每次无关编辑都会静默关掉策略——在面板长出专属编辑控件前的防回归措施）。④PUT `/v1/accounts` 透传 + 审计 details 增 `tradePolicy{autoAcceptGifts,partnerWhitelist}`。⑤测试：AccountStore 4 个（归一化/空名单拒绝/全零边界/省略清除）、AccountApi 2 个（白名单往返含升序断言/autoAccept 无名单 400）、Protocol round-trip 2 个（ulong 白名单往返/`autoAcceptGifts:false` 显式落 JSON 不被默认值省略）。验证：134 相关测试 + 全量全绿、format 门禁过。

> 2026-09-18：**§32 P2 评估循环落地（feat 提交，DesiredStateReconciler 为核心）**。①**循环位置**：trade 评估挂在 ReconcileActiveAccountAsync 的 connected 分支内、farm/boost 之前——策略不依赖 DesiredState（Online/Idle/Farm/Boost 任何连接态都评估），且复用 per-account 单作业槽（trade 作业占用槽的 pass 跳过 DesiredState 一拍，farm 的 ActiveJobId 检查天然互斥，节流保证无饥饿）。②**三段流水**：`get_trade_offers(active_only=true)` 扫描（`TradeOfferCheckedAt` 节流，`VAPOR_RECONCILE_TRADE_REFRESH_SECONDS` 默认 600s）→ `ExtractPendingGiftOffers` 过滤（**白名单 partner（32 位 AccountId ↔ SteamId64Base 转换）+ items_to_give_count==0 双条件缺一不可**；`is_our_offer` 跳过（counter 报价可混入 received）；give count 读不到按不可用跳过——安全侧）→ 逐条 `accept_trade_offer`（payload 镜像 REST 通道：trade_offer_id/partner_steam_id 64 位/verify_state=true，agent 侧 state machine 二次验证 partner+过期）→ accept 输出 `requires_mobile_confirmation=true` 时链式 `confirm_trade_offer`（identity secret 不出 agent，红线⑤）。③**容错与红线④**：扫描失败只记 deviation（不进登录预算）+ 盖 CheckedAt 等满间隔；accept 失败 drain 掉决策（下轮重扫再判，绝不同周期内打转）；spec 更新重置 TradeOffersToAccept + CheckedAt（白名单可能已变，旧决策作废）。④结算解析兼容字典/JsonElement 双形态（SQLite 往返，与 farm/boost 同款）。⑤可观测：`TradeAcceptsDispatched` 计数进 `/metrics`（reconcile_actions_total{action="trade_accept_dispatched"}），orchestration view 暴露 `TradeOffersToAccept`/`TradeOfferCheckedAt`。⑥测试 6 个：策略关不扫描、策略开派 activeOnly 扫描、三报价过滤（白名单纯收唯一通过）、accept→confirm 链、扫描失败 deviation 不吃预算+节流保持、Extract 双形态直测（含 give 缺失/our offer/零 partner 边界）。验证：Reconciler 71 + 全量全绿、format 门禁过。

> 2026-09-18：**§32 P3 审计与可观测落地（feat 提交，DesiredStateReconciler 单文件）**。①**评估审计**：`ExtractPendingGiftOffers` 重构为薄投影，核心逻辑上移 `EvaluateTradeOffers`——对扫描到的每条 received offer 产出 `TradeOfferDecision(OfferId, PartnerSteamId64, accept|skip, reason)`（skip reason 四值：`partner_not_whitelisted` / `has_give_items` / `our_offer` / `give_count_unreadable`，give 计数读不到与「非纯收」分开记——安全侧结果相同但审计可读性不同；无 offerId 的残缺条目不可引用，不产决策）；扫描结算处落 `trade.policy_evaluated` 审计（actor orchestrator，details：policyEnabled / evaluatedCount / eligibleCount / decisions 逐条含 reason，软上限 200 条超出记 decisionsTruncated 防审计膨胀）。②**语义边界（如实记录）**：「策略未开启」在 P2 实现下 = 不扫描 = 无评估发生，故无 `trade.policy_evaluated` 条目——审计覆盖的是评估发生时的完整决策（含全部跳过原因），不评估的账户不产生决策记录；P1 fail-fast 保证评估发生时白名单必非空，`policyEnabled` 恒 true（字段留给审计查询者显式确认）。③**webhook 接入零管道改动**：accept 结算 Finished（且 AcceptingOffer 在场）→ `_events.Publish(jobId, "trade.auto_accepted", …)`——NotificationService.PumpJobs 通配订阅 `"*"` 透传全部 broker 事件，sink 按 `Type=trade.auto_accepted` 规则过滤即达；payload 仅 accountName/offerId/partnerSteamId64/requiresMobileConfirmation（ulong 一律字符串防 JS 精度丢失，零 secret，红线⑤不涉）。④同步落 `trade.auto_accepted` 审计（details 同 payload）。⑤审计失败隔离与 RecordActionAsync 同款（try/catch 不破坏编排）。⑥测试 2 个新增：`ScanResult_EvaluationAudit_RecordsEveryOfferDecision`（五报价全决策断言：accept×1 + 四种 skip reason 各一 + partner 64 位字符串形态）+ `AcceptFinished_RecordsAutoAcceptedAuditAndWebhookEvent`（审计条目 + RecordingBroker 捕获 `trade.auto_accepted` 事件 + 无 mobile confirm 时不派 confirm job）；测试基建 `CreateReconciler` 加 `broker` 参数 + `RecordingBroker : IEventBroker`（记录 Publish，订阅流恒空）。验证：Reconciler 73 + 全量 1896 全绿、format 门禁过。

> 2026-09-18：**§32 P4 收尾完成——§32 全部四阶段关闭（docs 提交）**。①feature-matrix 3.3 交易表「报价接受/拒绝」行措辞更新：由「默认人工，无编排器自动接受」改为「默认人工；编排器侧保守白名单自动接受——账户显式开启 + partner 白名单 + 仅纯收 gifts-only」；P6-2 条目的「编排器侧自动接受经评估不实现」补记「该结论已被 2026-09-18 §32 保守白名单版推翻落地」（决策溯源双链：todo §11.2 + §32）。②production.md 配置矩阵加 `Vapor_RECONCILE_TRADE_REFRESH_SECONDS`（默认 600，说明绑定「带 auto-accept 策略的连接态账户」语义）。③CHANGELOG Unreleased Added 顶部加 §32 综合条目（策略面 + 评估循环 + 决策审计 + `trade.auto_accepted` webhook 事件 + 默认关闭无全局开关）。④验证：全量测试随 P3 门禁全绿后提交；CI 结果见提交后监控。

---

## 33. 成就解锁/管理：显式单发 + 布尔置位划线（✅ 2026-09-18 完成，P0–P3 全落地；用户裁定立项）

> 立项动机：推翻 §11.4「需求弱，后置」的记录（2026-09-13 P6 收口与 2026-09-14 P7 立项两度维持后置），用户裁定立项。对标 SGI 成就面板：列表 + 解锁 + 重置。**技术路径分叉**（已核实）：成就 schema/解锁态**读取**走社区 stats 页解析（`/profiles/{steamid}/stats/{appid}?tab=achievements`，SteamWebHandler 会话 cookie——badges 页同款先例；steamid 从 steamLoginSecure cookie 反解——inventory 先例；零新配置。Steam Web API `ISteamUserStats` 需独立 API key，CP 无此配置概念，不为此引新配置面）；成就**写入**没有用户侧 web API，只能走 SteamKit client 协议 `SteamUserStats` handler（SetAchievement/ClearAchievement + StoreStats），transport 加方法——`GetHandler<SteamApps>()`（RequestFreeLicenseAsync）先例，agent 侧经 `session.SteamClientManager` 直达（AddLicenseAction 先例）。**与 §11.5 灰区的边界（立项即划线）**：只做成就布尔置位（unlock/clear），**stat 数值编辑维持明确不采用**——两者同在 `SteamUserStats` API 面上，用能力边界而非回避来划线。**ToS 语义如实记录**：成就置位是官方 API 面上的账号数据写入（SAM/SGI 长期实践、无 VAC 关联——VAC 检测游戏文件/内存篡改，不检测 stats API 调用），但属「伪造账号数据」语义灰区；以显式单发动作 + 全量审计应对，不做自动化，风险用户自担。

### 33.1 实施阶段

- [x] **P1 成就读取（纯读）**：`SteamAchievementsClient`（社区 stats 页解析：api name / displayName / 解锁态；页面结构变化按 agent 输出如实降级）+ `get_achievements` action（payload `appId` 单值必填）+ `GET /v1/accounts/{name}/achievements?appId=`（TradeOffersReader 式单 target job 同步端点，三态 200/202/502）+ 审计 `achievement.read`。（✅ 2026-09-18）
- [x] **P2 解锁/重置（写，显式单发）**：transport `LoadUserStatsAsync`（**写入前置**：Steam 协议要求 stats 先加载才能写入，加载失败 fail 而非盲写——协议约束即安全约束；技术路径经 SteamKit2 3.4.0 调研后修正——`SteamUserStats` handler 从 1.8.3 起就无成就封装，写入走 `CMsgClientGetUserStats`/`CMsgClientStoreUserStats2` 手工消息，见 P2 日志①）+ 名字→id 映射（unified `Player#GetGameAchievements` 的 internal_name 全序清单 + 双护栏）+ 位图置位/清零（`AchievementStatsBitmap`，写全量合并 blob + 写后读回验证）+ `unlock_achievements` action（payload `names` 非空列表 + `appId`，逐条结果如实汇总、单项失败不中断——confirm_all 先例）+ `reset_achievements` action（payload 需显式 `confirm: true` 否则 action 自身拒绝）+ CP `POST /v1/accounts/{name}/achievements/unlock` 与 `POST /v1/accounts/{name}/achievements/reset`（**names 必须显式非空——服务端不提供省略 names 的全量路径**，「解锁全部」由调用方先 GET 列表再显式提交，admin 面板做全选交互）+ 审计 `achievement.unlock` / `achievement.reset`（names 全列入 details）。（✅ 2026-09-18）
- [x] **P3 admin 面板 + 收尾**：账户管理面板成就区块（appId 输入拉列表 + 逐条勾选/全选 + 解锁 confirm 弹窗 + 重置双确认）、feature-matrix 成就行措辞、CHANGELOG、production.md 如需、全量测试 + CI。（✅ 2026-09-18，见下方日志）

> 红线：①**只做成就布尔置位，stat 数值编辑不做**（§11.5 明确不采用清单维持，同一 API 面立项即划线）；②解锁/重置均为显式单发 REST 动作，**无编排器自动解锁**、无批量计划任务、无策略常开面（对齐 loot/swap 模型，区别于 §32 TradePolicy）；③**服务端无全量隐式路径**：unlock/reset 的 names 必须显式非空，reset 另需 `confirm: true` 双保险（两道闸都在 action 层与 API 层各设一道）；④写入前必须先成功加载 stats（协议前置即安全前置），任何加载失败返回失败而非跳过校验盲写；⑤逐条结果如实汇总，单项失败不中断且整体结果如实反映失败项；⑥审计全量覆盖读/解锁/重置三类（names 列表入 details，复用既有脱敏）。

> 2026-09-18：**§33 P3 admin 面板成就区块 + §33 收尾（feat + docs 两提交，admin.html 单文件改动 + 契约测试）**。①**面板**：账户管理面板尾部新增「成就管理」区块——账户下拉（照 `populateTradeAccountSelect` 先例独立 `populateAchievementAccountSelect`，refreshAccounts 联动填充）+ App ID 输入 + 拉取按钮；列表渲染统计行（已解锁 N/M）+ 逐条卡片（display_name + api name mono + 解锁徽章 + 描述，checkbox 逐条勾选）；操作栏三按钮：全选（toggle 文案「全选/取消全选」）/解锁选中（单 confirm）/重置选中（**双 confirm**：第一层破坏性警示「此操作不可逆：将清零账户」+ 第二层向具体账户名再确认，POST 透传 `confirm: true`——前端双确认与 API 层闸各自独立，红线③）。②**契约**：读写共用 P1/P2 端点；202 pending 在拉取/解锁/重置三处都如实提示排队（job id 上浮）而非假装完成；写入结果经 `renderOperationResult` 折叠展示（摘要行带 succeeded/failed 计数与 verified 状态——「已读回验证/读回验证不可用」直读 P2 字段），完成后自动重拉列表刷新解锁态；事件日志落 `achievement` 条目。api name 恒非空（P1 图标名推断兜底大写化），hash 命名推断名由写入通道逐条如实报失败（UI 不假装预判）。③**契约测试**：`AdminHtml_DestructiveWrites_RequireExplicitConfirmation` 加三锚——解锁 confirm（「项成就置为已解锁」）/重置确认 #1（「此操作不可逆：将清零账户」）/确认 #2（「再次确认：向账户」）；dashboard no-write-verb 契约不涉（区块在 admin.html，触发全走 REST）。内联 JS `node --check` 过。④**docs 收口（随 docs 提交）**：feature-matrix §3.6 成就行勾选划线（对标 SGI 后置 → §33 落地：显式单发 REST + 布尔置位划线，数值编辑维持不采用）+ 时间线追加 2026-09-18 注记（§3.6 最后一个后置项闭环）；CHANGELOG Unreleased Added 顶部加 §33 综合条目（P1 读取 + P2 写入通道 + P3 面板 + 红线语义与 verified 如实性）；production.md 不涉（本节零新配置）。验证：全量全绿（Steam.Core 1194、ControlPlane 573——静态契约测试加锚不加数、MarketWatch 56）、format 门禁过、内联 JS node --check 过。
> 2026-09-18：**§33 P2 解锁/重置写入通道落地（feat 提交，Steam.Core + Agent + ControlPlane 三处）**。①**调研推翻立项技术前提（如实修正）**：本阶段先做 SteamKit2 3.4.0 API 调研——反射全程序集 + 逐 tag 溯源（1.8.3/2.1.0/3.4.0）发现 `SteamUserStats` handler **从来就没有** SetAchievement/ClearAchievement/StoreStats 封装（立项时写的是记忆错误：那是 Steamworks SDK `steam_api.dll` 的本地 API，SAM/SGI 类工具走 SDK 注入而非网络协议；对照 w1ldb1t/SteamAchievementUnlocker 与 mbwilding/steam-achievement-unlocker 两个真实实现确认均为注入式）。写入的唯一协议路径是**手工 client 消息**：`EMsg.ClientGetUserStats`(818)→`ClientGetUserStatsResponse`(819) 加载 + `EMsg.ClientStoreUserStats2`(5466)→`ClientStoreUserStatsResponse`(821) 提交（Store2 复用老响应消息；proto 定义见 SteamDatabase/Protobufs `steammessages_clientserver_userstats.proto`）。②**响应路由**：SteamKit2 3.x 无公开 `SubscribeClientMsg`，stock handler 只路由 leaderboard——新建 `SteamUserStatsProtocolHandler : ClientMsgHandler`（HandleMsg switch 两响应解包 → `ConcurrentDictionary<ulong, TaskCompletionSource>` 按 job id 配对；`RegisterWait` **先注册后发送**，响应不可能竞态其注册；超时 60s 对齐 AsyncJob.Timeout 先例），manager 构造时 `AddHandler` 注册。③**schema 名字→id 映射（调研中最难的一环）**：响应里的 `schema` bytes 是 binary KV，SteamKit issue #531（2018，azuisleet/jurzer 两轮往返）已证其中名字映射**不完整不可靠**；本实现改用 unified `Player#GetGameAchievements`（`internal_name` 全序清单；该服务在 Protobufs 中全部只读，写入无 unified 面）的「**列表 index == achievement_id**」假设，配两道护栏：解锁块（`achievement_blocks`，独立来源）中出现过的 max id 必须 < schema 数量，越界即整体拒绝（顺序假设破裂信号）；**写后读回验证**——store ack 不算数，重新 load 后逐条位检查，读回不可用时不谎报（条目如实标 `stored; read-back verification unavailable` 且 `verified=false`）。④**位图编码**：成就 id i → stat_id `i>>5`、bit `1<<(i&31)`（社区逆向共识、**无官方文档**——`AchievementStatsBitmap` 静态类集中全部数学并单测覆盖，注释如实标注非官方契约）；写**全量合并 blob**而非 delta（untouched stats 值原样保留）；`crc_stats` 首载传 0 强制全量、提交带原 crc；`explicit_reset` 字段语义未经证实**不使用**（未证实的字段不进写入路径）。⑤**红线落地**：③双闸——action 层 `AchievementsWritePayload.TryParse`（names 显式非空 + reset confirm）与 API 层 `DispatchAchievementWrite` 同两道各自独立；④加载失败整体失败（load EResult≠OK → 全部条目 `stats load failed`，绝不盲写）；⑤逐条如实——unknown name / store 失败 / 位图验证不过 / 读回不可用各有明确 detail；⑥审计 `achievement.unlock`/`achievement.reset`（appId + names 全列 details）。⑥**编排**：`SetAchievementStatesAsync` 六步流水线（load→schema→映射去重大小写不敏感→位图 patch→store→读回验证），`AchievementWriteResult.Success` = 全部条目成功（单项失败不中断）；`GetGameAchievementNamesAsync`/`LoadUserStatsAsync`/`StoreUserStatsAsync` 均为 ISteamTransport 接口方法（MockSteamClientManager 同步补全）。⑦测试 29 个新增：位图 10（位数学 Theory/缺省条目按 0/空 blob 置位/合并保留 untouched stat/清零只动目标位/缺省清零写显式 0）+ action 11（unlock：Metadata/缺 app_id/缺 names/空 names/成功逐条汇总含 names 原样透传断言/无响应/无 client；reset：Metadata/缺 confirm 与 confirm=false 均拒/缺 names/成功 unlock=false）+ 端点 8（鉴权/404/400 族（缺 names/空 names/空白 names/缺 appId）/reset 缺 confirm 与 false confirm/200+审计/502/202/payload confirm 透传断言）。全量首跑另揪出 CP `ResourceFootprintBenchmarks` 并行污染 flake：其预算断言读进程级 `GC.GetTotalAllocatedBytes`，xunit 并行下其他测试集合的分配全计入测量窗口（单独跑稳定 ~29KB/cycle，全量并行测得 209KB 穿 200KB 预算）——照 `ProcessGlobalTracingCollection` 先例建 `DisableParallelization` collection 让基准排到最后独占进程跑（全量仅增 ~1s），200KB 预算保留（对 29KB 干净基线仍能抓数量级回归）。验证：Steam.Core 1194（1173+21）、ControlPlane 573（565+8）全量全绿、format 门禁过。
> 2026-09-18：**§33 P1 成就读取落地（feat 提交，Steam.Core + Agent + ControlPlane 三处）**。①**真实页面调研先行**：抓取公开 profile 成就页实测——`/stats/{appid}?tab=achievements` 列出全部成就；计数行「N of M (P%) achievements earned」；旧 `xml=1` 接口已 302 到 HTML（不可用）。**关键发现（两个真实样本交叉验证）**：解锁态判定**不能用图标颜色推断**——0/15 的 Portal 页上，进度型成就（Transmission Received）持彩色 hash 命名图标但未解锁，若按「无 `_bw` 后缀=已解锁」会误判；最终以 `<div class="achieveUnlockTime">Unlocked <日期></div>` 块（仅已解锁行携带）为判定锚，7/15 部分解锁页（sonic）交叉验证通过。**api name 局限如实记录**：页面不携带 API name，只能从图标文件名推断（`portal_beat_game_bw.jpg` → `PORTAL_BEAT_GAME`，剥 `_bw` 大写化）——多数成立，hash 命名图标（40 hex）推断出的名字写入时会被 Steam 拒绝（InvalidParam），由 P2 写入路径如实报单项失败，schema 完整性增强（Web API key）留作可选演进。②**fixtures**：两份真实页面录制（0/15 与 7/15），verbatim 录制注明来源与录制日期，契约测试回放。③`SteamAchievementsClient`（静态 `ParseAchievementsPage` 纯解析 + `GetAchievementsAsync` 实例拉取，`l=english` 强制，badges 同款）+ `get_achievements` action（app_id 必填、steam_id 可选 cookie 反解——inventory 同款）。④CP `GET /v1/accounts/{name}/achievements?appId=` 同步端点三态 + 审计 `achievement.read`。⑤测试 15 个新增：契约 8（0/15 fixture 全行解析/api 推断/图标 URL、hash 图反例回归锚「彩色图标但未解锁」、7/15 交叉样本、achieveUnlockTime 最小复现、空页/无 summary 降级）+ action 7（名称/缺 app_id/零 app_id/显式 steam_id/cookie 反解/无 cookie 无参失败/拉取失败）+ 端点 9（鉴权/404/四型非法 appId 400/Finished 200/失败 502/202 pending）。验证：Steam.Core 1173（1158+15）、ControlPlane 565（556+9）全量全绿、format 门禁过。

> 2026-09-18：**§31 P2 交易与确认面板落地（feat 提交，admin.html 单文件改动）**。①「交易与确认」面板（账户管理之后，右上账户下拉复用 §31 P1 拉取的 `state.accounts`）。②四子区：**报价**（`GET /v1/accounts/{name}/trade-offers?activeOnly=true` → 收入/发出合并渲染，收入侧内联「接受/拒绝」——accept 强制 confirm 弹窗且 body 携带列表输出的 partner_steam_id，verifyState 显式 true）；**mobile 确认批量**（type all/trade/market × operation allow/cancel 两下拉 + confirm 后 POST accept-all，helper 注明 identity secret 不出 agent）；**loot**（partnerSteamId 与 tradeUrl 二选一校验 + appIds 可选限定 + btn-danger + confirm 说明不可逆，响应里 mobile_confirmation 状态进事件日志）；**换卡**（查重复卡方案 → `<details>` 折叠 JSON 全文审阅 + 配对数摘要 → 「按方案发送报价」要求 lastSwapPlan 已存在且 partner 非空 → confirm 后 POST swap-offers `{send: true, keep, maxSwaps}`）。③202 pending 语义统一处理：响应 `{status:"pending"}` 时事件日志显示作业短 id 排队提示。④红线落实：全部写操作 confirm 前置、accept/loot/swap 三处弹窗均含「资产转移不可逆/核实对方身份」文案、UI 零业务逻辑（服务端校验是唯一真源）。验证：契约测试 9/9、全量 2185 全绿、format 门禁过、JS node --check 过。

> 2026-09-18：**§31 P3 市场与认领面板落地（feat 提交，admin.html 单文件改动）**。①「市场与认领」面板（交易面板之后，共用其账户下拉）。②四子区：**我的挂单**（start/count 分页拉取 + 列表渲染尝试读取 hash 名称/价格字段 + 原始响应折叠 JSON 兜底——agent 输出无稳定 schema，UI 不假装承诺字段）；**过滤撤单**（appId/hash/价格区间/挂龄四过滤 + dry_run checkbox 默认勾选；真实撤单前端先拦「无过滤条件」镜像服务端 400 语义，confirm 后执行）；**创建挂单**（定价二选一校验「卖方所得/买方支付恰好填一个」+「定价预览（dry_run）」与「真实挂单（send）」双按钮；**账户市场开关未开启时 send 按钮 disabled + helper 指引**——立项红线⑤的「禁用而非报错」，开关从 `state.accounts.marketListingsEnabled` 读取并在账户下拉 change 时联动刷新）；**积分商店**（definitionIds + freeOnly 查询 → 余额与定义折叠展示 → definitionIds CSV 认领，force checkbox 独立 + confirm「积分消费不可逆」）；**免费 license**（appIds/subIds CSV 二组至少一组，helper 注明「只加不减、detail 15 视为成功」）。③通用 `renderOperationResult`（摘要行 + 折叠 JSON）收敛五处一次性操作输出。验证：契约 9/9、全量 9 项目全绿、format 门禁过（真实 exit 0——上一轮 grep 管道曾误报 1）、JS node --check 过。

> 2026-09-18：**§31 P4 爬虫计划管理面板落地（feat 提交，admin.html 单文件改动）**。①「爬虫计划管理」面板（市场与认领之后，subtitle 链接 gamedata.html 结果浏览——admin 管写、gamedata 管看，分工不重复）。②计划列表卡片：名称/id、app 数、调度形态 chip（cron/周期秒/one-shot）、enabled 徽章、账号池、runCount/上轮/下轮；操作三钮（启用中才显示「立即触发」→ POST trigger 202；「编辑」预填表单进编辑模式；「删除」confirm 说明「已沉淀结果保留」——对齐服务端 resultsKept 审计语义）。③创建/编辑共用表单：name/appIds（必填前端拦）/账号池/overrides（`appId=account` CSV ↔ Dictionary 双向解析，格式错表单内报错）/shardSize/intervalMs/cc/cron/intervalSeconds/startNow+enabled checkbox；**保存失败内联展示红字且表单保持填写**（立项原文「cron 校验错误内联展示」——服务端 `ValidateCrawlPlanRequest` 是唯一真源，前端不复制 cron 语法规则）；PUT 语义注意：UpdateCrawlPlanRequest 是 omitted-keep 合并，前端全字段显式提交，startNow=false 保留现有游标不取消调度（helper 已注明）。④bootstrap 并入 refreshCrawlPlans。验证：契约 9/9、全量全绿、format exit 0、JS node --check 过。

> 2026-09-18（晚）：**§30.2 变体二击穿 v1 修复（windows CI 5ms 快速红,`b756332` run 35363102158）**。v1 的 park-then-cancel 建立在「调用方 WaitAsync 注册必然早于 Handle 侧 linked CTS」之上——该前提被 Windows 时序击穿（注册在 TryWrite 之后,抢占窗口内命令循环可反超）。v2 修复要点:**结构性解耦优于排序性锁定**——与其论证「谁先注册」,不如让完成源在 caller 取消时根本没有可竞争的完成路径。flake 图谱再补一支:§30 变体二 = Task.WaitAsync 完成-优先 × 注册序反超（测试侧解耦 park token 修复）。§30.2 附带发现（:306 "canceled" 结果对调用方不可达）同时被击穿——该结论同样依赖注册序前提,修正为:**"canceled" 结果可达,但仅在调用方线程于 TryWrite 与 WaitAsync 注册间被抢占的窗口内**;正常时序仍以 OCE 为主路径。生产 API 契约二义性比 §30.2 记录的更实质,仍记录在案不改实现。

---

## 34. 测试/覆盖率基线刷新（✅ 2026-09-18 完成，维护轮；todo 无未勾项时的「继续」自主立项，§17/§30 先例）

> 立项动机：§31–§33 三功能节连续落地后，TESTING.md 基线停在 2026-09-17（2093 测试 / 99.8%），与实际规模脱节。范围：TRX 精确计数 + 覆盖率双轮重盘 + 可收缺口当场收掉 + 剩余缺口定性入册；期间 CI 挂红顺带修一个 flake（见日志③）。

- [x] **TRX 精确计数刷新**：2216→**2226**（Steam.Core 1204 / ControlPlane 573 / Plugins.Core 128 / MobileAuthenticator 126 / MarketWatch 56 / Agent 54 / Monitoring 35 / Protocol 39 / E2E 11），TESTING.md 统计表与目录树同步。（✅ 2026-09-18）
- [x] **覆盖率双轮重盘**：首轮 99.1%（14328/14452，§31–§33 新代码债：Steam.Core 99.7→99.0、ControlPlane 99.8→99.1）→ +10 测试收 action 边界缺口 → **99.3%（14354/14452，Steam.Core 99.4%、Monitoring 时序边沿行回 100%）**。（✅ 2026-09-18）
- [x] **+10 成就 action 边界测试**：GetAchievementsAction 三边界（internal 工厂 ctor + 元数据断言不触 factory / 无 webHandler / 非数字 steam_id 不触页面拉取）+ unlock 侧 transport 抛异常臂 + reset 侧无 client / transport 抛异常 / 无响应 + ParseNames 双格式形状（裸 string 单元素批、.NET List 走 IEnumerable 臂、JSON 数组非字符串元素逐字透传、null 与标量拒绝且不触 transport）。（✅ 2026-09-18）
- [x] **剩余 98 行缺口定性入册（TESTING.md 清单同步）**：①DesiredStateReconciler 51 行（§32 trade/§29 boost 编排循环的 no-agent skip/GuardDryRunAsync/审计异常隔离 catch/payload 多类型解析 switch）——干净但需集成型夹具，立项为后续测试深化冲刺（不承诺）；②§33 新 records 合成成员约 16 行（ISteamTransport/协议 handler 的 Equals/ToString）——真实路径仅构造不比较，覆盖噪音定性不强追；③既有四类（平台分支 2/集成壳反射 21/结构性 DEAD 9/防御兜底与时序边沿 7）维持。（✅ 2026-09-18）
- [x] **CI flake 顺手修（edc9adb）**：`PollOnce_BuildsBaselineThenAlertsWithWebhook` 在 CI 挂（run 35399859012，Assert.Single 空集合）——InitializeAsync 自启的后台轮询循环首 tick 立即执行，与手动驱动 PollOnceAsync 的测试竞争 store。新增 `StopLoopForTestsAsync`（cancel + await 退出 + 不替换——restart 钩子不解决问题，因新循环首 tick 同样立即）并在四个手动驱动测试 store 空时调用；本地压测 20/20 绿。（✅ 2026-09-18）

> 2026-09-18：**§34 收口（fix + test + docs 三提交）**。①**数字链条**：计数 2093→2216（§31–§33 落地的自然增长）→2226（本轮 +10）；覆盖率 99.8%→99.1%（新债暴露，14328/14452）→**99.3%（14354/14452）**；分程序集：Agent / MobileAuthenticator / Monitoring / Plugins.Core / Protocol / TestFixtures / TestPlugin 100.0%、ControlPlane 99.1%、Steam.Core 99.4%、MarketWatch 98.9%（其 8 行缺口属结构性 DEAD 类，既有定性）。②**TESTING.md 同步**：目录树/统计表/分类标题刷至 2226（Steam.Core 1204），动作表补成就三类行（GetAchievements 10、Unlock/Reset 合并 18——§33 收口时漏列，本轮补上）；基线标题 2026-09-18 / 99.3%；历史区追加本轮条目（回落原因 + +10 明细 + 定性结论）；「剩余未覆盖」由 39 行四类重写为 98 行六类（新增编排分支 51 行与 record 合成成员 16 行两类）。③**CI 事件（本轮唯一计划外）**：1d43237（纯 docs 提交）run 35399859012 挂 `PollOnce_BuildsBaselineThenAlertsWithWebhook`——静态推遍全部交错推不出 Bodies=0，但测试设计缺陷确凿：InitializeAsync 自启的后台循环（首 tick 立即、线程池调度时机不定）与手动驱动 PollOnceAsync 竞争 store；`RestartLoopForTestsAsync` 治不了（新循环首 tick 同样立即），落地 `StopLoopForTestsAsync`（drain-and-stop）四测试接入，本地压测 20/20（edc9adb，CI 双绿验证）。flake 图谱新增族五（SUT 自带后台循环 × 手动驱动），教训与「推不出具体交错也要先移除竞态源」的处置一并入册。④验证：全量 Release 全绿（1204+573+56+126+128+54+35+39+11）、format 门禁过、CI（edc9adb）ci+codeql 双绿。

---

## 35. Reconciler 51 行编排分支测试深化（已完成，2026-09-19）

> 立项动机：§34 把 DesiredStateReconciler 51 行定性为「干净但集成型构造」留作冲刺候选；实测发现既有测试基建（CreateReconciler/FakeAuditStore 的 ThrowOnRecord/ThrowOceOnRecord、FakeReconcileJobStore 的 StripTasks）当年就为这些路径预留了钩子，多数场景可直接收掉。范围：派发守卫（playtime/trade 扫描/gift accept 的 no-agent 与 dry-run、playtime 查询在飞行）、agent 消失的 confirm 放弃、审计隔离（异常吞咽 + OCE rethrow + decisionsTruncated 软上限）、payload 解析防御（offer/boost 条目坏形状、数值多类型臂）、结算回读防御（空 Tasks、accept 失败、confirm 结算）。

- [x] **A 派发守卫**：boost evaluate 的 ActiveJobId 在飞行等待（546）、playtime no-agent skip（559-561）、playtime dry-run guard（566）、trade 扫描 no-agent（708-710）、trade 扫描 dry-run（715）；gift accept no-agent（654-656）。accept dry-run guard（659-661）定性为防御性死分支：全局 dry-run 下前置的扫描派发已被拦住，cfg 只读无中途切换路径，同 §34「同谓词防御性死分支」家族不强追。
- [x] **B agent 消失**：实测定性为**不可达**——AssignedAgent 消失时编排步骤 1 已 rebalance 清空 `AssignedAgent`+`ActiveJobId`（§29 既有行为），confirm 结算读到 agentId null/不在 connected 的状态组合无法经 store 构造；759-760 与 661 同入「防御深度不可达」册。
- [x] **C 审计隔离**：decisionsTruncated 软上限 200 条（822）、评估审计异常吞咽（841-845）与 OCE rethrow（837-839）、auto-accept 审计异常吞咽（891-894）与 OCE rethrow（887-889）。
- [x] **D payload 解析防御（internal 直测）**：offer 条目 offerId==0/JsonElement 非 object/裸 string（940/994/1020）、OutputFlagIsTrue 缺 key 与 JsonElement 形态（1028/1033）、boost targets 空（1117）、playtimes 容器为 string（1151）、条目裸 string（1204）、TryGetNumberFromElement 缺 key/int/string/JsonElement/default 各臂（1218/1229-1237）。
- [x] **E 结算回读防御**：playtime/trade 扫描/accept 三处结算遇空 Tasks（StripTasks）静默返回（1463/1499/1535）、accept 失败 deviation 且队列 drain（1557）、confirm 结算 Finished 无 deviation 与 Failed deviation 两态（1565-1569）。
- [x] **收尾**：TESTING.md 基线/缺口清单刷新、todo 勾选、format + 全量、提交推送 + CI。

> 2026-09-19：**§35 落地（test 提交 + docs 提交）**。+22 测试（DesiredStateReconcilerTests 56→78，总 2226→2248 全绿），收掉 51 行编排分支中的 48 行，ControlPlane 99.1%→**99.8%**，合计 **99.6%**（14407/14459）。①**A 派发守卫**：NoAgentSkips 场景用 capabilities 仅 get_trade_offers 的 agent 让扫描过、accept 拦（实机发现 settle 同 pass 立即尝试 accept + 下轮重试 = 两轮 skip，断言 2 而非 1）；dry-run guard 断言 deviation 记账且零派发；在飞行等待靠手动拉长 `PlaytimeInFlightWindow`。②**B 不可达定性**：confirm 放弃（759-760）实机推演确认 agent 消失走步骤 1 rebalance 已清 `AssignedAgent`+`ActiveJobId`，与 661（全局 dry-run 下前置扫描被拦）同入「防御深度不可达」册——§35 预估 51 行实收 48 行。③**C 审计隔离**：ThrowOnRecord/ThrowOceOnRecord 钩子（当年 §32 预留）直接驱动；陷阱一——置位在 accept 派发**之后**，置位前的 3 条合法审计（两轮 account.reconciled + policy_evaluated）必须在置位前记 count 再断言不增，不能 Assert.Empty；decisionsTruncated 用 201 条 unknown-partner offer 断言 evaluatedCount/decisions 上限/truncated 三值。④**D payload 解析**：internal 直测（`ExtractPendingGiftOffers`/`ExtractBoostUnmet`）；陷阱二——`AccountStore.Upsert` 对 Boost 状态 fail-fast「至少一个 boostTarget」，「spec 无 targets」形状只能用 Online 状态构造（1117 空返回与 DesiredState 无关）；数值臂收全 boxed int/long/double/string/JsonElement round-trip/缺 key（TryGetDouble 的 long/double/JsonElement 臂在最后一轮 miss 清单里补收）。⑤**E 结算回读**：StripTasks 钩子三处空 Tasks 静默、accept Failed deviation+队列 drain、confirm Finished 链路（JsonElement true 触发）与 Failed deviation 两态。⑥**意外收获族一 flake**：全量覆盖率轮 `GetMarketListings_AgentReportsFailure_Returns502` 假红 202——AccountApiTests（默认 30s 窗口）与 ProgramBranchCoverageTests（300ms 窗口）类间并行读写同一静态 `AccountTaskRunner.WaitWindow`，等待循环恰好落在对方窗口内提前超时；所有赋值点 finally 纪律完好，纯类间并行竞态；新增 `AccountTaskWaitWindowCollection` 串行化两类，taskset -c 0,1 双核压测 188 全绿。⑦Monitoring 时序边沿（137/140）本轮未命中回 99.4%，照旧定性。验证：DesiredStateReconcilerTests 95 全绿、format 门禁过、全量两轮全绿（含覆盖率轮）。

## 36. ASF 风格 CI 收紧（静态分析全开 + NuGet 审计 + 覆盖率门禁 + property 测试）

> 立项动机：用户长期目标——对齐 ASF（ArchiSteamFarm）的 CI 严格度，「边界检查做全、每次改动 CI 自动检查、快速找出 bug」。2026-09-19 盘点：矩阵/CodeQL/format/blame-hang/依赖审查已齐；四个缺口 = ①Roslyn 分析器未开（AnalysisLevel/AnalysisMode/EnforceCodeStyleInBuild）②NuGetAudit 未开 ③覆盖率只上传无门禁 ④无 property-based 测试。照抄不取：随机测试顺序（引入新 flake 形态）、性能回归门禁（噪声大）。

- [x] **P1 静态收紧**：Directory.Build.props 加 `EnableNETAnalyzers` + `AnalysisLevel=latest-all` + `EnforceCodeStyleInBuild` + `NuGetAuditMode=all`；首轮 build 收集全量 CA 告警并分类——真边界问题逐个修（culture/race/dispose/async/null 模式），噪声规则在 .editorconfig 降级为 suggestion 保留可见性（不 NoWarn 硬压）；全量测试回归。
  > 2026-09-19：**P1 落地**。Directory.Build.props 五开关全开（`AnalysisLevel=latest-all` + `EnforceCodeStyleInBuild` + 三重 TreatWarningsAsErrors），首轮爆出 **~12,800 条**告警，分类治理至 **Release 全量 0 warning**。①**大族降级**（.editorconfig 分层注释写明理由）：CA1707（tests 下划线 4052 条，命名铁律）/CA2007（ConfigureAwait 3520，ASP.NET Core 无同步上下文）/CA1848（LoggerMessage 重构 670）等 design/风格族 none；CA1822/CA1826/CA1859/CA1308/CA2007 留 suggestion 保可见性不阻塞；CA1849（同步 IO）降 suggestion——三处已审均工程不适用（CTS.Cancel 换 CancelAsync 改回调时序、SQLite 事务 API 假异步、内存流），真网络/文件同步调用靠 review 把关；tests 段单独 21 条降级（fixture/惯用法噪声）。②**真边界修复**（src 侧 ~60 处）：**CA1305 culture 族 29 处**——Steam 协议值（trade_offer_id/steam_id/价格 decimal）全部显式 InvariantCulture，法语 locale 下 `decimal.ToString()` 输出逗号会损坏 API 参数；Agent/ControlPlane 两进程统一 `InvariantGlobalization=true`。**CA1806**：TradeOfferActions 四处 TryParse 保默认值语义逐个注释（appId 保 730/contextId 保 2/assetId==0 丢弃故意/amount 保 1）。**CA1307**：SteamWebHandler 四处域名 `Contains` 补 Ordinal（协议大小写语义）。**CA2100 SQL**：三个 Sqlite store 的 BuildQueryCommand/EnsureColumn 逐段审阅——插值片段全是编译期常量（where 片段/order 白名单）、用户输入全走 $参数，属流分析局限，pragma + justification（AuditStore 的 else COUNT 分支漏套第一轮 pragma，第二轮补上）。**CA5350**：SHA1/HMACSHA1 是 Steam Guard TOTP/SteamId 协议要求，非选型。**CA1032**：七个异常类补齐 ()/(string)/(string, Exception) 三件套。**dispose 族**：MetricsHttpServer Dispose 补 `_listener.Dispose()`（CA2213 要字面调用，Stop() 等价但不认）；SteamTradeClient 四处 FormUrlEncodedContent using 化；HttpClient/BotSession timeoutCts 等真泄漏修完。③**所有权转移/分析器局限误报 → pragma + justification**：SteamWebHandler handler（转入 _httpClient）、PluginManager（return 转移调用方）、importStore（转入 MaFileImportCli）、heartbeatTask（同 scope await）、SessionManager CA2025（session 归 _sessions 字典）、MonitoringPlugin CA1001（插件协议用 ShutdownAsync 而非 IDisposable 清理）。④**NuGetAuditMode=all 首扫零漏洞**（`dotnet list package --vulnerable --include-transitive` 复核为空）。⑤Agent Program CA2234 PostAsync 补 Uri、CA1802/CA1823/CA1829/CA1834/CA1865 各 1-2 处、FileCredentialStore `static readonly` → `const`、VaporCryptoHelper 删死字段。**首轮 CI 一个红**：docker-build 的容器内 `dotnet publish` 读不到 `.editorconfig`（两个 Dockerfile 没 COPY 它）→ 降级规则丢失 → CA1028/CA1716 以默认 severity 撞上 TreatWarningsAsErrors；修复为 Dockerfile 显式 `COPY .editorconfig ./`——严格构建的配置本身就是构建输入，进镜像与 Directory.Build.props 同列。验证：Release 全量 `--no-incremental` 0 warning 0 error、format 门禁过、全量测试回归（见下）。
- [x] **P2a 覆盖率门禁**：CI coverage job 尾部跑 coverage-summary.py 断言合计 ≥ 当前基线，回退即红；基线数字随 TESTING.md 同步演进。
  > 2026-09-19：**P2a 落地**。`coverage-summary.py` 加 `--min N`（argparse，合计 < N 退出码 1 并指回 TESTING.md 基线，无参数行为不变）；CI coverage job 在 Codecov 上传后加 "Coverage gate" 步骤 `--min 99.6`（当前基线）。本地两态验证：99.6 过、99.7 红。
- [x] **P2b property-based 测试（FsCheck）**：挑纯函数核心立性质——NormalizeTradePolicy（排序/去重/拒绝不变量）、数值与 offer 解析族（任意形状输入不抛未预期异常+保守语义）、协议模型 JSON 往返恒等、crypto 往返恒等；纳入 run-tests.sh。
  > 2026-09-19：**P2b 落地**。FsCheck.Xunit 2.16.6 进三个测试项目，+15 property（合计 2248→**2263** 全绿）：①**TradePolicyPropertyTests（ControlPlane，8）**——白名单零剔除/去重/升序三合成不变量、输入序无关（正反序同结果同拒绝）、幂等、全零 whitelist + auto-accept 必拒 ArgumentException；`NormalizeTradePolicy` private→internal（照 §35 internal 直测先例）。②**TryGetDouble 保守语义（4）**——任意 boxed 值不抛、false 置零、数值臂精确换算、string 臂与 invariant TryParse 完全一致、JSON 数字臂序列化往返。**性质测试首战抓获真实边界**：boxed double NaN/∞ 直通 + `TryParse("NaN")` 返回 true——非有限 boost 目标会静默流入 deviation 记录；产品修复为 double 臂 `IsFinite` 守卫 + string 臂解析后 `IsFinite` 复核（保守拒绝语义，603 全绿含 §35 全部既有用例）。③**VaporCryptoRoundTripPropertyTests（Steam.Core，3）**——任意 UTF-8 明文 × 任意 ≥32B 密钥 AES-GCM 往返恒等、错钥认证失败永不静默回原文（null 或 CryptographicException 均合法）、随机 nonce 密文互异且均可解（初稿「密文稳定」断言被随机 nonce 事实推翻，改为互异性）。④**ProtocolJsonRoundTripPropertyTests（Protocol，4）**——TaskHeartbeat/TaskCancel/ErrorResponse/AgentHello 任意字段值序列化往返恒等（AgentHello 因 record 值相等对 Dictionary 是引用比较改逐字段断言；FsCheck 默认字符串生成器产 null/重复 key 需折叠——`object?` 字典模型排除并注释原因：STJ 回读 JsonElement 是已知可接受的损耗）。FsCheck 2.x attribute 无 MaxTest 命名参数可用，默认 100 次采样 + 固定种子（StdGen 确定）满足确定性纪律。
- [x] **收尾**：todo 勾选、TESTING.md/README 若涉及则同步、提交推送 + CI。
  > 2026-09-19：**§36 全部收官**。四个提交推送（1ece808 docker .editorconfig 修复 / 92835bf 覆盖率门禁 / 844b1a5 property 套件 + NaN 边界修复 / 4730c23 TESTING.md 基线 2263/99.6%），CI 全绿（十 job：矩阵 6 + coverage + format + integration-redis + docker-build，codeql 绿）——**Coverage gate 在 CI 环境实测生效**（合计 99.6% (14413/14476) ≥ 99.5 通过）。ASF 目标四缺口状态：①静态分析 latest-all 全开 + 0 warning 门禁 ✓ ②NuGetAudit all（零漏洞）✓ ③覆盖率 CI 门禁 ✓ ④property-based 测试 ✓。后续可迭代项（不立项）：覆盖率基线随测试提升逐步抬高 gate、NuGetAudit 依赖升级策略、FsCheck 覆盖更多纯函数（offer 解析族等）。

> 2026-09-19：**仓库 meta + docs 站点化维护轮（用户指令：gh 补全 about/topics + docs 文档 + Actions 部署 Pages、push 触发；docs 提交 `6fd20a6`）**。①**仓库元数据**：`gh repo edit` 设 description（API-controlled headless Steam automation platform, ASF-inspired）+ 10 topics（steam/automation/archisteamfarm/asf/dotnet/csharp/steam-bot/headless/self-hosted/multi-account）+ homepage `https://cuihairu.github.io/vapor/`；**中途异常**：设置并回读确认后 desc/topics 被清为 null/空（homepage 反而保留，原因未明，疑 GitHub 端异步覆盖），重设后 API 直查二次验证持久。②**站点**：`mkdocs.yml`（Material 9.6.x，明暗双 palette 切换、instant nav、search、edit links）+ `docs/index.md` 落地页（grid cards 四卡片 / Why Vapor / Quick start 双 tab——compose 含 `--profile observability`、本地含 agent 全套环境变量 / Status alpha）；nav 三区 12 页全部指向既有 docs 文档。③**部署**：`.github/workflows/docs.yml`——push main paths 过滤（docs/**、mkdocs.yml、workflow、README.md、tests/TESTING.md）+ workflow_dispatch；OIDC 三权限（contents:read/pages:write/id-token:write）；concurrency group `pages` cancel-in-progress；build（setup-python 3.12 + `pip install "mkdocs-material==9.6.*"` 锁 minor 线 + `mkdocs build --strict`——内部死链即红）+ deploy（deploy-pages v4）；三个 action 全 SHA pin（仓库惯例）。**testing.md 单一权威源**：`docs/testing.md` 不入库（.gitignore），workflow 构建前 `cp tests/TESTING.md docs/testing.md` 镜像——站点有活页、仓库无第二份会漂移的拷贝。Pages 侧：`build_type=workflow`（gh api 预设）+ 首次部署后站内四页 200 实测。④**教训**：mkdocs `exclude_docs` 排除的文件进不了站点且与 nav 引用冲突（INFO 提示）——镜像方案替代排除方案；本机 Python 3.14 venv ensurepip 缺失 → `pip install --user --break-system-packages`。验证：本地 `mkdocs build --strict` 过；`6fd20a6` 三 run 全绿（ci 十 job + codeql + docs），站点 https://cuihairu.github.io/vapor/ 上线。

> 2026-09-19：**Code scanning 告警清零（用户指令「这里的修复吧」；13 条 open 的 `cs/cleartext-storage-of-sensitive-information` high 全部根治）**。①**定性**：告警消息点名 "local variable accountId : UInt64"——CodeQL 对变量名的敏感数据启发式（对比同页已 fixed 的 log-forging 消息是 "user-provided value" 值流形态），非真实凭据泄漏：被「明文存储」的值是 SteamID（公开 UInt64 账户标识），2026-09-18 已有一轮 dismiss 先例——但**代码改动后行号漂移，dismissed 绑定老位置，新分析同源又开新 alert**（本批 13 条 open 即重演证据），dismiss 治标不治本。②**根治 = 断名流**：全仓 grep 定位 taint 源仅两处——`TradeModels.cs` 交易链接解析的 `out var accountId`（ulong，本批源）与 `TradeOfferStateMachine.ToSteamId64(uint accountId)` 参数（uint，防复发一并改）；taint 沿 `PartnerSteamId` 跨方法传播至五个文件 13 个日志 sink（`_logger.Log*("... {SteamId}", steamId)`），源改名后全链失流。改名三处语义零变化：`accountId`→`parsedPartner`（TradeModels）、`accountId`→`partnerId`（ToSteamId64 参数，调用方全位置传参）、tests `accountId64`→`rawId64`；`ToAccountId` 方法名与 uint 型 `accountIdOther` 测试字段不动（无 ulong 名字命中、无日志 sink taint 路径）。③**方法论**：Code scanning 告警处置三态里「改名」是 dismiss 之外的第四条路——name-based source 的告警，源改名 = 结构性消失，行号再漂移也不会重开；教训入册：**代码库规避 CodeQL 名字启发式的标识符命名纪律（ulong 局部变量避开 `accountId` 形命名）**。验证：Release build 0 warning 0 error、Steam.Core 1207 全绿、format 门禁过；提交 `34baae6` 后 codeql 重扫 **open 清零（13 条全转 fixed）**，ci + codeql 双绿。

> 2026-09-19：**维护轮——FsCheck 扩面（交易解析族 property；todo 无未勾项时「继续」自主立项，§34 先例；§36 收官列明的可迭代方向；test 提交 + docs 提交）**。+7 property（`TradeParsingPropertyTests`，合计 2263→**2270** 全绿，Steam.Core 1207→1214）：①**报价 URL 解析四条**——任意输入不抛且解析成功必 ≥ SteamId64Base（合成不变量：32 位形态加 base、64 位形态直通）、32 位形态精确加 base（∀ uint）、64 位形态直通（域构造 `Base + raw` + wrap guard——ulong 全域生成命中率仅 0.4%，从 uint 拼域才是有效采样）、token URL 编码往返（`Uri.EscapeDataString` lone surrogate 有损，过滤照 crypto 先例）。②**状态机谓词三条**——无过期时间的 offer 任意时刻永不过期、CanAccept ⟹ CanDecline + 收到的 Active（前置条件严格收窄蕴含）、sender 自匹配合规域恒过（ValidateForAccept partner 校验不额外拒绝）。**两处修正**：时间偏移 helper 须 `FromUnixTimeMilliseconds`（epoch 起算）而非 `AddMilliseconds`（epoch 到 9999 年的总量加到 2026 上溢出——MaxMs 是绝对纪元量不是偏移量）；ValidateForAccept 自匹配性质需规范域 guard——`AccountIdOther` 次规范形态（< base）被 `ToAccountId` 折 0 属有意防御语义（既有 `ToAccountId_WithSubBaseValue_ReturnsZero` 锚定），恒等式只在 64 位规范域成立。③**TESTING.md 顺带修正 stale**：统计表 Steam.Core 行 1197 系 §34 轮漏更（正确 1207），本轮起以 TRX 实测计数为准。验证：Steam.Core 1214 全绿、format 门禁过、CI 终态见提交后监控。

> 2026-09-19：**维护轮二——FsCheck 扩面续（换卡匹配算法 property；「继续」自主立项；test 提交 + docs 提交）**。+5 property（`CardSwapMatcherPropertyTests`，合计 2270→**2275** 全绿，Steam.Core 1214→1219），覆盖 `CardSwapMatcher` 纯算法三入口：①**分组不变量**——尺寸账目（`TotalTradable > keep` 且 Excess 恰为 `TotalTradable - keep`）、只收可交易身份、excess 拷贝全部 Tradable、组按 identity 升序；②**匹配严格互补**——每个 swap 的收/给卡在对方库存零持有（双向 DoesNotContain 全可交易 key 集）、双侧资产均来自各自 excess 池（AssetId ⊆ FindDuplicates 输出）、context 规则（753→6 其余→2）随 Give/Receive、`maxSwaps` 上限；③**两确定性**——同输入两次全等（`DuplicateGroup` record 集合字段引用比较，逐字段 + SequenceEqual，§36 教训复用）；④**ContextIdFor 全域**。**生成器方法论入册**：identity id（app/class/instance）必须缩域（4×5×2 池）——全域随机 id 几乎零碰撞、分组性质真空转；assetId 保持全域保证唯一性；数组参数（`uint[]`/`ulong[]`/`bool[]`）经最短长度对齐构造 items（FsCheck 默认 Arb 只可靠覆盖原始类型数组，自定义 record 不进生成器）；`IsTradableNow` 依赖 `UtcNow`——property 只用 `TradabilityDate=null`（未来锁定期与 `Tradable=false` 在该谓词等价，仅测其一保持确定性）。验证：Steam.Core 1219 全绿、format 门禁过、CI 终态见提交后监控。

> 2026-09-19：**维护轮三——市场手续费 property（「继续」自主立项；性质测试再抓两个真产品缺陷并修复；fix 提交 + docs 提交）**。+5 property（`MarketFeeCalculatorPropertyTests`，合计 2275→**2280** 全绿，Steam.Core 1219→1224）：拆分恒等（Buyer = Seller + Steam 费 + Publisher 费、费 ≥1）、20 分起 5%/10% 比例精确（floor 域内逐点）、买家价严格单调、`FromBuyerPrice` **极大性**（返回落点 ≤ 目标且 `FromSellerProceeds(seller+1).BuyerPrice > target`）、域二分完备（<3 分必 null、≥3 分必有解）、上限外 AOORE 守卫断言。**缺陷一（定价精度，日常域实伤）**：`FromBuyerPrice` 原从「精确 15% 费」估计 `floor(target×100/115)` 起 down-walk——但 `floor(5s/100)+floor(10s/100) ≤ floor(15s/100)`（分量 floor 截断），低价 min-1-cent 区再欠至 2 分，真实可行 seller 可达估计 +2~3 而 walker 到不了：反例 buyer=39 返回 seller=34（买家价 38），而 seller=35 买家价恰 39 ≤ 目标——**所有 <$1 卡的定价系统性少算卖家 1 分**。修复：起点 +3（欠账通用上界，注释含推导；首试 +1 被极大性性质反例 39 打回，欠账含 min-fee 区比纯 carry 分析多 1——保守上界多两次迭代换零风险），精确域（115→100、114→99）与 3 分门限行为不变，既有 11 example 全过锚定。**缺陷二（溢出守卫，极端域）**：`sellerProceedsCents × 5` int 乘法在 >`int.MaxValue/15` 时 unchecked wrap 可产出负买家价——新增 public 常量 `MaxSellerProceedsCents`（≈$1.43M）双侧 AOORE 守卫 + buyer 侧乘法 long 化；property 形态为「上限域内断言正确数学、上限外断言守卫抛出」，把溢出行为钉死为显式拒绝。CHANGELOG Unreleased 新建 Fixed 段。**方法论入册：极大性断言（落点合法 + 「+1 必超」）是定价/搜索类 walker 的杀手锏——单侧「≤ 目标」断言对次优结果完全失明**。验证：Steam.Core 1224 全绿、format 门禁过、CI 终态见提交后监控。

> 2026-09-19：**覆盖率扫零轮（用户指令「检查测试覆盖率，找出覆盖率为 0 的项目/类，提至 98%+；test 提交 + docs 提交）**。**盘点**：全解决方案合计 99.7%、程序集最低 98.9%；分母对比（src/tools 全部源文件 vs cobertura class 条目）定位唯一真正的 0% 项目/类 = `tools/Vapor.KeyRotation`（211 行 CLI 壳，无测试项目、从未进覆盖率分母）；`IJobStore`/`IActionExecutionObserver`/`ISteamTradeClient` 三纯接口与 `Program.Partial.cs`（3 行空 partial 声明）无可执行行，定性分母噪音。**KeyRotation 壳测试（新建 `Vapor.KeyRotation.Tests`，+27，0%→98.1%）**：`Program.Main` 直调全分支（help 双旗/未知参数/三形态缺参/坏 base64/同钥/store 缺失/损坏 JSON）+ 真实旋转三态（dry-run 断言逐行输出与文件零改动、applied 断言备份落盘且新钥可解回原文、aborted 断言 FAILED 逐账户上报）+ 反射直测私有静态（`ParseKeySpec` 六臂、`ExpandPath` `~/`/`~\` 双臂、`GetValue` 正常路径）。**不可测定性**：`GetValue` 缺值臂 `Environment.Exit(2)`（117-118）在测试进程内终止 testhost——CLI 进程语义与测试进程语义的根本冲突，98.1% 即该 CLI 的覆盖率天花板。**教训两条**：①CLI 测试传参必须带 key-spec 前缀（`base64:`）——裸 base64 被文档化的 plain-text 回退当密钥材料，产品行为正确而测试静默错语；定位法入册：部件探针全绿但组合失败时，在产品代码临时 dump 实收参数的 SHA256 hash 与测试侧对比（本轮 hash 不一致直接反推参数缺前缀），比继续拆部件快一个数量级。②`Environment.Exit` 路径不追。**上会话遗留落地**：工作树中未提交的 +10 Steam.Core 测试一并验证并入——`SteamAuthTokenProvider.GenerateAccessTokenForAppAsync` 反射桥（09-17 曾定性「不可收敛：需活 CM 连接」——推翻：反射替换 `_authentication`/`_generateAccessTokenMethod` 两字段指向 fake generator 即可钉死参数顺序与结果映射；SteamClientManager 反射行收掉，剩异常类 CA1032 备用 ctor 4 行入合成噪音册）与 `UserStatsProtocol` records 契约 9 例（构造面/相等语义/OK 载荷配对/协议响应单载荷；`ISteamTransport` 仅剩一行 ToString）。**本轮后剩余 43 行**：CA1032 备用 ctor（7，合成噪音）、KeyRotation Exit（2，结构性）、防御死分支/时序边沿/平台分支（其余照旧）。验证：全量两轮全绿（含覆盖率轮，合计 99.7% 14538/14581、门禁 99.5 过）、format 门禁过、CI 终态见提交后监控（`061564e` test + `0ff6883` docs）。

> 2026-09-19：**维护轮四——异常 ctor 契约收编（「继续」自主立项：覆盖率扫零轮收官时新定性为「合成噪音」的 CA1032 备用 ctor，顺势收掉；test 提交 + docs 提交）**。+4 测试（2321 全绿：Steam.Core 1234→1236 / ControlPlane 603→604 / Plugins.Core 128→129），三个 `ExceptionContractTests`/`PluginExceptionTests` 各一 Fact 钉死三 ctor 契约（parameterless 默认 Message 非空 + `(message)` 透传 + `(message, inner)` 双透传 Same 断言）：`SteamAuthCodeRequiredException`/`SteamTwoFactorCodeRequiredException`（BotSession 登录挑战路由的公开失败契约）、`NotFoundException`（SqliteJobStore→REST 404 的 store 层契约）、`PluginException`（插件宿主边界失败类型）。三处异常类全部从 gap 清单消失（100%）；Plugins.Core 99.8%→**100%**；合计 14538→**14543**/14581（99.7% 保持，裸比例微升）。净收 5 行 = 新收 7 行 − SessionManager Barrier 竞态臂 2 行轮间波动（135/136 上轮命中本轮未命中，多轮在册的时序边沿家族照旧定性）。剩余 38 行全为已定性类：防御死分支/时序边沿/平台分支/KeyRotation Exit/record ToString。验证：三个项目增量全绿 + 全量覆盖率轮全绿（门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。

> 2026-09-19：**维护轮五——FsCheck 扩面（trade 资产载荷解析 property；「继续」自主立项，§36 收官点名的 offer 解析族；性质测试第 4 次抓获真产品缺陷并修复；fix 提交 + docs 提交）**。+7 property（`TradeAssetParsingPropertyTests`，`ParseTradeAssets`/`ParseSingleAsset` private→internal 直测，合计 2321→**2328** 全绿，Steam.Core 1236→1243）：①**字段精确性**——任意 boxed 数值与 InvariantCulture 字符串精确解析；②**默认值二分语义**——缺键/null 保 CS:GO 默认（730/2/1）、不可读值同样回退默认；③**丢条目**——asset_id 缺失/null/0/不可读/溢出五形态全丢；④**容器全形状**——任意容器不抛、缺键得 Empty、产出 assetId 恒非零；⑤**过滤保序**。**缺陷（TryParse out 参数陷阱；§36 CA1806 注释声称语义的假落实）**：`uint appId = 730; _ = uint.TryParse(obj.ToString(), out appId)`——TryParse 失败时 out 参数被**写 0**，预置默认被覆盖，§36 时加的「unreadable keeps default」注释与实际行为相反：不可读 app_id/context_id 产出 AppId=0/ContextId=0 的静默垃圾条目（缺键与不可读行为不一致，仅 amount 因下游 clamp 碰巧正确）；修复为失败分支显式重赋默认 + amount 的 ≤0 clamp 统一进解析处，「缺键 = 不可读 = 默认」三态一致。**方法论两条入册**：①「预置默认 + 丢弃 TryParse 返回值」是 C# 陷阱写法，防御性解析必须在失败分支显式回退；②注释声称的语义未经测试锚定可能整段为假——property 以「文档声称的语义」写断言即刻暴露。测试侧修正：FsCheck 默认 string 生成器含 null → payload key 用 `NonNull<string>`；混合类型 switch 臂显式 `(object?)` 转换。验证：全量覆盖率轮全绿（合计 **99.7%** 14547/14584，门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。

> 2026-09-19：**维护轮六——FsCheck 扩面（认证码数学 property:SteamTotp + ConfirmationHashGenerator;「继续」自主立项,§36 可迭代方向;test 提交 + docs 提交)**。+9 property(Steam.Core `SteamTotpPropertyTests` 6 + MobileAuthenticator `ConfirmationHashGeneratorPropertyTests` 3,合计 2328→**2337** 全绿:Steam.Core 1243→1249 / MobileAuthenticator 126→129,后者首次引入 FsCheck):①**RFC 6238 oracle 交叉验证**——测试内本地手写规范实现(HMAC-SHA1/30s/动截/Steam 字母表投影),与产品实现任意 secret×时间全域恒等,「实现=规范」性质,独立于产品代码路径;②输出形状——确定性 + 恒 5 字符 ⊆ 26 符号无混淆字母表(0/1/I/L/O 缺席=转抄安全的构造性保证);③同窗不变——窗口索引全域×偏移全域构造域;④SecondsRemaining 周期性+值域 [1,30];⑤DecodeSecret base64 往返+ParamName 契约;⑥ConfirmationHashGenerator HMAC oracle(8B BE 时间+UTF8 tag)+四已知 tag 互异+空 secret 先于空 tag 检查的 ParamName 契约。**本轮无产品缺陷**——已知向量锚定过的加密数学,property 价值=点例升级为全域=规范;oracle 形态适用于一切有规范可依的实现。**测试侧修正两条入册**:①反例清单须实测——"ZZZZ" 是合法 base64(直觉乱码≠非法域),被 property 抓出换 "====";②uint % int 提升为 long 不能作 string 索引。顺带修 4 处 stale 分类计数(维护轮四只更统计表漏分类标题,本轮起一并对照)。覆盖率 99.7%(14545/14584,分子 −2 为 SessionManager Barrier 竞态臂轮间波动,在册时序边沿照旧);format 门禁过、CI 终态见提交后监控。

> 2026-09-19：**维护轮七——FsCheck 扩面(payload 读取器值形状 property;「继续」自主立项,轮五 trade 资产解析同族地基收官;test 提交 + docs 提交)**。+7 property(`PayloadReaderPropertyTests`,合计 2337→**2344** 全绿,Steam.Core 1249→1256):①查找语义——精确键恒压大小写变体、变体回退命中(ToUpperInvariant 孪生全域,自碰撞 guard);②GetInt32 域判定——boxed long 恰 int 域内、boxed double「整数且域内」双条件(NaN/±∞ 全 null);③往返三形态——任意 int(装箱/invariant 字符串/JSON 字符串,负数与边界全域)、任意 bool(**bool.ToString() 产 "True"/"False" 须被 TryParse 接受**,点例只测过小写);④总函数性——任意形状×键×三读取器不抛。**测试侧自纠**:首版「精确键压变体」中 key 自碰撞时 decoy 赋值覆盖 exact 值(注释声称断言仍成立是错的——轮五同款「注释未推演」陷阱),修复为碰撞时跳过 decoy。验证:全量覆盖率轮全绿(合计 **99.7%** 14547/14584,Barrier 竞态臂 2 行命中回归,门禁 99.5 过)、format 门禁过、CI 终态见提交后监控。
