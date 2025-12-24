# SteamControl 测试概览

本文档提供 SteamControl 项目的完整测试概览。

## 测试项目结构

```
tests/
└── SteamControl.Steam.Core.Tests/
    ├── Unit/
    │   ├── Actions/
    │   │   ├── PingActionTests.cs        (15 tests, ~180 lines)
    │   │   ├── EchoActionTests.cs        (15 tests, ~220 lines)
    │   │   ├── LoginActionTests.cs       (12 tests, ~160 lines)
    │   │   ├── IdleActionTests.cs        (16 tests, ~210 lines)
    │   │   └── RedeemKeyActionTests.cs   (19 tests, ~280 lines)
    │   ├── ActionRegistryTests.cs        (13 tests, ~180 lines)
    │   ├── BotSessionTests.cs            (22 tests, ~320 lines)
    │   ├── SessionManagerTests.cs        (21 tests, ~280 lines)
    │   ├── SteamClientManagerTests.cs    (15 tests, ~200 lines)
    │   ├── ModelsTests.cs                (55 tests, ~620 lines)
    │   └── EdgeCaseTests.cs              (40 tests, ~540 lines)
    ├── Integration/
    │   └── SessionWorkflowTests.cs       (15 tests, ~280 lines)
    ├── Performance/
    │   └── ConcurrencyTests.cs           (10 tests, ~420 lines)
    ├── README.md
    └── SteamControl.Steam.Core.Tests.csproj
```

## 测试统计

| 指标 | 数值 |
|------|------|
| 测试类 | 13 |
| 测试方法 | 268 |
| 测试代码行数 | ~3,900+ |
| 测试与生产代码比例 | ~3.7:1 |

## 测试分类

### 单元测试 (Unit Tests) - 233 个测试

#### Actions 测试 (77 个测试)
| 测试类 | 测试数量 | 说明 |
|--------|----------|------|
| PingActionTests | 15 | 心跳动作测试 |
| EchoActionTests | 15 | 回显动作测试 |
| LoginActionTests | 12 | 登录动作测试 |
| IdleActionTests | 16 | 空闲动作测试 |
| RedeemKeyActionTests | 19 | Key 激活测试 |

#### 核心组件测试 (96 个测试)
| 测试类 | 测试数量 | 说明 |
|--------|----------|------|
| ActionRegistryTests | 13 | 动作注册表测试 |
| BotSessionTests | 22 | 会话状态机测试 |
| SessionManagerTests | 21 | 会话管理器测试 |
| SteamClientManagerTests | 15 | Steam 客户端管理器测试 |
| ModelsTests | 55 | 数据模型和枚举测试 |
| EdgeCaseTests | 40 | 边界和异常场景测试 |

#### ModelsTests 详细 (55 个测试)
- SessionState 枚举测试 (5 tests)
- SessionEventType 枚举测试 (3 tests)
- ActionMetadata 测试 (5 tests)
- ActionResult 测试 (5 tests)
- AccountCredentials 测试 (9 tests)
- SessionEvent 测试 (8 tests)

#### EdgeCaseTests 详细 (40 个测试)
- 空值和空字符串测试 (8 tests)
- Unicode 和编码测试 (6 tests)
- ActionRegistry 边界测试 (4 tests)
- SessionManager 边界测试 (3 tests)
- BotSession 边界测试 (3 tests)
- CancellationToken 边界测试 (2 tests)
- 数据类型边界测试 (2 tests)

### 集成测试 (Integration Tests) - 15 个测试

| 测试类 | 测试数量 | 说明 |
|--------|----------|------|
| SessionWorkflowTests | 15 | 完整工作流测试 |

#### SessionWorkflowTests 详细
1. 完整工作流：创建会话并执行动作
2. 多账户独立会话
3. 会话移除后阻止执行
4. 动作顺序执行
5. 同一会话上的并发动作
6. 无效动作返回失败
7. 需要登录的动作在未登录时失败
8. 使用有效 Key 激活成功
9. 缺失 Key 返回失败
10. 列出所有活动会话
11. 事件订阅接收事件
12. 账户名大小写不敏感
13. 不同持续时间的 Idle 动作
14. Echo 动作保持负载完整性
15. 多账户独立会话执行

### 性能测试 (Performance Tests) - 10 个测试

| 测试类 | 测试数量 | 说明 |
|--------|----------|------|
| ConcurrencyTests | 10 | 并发和压力测试 |

#### ConcurrencyTests 详细
1. 并发创建会话 (50 线程 × 10 会话)
2. 同一会话上的并发动作执行 (100 个动作)
3. 多会话上的并发动作执行 (20 会话 × 10 动作)
4. 快速会话创建和销毁压力测试 (100 次迭代 × 5 会话)
5. 并发获取或创建同一账户 (100 个线程)
6. 大负载内存压力测试 (1000 个键 × 100 字符值)
7. 并发移除和访问
8. ActionRegistry 并发注册线程安全
9. 多个并发事件订阅 (20 个订阅)

## 测试覆盖的功能点

### Actions 功能
- ✅ 元数据验证
- ✅ 成功/失败结果处理
- ✅ 输出数据结构验证
- ✅ 负载处理 (空、单值、多值、嵌套、数组)
- ✅ 特殊字符处理 (emoji, unicode)
- ✅ 大负载处理
- ✅ 参数类型验证
- ✅ 并发执行
- ✅ 取消令牌支持
- ✅ Key 遮罩安全处理

### 核心组件功能
- ✅ 动作注册表管理
- ✅ 会话状态机转换
- ✅ 会话生命周期管理
- ✅ 多会话管理
- ✅ 大小写不敏感账户名
- ✅ 事件发布和订阅
- ✅ 命令队列处理
- ✅ 认证码/2FA 码处理
- ✅ 并发安全
- ✅ 资源释放

### 边界和异常场景
- ✅ 空值和空字符串处理
- ✅ Unicode 和特殊字符处理
- ✅ 超长字符串处理
- ✅ 空负载处理
- ✅ 已取消的令牌处理
- ✅ 多次释放资源
- ✅ 重复操作处理
- ✅ 并发竞态条件

### 集成场景
- ✅ 完整工作流执行
- ✅ 多账户独立操作
- ✅ 动作链执行
- ✅ 并发场景
- ✅ 错误处理和恢复
- ✅ 事件流

## 运行测试

### 基本命令

```bash
# 运行所有测试
dotnet test

# 运行特定测试项目
dotnet test tests/SteamControl.Steam.Core.Tests/SteamControl.Steam.Core.Tests.csproj

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
# OpenCover 格式
dotnet test --collect:"XPlat Code Coverage"

# Cobertura 格式
dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollectorConfiguration.Format=cobertura

# HTML 报告 (需要 ReportGenerator)
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:**/coverage.opencover.xml -targetdir:**/TestResults/coveragereport
```

## 代码覆盖率

测试项目已配置 Coverlet 代码覆盖率工具。

### 配置文件

- `Directory.Build.props` - 全局测试设置
- `.run/settings.run.xml` - Visual Studio 运行配置

### 排除项

- 测试项目本身 (`[SteamControl.Steam.Core.Tests]*`)
- xUnit 框架 (`[xunit.*]*`)
- Moq 框架 (`[Moq]*`)
- Microsoft 命名空间 (`[Microsoft.*]*`)
- System 命名空间 (`[System.*]*`)

## 测试框架和工具

- **xUnit** - 测试框架
- **Moq** - Mock 框架
- **Coverlet** - 代码覆盖率工具
- **ReportGenerator** - HTML 报告生成器

## 测试编写指南

### 命名约定

- 测试类: `<ClassName>Tests`
- 测试方法: `MethodName_State_ExpectedResult`
- 测试命名空间: `SteamControl.Steam.Core.Tests.{Unit|Integration|Performance}`

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

```yaml
# GitHub Actions 示例
- name: Run Tests
  run: dotnet test --configuration Release --no-build

- name: Generate Coverage
  run: dotnet test --collect:"XPlat Code Coverage"

- name: Upload Coverage
  uses: codecov/codecov-action@v3
  with:
    files: ./TestResults/**/coverage.opencover.xml
```

## 性能基准

基于测试执行的观察：

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
- 保持测试覆盖率 > 80%
- 新功能必须包含测试
- 修复 bug 时添加回归测试
- 定期审查和重构测试代码
