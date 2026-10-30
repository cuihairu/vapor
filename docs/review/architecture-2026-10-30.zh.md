# 架构实现核对 — 2026-10-30

本次基于当前检出代码进行静态分析，不代表重新运行测试、覆盖率或真实 Steam 验证。
[SVG 架构图](../assets/architecture/overview.zh.svg) 描绘当前实现；后续工作按原子验收项登记在
[`todo.md`](https://github.com/cuihairu/vapor/blob/main/todo.md) 的“架构核对”小节。

## 架构与状态所有权

Vapor 将编排与 Steam 执行分离。单个 ASP.NET Core 控制平面进程拥有 SQLite 存储，区域
Agent 主动连接控制平面。当前没有外部消息队列、跨实例锁、选主或可用的双活控制平面。
区域是调度属性；在线 Agent 注册表保存在内存，由重连重建，并非持久化区域 / Agent 数据库。

| 边界 | 代码依据 | 实际含义 |
|---|---|---|
| 组合入口 | `src/Vapor.ControlPlane/Program.cs` 服务注册 | Job、审计、配置、采集、脚本 / 编排、webhook 投递存储，以及后台调度和调和器 |
| 持久派发 | `TaskSchedulerService.DispatchOnce`；`SqliteJobStore` 的 `ClaimNextQueuedTask`、`HeartbeatTask`、`SetTaskResult` | SQLite 任务行是队列事实源；领取递增 attempt，心跳续租，旧结果被栅栏拒绝 |
| Agent 选择 | `AgentRegistry.Pick`、`HostTaskTarget`、`DesiredStateReconciler` | 区域 / 能力 / 负载选择；宿主动作直达 `agent:{id}`；声明式账号分配另有调和循环 |
| 任务隧道 | 两端 `Program.cs`；`Vapor.Protocol/Models.cs` | WebSocket 承载 hello、任务、取消、结果、心跳；连接由 Agent 发起 |
| 事件与人工认证 | Agent 的 `PublishSessionEventAsync`、`PollAuthChallengesAsync`；控制平面的会话 / 认证端点 | 会话事件经 HTTP POST 上报，人工验证码经 SSE 返回；并非所有通信都走 WS |
| 执行引擎 | Agent 的 `RunOnce`；`AgentTaskExecutor`；`BotSession.HandleExecuteAction` | 每个 Agent 串行等待派发任务；常驻账号会话可以共存；每个会话的动作也串行化 |
| 插件扩展 | Agent 的 `LoadPluginsAsync`；`src/Vapor.Plugins.Core/`；`HostActions/` | 可回收 ALC 隔离程序集加载，不隔离 CPU / 内存 / 进程故障；宿主动作绕过会话查找 |
| 凭据边界 | `FileCredentialStore`、`AgentTaskExecutor`、控制平面的 jobs/auth 端点 | Agent 本地令牌加密；API 提交的凭据载荷与人工验证码也会经过控制平面 |
| 可选服务 | Agent 缓存注册；`docker-compose.yml`；`NotificationService` | 默认内存缓存，可选 Redis、Prometheus/Grafana profile 与 webhook sink |

核心闭环为：`账号规格 → 调和器 → Job → Task → Agent → 会话 → Steam →
结果 / 事件 → 实际状态 → 再调和`。API、循环任务、脚本编排、数据采集与仓库归集最终都复用
任务模型，没有各自另建一套执行基础设施。

## 交付、恢复与一致性

调度器轮询 SQLite，领取任务后放进 Agent 的内存发送队列。Agent 实际开始执行时才启动
该任务的心跳；租约过期会重新派发。attempt 栅栏保护的是最终记录，无法撤销旧 attempt
已经产生的交易等外部副作用。已有 ActionSafety 分类与重派上限能降低风险，不提供恰好一次执行。

控制平面重启保留 SQLite，但丢失连接、挑战提示、SSE 订阅和运行时镜像。Agent 重启保留本地
凭据文件，不保留在途结果；没有持久结果发件箱。SSE 每订阅者缓冲 256 个事件，拥塞时丢弃
最旧事件，没有历史重放。webhook 投递日志也不等于源事件的持久发件箱。

## 确认的差异与缺口

1. **凭据边界（高优先级）。** `POST /v1/jobs` 将请求交给 `CreateJob`，
   `InsertTasksAsync` 未过滤秘密便将 payload 序列化进 `payload_json`。
   `AgentTaskExecutor` 支持从该载荷取密码、refresh/access token 与验证码。
   Agent 本地加密和审计脱敏不会加密这份控制平面任务载荷。因此，旧文档的“凭据从不进入控制平面”
   不适用于这条 API 链路。
2. **人工验证码的授权范围（高优先级）。** 认证 SSE 端点接受任意有效 Agent Key；
   可选账号过滤参数由调用者选择。Agent 收到带明文 code 的 `code_provided_*`，管理员收到
   `Code = null` 的投影。该端点没有把 Key 身份与账号所属 Agent 做服务端绑定。
   webhook 使用 `codeSupplied`，不能用这条规则概括 Agent 通道。
3. **队列压力与租约（高优先级）。** `ConnectedAgent._send` 是容量 1,024 的
   `DropOldest` 队列，入队成功不代表之前的任务 / 取消消息仍然保留。Agent 接收队列无界，
   任务消费串行。排在长任务后面的任务已被领取，却未启动心跳，可能在仍排队时过期重派。
4. **排队任务取消（高优先级）。** Agent 接收器只取消正在执行的 `(taskId, attempt)`，
   没有删除或作废已排队任务。仅取消数据库记录 / 拒绝旧结果，不能阻止旧派发随后触发外部动作。
5. **超时隔离（条件性后续）。** `CancelAfter` 发出协作取消；会话的 `WaitAsync`
   可约束调用方等待。不响应取消的插件仍可能占住会话动作锁，宿主动作则可能卡住 Agent 串行执行器。
   当前没有进程沙箱或强制终止线程；既有进程边界决策将中毒会话回收 / 配额延后到第三方插件需求出现。
6. **无消费者事件队列（维护缺口）。** `EventBroker` 向无界的 `_globalSessionChannel` /
   `_globalAuthChannel` 写每条事件，但类及接口没有读取路径。通知服务用另一套 `Subscribe*`
   通道，不能消耗这两条队列；它们会保留事件，包括带验证码的事件。
7. **文档偏差。** 架构前半段推荐外部 MQ / 双活，后半段已明确拒绝；中文首页 Markdown
   被压成长行。本轮修正文档表述、恢复首页结构，并接入双语 SVG。
8. **验证边界。** 背包 / 开箱 mock 验收已完成，真实 Steam 仍待操作者可用凭据。
   历史全绿测试数不能证明以上故障场景已经覆盖。

## 范围取舍

HA、外部 MQ、多租户、KMS、持久事件重放、插件进程沙箱属于未实现能力或已有明确非目标，
本轮不自动转成开发承诺。Redis 保持可选；真实账号验证独立保留未勾选项。
需要后续代码处理的内容已在根目录 TODO 拆成有验收条件的原子任务；本轮仅修改文档和 SVG 资源。
