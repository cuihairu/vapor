# Vapor TODO (ASF 对标 + 优雅架构版)

> 目标：在不牺牲 ASF 实用能力的前提下，形成更可扩展的"控制面 + 区域 Agent + 插件化动作"架构。

## 里程碑进度

| 里程碑 | 状态 | 完成度 |
|--------|------|--------|
| M0 - 契约与骨架 | ✅ 完成 | 100% |
| M1 - IPC 与配置体系 | ✅ 完成 | 100% |
| M2 - ASF 核心能力对齐 | 🚧 进行中 | ~30% |
| M3 - 插件与生态 | ❌ 未开始 | 0% |
| M4 - 爬虫与数据获取 | ❌ 未开始 | 0% |
| M5 - 账号安全增强 | ❌ 未开始 | 0% |

---

## M0 - 契约与骨架 ✅ 100%

### 已完成
- [x] `Vapor.Protocol/Actions.cs` - ActionDescriptor、ActionParamSchema
- [x] `Vapor.Protocol/Models.cs` - Job/Task/Result 模型
- [x] `Vapor.Protocol/Events.cs` - JobEvent/TaskEvent/SessionEvent/AuthEvent
- [x] `Vapor.Protocol/Commands.cs` - CommandRequest/CommandResult/PermissionLevel
- [x] `Vapor.Protocol/Configs.cs` - GlobalConfig/AccountConfig
- [x] `Vapor.Steam.Core/ActionRegistry.cs` - 动作注册/发现
- [x] `Vapor.Steam.Core/IAction.cs` - 动作接口 + 元数据
- [x] `Vapor.Steam.Core/BotSession.cs` - 会话状态机
- [x] `Vapor.Steam.Core/SessionManager.cs` - 会话池管理
- [x] `Vapor.Agent/ActionDispatcher.cs` - Task → Action 执行
- [x] `Vapor.Agent/TaskRunner.cs` - 幂等/重试/超时/心跳
- [x] `Vapor.ControlPlane/` - REST API + WebSocket 隧道

---

## M1 - IPC 与配置体系 ✅ 100%

### 已完成的 API 端点
- [x] `GET /v1/agents` - 列出 Agent
- [x] `GET /v1/agents/status` - Agent 状态
- [x] `WS /v1/agent/ws` - Agent WebSocket 隧道
- [x] `GET /v1/config` - 获取配置
- [x] `PUT /v1/config/global` - 更新全局配置
- [x] `PUT /v1/config/account/{name}` - 更新账号配置
- [x] `POST /v1/jobs` - 创建 Job
- [x] `GET /v1/jobs` - 列出 Jobs
- [x] `GET /v1/jobs/{id}` - 获取 Job 详情
- [x] `POST /v1/jobs/{id}/cancel` - 取消 Job
- [x] `GET /v1/jobs/{id}/events` - Job 事件流 (SSE)
- [x] `GET /v1/jobs/events` - 全局 Job 事件流
- [x] `GET /v1/sessions` - 列出会话
- [x] `GET /v1/sessions/events` - 会话事件流
- [x] `GET /v1/auth/challenges` - 认证挑战列表
- [x] `GET /v1/auth/challenges/events` - 认证挑战事件流
- [x] `POST /v1/auth/challenges/{account}/code` - 提交验证码

### 已完成功能
- [x] `ConfigStore.cs` - 全局/账号配置存储
- [x] `SqliteJobStore.cs` - Job/Task 持久化
- [x] `EventBroker.cs` - 事件发布订阅
- [x] `SessionTracker.cs` - 会话状态追踪
- [x] `AuthChallengeTracker.cs` - 认证挑战追踪
- [x] `admin.html` - 完整的管理 UI

---

## M2 - ASF 核心能力对齐 🚧 30%

### 已完成
- [x] 登录流程 (`LoginAction`)
- [x] 邮箱验证码处理 (`SessionState.ConnectingWaitAuthCode`)
- [x] TOTP 验证码处理 (`SessionState.ConnectingWait2FA`)
- [x] RefreshToken/AccessToken 字段
- [x] 会话重连 (`SessionState.Reconnecting`)

### 待实现 Actions

#### 2FA 相关
- [ ] `GenerateAuthCodeAction` - 生成 TOTP 代码（需 SDA 支持）
- [ ] `GetConfirmationsAction` - 获取待确认列表
- [ ] `HandleConfirmationsAction` - 处理交易确认

#### 交易系统 (Trading)
- [ ] `GetInventoryAction` - 获取库存物品
  - 参考: `ArchiHandler.GetMyInventoryAsync()`
- [ ] `SendTradeOfferAction` - 发送交易报价
  - 参考: `Actions.SendInventory()`
- [ ] `AcceptTradeOfferAction` - 接受交易报价
  - 参考: `ArchiWebHandler.AcceptTradeOffer()`
- [ ] `DeclineTradeOfferAction` - 拒绝交易报价
- [ ] `CancelTradeOfferAction` - 取消交易报价

#### 游戏管理
- [ ] `PlayGamesAction` - 设置游戏状态
  - 参考: `Actions.Play()`
- [ ] `RedeemKeyAction` - 完整实现（当前仅为 stub）
  - 参考: `Actions.RedeemKey()` + `ArchiHandler.RedeemKey()`
- [ ] `AddFreeLicenseAction` - 添加免费许可
  - 参考: `Actions.AddFreeLicenseApp()`
- [ ] `RemoveLicenseAction` - 移除许可
- [ ] `UnpackBoosterPacksAction` - 开启补充包
- [ ] `RedeemPointsAction` - 兑换积分

#### Farming 核心
- [ ] `CardsFarmer` - 卡牌挂机核心算法
  - 参考: `CardsFarmer.cs` (~1400 行)
  - 支持离线 farming
  - 游戏优先级队列
  - 时长控制
  - 风险游戏黑名单
- [ ] `PauseFarmingAction` - 暂停挂机
- [ ] `ResumeFarmingAction` - 恢复挂机
- [ ] `FarmingStatusAction` - 获取挂机状态

### 待实现基础设施
- [ ] `SteamWebHandler` - Steam Web API 请求封装
  - 参考: `ArchiWebHandler.cs`
  - Cookie 管理
  - Session 管理
  - 请求限速
- [ ] `ArchiHandler` - Steam 协议处理器
  - 参考: `ArchiHandler.cs`
  - PlayGames
  - RedeemKey
  - TradeOffers

---

## M3 - 插件与生态 ❌ 0%

### 插件系统
- [ ] `Vapor.Plugins.Core` - 插件加载器
  - 参考: `PluginsCore.cs` (使用 MEF/System.Composition)
  - 支持热加载/卸载
  - 插件发现（目录扫描）
  - 依赖管理
- [ ] 插件 API 接口
  - `IPlugin` - 插件基础接口
  - `IAction` - 自定义动作
  - `ICommand` - 自定义命令
  - `IWebApi` - 自定义 API 端点
- [ ] 插件配置管理
- [ ] 插件沙箱隔离

### 官方插件
- [ ] `MobileAuthenticatorPlugin` - Steam 手机验证器
  - 参考: `MobileAuthenticator.cs`
  - TOTP 代码生成
  - 确认码生成
  - Steam 时间同步
- [ ] `ItemsMatcherPlugin` - 物品匹配交易
  - 参考: ASF ItemsMatcher
- [ ] `MonitoringPlugin` - 性能监控
  - Grafana 集成
  - 指标导出

---

## M4 - 爬虫与数据获取 ❌ 0%

### Steam Web API
- [ ] `SteamStoreApi` - 商店 API
  - `GET /api/appdetails` - 游戏详情
  - `GET /api/featuredcategories` - 特惠商品
  - `GET /api/packagedetails` - 包详情
- [ ] `SteamCommunityApi` - 社区 API
  - 市场价格
  - 物品详情
- [ ] `SteamPICS` - PICS 协议
  - 参考: `SteamPICSChanges.cs`
  - 变化监听
  - 产品信息获取

### 数据模型
- [ ] `GameInfo` - 游戏信息模型
  - AppID, 名称, 类型, 开发商
  - 价格, 折扣, 标签
- [ ] `ItemInfo` - 物品信息模型
  - AppID, ContextID, AssetID
  - 名称, 类型, 图片, 价格
- [ ] `GameInfoCache` - 游戏信息缓存

### 新增 Actions
- [ ] `GetGameInfoAction` - 获取游戏详情
- [ ] `SearchGamesAction` - 搜索游戏
- [ ] `GetPriceAction` - 获取价格
- [ ] `GetMarketListingsAction` - 获取市场列表

### 依赖
- [ ] HttpClient 封装
- [ ] HTML 解析器集成 (AngleSharp)
- [ ] 速率限制器

---

## M5 - 账号安全增强 ❌ 0%

### 密码存储加密
- [ ] `VaporCryptoHelper` - 加密工具类
  - 参考: `ArchiCryptoHelper.cs`
  - 支持: PlainText, AES, EnvironmentVariable, File
  - AES-256-GCM 实现
- [ ] `ECryptoMethod` 枚举
  - PlainText - 开发测试
  - AES - 推荐
  - EnvironmentVariable - 密码从环境变量读取
  - File - 密码从外部文件读取
- [ ] 配置文件加密支持
  ```json
  {
    "accounts": {
      "bot1": {
        "password": "aes:encrypted_base64_here",
        "passwordFormat": "AES"
      }
    }
  }
  ```

### 凭证持久化
- [ ] `ICredentialStore` - 凭证存储接口
  - SaveRefreshTokenAsync
  - SaveAccessTokenAsync
  - GetCredentialsAsync
  - RevokeCredentialsAsync
- [ ] `FileCredentialStore` - 文件实现
- [ ] `SecureCredentialStore` - 安全存储实现

### Mobile Authenticator (SDA)
- [ ] `MobileAuthenticator` 核心类
  - 参考: `MobileAuthenticator.cs`
  - `GenerateToken()` - TOTP 代码生成
  - `GenerateConfirmationHash()` - 确认哈希
  - `GetConfirmations()` - 获取待确认
  - `HandleConfirmations()` - 处理确认
- [ ] `MobileAuthenticatorConfig` - 配置模型
  - SharedSecret
  - IdentitySecret
  - DeviceId

### 会话管理增强
- [ ] AccessToken 自动刷新
- [ ] RefreshToken 续期
- [ ] 会话恢复（重启后无需登录）

### 安全最佳实践
- [ ] 日志脱敏（不打印密码/令牌）
- [ ] 配置文件权限检查 (600)
- [ ] 审计日志（认证操作记录）
- [ ] 速率限制（防暴力破解）

---

## 游戏特定支持 (Dota2/CSGO)

### Dota2
- [ ] Dota2 API 集成
- [ ] 战绩查询
- [ ] 英雄数据

### CSGO
- [ ] CSGO API 集成
- [ ] 库存物品操作
- [ ] 战绩查询

---

## 实施优先级

### P0 - 立即开始
1. M5: 环境变量密码支持
2. M5: RefreshToken 持久化
3. M2: RedeemKeyAction 完整实现

### P1 - 短期
1. M2: SteamWebHandler
2. M2: Trading 系统
3. M5: AES 密码加密

### P2 - 中期
1. M2: CardsFarming 核心
2. M4: Steam Web API 集成
3. M5: Mobile Authenticator

### P3 - 长期
1. M3: 插件系统
2. M4: 游戏特定支持
3. M3: 官方插件

---

## 技术债务

- [ ] 完善 API 文档 (OpenAPI)
- [ ] 增加 E2E 测试
- [ ] Docker 镜像构建
- [ ] CI/CD 优化
- [ ] 性能基准测试

---

## 参考资料

- ArchiSteamFarm: https://github.com/JustArchiNET/ArchiSteamFarm
- SteamKit2: https://github.com/SteamRE/SteamKit2
- SteamDesktopAuthenticator: https://github.com/Jessecar96/SteamDesktopAuthenticator
