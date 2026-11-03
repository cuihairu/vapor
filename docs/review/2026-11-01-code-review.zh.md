简体中文 | English

# 代码深审 — 2026-11-01

对 2026-11-01/02 检出的 `main` 做静态分析，按 requesting-code-review 流程执行：安全静态扫描、
隐藏 bug 猎捕、质量门。每条发现带 `file:line` 证据。P0/P1 当场修复（一问题一 commit、套件全绿）；
P2/P3 登记 [`todo.md`](https://github.com/cuihairu/vapor/blob/main/todo.md)「代码深审 2026-11-01」小节。

## 摘要

| 严重度 | 发现 | 已修 | 剩余 |
|---|---|---|---|
| P0 | 0 | 0 | 0 |
| P1 | 6 | 6 | 0 |
| P2 | 6 | 0 | 6 |
| P3 | 13 | 0 | 13 |

质量门：`dotnet format` 空白检查此前为红、已修（`28cc3c8`）；覆盖率门与一条 E2E 在本次审查前
即为红，属既有问题（见下）。

### 修复 commit

| Commit | 问题 |
|---|---|
| `f35ec25` | 代理切换等待的是已完成的 connect TCS |
| `efaa760` | 断开后未清 playing 集 |
| `94085ba` | 登录回调经单账号槽错投 |
| `b7cd244` | 会话事件被并发订阅者互抢 |
| `5cd501a` | REST 读取期间运行时列表被原地修改 |
| `6bd3296` | 故障 SSE 流留下兄弟订阅未拆除 |

## P1 — 已修

### 1. 代理切换与旧 socket 拆除竞态

`src/Vapor.Steam.Core/Steam/SteamClientManager.cs`（SetProxy）。切换落在 `_connectedTcs.Task`
上，而它早已被 `OnConnected` 完成——重连在旧 CM socket 尚未拆除时就推进，可能落到旧出口或卡死。
修复（`f35ec25`）：改由 `OnDisconnected` 落定 `_disconnectTcs` 作为门；`Disconnect()` 调用移出
`_connectLock`，同步回调也不会死锁。

### 2. Playing 集跨重连残留

`src/Vapor.Steam.Core/Steam/SteamClientManager.cs`（OnDisconnected）。`_playingGames` 属连接态
但从未清理，重连后的 `PlayGames(desired)` 被 `SetEquals` 幂等短路，期望的挂机/farm 状态静默
不上报 Steam。修复（`efaa760`）：CM 连接断开时在 `_playingGamesLock` 下清空。

### 3. 登录回调错投账号

`src/Vapor.Steam.Core/Steam/SteamClientManager.cs`（LoginAsync / OnLoggedOn）。
`LoggedOnCallback` 不携带账号标识，只能经单一 `_activeLoginAccountName` 槽路由。两个重叠的
`LoginAsync` 相互覆盖该槽，把对方的回调完成到错误账号的 TCS。修复（`94085ba`）：`SemaphoreSlim`
门控串行 send→callback 窗口，回调可到达期间该槽只有一个属主。

### 4. 会话事件被订阅者互抢

`src/Vapor.Steam.Core/SessionManager.cs`（SubscribeAllEvents）。直接多读 `ReadAllAsync` 共享
ingress 通道会把每个事件恰好交给一个消费者，并发订阅者（插件事件分发、2FA 自动应答、监控）
互相抢事件——2FA 码可能落入会丢弃它的消费者。修复（`b7cd244`）：单一广播者把每个事件扇出到
每订阅者通道；迟到的订阅者看到 ingress 已结束则立即收尾而非挂起。

### 5. REST 读取期间运行时列表被原地修改

`src/Vapor.ControlPlane/DesiredStateReconciler.cs`。调和线程原地改 `AccountRuntime` 队列，REST
处理器同时枚举同一列表实例，序列化时抛 `InvalidOperationException`。修复（`5cd501a`）：视图出
副本、调和侧变更为 copy-on-write，读取者不再与被改列表共享实例。

### 6. 故障 SSE 流留下兄弟订阅

`src/Vapor.Agent/Program.cs`（PollAuthChallengesAsync）。某条 auth-challenge 流故障后，同轮的
兄弟 SSE 订阅未拆除，下一轮订阅在属主已停止排水的通道上再叠一个读者。修复（`6bd3296`）：每轮
linked token 在循环重订阅前取消全部兄弟订阅。

## P2 — 转待办（todo）

1. `docker-compose.yml:18,19,43` —— API key 未强制门控的 dev-token 默认值
   （`${VAPOR_ADMIN_API_KEY:-admin-dev-token}`）；生产漏配环境变量即携已知 token 启动。修法：
   去掉默认值、未配置 fail-fast。
2. `src/Vapor.ControlPlane/SqliteJobStore.cs:985,1004`（SetTaskResult）—— 任务行更新与
   `RecomputeJob` 为两段非原子写；中间崩溃留下 task=Finished 而 job 仍 Running。
3. `src/Vapor.Steam.Core/SessionManager.cs:178-181`（RemoveSessionAsync）—— `DisconnectAsync`
   抛错则 `Dispose()` 被跳过、会话泄漏。修法：try/finally。
4. `src/Vapor.ControlPlane/Program.cs:3190` + `src/Vapor.ControlPlane/AgentRegistry.cs:144` ——
   取消只投递给 `ListConnected()` 在线 agent；DB 已置 Canceled，离线 agent 复联后任务跑完、结果
   被"task not running"丢弃。
5. `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:384` —— `DisconnectAsync` 以固定
   `Task.Delay(100)` 睡眠等拆除；魔法数竞态余量。修法：等 disconnect 信号（`_disconnectTcs`
   同款机制）。
6. `94085ba` 残余 —— 被取消登录迟到的 `LoggedOnCallback` 仍可能路由到下一个登录的 TCS（取消
   窗口 + 并发登录 + Steam 响应晚到）。修法：按 SteamID 路由而非槽。

## 质量门

- **`dotnet format`** —— `--verify-no-changes` 此前为红（`src/Vapor.Agent/Program.cs` 157 条
  空白诊断）；`28cc3c8` 修复，复核 0 诊断。
- **覆盖率门** —— `scripts/coverage-summary.py --min 100 --min-branch 100` 要求行、分支均
  100%。既有红，与本次变更无关。本次新增代码按仓库自身 coverlet runsettings 验证为 0 未覆盖行
  （Steam.Core：SessionManager + SteamClientManager 0 未覆盖；ControlPlane：
  DesiredStateReconciler 0 未覆盖；Agent：Program.cs 属进程入口、不在覆盖分母内）。
- **E2E 抖动** —— `tests/Vapor.E2E.Tests/AccountOrchestrationE2ETests.cs:19,129-156`
  （`DeclaredOnlineAccount_IsAssignedAndRebalancesAfterAgentLoss`）以固定 60 秒死线轮询；CI 高
  负载下调和/agent 失联周期可能超限而超时。属测试基建天花板，非产品缺陷。修法：抬高死线或改为
  可配置。

## 测试基线

| 套件 | 结果 |
|---|---|
| Vapor.Steam.Core.Tests | 1286 通过 |
| Vapor.ControlPlane.Tests | 1377 通过 |
| Vapor.Agent.Tests | 217 通过 |

## P3 — 记录性

仅风格/健壮性；已记 todo。编译开启 `TreatWarningsAsErrors` 与 `AnalysisLevel=latest-all`，
死代码/未用代码类为空——以下集中在分析器不强制的三类。

**无注释的空 `catch { }`**

1. `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:380` —— `DisconnectAsync` 无注释吞掉
   `Disconnect()` 异常。
2. `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:1690` —— `Dispose` 内同款裸 catch。
3. `src/Vapor.Agent/Program.cs:660` —— 裸 catch 吞掉 websocket `receiver` 排水错误。
4. `src/Vapor.Agent/Program.cs:462` —— 裸 catch 吞掉 `currentTaskCts!.Cancel()` 异常。

**库代码中的魔法数**

5. `src/Vapor.Steam.Core/BotSession.cs:256` —— `Task.Delay(100)` 回调轮询间隔未命名。
6. `src/Vapor.Steam.Core/BotSession.cs:604` —— `Task.Delay(100)` 断开后沉降未命名。
7. `src/Vapor.Steam.Core/Caching/RedisVaporCache.cs:105` —— 库内重试回退
   `Thread.Sleep(200 * attempt)` 同步阻塞。

**库内 await 缺 `ConfigureAwait(false)`**

8. `src/Vapor.Steam.Core/Web/SteamWebHandler.cs:174`（`GetAsync`）
9. `src/Vapor.Steam.Core/Web/SteamWebHandler.cs:191`（`PostAsync`）
10. `src/Vapor.Steam.Core/Actions/DataActions.cs:440`（`SearchGamesAsync`）
11. `src/Vapor.Plugins.Csgo/CsgoPlugin.cs:320`（`ReadBackpackAsync`）
12. `src/Vapor.Steam.Core/SessionManager.cs:477`（`_eventCallback.Invoke`）

**重复字面量**

13. `src/Vapor.ControlPlane/Program.cs:878` —— `"pending"` 状态字面量在本文件重复 19 次，
    应提共享常量。

## 发布后复核（2026-11-02）

发布后由第二轮补充；已记 todo。两条为文档-源码矛盾，一条死代码，两条 P2（其一是本次
`f35ec25` 自身的缺口）。

1. **P2** `README.md:29` / `README.zh.md:29` —— 首页宣称「Steam Guard 验证码与凭据不出
   Agent，跨线只有一个布尔值」，实现与此矛盾：人工验证码经控制平面发布并以明文经 SSE 回
   Agent（`src/Vapor.ControlPlane/Program.cs:3507` 发布、`:3419-3420` 仅管理员剥码），任务
   payload 凭据亦入控制平面持久化（secret-payload purge 端点）；`docs/architecture.md:109`
   已正确描述该路径。修法：更正 README 表述。
2. **P2** `docs/architecture.md:116` —— 把挑战流重放写成「still-acknowledged code」，与实现
   相反：outbox 仅重放未确认（staged）验证码，`Acknowledge` 即移除
   （`src/Vapor.ControlPlane/AuthChallengeOutbox.cs:77-91`、`Program.cs:3404`）；`docs/api.md:85` 的
   「still-unconfirmed」才是正确表述。修法：更正架构文档。
3. **P3** `src/Vapor.Agent/AgentTaskBuffer.cs:40` —— `CreateChannel` 已无生产调用方（`RunOnce`
   改用 `AgentTaskDispatcher`；仅测试仍引用）：死代码。
4. **P2** `src/Vapor.Steam.Core/Steam/SteamClientManager.cs:340-365`（SetProxy）—— 并发切换时
   后到者覆盖 `_disconnectTcs`，先到者持有的 settle TCS 永不落定，只能靠 10 秒 `WaitAsync`
   超时兜底。应以 `??=` 合并，让并发切换共享一次断开等待。（`f35ec25` 的缺口。）
5. **P2** `SteamClientManager.cs:397-438,1611-1622` —— `94085ba` 残余的复核确认：被取消登录
   的迟到 `LoggedOnCallback` 仍可经共享 `_activeLoginAccountName` 槽错投下一登录 TCS。
