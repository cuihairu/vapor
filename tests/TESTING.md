# Vapor 测试概览

本文档提供 Vapor 项目的完整测试概览。

## 测试项目结构

`tests/` 下 9 个测试项目(外加 2 个测试基建程序集:示例插件与故障 fixture 库):

```
tests/
├── Vapor.Steam.Core.Tests/               (970 tests)
│   ├── Unit/                             动作/会话/交易/安全/数据/Web 客户端
│   ├── Integration/                      会话工作流 + Redis 缓存(门控)
│   └── Performance/                      并发与压力
├── Vapor.ControlPlane.Tests/             (357 tests)
│   └── Performance/                      队列吞吐/派发/SSE 扇出基准
├── Vapor.Plugins.Core.Tests/             (121 tests)
├── Vapor.Plugins.MobileAuthenticator.Tests/ (126 tests)
├── Vapor.Agent.Tests/                    (50 tests)
├── Vapor.Plugins.MarketWatch.Tests/      (56 tests)
├── Vapor.Plugins.Monitoring.Tests/       (33 tests)
├── Vapor.Protocol.Tests/                 (37 tests)
├── Vapor.E2E.Tests/                      (6 tests,真实双进程)
├── Vapor.Plugins.TestPlugin/             插件基础设施测试用示例插件
└── Vapor.Plugins.TestFixtures/           故障 fixture 库(故意坏实现/多实现类,供失败路径测试)
```

## 测试统计

| 测试项目 | 数量 | 覆盖范围 |
|----------|------|----------|
| Vapor.Steam.Core.Tests | 970 | 动作、会话状态机、交易校验、凭据/加密、maFile 解析、数据缓存(Redis mock 离线全覆盖)、Steam Web 客户端 + 契约回放、徽章页解析、报价列表、loot、addlicense、库存多 app 扫描、重复卡分析与 1:1 换卡匹配、QR 扫码登录会话流、payload 值形状与分支加固、熔断器/限流器边界 |
| Vapor.ControlPlane.Tests | 357 | REST API、SQLite job/审计存储、任务派发、账户编排、周期任务、通知、追踪 + WS 协议回放、报价查询/接受/拒绝/批量确认/loot/免费认领/库存读取/重复查询/换卡报价、静态只读面板契约、QR 挑战归类、Program 分支加固、Bearer 鉴权解析 |
| Vapor.Plugins.Core.Tests | 121 | 插件发现/清单/SemVer 兼容/加载/卸载/ALC 回收/事件分发/配置/信任与权限 |
| Vapor.Plugins.MobileAuthenticator.Tests | 126 | TOTP、确认哈希、移动交易确认(单个/批量)、shared/identity secret 持久化、报价确认闭环、插件宿主实战加载 + 动作边界(payload 形状/失败语义/冷却)与确认客户端解析分支 |
| Vapor.Agent.Tests | 50 | 重连退避策略、任务执行器(含 QR 登录与 password+refreshToken 组合 payload)、WS URI 构造、maFile 离线导入 CLI |
| Vapor.Plugins.MarketWatch.Tests | 56 | watch 存储/阈值评估/free watch 边沿告警/轮询告警与 webhook(含传输崩溃与取消路径)/轮询循环确定性停机/阈值 payload 值形状/插件宿主实战加载 |
| Vapor.Plugins.Monitoring.Tests | 33 | 指标注册表/HTTP 指标服务/插件生命周期 |
| Vapor.Protocol.Tests | 37 | JsonDefaults 序列化契约(camelCase/枚举字符串/null 省略/前向兼容)+ 全部协议模型逐字段往返 + record 边界(畸形 JSON/缺字段/默认值) |
| Vapor.E2E.Tests | 6 | 真实双进程闭环:CP 进程 + Agent 子进程(job 派发、任务回报、SSE、账户编排重平衡) |
| **合计** | **1756** | (2026-09-14 基线;另 E2E 以真实子进程覆盖 Agent 主循环,单测统计测不到) |

> 基线刷新方式:`for p in Agent ControlPlane E2E Plugins.Core Plugins.MarketWatch Plugins.MobileAuthenticator Plugins.Monitoring Protocol Steam.Core; do dotnet test tests/Vapor.$p.Tests --no-build --list-tests | grep -c "^    "; done`

## 测试分类

### Steam.Core(900 个测试)

#### 动作(Actions)
| 测试类 | 数量 | 说明 |
|--------|------|------|
| RedeemKeyActionTests | 33 | Key 激活(含遮罩与边界/null 响应/良性码/不可重试与瞬态错误重试/可选字段透出) |
| IdleActionTests | 23 | 空闲动作(含 PlayGamesPayloadParser 12 个) |
| LoginActionTests | 17 | 登录动作 |
| PlayGamesActionTests | 7 | 挂机游玩 |
| DataActionsTests | 34 | 数据动作(游戏信息/价格/市场/搜索 + 无 web handler/坏 app_id/商店客户端工厂故障) |
| EchoActionTests / PingActionTests | 27 | 回显/心跳 |
| ActionRegistryTests | 18 | 注册表(执行观察者 4 个另计) |
| SendTradeOffer / AcceptTradeOffer / DeclineTradeOffer / CancelTradeOffer ActionTests | 12 | 交易动作 |
| LootInventoryActionTests | 20 | loot(可交易过滤/默认社区 app 753-6/app_ids 覆盖与 game context 2/分页/mobile 确认标志/空库存/失败语义/上限/值形状/限流/无登录态) |
| FindDuplicatesActionTests | 17 | 重复卡分析(keep 语义与非法值/untradable 不计/excess asset ids/空结果成功/库存失败/无 SteamID/无 web handler/上限/值形状) |
| SwapDuplicatesActionTests | 23 | 1:1 换卡报价(默认 dry_run 不发送/send=true 对称报价与 mobile 标志/自换拒绝/无互补失败/对方库存失败标明 partner 侧/双侧分页/参数范围/trade_url/失败语义/限流/值形状) |
| GetInventoryActionTests + GetInventoryActionBranchTests | 21 | 库存读取(steam_id 缺省 cookie 反解/app_ids 多 app 扫描 + loot context 规则/tradable 与 marketable 过滤/上限/JSON 往返/单 app 旧输出兼容/坏 steamid/cookie 无身份/classic 路径覆盖/分页去重/值形状) |
| GetCardDropsActionTests | 13 | 卡牌剩余掉落查询(排序/错误/steam_id 缺省 cookie 反解/缓存 SWR/force_refresh/名称与元数据/默认真实客户端装配) |
| GetTradeOffersActionTests | 8 | 报价列表读取(active_only 透传/输出映射/失败语义) |
| AddLicenseActionTests | 18 | 免费 license 认领(app 走 client 协议/sub 走商店 checkout/已拥有视为成功/双 ID 混合/去重/JSON 往返 payload/边界/混合值形状) |

#### 会话与核心组件
| 测试类 | 数量 | 说明 |
|--------|------|------|
| BotSessionTests | 26 | 会话状态机 |
| BotSessionQrLoginTests | 7 | QR 扫码登录会话流(批准后 refresh token 走 token 登录、挑战 URL 上浮与轮转重发、拒绝/超时/连接失败映射、stub 模式) |
| SessionManagerTests | 36 | 会话管理器(恢复回调/事件订阅/无凭证库/重复恢复恢复同一会话/登录失败清会话/后台 token 刷新跳过与故障吸收) |
| SteamClientManagerTests | 21 | Steam 客户端管理器 |
| SteamTransportContractTests | 14 | 传输层契约(SteamResult 线上编码镜像 + 接口可替换性) |
| ModelsTests | 41 | 数据模型和枚举 |
| EdgeCaseTests | 23 | 边界和异常场景 |
| SessionWorkflowTests(集成) | 19 | 完整工作流 |
| ConcurrencyTests(性能) | 9 | 并发和压力 |
| TwoFactorAutoResponderTests | 7 | 2FA 自动应答(本地 TOTP 闭环 + 无活跃会话跳过) |
| TokenRefreshTests / LoginFlowTests / RedeemKeyFlowTests | 5 | 认证与激活流程 |

#### 交易安全层(Trade Safety)
| 测试类 | 数量 | 说明 |
|--------|------|------|
| TradeOfferStateMachineTests | 35 | 报价状态机 |
| TradeActionValidationTests | 15 | 交易动作校验 |
| TradeAssetValidatorTests | 13 | 资产校验 |
| TradeRateLimiterTests | 16 | 频控(窗口/并发槽/超时/租约与销毁交互) |
| CardSwapMatcherTests | 9 | 重复分组与 1:1 互补匹配(keep/excess 只取可交易/排序确定性/双向互补条件/单向不配/maxSwaps 截断/context 规则) |
| TradeUrlParamsTests / TradeUrlParamsExtendedTests | 7 | 报价 URL 参数 |

#### 安全与凭据
| 测试类 | 数量 | 说明 |
|--------|------|------|
| FileCredentialStoreTests | 21 | 凭据存储(加密落盘/备份恢复/权限收紧/shared secret/新版本拒载/缺 accounts 拒载) |
| MaFileParserTests | 12 | maFile 解析(SDA 嵌套/steamguard-cli 平铺/密码加密 PBKDF2+AES-CBC/无密码与错密码/无 secret/账户键回退/根数组与坏 payload 拒绝) |
| VaporCryptoHelper(Encryption)Tests + VaporCryptoHelperMethodTests + VaporCryptoHelperTests | 38 | AES-GCM 加密助手(往返/篡改/边界 + 方法级分支/文件密钥回退/不可读文件降级) |
| CredentialStoreRotatorTests | 5 | 密钥轮换 |
| RedactingLoggerProviderTests / SensitiveDataRedactorTests | 15 | 日志脱敏(嵌套异常/结构化 scope/计数与索引器/空值归一) |
| AgentReconnectPolicyTests | 4 | Agent 重连策略 |

#### 数据与 Web 客户端
| 测试类 | 数量 | 说明 |
|--------|------|------|
| MemoryVaporCacheTests | 22 | 内存缓存(TTL/SWR/单飞行去重) |
| RedisCacheEntryTests | 11 | Redis 信封编解码/新鲜度判定(纯逻辑,无需 Redis) |
| RedisVaporCacheTests | 32 | Redis 缓存全路径离线覆盖(mock IConnectionMultiplexer/IDatabase:信封协议/SWR 跨实例锁/索引维护/SCAN 前缀清理/单飞行共享) |
| RedisVaporCacheIntegrationTests(集成,门控) | 11 | Redis 端到端(需 `VAPOR_TEST_REDIS`) |
| SteamStoreApiClientTests | 32 | 商店 API 客户端(解析/降级与畸形响应/market 新旧双契约回退) + addlicense 结账端点(detail 解析/已拥有/HTTP 失败) |
| SteamStoreApiContractTests | 4 | 录制响应契约回放(appdetails/storesearch/market render 新旧双契约,fixture 见 `TestData/`) |
| SteamBadgesClientTests | 14 | 徽章页解析变体(appid 双载体/掉落文案/分页/失败语义) |
| SteamBadgesPageContractTests | 3 | 徽章页 HTML 契约回放(fixture 为三方解析器互证构造,登录门控不可匿名录制) |
| SteamWebHandlerResilienceTests | 8 | 429/5xx 退避重试与熔断 |
| SteamWebHandlerRequestTests | 17 | 请求构造(cookie/自定义 header/分主机 Referer+Origin/UA/限流窗延迟/重试耗尽语义/无与不可解析 Retry-After 回退/Dispose 幂等/SteamWebResponse 分类属性) |
| HttpCircuitBreakerTests | 8 | 熔断器状态机(Closed/Open/HalfOpen 转换与单探测) |
| GameModelsTests | 4 | 游戏数据模型 |
| PayloadReaderTests / TradeModelsEdgeTests | 25 | payload 读取器方法级分支 + 交易模型边界(TradeUrlParams/TradeAsset 等值形状与非法输入) |

#### Steam 认证
| 测试类 | 数量 | 说明 |
|--------|------|------|
| SteamTotpTests | 14 | Steam TOTP(本地 2FA 码生成) |
| SteamTimeSynchronizerTests | 6 | Steam 服务器时间同步 |

### ControlPlane(357 个测试)

| 测试类 | 数量 | 说明 |
|--------|------|------|
| ProgramBranchCoverageTests | 73 | Program 组装层分支(配置解析/环境变量回退/装配路径逐支驱动) |
| ScheduleClockTests | 16 | 周期计划时钟(interval/cron/触发点计数) |
| WsProtocolReplayTests | 7 | WS 隧道协议录制回放(5 类帧快照 roundtrip + 会话序列路由 + 前向兼容) |
| DesiredStateReconcilerTests | 54 | 账户编排(登录派发/退避/节流/重平衡/dry-run/smart farming 调度 + 循环存活/无 agent/在途窗口/形状怪癖执行路径加固) |
| SqliteJobStoreTests | 22 | job 存储(并发/迁移/周期模板) |
| NotificationTests | 19 | 通知规则/webhook 签名/派发隔离/无 sink 快速返回 |
| AccountStoreTests | 22 | 账户存储(ConfigVersion 并发/空名校验) |
| AccountApiTests | 58 | `/v1/accounts` REST(含 farm 状态、报价查询/接受/拒绝、自动确认、批量移动确认、loot 同步端点、免费 license 认领、库存读取:fake agent 顺序回报) |
| RecurringJobSchedulerTests + RetireTests | 12 | 周期任务触发/missed/overlap/退役 |
| ControlPlaneApiTests | 10 | REST API(鉴权/任务/SSE/计划 job、QR 挑战归类与 URL 透传) |
| TaskSchedulerServiceTests | 7 | 任务派发/终态机制 |
| SqliteAuditStoreTests | 8 | 审计存储 |
| AuditApiTests | 6 | 审计查询 API |
| DashboardStaticTests | 4 | 静态只读面板(/dashboard.html 服务、无写动词契约、与管理视图互链、`/` 302 重定向) |
| TracingTests | 4 | OpenTelemetry 追踪注入 |
| AgentRegistryTests | 4 | Agent 注册表 |
| AuthTests | 8 | Bearer 鉴权解析(合法/缺 scheme/非 Bearer/空 header/空配置键 fail-closed) |
| EventBrokerTests | 8 | 事件总线 |
| ControlPlaneBenchmarks(性能) | 4 | 吞吐/派发/扇出基准 |

### 插件体系(336 个测试)

- **Plugins.Core(121)**:清单解析(13)、发现(5)、加载(11)+加载器(8)、卸载与 ALC 回收(8)、信任与权限(18)、事件分发(12)、配置扩展(20)、能力(7)、插件 API 与 SemVer 兼容(17,含 TryParseVersion theory 展开)、管理器并发(1)
- **MobileAuthenticator(126)**:动作含 save_shared_secret/save_identity_secret/confirm_trade_offer/confirm_all_confirmations(41)+ 动作边界:payload 形状/失败语义/冷却与并发(44)、确认客户端解析含 type 归一化(10)+ 客户端会话分支(17)、解析分支(10)、确认哈希(8)、设备 ID(3)、插件加载与 9-action 断言(3)、shared/identity secret 存储行为(7,位于 Steam.Core 的 FileCredentialStoreTests)
- **MarketWatch(56)**:watch 存储(18)/阈值评估/三个 watch action(kind=price/free)/轮询告警与 webhook(free_game_alert 与 price_alert/传输崩溃吞并/周期中取消干净停机/循环重启测试钩子)/单 app 抓取失败隔离/阈值 payload 值形状/无参构造 Info/插件宿主实战加载(2)
- **Monitoring(33)**:指标注册表(9)/HTTP 服务(14)/插件生命周期(10)

### Agent(50 个测试)

重连退避策略(25)、任务执行器(15,含 QR 登录与 password+refreshToken 组合 payload 解析与优先级)、maFile 离线导入 CLI(6)、WS URI 构造(4)

### E2E(6 个测试)

真实双进程:`E2EStack` 启动真实 ControlPlane 进程 + Agent 子进程(独立 HOME、WS 隧道、SQLite)。覆盖:健康检查、job 全链路(创建→派发→执行→回报→REST 读取 output)、任务取消、SSE 事件流、2FA 挑战人工提交、账户声明→自动分配→kill agent→重平衡。

## 运行测试

> 测试目标框架为 `net10.0`（.NET SDK 10.x）。`./scripts/run-tests.sh` / `.\scripts\run-tests.ps1` 已默认设置 `DOTNET_ROLL_FORWARD=Major`。

### 基本命令

```bash
# 运行所有测试
dotnet test

# 运行特定测试项目
dotnet test tests/Vapor.Steam.Core.Tests/Vapor.Steam.Core.Tests.csproj

# 运行特定测试类
dotnet test --filter "FullyQualifiedName~PingActionTests"

# 运行特定测试方法
dotnet test --filter "FullyQualifiedName~ExecuteAsync_ReturnsSuccess"
```

### 按测试类别运行

```bash
# 只运行单元测试
dotnet test --filter "FullyQualifiedName~Unit"

# 只运行集成测试
dotnet test --filter "FullyQualifiedName~Integration"

# 跑 Redis 集成测试（需本地或容器 Redis；未设 VAPOR_TEST_REDIS 时自动跳过）
VAPOR_TEST_REDIS=localhost:6379 dotnet test --filter "FullyQualifiedName~RedisVaporCacheIntegrationTests"

# 只运行性能测试
dotnet test --filter "FullyQualifiedName~Performance"

# 运行特定类型的测试
dotnet test --filter "FullyQualifiedName~PingActionTests"
dotnet test --filter "FullyQualifiedName~SessionWorkflowTests"
dotnet test --filter "FullyQualifiedName~ConcurrencyTests"
```

### 使用测试脚本

```bash
# Linux/macOS
./scripts/run-tests.sh                    # 运行所有测试
./scripts/run-tests.sh -c                # 带覆盖率报告
./scripts/run-tests.sh -v                # 详细输出
./scripts/run-tests.sh -f "PingAction"   # 过滤测试

# Windows PowerShell
.\scripts\run-tests.ps1
.\scripts\run-tests.ps1 -Coverage
.\scripts\run-tests.ps1 -Verbose
```

### 生成代码覆盖率报告

```bash
# 推荐（整个解决方案，coverlet.collector，输出到 */TestResults/*/coverage.cobertura.xml）
./scripts/run-tests.sh -c

# HTML 报告（可选，需要 ReportGenerator）
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:**/TestResults/*/coverage.cobertura.xml -targetdir:./TestResults/coveragereport
```

## 代码覆盖率

9 个测试项目统一接入 coverlet.collector；`run-tests.sh -c` 在收集前清理历史残留报告（清理必须在测试之前——测试结束后这些路径上的文件就是本次结果），覆盖整个解决方案。

### 当前基线（2026-09-14，行覆盖 98.6%）

合并全部报告计算：`./scripts/coverage-summary.py`（按程序集归一化文件路径后，以 (程序集, 文件, 行) 去重取最大命中）：

| 程序集 | 行覆盖 |
|--------|--------|
| MobileAuthenticator | 100.0% |
| Protocol | 100.0% |
| Plugins.TestPlugin | 100.0%（示例插件，fixture 程序集） |
| ControlPlane | 99.5% |
| Plugins.Core | 99.1% |
| Agent | 98.1% |
| Steam.Core | 98.0% |
| MarketWatch | 97.8% |
| Monitoring | 97.2% |
| Plugins.TestFixtures | 68.3%（故障 fixture 库：故意坏实现/多实现类本身即为被测失败路径） |
| **合计** | **98.6%** (10961/11120) |

> 历史基线：2026-09-12 首次真实全解决方案基线为 74.3%（此前 44.6% 的初版系统性偏低：不同 testhost 生成的报告里同一源文件的 `filename` 前缀写法不一致，合并未归一化导致同一行被重复计入分母）。2026-09-13 覆盖率冲刺（逐文件提取未覆盖行并针对性补测）后达 95.9%。2026-09-14 第二轮冲刺后达 98.6%（Agent 91.9%→98.1%、ControlPlane 97.9%→99.5%、Plugins.Core 88.1%→99.1%、Monitoring 89.7%→97.2%；159 行未覆盖，见下）。
>
> 剩余 159 行未覆盖，四类（非测试缺口，不计入门禁预期）：
> - **死代码/物理不可达**（约 39 行）：`VaporCryptoHelper` 的 `Enum.IsDefined` 前置挡住的 switch 分支与 SHA256 密钥归一化后不可能失败的 catch、`RedactingLoggerProvider.AppendPairs`（私有嵌套类非接口成员，无调用点）、`HttpCircuitBreaker` 的 HalfOpen 存储态分支（`_state` 从不赋值为 HalfOpen——半开是推导态，switch default 同理）、`SteamTotp` 空 base64 分支（必被 IsNullOrWhiteSpace 前置拦截）、`RecurringJobScheduler` 的 `missed>0 ∧ future=null` 组合（周期性 cron 匹配过一次必有下一次）、TestFixtures 故意坏实现。
> - **集成壳**（约 37 行）：`SteamTimeSynchronizer.QuerySteamServerTimeAsync`（内部 new HttpClient 无法注入）、`SteamClientManager` 的 SteamKit2 内部类型反射、`BotSession` SteamKit 回调深处、`SteamWebHandler` 登录态 cookie 合并。
> - **平台分支**（约 12 行）：`FileCredentialStore` 的 Windows return 与 Unix 权限收紧 catch（本机 Linux 只能走单侧）。
> - **防御分支**（约 71 行）：后台循环/泵的 OCE 与兜底 catch（`MarketWatchPlugin.PollLoopAsync`、`NotificationService` 泵、`PluginEventDispatcher`、`MonitoringPlugin`、`DesiredStateReconciler`、`SessionManager` 刷新循环）、`MetricsHttpServer` accept 层 SocketException/ObjectDisposed、E2E 6 个测试以真实双进程覆盖 Agent 主循环（插桩无法跨进程归集）。

### 排除项

- 测试项目自身与 `Vapor.Plugins.TestPlugin`
- xUnit / Moq 框架程序集
- Microsoft / System 命名空间

## 测试框架和工具

- **xUnit** - 测试框架
- **Moq** - Mock 框架
- **Coverlet** - 代码覆盖率工具
- **ReportGenerator** - HTML 报告生成器

## 测试编写指南

### 命名约定

- 测试类: `<ClassName>Tests`
- 测试方法: `MethodName_State_ExpectedResult`
- 测试命名空间: `Vapor.Steam.Core.Tests.{Unit|Integration|Performance}`

### AAA 模式

```csharp
[Fact]
public async Task ExecuteAsync_WithValidPayload_ReturnsSuccess()
{
    // Arrange - 设置测试数据
    var session = CreateTestSession("test_account");
    var payload = new Dictionary<string, object?>();

    // Act - 执行被测试的方法
    var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

    // Assert - 验证结果
    Assert.True(result.Success);
}
```

### Theory 测试

```csharp
[Theory]
[InlineData("value1")]
[InlineData("value2")]
[InlineData("value3")]
public void Test_MultipleValues(string input)
{
    // Arrange & Act & Assert
    Assert.NotNull(input);
}
```

### Mock 使用

```csharp
// 创建 Mock
var mockLogger = new Mock<ILogger<MyClass>>(MockBehavior.Loose);
var mockDependency = new Mock<IDependency>(MockBehavior.Strict);

// 设置期望
mockDependency.Setup(d => d.Method(It.IsAny<string>())).Returns(true);

// 验证调用
mockDependency.Verify(d => d.Method("expected"), Times.Once);
```

## CI/CD 集成

测试可以轻松集成到 CI/CD 流程中：

CI（`.github/workflows/ci.yml`）在 Ubuntu Release 上跑全解决方案测试并上传覆盖率：

```yaml
- name: Test with coverage (Ubuntu Release)
  run: ./scripts/run-tests.sh -c
- name: Upload coverage to Codecov (Ubuntu Release)
  uses: codecov/codecov-action@v5
  with:
    files: "**/TestResults/*/coverage.cobertura.xml"
```

## 性能基准

### ControlPlane 基准（`Vapor.ControlPlane.Tests/Performance/ControlPlaneBenchmarks.cs`）

覆盖三个跟踪维度：队列吞吐（SQLite job store）、并发任务派发（多 claimer 竞争）、
SSE 扇出（HTTP 连接数 + EventBroker 订阅数）。断言只设置宽松上限（30–60s）防止
CI 抖动；实际数字以本地开发机（.NET 10, Linux x64）实测为准：

| 基准 | 规模 | 实测 |
|------|------|------|
| 队列吞吐：job 创建 | 500 jobs | ~5,200/s |
| 队列吞吐：claim+finish 循环 | 500 tasks | ~2,000/s |
| 并发 claimer（4 竞争者） | 200 tasks | ~1,760/s，0 重复派发 |
| EventBroker 扇出 | 600 订阅者 × 100 事件 | ~19ms 全量送达 |
| SSE 并发连接 | 50 连接 | ~193ms 全部收到事件 |

运行方式：`dotnet test tests/Vapor.ControlPlane.Tests --filter "FullyQualifiedName~Performance"`

### Steam.Core（基于 ConcurrencyTests 的观察）

| 操作 | 预期性能 |
|------|----------|
| 单个动作执行 | < 10ms |
| 会话创建 | < 50ms |
| 会话移除 | < 100ms |
| 100 个并发动作 | < 1s |
| 1000 个并发会话 | < 5s |

## 最佳实践

1. **单一职责**: 每个测试只验证一个行为
2. **独立性**: 测试之间不共享状态
3. **可重复性**: 测试结果稳定，不依赖外部因素
4. **快速运行**: 单元测试应该快速执行
5. **清晰命名**: 测试名称应该描述它测试的内容
6. **适当的隔离**: 使用 Mock 隔离外部依赖
7. **边界条件**: 测试边界值和异常情况
8. **文档化**: 测试作为代码行为的文档

## 测试维护

- 定期更新测试以匹配代码变更
- 保持测试覆盖率稳步提升（当前 98.6%，CI/Codecov 门禁 70%，见上方基线表）
- 新功能必须包含测试
- 修复 bug 时添加回归测试
- 定期审查和重构测试代码

