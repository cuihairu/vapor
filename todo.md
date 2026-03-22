# Vapor 完成交付计划（从当前 Alpha 到可用 GA）

> 目标：把当前"骨架完整、能力未闭环"的状态推进到"可稳定运行、可运维、可扩展"的生产可用版本。

## 0. 现状与完成定义

### 当前状态快照（2025-03-11）

- **构建**: `dotnet build` 通过，0 错误 0 警告。
- **测试**: 289 个测试全部通过（15 个测试文件，覆盖 Unit / Integration / Performance）。
- **CI**: 多平台（Ubuntu / Windows / macOS）构建 + 测试门禁已就绪。
- **已实现 Actions（11 个）**: Ping, Echo, Login, Idle, PlayGames, RedeemKey, GetInventory, SendTradeOffer, AcceptTradeOffer, DeclineTradeOffer, CancelTradeOffer。
- **已实现基础设施**: ControlPlane（15+ API 端点 + SSE 事件流 + SQLite 持久化 + Admin UI）、Agent（WebSocket 隧道 + 全部 Action 注册）、SessionEngine（BotSession 状态机 + SessionManager + SteamClientManager）、SteamWebHandler、SteamTradeClient、FileCredentialStore + AES-256-CBC 加密。
- **整体完成度**: ~45%。

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
- [ ] 解析 Steam 回包中 app/package/receipt 明细，丰富输出结构。
- [ ] 增加可观测字段（请求 ID、耗时、结果码）。
- [ ] 增加重试策略（瞬时失败自动重试）。

### 2.3 Trading MVP ✅ 已完成

- [x] `GetInventoryAction`（支持分页，50k+ 物品）。
- [x] `SendTradeOfferAction`（Trade URL 解析、物品资产解析）。
- [x] `AcceptTradeOfferAction` / `DeclineTradeOfferAction` / `CancelTradeOfferAction`。
- [x] `SteamTradeClient` 完整实现（库存、报价 CRUD、IEconService API）。
- [x] `TradeModels` 数据模型（InventoryItem、TradeOffer、TradeOfferState 等）。
- [ ] 交易流程校验增强（资产归属验证、报价状态机、风控节流）。

### 2.4 会话可靠性增强（⚠️ 部分完成）

- [x] Token 持久化（FileCredentialStore 存储 RefreshToken / AccessToken）。
- [x] Agent 断线重连（指数退避 500ms → 10s）。
- [ ] AccessToken 自动刷新（过期前主动续期）。
- [ ] RefreshToken 续期策略。
- [ ] 断线重连策略可配置化（退避上限、最大重试次数）。
- [ ] 进程重启后会话恢复（从持久化 Token 自动重建会话）。

### 2.5 登录流程测试补全

- [ ] 登录成功/失败/需验证码/需 2FA 的单测。
- [ ] 登录流程集成测试（模拟 Steam 响应）。

---

## 3. P2 阶段：M5 安全闭环（~50% 完成）

### 3.1 凭证体系（⚠️ 部分完成）

- [x] `ICredentialStore` 接口定义（Save/Get RefreshToken、AccessToken、Revoke、HasCredentials）。
- [x] `FileCredentialStore` 生产级实现（`~/.vapor/credentials.json`，SemaphoreSlim 并发安全，懒加载）。
- [x] 支持多密码来源：明文、AES、环境变量、文件（`VaporCryptoHelper`）。
- [ ] 凭证文件损坏恢复（备份 + 回滚）。
- [ ] 凭证版本化与迁移策略（老格式 → 新格式）。

### 3.2 加密与密钥管理（⚠️ 部分完成）

- [x] AES-256-CBC 加密实现（随机 IV、Base64 编码）。
- [x] 自定义密钥支持（`SetEncryptionKey()`，一次性设置）。
- [ ] 升级为 AES-GCM + 随机 nonce + 完整性校验。
- [ ] 禁止默认密钥 "Vapor" 用于生产（启动检查 + 告警）。
- [ ] 引入主密钥配置规范（环境变量 / KMS）。
- [ ] 密钥轮换工具脚本。

### 3.3 安全审计与脱敏（⚠️ 部分完成）

- [x] RedeemKey / SteamClientManager 中的 Key masking。
- [ ] 全链路日志脱敏（密码、令牌、验证码、key 统一拦截）。
- [ ] 审计日志（登录、验证码提交、交易、关键配置修改）。
- [ ] 配置文件权限检查与启动告警。

---

## 4. P3 阶段：M4 数据与爬虫能力（~30% 完成）

### 4.1 Steam Web API 客户端产品化（✅ 基本完成）

- [x] `SteamWebHandler`：重试（指数退避，最多 5 次）、限流（1 req/s）、Cookie 管理。
- [x] 请求头规范（User-Agent、Accept、Referer、Origin）。
- [ ] 429/5xx 退避策略增强（区分限流 vs 服务异常）。
- [ ] 统一 HTTP 中间件：熔断、指标采集。

### 4.2 数据模型与缓存

- [ ] `GameInfo` / `ItemInfo` 数据模型定义。
- [ ] 缓存层落地（内存缓存 + 可选 Redis）。
- [ ] 增量更新策略与缓存失效策略。

### 4.3 新动作

- [ ] `GetGameInfoAction`（获取游戏详情）。
- [ ] `SearchGamesAction`（搜索游戏）。
- [ ] `GetPriceAction`（获取价格信息）。
- [ ] `GetMarketListingsAction`（获取市场列表）。

---

## 5. P4 阶段：M3 插件系统（0% 未开始）

### 5.1 插件基础设施

- [ ] `Vapor.Plugins.Core`（发现、加载、隔离、卸载）。
- [ ] 插件 API：`IPlugin` / 自定义 `IAction` / `ICommand` / `IWebApi`。
- [ ] 版本兼容策略（插件 API SemVer）。

### 5.2 官方插件首批

- [ ] MobileAuthenticatorPlugin（TOTP、确认哈希、时间同步）。
- [ ] MonitoringPlugin（指标导出、Grafana 面板模板）。

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
- [ ] ControlPlane 单测与集成测试。
- [ ] Agent 单测与集成测试。
- [ ] E2E 测试（控制面 + Agent + SQLite + 模拟 Steam 依赖）。
- [ ] 性能基准扩展（并发任务、SSE 连接数、队列吞吐）。

### 6.3 发布与运维

- [x] CI/CD 多平台构建（Ubuntu / Windows / macOS，Debug / Release）。
- [x] Codecov 覆盖率上报。
- [ ] Docker 镜像（ControlPlane + Agent）。
- [ ] docker-compose 本地编排。
- [ ] 自动发布流水线与回滚策略。
- [ ] 可观测性：结构化日志、指标（Prometheus）、追踪（OpenTelemetry）、告警规则。

---

## 7. 建议里程碑排期

| 阶段 | 周期 | 完成度 | 说明 |
|------|------|--------|------|
| P0 构建恢复 | Week 1 | ✅ 100% | 已完成 |
| P1 M2 核心能力 | Week 2-4 | ⚠️ ~75% | 剩余：Token 刷新、会话恢复、RedeemKey 明细、登录测试 |
| P2 安全闭环 | Week 5-7 | ⚠️ ~50% | 剩余：AES-GCM 升级、审计日志、全链路脱敏、密钥轮换 |
| P3 数据能力 | Week 6-9 | ⚠️ ~30% | 剩余：数据模型、缓存层、4 个新 Action |
| P4 插件系统 | Week 8-12 | ❌ 0% | 未开始 |
| GA 收口 | Week 10-12 | ⚠️ ~30% | 剩余：Docker、E2E、性能基准、文档 |

---

## 8. 风险清单与缓解

| 风险 | 缓解措施 |
|------|----------|
| Steam 协议/接口变化导致行为不稳定 | 建立协议适配层，隔离 SteamKit2 变化 |
| 交易与认证流程边界复杂，回归成本高 | 高风险流程先做 contract tests + replay tests |
| 安全改造（加密/密钥管理）易引入兼容问题 | 灰度开关与数据迁移脚本，逐步切换 |

---

## 9. 下一步优先事项

1. **P1 补完**: AccessToken 自动刷新 + 进程重启会话恢复。
2. **P1 补完**: RedeemKey 回包明细解析 + 可观测字段。
3. **P1 补完**: 登录流程单测与集成测试。
4. **P2 推进**: AES-GCM 升级 + 禁止默认密钥。
5. **P2 推进**: 全链路日志脱敏 + 审计日志。
