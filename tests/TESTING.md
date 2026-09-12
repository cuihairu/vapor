# Vapor 测试概览

本文档提供 Vapor 项目的完整测试概览。

## 测试项目结构

`tests/` 下 9 个测试项目(外加 1 个供插件基础设施测试使用的示例插件程序集):

```
tests/
├── Vapor.Steam.Core.Tests/               (573 tests)
│   ├── Unit/                             动作/会话/交易/安全/数据/Web 客户端
│   ├── Integration/                      会话工作流 + Redis 缓存(门控)
│   └── Performance/                      并发与压力
├── Vapor.ControlPlane.Tests/             (147 tests)
│   └── Performance/                      队列吞吐/派发/SSE 扇出基准
├── Vapor.Plugins.Core.Tests/             (85 tests)
├── Vapor.Plugins.MobileAuthenticator.Tests/ (44 tests)
├── Vapor.Agent.Tests/                    (41 tests)
├── Vapor.Plugins.MarketWatch.Tests/      (24 tests)
├── Vapor.Plugins.Monitoring.Tests/       (21 tests)
├── Vapor.Protocol.Tests/                 (20 tests)
├── Vapor.E2E.Tests/                      (6 tests,真实双进程)
└── Vapor.Plugins.TestPlugin/             插件基础设施测试用示例插件
```

## 测试统计

| 测试项目 | 数量 | 覆盖范围 |
|----------|------|----------|
| Vapor.Steam.Core.Tests | 573 | 动作、会话状态机、交易校验、凭据/加密、数据缓存、Steam Web 客户端 + 契约回放 |
| Vapor.ControlPlane.Tests | 147 | REST API、SQLite job/审计存储、任务派发、账户编排、周期任务、通知、追踪 + WS 协议回放 |
| Vapor.Plugins.Core.Tests | 85 | 插件发现/清单/SemVer 兼容/加载/卸载/ALC 回收/事件分发/配置/信任与权限 |
| Vapor.Plugins.MobileAuthenticator.Tests | 44 | TOTP、确认哈希、移动交易确认、shared secret 持久化、插件宿主实战加载 |
| Vapor.Agent.Tests | 41 | 重连退避策略、任务执行器、WS URI 构造 |
| Vapor.Plugins.MarketWatch.Tests | 24 | watch 存储/阈值评估/轮询告警与 webhook/插件宿主实战加载 |
| Vapor.Plugins.Monitoring.Tests | 21 | 指标注册表/HTTP 指标服务/插件生命周期 |
| Vapor.Protocol.Tests | 20 | JsonDefaults 序列化契约(camelCase/枚举字符串/null 省略/前向兼容)+ 全部协议模型逐字段往返 |
| Vapor.E2E.Tests | 6 | 真实双进程闭环:CP 进程 + Agent 子进程(job 派发、任务回报、SSE、账户编排重平衡) |
| **合计** | **961** | (2026-09-12 基线;另 E2E 以真实子进程覆盖 Agent 主循环,单测统计测不到) |

> 基线刷新方式:`for p in Agent ControlPlane E2E Plugins.Core Plugins.MarketWatch Plugins.MobileAuthenticator Plugins.Monitoring Protocol Steam.Core; do dotnet test tests/Vapor.$p.Tests --no-build --list-tests | grep -c "^    "; done`

## 测试分类

### Steam.Core(573 个测试)

#### 动作(Actions)
| 测试类 | 数量 | 说明 |
|--------|------|------|
| RedeemKeyActionTests | 24 | Key 激活(含遮罩与边界) |
| IdleActionTests | 23 | 空闲动作(含 PlayGamesPayloadParser 12 个) |
| LoginActionTests | 17 | 登录动作 |
| PlayGamesActionTests | 7 | 挂机游玩 |
| DataActionsTests | 22 | 数据动作(游戏信息/价格/市场/搜索) |
| EchoActionTests / PingActionTests | 27 | 回显/心跳 |
| ActionRegistryTests | 16 | 注册表(执行观察者 4 个另计) |
| SendTradeOffer / AcceptTradeOffer / DeclineTradeOffer / CancelTradeOffer ActionTests | 12 | 交易动作 |
| GetInventoryActionTests | 4 | 库存读取 |

#### 会话与核心组件
| 测试类 | 数量 | 说明 |
|--------|------|------|
| BotSessionTests | 26 | 会话状态机 |
| SessionManagerTests | 24 | 会话管理器 |
| SteamClientManagerTests | 21 | Steam 客户端管理器 |
| SteamTransportContractTests | 13 | 传输层契约(SteamResult 线上编码镜像 + 接口可替换性) |
| ModelsTests | 41 | 数据模型和枚举 |
| EdgeCaseTests | 23 | 边界和异常场景 |
| SessionWorkflowTests(集成) | 19 | 完整工作流 |
| ConcurrencyTests(性能) | 9 | 并发和压力 |
| TwoFactorAutoResponderTests | 6 | 2FA 自动应答(本地 TOTP 闭环) |
| TokenRefreshTests / LoginFlowTests / RedeemKeyFlowTests | 5 | 认证与激活流程 |

#### 交易安全层(Trade Safety)
| 测试类 | 数量 | 说明 |
|--------|------|------|
| TradeOfferStateMachineTests | 29 | 报价状态机 |
| TradeActionValidationTests | 15 | 交易动作校验 |
| TradeAssetValidatorTests | 13 | 资产校验 |
| TradeRateLimiterTests | 10 | 频控 |
| TradeUrlParamsTests / TradeUrlParamsExtendedTests | 7 | 报价 URL 参数 |

#### 安全与凭据
| 测试类 | 数量 | 说明 |
|--------|------|------|
| FileCredentialStoreTests | 15 | 凭据存储(加密落盘/备份恢复/权限收紧/shared secret) |
| VaporCryptoHelper(Encryption)Tests | 16 | AES-GCM 加密助手 |
| CredentialStoreRotatorTests | 5 | 密钥轮换 |
| RedactingLoggerProviderTests / SensitiveDataRedactorTests | 10 | 日志脱敏 |
| AgentReconnectPolicyTests | 4 | Agent 重连策略 |

#### 数据与 Web 客户端
| 测试类 | 数量 | 说明 |
|--------|------|------|
| MemoryVaporCacheTests | 21 | 内存缓存(TTL/SWR/单飞行去重) |
| RedisCacheEntryTests | 11 | Redis 信封编解码/新鲜度判定(纯逻辑,无需 Redis) |
| RedisVaporCacheIntegrationTests(集成,门控) | 11 | Redis 端到端(需 `VAPOR_TEST_REDIS`) |
| SteamStoreApiClientTests | 9 | 商店 API 客户端(解析) |
| SteamStoreApiContractTests | 4 | 录制响应契约回放(appdetails/storesearch/market render 新旧双契约,fixture 见 `TestData/`) |
| SteamWebHandlerResilienceTests | 8 | 429/5xx 退避重试与熔断 |
| HttpCircuitBreakerTests | 8 | 熔断器状态机 |
| GameModelsTests | 4 | 游戏数据模型 |

#### Steam 认证
| 测试类 | 数量 | 说明 |
|--------|------|------|
| SteamTotpTests | 13 | Steam TOTP(本地 2FA 码生成) |
| SteamTimeSynchronizerTests | 5 | Steam 服务器时间同步 |

### ControlPlane(147 个测试)

| 测试类 | 数量 | 说明 |
|--------|------|------|
| ScheduleClockTests | 16 | 周期计划时钟(interval/cron/触发点计数) |
| WsProtocolReplayTests | 7 | WS 隧道协议录制回放(5 类帧快照 roundtrip + 会话序列路由 + 前向兼容) |
| DesiredStateReconcilerTests | 16 | 账户编排(登录派发/退避/节流/重平衡/dry-run) |
| SqliteJobStoreTests | 14 | job 存储(并发/迁移/周期模板) |
| NotificationTests | 14 | 通知规则/webhook 签名/派发隔离 |
| AccountStoreTests | 13 | 账户存储(ConfigVersion 并发) |
| AccountApiTests | 12 | `/v1/accounts` REST |
| RecurringJobSchedulerTests | 10 | 周期任务触发/missed/overlap/退役 |
| ControlPlaneApiTests | 9 | REST API(鉴权/任务/SSE/计划 job) |
| TaskSchedulerServiceTests | 7 | 任务派发/终态机制 |
| SqliteAuditStoreTests | 7 | 审计存储 |
| AuditApiTests | 6 | 审计查询 API |
| TracingTests | 4 | OpenTelemetry 追踪注入 |
| AgentRegistryTests | 4 | Agent 注册表 |
| EventBrokerTests | 3 | 事件总线 |
| ControlPlaneBenchmarks(性能) | 4 | 吞吐/派发/扇出基准 |

### 插件体系(174 个测试)

- **Plugins.Core(85)**:清单解析(8)、发现(5)、加载(10)、卸载与 ALC 回收(5)、信任与权限(18)、事件分发(6)、配置扩展(15)、插件 API 与 SemVer 兼容(11,含 TryParseVersion theory 展开)
- **MobileAuthenticator(44)**:动作含 save_shared_secret(22)、确认客户端解析(8)、确认哈希(8)、设备 ID(3)、插件加载与 6-action 断言(3)、shared secret 存储行为(3,位于 Steam.Core 的 FileCredentialStoreTests)
- **MarketWatch(24)**:watch 存储/阈值评估/三个 watch action/轮询告警与 webhook/插件宿主实战加载
- **Monitoring(21)**:指标注册表(9)/HTTP 服务(6)/插件生命周期(6)

### Agent(41 个测试)

重连退避策略(25)、任务执行器(12)、WS URI 构造(4)。Agent 主循环(会话泵/WS 客户端/任务派发闭环)由 **E2E 套件以真实子进程覆盖**——单测统计与覆盖率均测不到。

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

### 当前基线（2026-09-12，行覆盖 74.3%）

合并全部报告计算：`./scripts/coverage-summary.py`（按程序集归一化文件路径后，以 (程序集, 文件, 行) 去重取最大命中）：

| 程序集 | 行覆盖 |
|--------|--------|
| Plugins.TestPlugin | 97.4%（示例插件，fixture 程序集） |
| Monitoring | 89.7% |
| MarketWatch | 89.0% |
| Protocol | 89.1% |
| ControlPlane | 86.0% |
| Plugins.Core | 88.1% |
| MobileAuthenticator | 77.0% |
| Steam.Core | 67.9% |
| Agent | 22.6%（结构性，见下） |
| **合计** | **74.3%** |

> 初版基线（44.6%）系统性偏低：不同 testhost 生成的报告里同一源文件的 `filename` 前缀写法不一致（`src/<项目>/…`、`<项目>/…`、裸文件名并存），合并时未归一化导致同一行被重复计入分母。`coverage-summary.py` 归一化去重后重算，整体 44.6% → 74.3%。
>
> 结构性未覆盖（非测试缺口，不计入门禁预期）：
> - **Agent**：除 `Program.cs`（452 行顶层组装语句）外全部单测文件 100%；Agent 主循环（WS 客户端/会话泵/任务派发闭环）由 E2E 套件以真实双进程覆盖，插桩无法跨进程归集。E2E 6 个测试是独立进程，不计入覆盖率插桩。
> - **Steam.Core** 未覆盖大头是集成壳：`SteamTradeClient`（587 行，需真实 SteamKit2 网络会话）、`RedisVaporCache`（148 行，需 Redis 实例；同接口内存实现已 100%）。

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
- 保持测试覆盖率稳步提升（当前 74.3%，CI/Codecov 门禁 70%，见上方基线表）
- 新功能必须包含测试
- 修复 bug 时添加回归测试
- 定期审查和重构测试代码

