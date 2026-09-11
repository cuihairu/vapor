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

## 2. P1 阶段：M2 核心能力闭环（~75% 完成）

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

## 3. P2 阶段：M5 安全闭环（~70% 完成）

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

## 4. P3 阶段：M4 数据与爬虫能力（~30% 完成）

### 4.1 Steam Web API 客户端产品化（✅ 完成）

- [x] `SteamWebHandler`：重试（指数退避，最多 5 次）、限流（1 req/s，可配置）、Cookie 管理。
- [x] 请求头规范（User-Agent、Accept、Referer、Origin）。
- [x] 429/5xx 退避策略增强（区分限流 vs 服务异常：429 尊重 Retry-After（有上限），5xx 指数退避）。
- [x] 统一 HTTP 中间件（`HttpCircuitBreaker` 熔断：Closed/Open/HalfOpen + 单探针；`WebRequestMetrics` 指标采集：成功率/429/5xx/网络失败/重试/熔断拒绝）。

### 4.2 数据模型与缓存（✅ 基本完成）

- [x] `GameInfo` / `ItemInfo` / `PriceOverview` / `GameSearchResult` 数据模型定义（含缓存 key 生成与 FetchedAt 新鲜度标记）。
- [x] 缓存层落地（`IVaporCache` 接口 + `MemoryVaporCache`：TTL、LRU 淘汰、hit/miss 统计、单飞行防击穿、可注入时钟）。
- [ ] 增量更新策略与缓存失效策略（数据 Action 接入后完善）。
- [ ] 可选 Redis 后端实现（接口已就绪）。

### 4.3 新动作（✅ 完成）

- [x] `GetGameInfoAction`（获取游戏详情，appdetails API，缓存接入）。
- [x] `SearchGamesAction`（搜索游戏，storesearch API，limit 1-50，缓存接入）。
- [x] `GetPriceAction`（获取价格信息，price_overview 提取，缓存接入）。
- [x] `GetMarketListingsAction`（获取市场列表，market search render API，分页 + 缓存接入）。
- [x] `SteamStoreApiClient`（三个数据源统一客户端）+ `MarketListing`/`MarketListingsPage` 模型。
- [x] 缓存策略：payload `cache_ttl_seconds` 覆盖（0 = 禁用），Agent 共享 4096 容量 / 10 分钟默认 TTL 缓存。

---

## 5. P4 阶段：M3 插件系统（~40% 进行中）

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

---

## 6. 横向工程化（~30% 完成，并行推进）

### 6.1 文档与接口

- [x] OpenAPI / Swagger 基础配置。
- [x] 架构文档（`docs/architecture.md`）。
- [x] 会话引擎文档（`docs/session-engine.md`）。
- [x] 测试文档（`tests/TESTING.md`）。
- [ ] OpenAPI 完整化（错误码、示例、鉴权说明）。
- [ ] 生产部署指南。
- [ ] 故障排查手册。

### 6.2 测试体系

- [x] Steam.Core 单测（15 个测试文件，289 个测试方法）。
- [x] 集成测试（SessionWorkflowTests）。
- [x] 性能测试（ConcurrencyTests）。
- [x] ControlPlane 单测（API / 审计 / 存储 / 调度，38 个测试）。
- [x] Agent 单元测试（`Vapor.Agent.Tests`，41 个测试：重连策略默认值/环境变量解析/指数退避封顶/重试上限、任务执行器凭证分支（password/pass 别名、refreshToken/snake_case、无凭证回退存储会话、取消与异常映射）、WebSocket URL 构建）。
- [x] E2E 测试（`Vapor.E2E.Tests`，5 个测试：spawn 真实 ControlPlane + Agent 进程 + 临时 SQLite/审计库，stub 会话模式隔离 Steam 依赖；覆盖 echo 任务全链路闭环（创建→调度→WS 派发→执行→结果落库）、未知 action 的调度行为（保持 queued + 持续重试 + 取消清理）、job.created 审计断言、未认证/agent key 越权拒绝、healthz）。
- [ ] 性能基准扩展（并发任务、SSE 连接数、队列吞吐）。

### 6.3 发布与运维

- [x] CI/CD 多平台构建（Ubuntu / Windows / macOS，Debug / Release）。
- [x] Codecov 覆盖率上报。
- [x] Docker 镜像（ControlPlane + Agent，含 Monitoring 插件、非 root 用户、CI 构建门禁）。
- [x] docker-compose 本地编排（含 observability profile：Prometheus + Grafana 自动 provisioning）。
- [ ] 自动发布流水线与回滚策略。
- [x] 可观测性：结构化日志（脱敏）、指标（MonitoringPlugin Prometheus 端点 + Grafana 面板 + compose observability profile）。
- [ ] 可观测性增强：追踪（OpenTelemetry）、告警规则。

---

## 7. 建议里程碑排期

| 阶段 | 周期 | 完成度 | 说明 |
|------|------|--------|------|
| P0 构建恢复 | Week 1 | ✅ 100% | 已完成 |
| P1 M2 核心能力 | Week 2-4 | ✅ 100% | 交易校验增强已完成 |
| P2 安全闭环 | Week 5-7 | ✅ 100% | 日志 Provider 级脱敏已完成 |
| P3 数据能力 | Week 6-9 | ✅ ~95% | 剩余：Redis 缓存后端（可选）、增量更新策略 |
| P4 插件系统 | Week 8-12 | ✅ 100% | 基础设施 + MobileAuthenticator + Monitoring 官方插件已完成 |
| GA 收口 | Week 10-12 | ⚠️ ~70% | Docker/compose/可观测性/Agent 单测/E2E 已就绪；剩余：发布流水线、部署/排障手册、OpenAPI 完整化 |

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
8. **横向**: 生产部署指南、故障排查手册、OpenAPI 完整化。

> 2026-09-11：全解决方案已从 net8.0 迁移到 net10.0（SDK 10.x，CI 同步）。
> 2026-09-11：MonitoringPlugin + Docker/compose + Prometheus/Grafana 可观测性栈落地；660 个测试全部通过。
> 2026-09-11：Agent 单元测试（41 个）+ E2E 测试（5 个，真实双进程闭环）落地；全解决方案 706 个测试通过。
