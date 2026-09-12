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
| P5 规模化运营 | Week 13-16 | 🔄 计划中 | 账户农场编排 + 通知/自动化闭环 + 质量与协议韧性（见第 10 节） |

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
9. P5 推进（2026-09-12 定案，实施顺序 A → C → B）: ~~**方向 A 账户农场编排**~~（✅ 已完成：账户 CRUD + DesiredStateReconciler + 生命周期 + 聚合视图 + 账户过滤 + 审计/metrics + E2E 重平衡）→ ~~**方向 C 通知与自动化闭环**~~（✅ 已完成：webhook 通知 + 2FA 自动提交 + 周期任务 + 通知 metrics，建立在 A 的账户模型上）→ **方向 B 质量与协议韧性**（最后一个方向：replay tests 与覆盖率覆盖 A/C 落地后的代码面更划算；统计修正先行）。

---

## 10. P5 阶段：规模化运营（🔄 进行中：A ✅ / C ✅ / B 待实施，2026-09-12 定案）

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

### 10.3 方向 B：质量与协议韧性（第三个实施）

- [ ] 统计修正（可先行）：TESTING.md 测试统计刷新（当前停更于 268，实际 822）；P1/P2/横向过时标题修正（已完成）。
- [ ] SteamKit2 协议适配层（风险清单承诺）：提取 `ISteamTransport` 类隔离接口，SteamKit2 类型不外泄出 Core 内部，协议升级只动适配层。
- [ ] contract tests（风险清单承诺）：Steam Web API 响应用录制 JSON fixture（GetGameInfo/GetPrice/GetMarketListings/SearchGames）离线回放，防上游响应结构漂移导致解析静默失败。
- [ ] replay tests（风险清单承诺）：ControlPlane↔Agent WS 任务派发协议录制回放（消息序列快照测试）。
- [ ] 覆盖率 44.6% → 60%+：优先 Agent（25.1%：TaskExecutor/会话泵/WS 客户端分支）、Protocol（44.4%）；补齐后更新 CI 门禁与 TESTING.md 基线数字。

> 2026-09-11：全解决方案已从 net8.0 迁移到 net10.0（SDK 10.x，CI 同步）。
> 2026-09-11：MonitoringPlugin + Docker/compose + Prometheus/Grafana 可观测性栈落地；660 个测试全部通过。
> 2026-09-11：Agent 单元测试（41 个）+ E2E 测试（5 个，真实双进程闭环）落地；全解决方案 706 个测试通过。
> 2026-09-12：任务派发终态机制落地（`Vapor_TASK_MAX_DISPATCH_ATTEMPTS` / `Vapor_TASK_DISPATCH_RETRY_DELAY_MS`）：无 capable agent 的任务带延迟重试，达到上限后置 Failed 并记录 error（tasks 表新增 error / next_attempt_at_ms 列，自动迁移旧库）；修复 SubscribeAllEvents 测试线程池竞态 flake。
> 2026-09-12：覆盖率体系修复：7 个测试项目统一接入 coverlet.collector（此前 CI 的 codecov 上传指向不存在的文件，从未真正上报）；`run-tests.sh/.ps1` 覆盖率模式扩展到整个解决方案并在收集前清理历史残留报告；CI 新增 Ubuntu Release 覆盖率收集步骤并修正 Codecov glob 为 `**/TestResults/*/coverage.cobertura.xml`。首次取得真实全解决方案基线：**行覆盖约 44.6%**（ControlPlane 74.6%、Monitoring 89.3%、MobileAuthenticator 75.6%、Steam.Core 68.6%、Plugins.Core 67.0%、Protocol 44.4%、Agent 25.1%——Agent 实际另有 41 单测 + 5 E2E 集成覆盖，E2E 跑在子进程中测不到）。
> 2026-09-12：修复 PluginUnloadTests 的 ALC 回收断言在覆盖率运行下必失败的问题——coverlet instrumentation 持有被插桩 collectible 程序集的强引用导致 ALC 永不可回收（非产品缺陷）；测试现于检测到 coverlet 注入时跳过回收断言，非 coverage 运行仍完整验证（30 秒时间预算轮询）。
> 2026-09-12：任务 output 持久化落地：tasks 表新增 `output_json` 列（自动迁移旧库），`SetTaskResult` 同时持久化 output 与 error（此前两者均随会话丢弃）；`JobTask` 新增 `Output` 字段并出现在 REST `/v1/jobs/{id}` 响应中；E2E 恢复 output 断言，全链路（Agent 回报 → 存储 → REST）验证通过。
> 2026-09-12：P5 方向 A 账户农场编排完成（5 个提交）：`/v1/accounts` CRUD + `DesiredStateReconciler` 期望状态编排（周期对账/login 派发/指数退避冷静期/连续失败节流/agent 丢失重平衡/dry-run）+ enable/disable/remove 生命周期 + 聚合视图与列表过滤 + jobs/sessions 按账户过滤 + 编排审计与 metrics + E2E 重平衡测试（E2E 套件 5→6 个）。前置修复：agent 全量会话状态上报（此前普通状态变化不进 CP SessionTracker，编排器无数据可用）。ControlPlane 99 单测 + E2E 6 全过。
> 2026-09-12：10.2 通知系统落地：`INotificationSink` + `WebhookNotificationSink`（HMAC 签名 + 指数退避）+ `NotificationService` 三泵订阅 EventBroker（规则过滤 + 失败隔离，验证码永不外发）+ webhook 配置 5 参数 + 派发 metrics，15 个新测试。
> 2026-09-12：10.2 2FA 自动提交闭环落地：SteamTotp/SteamTimeSynchronizer 移入 Steam.Core、`ICredentialStore` 扩展加密 SharedSecret、`TwoFactorAutoResponder`（本地 TOTP 自动应答，60s/账户冷却，无 secret 回落人工 SSE）、插件 `save_shared_secret` action、agent 端 `AGENT_2FA_AUTO_SUBMIT` 显式开启 + Steam 时间同步循环；19 个测试迁移或新增（Steam.Core 558 / MobileAuthenticator 44 / Agent 41 全过）。
> 2026-09-12：10.2 周期任务落地：`POST /v1/jobs` 支持 `schedule`（interval ≥ 5s 或 5 字段 UTC cron/Cronos，missed=skip|run_once、overlap=skip|allow）创建模板 job，`RecurringJobScheduler` 每秒触发派生 job（`meta.scheduledFrom` + `parent_job_id`），停机补跑防风暴、cancel 即停、cron 用尽自动退役、`/metrics` 新增 `schedule_triggers_total{outcome}`；jobs 表自动迁移 4 列 + 2 索引。**方向 C（通知与自动化闭环）至此全部完成**（10.2 剩余 metrics 子项早已随方向 A 与通知提交落地）。
