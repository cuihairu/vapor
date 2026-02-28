# Vapor TODO (ASF 对标 + 优雅架构版)

> 目标：在不牺牲 ASF 实用能力的前提下，形成更可扩展的“控制面 + 区域 Agent + 插件化动作”架构。

## 里程碑

### M0 - 契约与骨架（可编译、可跑最小闭环）
- Protocol：动作/事件/命令/配置 DTO
- Steam.Core：ActionRegistry + 基础 Session 管理
- Agent：TaskRunner + ActionDispatcher（stub）
- ControlPlane：/v1/actions, /v1/events（只读）

### M1 - IPC 与配置体系（可管理/可配置/可观测）
- ControlPlane：/v1/config, /v1/commands
- ConfigStore（全局/账号）
- CommandRouter（命令 → Job）
- 管理 UI：Actions 列表 + 命令执行 + 配置管理

### M2 - ASF 核心能力对齐（可用性）
- 2FA 流程
- BGR（批量 key 兑换）
- 交易系统
- Farming 核心算法

### M3 - 插件与生态（可扩展）
- 插件加载器
- 插件 API（动作/命令/UI）
- Monitoring 插件

---

## A. Vapor.Protocol（跨服务契约）
- 新增 `src/Vapor.Protocol/Actions.cs`
  - `ActionDescriptor`（name/summary/params/permissions/tags）
  - `ActionParamSchema`（name/type/required/description）
- 新增 `src/Vapor.Protocol/Configs.cs`
  - `GlobalConfig`, `AccountConfig`, `ConfigVersion`
- 新增 `src/Vapor.Protocol/Events.cs`
  - `JobEvent`, `TaskEvent`, `SessionEvent`, `AuthEvent`, `PluginEvent`
- 新增 `src/Vapor.Protocol/Commands.cs`
  - `CommandRequest`, `CommandResult`, `PermissionLevel`
- 更新 `src/Vapor.Protocol/Models.cs`
  - 补齐 `Job/Task/Result` 字段（重试次数、幂等键、取消原因、租约）

## B. Vapor.Steam.Core（会话与动作核心）
- 新增 `src/Vapor.Steam.Core/Actions/ActionRegistry.cs`
  - 支持注册/发现/描述动作
- 更新 `src/Vapor.Steam.Core/IAction.cs`
  - 增加 `Metadata()` 或 `Descriptor` 输出
- 新增 `src/Vapor.Steam.Core/ActionPolicy.cs`
  - 超时/重试/并发限制/速率限制策略
- 新增 `src/Vapor.Steam.Core/Session/AccountSession.cs`
  - 状态机（Connecting/LoggedIn/Waiting2FA…）
- 更新 `src/Vapor.Steam.Core/SessionManager.cs`
  - 会话池 + 并发限制 + 生命周期管理
- 新增 `src/Vapor.Steam.Core/Session/SessionStore.cs`
  - 会话凭据缓存（token/2FA state）

## C. Vapor.Agent（执行面）
- 新增 `src/Vapor.Agent/ActionDispatcher.cs`
  - 将 Task → Action 执行
- 新增 `src/Vapor.Agent/TaskRunner.cs`
  - 幂等、重试、超时、心跳
- 更新 `src/Vapor.Agent/Program.cs`
  - 注册 ActionRegistry + ActionPolicy
  - 与 ControlPlane 建立 WS/gRPC stream 时同步 actions 支持列表

## D. Vapor.ControlPlane（控制面）
- 新增 `src/Vapor.ControlPlane/ActionCatalog.cs`
  - 从 Agent 上报的 actions 做聚合
- 新增 `src/Vapor.ControlPlane/ConfigStore.cs`
  - 存储 `GlobalConfig` + `AccountConfig`
- 新增 `src/Vapor.ControlPlane/CommandRouter.cs`
  - IPC 命令入口 → Job 调度
- 更新 `src/Vapor.ControlPlane/Program.cs`
  - API endpoints:
    - `GET /v1/actions`
    - `GET /v1/config`
    - `PUT /v1/config/global`
    - `PUT /v1/config/account/{name}`
    - `POST /v1/commands`
    - `GET /v1/events`
- 新增 `src/Vapor.ControlPlane/EventStore.cs`
  - 可选持久化（短期内可内存）

## E. Web UI（ControlPlane/wwwroot）
- 更新 `src/Vapor.ControlPlane/wwwroot/admin.html`
  - 新增 Actions 列表
  - 新增 命令执行 界面
  - 新增 配置管理 页面

## F. Docs
- 更新 `docs/architecture.md`
  - 写入新的模块/事件/动作模型
- 新增 `docs/ipc.md`
  - 记录 API 端点
- 更新 `docs/running.md`
  - 配置/命令示例

## 建议实施顺序
1) Protocol（DTO+契约）
2) Steam.Core（ActionRegistry + SessionEngine）
3) Agent（ActionDispatcher + TaskRunner）
4) ControlPlane（API + Config + Actions）
5) UI & Docs
