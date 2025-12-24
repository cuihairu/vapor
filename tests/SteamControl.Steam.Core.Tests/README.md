# SteamControl.Steam.Core.Tests

Steam Session Engine 的单元测试套件，使用 xUnit 和 Moq 框架。

## 测试结构

```
tests/SteamControl.Steam.Core.Tests/
├── Unit/
│   ├── Actions/
│   │   ├── PingActionTests.cs        # PingAction 测试 (15 个测试)
│   │   ├── EchoActionTests.cs        # EchoAction 测试 (15 个测试)
│   │   ├── LoginActionTests.cs       # LoginAction 测试 (12 个测试)
│   │   ├── IdleActionTests.cs        # IdleAction 测试 (16 个测试)
│   │   └── RedeemKeyActionTests.cs   # RedeemKeyAction 测试 (19 个测试)
│   ├── ActionRegistryTests.cs        # ActionRegistry 测试 (13 个测试)
│   ├── BotSessionTests.cs            # BotSession 测试 (22 个测试)
│   ├── SessionManagerTests.cs        # SessionManager 测试 (21 个测试)
│   └── SteamClientManagerTests.cs    # SteamClientManager 测试 (15 个测试)
└── SteamControl.Steam.Core.Tests.csproj
```

## 测试覆盖

### Actions 测试
- **PingActionTests**: 测试心跳功能，验证输出包含 pong、账户名、状态和时间戳
- **EchoActionTests**: 测试回显功能，验证 payload 正确回显
- **LoginActionTests**: 测试登录动作，验证输出结构
- **IdleActionTests**: 测试空闲动作，验证持续参数处理
- **RedeemKeyActionTests**: 测试 Key 激活，验证 Key 遮罩和必需参数检查

### 核心组件测试
- **ActionRegistryTests**: 动作注册表测试，包括注册、查找、大小写不敏感等功能
- **BotSessionTests**: 会话状态机测试，包括命令执行、状态转换、并发操作等
- **SessionManagerTests**: 会话管理器测试，包括创建、查找、删除会话等
- **SteamClientManagerTests**: Steam 客户端管理器测试，包括连接、登录状态管理等

## 运行测试

### 运行所有测试
```bash
dotnet test
```

### 运行特定测试项目
```bash
dotnet test tests/SteamControl.Steam.Core.Tests/SteamControl.Steam.Core.Tests.csproj
```

### 运行特定测试类
```bash
dotnet test --filter "FullyQualifiedName~ActionRegistryTests"
```

### 运行特定测试方法
```bash
dotnet test --filter "FullyQualifiedName~Register_AddsActionToRegistry"
```

### 生成代码覆盖率报告
```bash
dotnet test --collect:"XPlat Code Coverage"
```

### 生成 HTML 覆盖率报告（需要安装 ReportGenerator）
```bash
dotnet test --collect:"XPlat Code Coverage" --results-directory ./TestResults
reportgenerator -reports:**/coverage.cobertura.xml -targetdir:**/TestResults/coveragereport
```

## 测试统计

| 测试类 | 测试数量 |
|--------|----------|
| PingActionTests | 15 |
| EchoActionTests | 15 |
| LoginActionTests | 12 |
| IdleActionTests | 16 |
| RedeemKeyActionTests | 19 |
| ActionRegistryTests | 13 |
| BotSessionTests | 22 |
| SessionManagerTests | 21 |
| SteamClientManagerTests | 15 |
| **总计** | **148** |

## 测试原则

测试遵循以下原则：

1. **AAA 模式**: Arrange-Act-Assert 结构
2. **单一职责**: 每个测试只验证一个行为
3. **独立性**: 测试之间相互独立，可按任意顺序运行
4. **可重复性**: 测试结果稳定一致
5. **命名规范**: `MethodName_State_ExpectedResult` 格式

## 持续集成

测试项目已配置 Coverlet 代码覆盖率工具，在 CI/CD 流程中自动生成覆盖率报告。

配置文件位置：
- `.run/settings.run.xml` - Visual Studio 运行配置
- `Directory.Build.props` - 项目级覆盖率设置
