# Vapor.Steam.Core.Tests

Steam Session Engine 的单元测试套件，使用 xUnit 和 Moq 框架。

## 测试结构

```
tests/Vapor.Steam.Core.Tests/
├── FakeSteam/
│   ├── FakeSteamClientManager.cs     # 脚本化传输双（登录/挑战/QR/connect 全编排 + 调用日志）
│   └── BotSessionStateMachineTests.cs # 会话状态机六场景（登录/断线重连/挑战/超时/错误/受限）
├── Unit/
│   ├── Actions/
│   │   ├── PingActionTests.cs        # PingAction 测试 (15 个测试)
│   │   ├── EchoActionTests.cs        # EchoAction 测试 (15 个测试)
│   │   ├── LoginActionTests.cs       # LoginAction 测试 (12 个测试)
│   │   └── IdleActionTests.cs        # IdleAction 测试 (16 个测试)
│   │
│   │   # 游戏访问类动作测试已随动作拆分迁往官方插件工程
│   │   # (tests/Vapor.Plugins.GameAccess.Tests，含 RedeemKeyActionTests)
│   ├── ActionRegistryTests.cs        # ActionRegistry 测试 (13 个测试)
│   ├── BotSessionTests.cs            # BotSession 测试 (22 个测试)
│   ├── SessionManagerTests.cs        # SessionManager 测试 (21 个测试)
│   └── SteamClientManagerTests.cs    # SteamClientManager 测试 (15 个测试)
└── Vapor.Steam.Core.Tests.csproj
```

## 测试覆盖

### Actions 测试
- **PingActionTests**: 测试心跳功能，验证输出包含 pong、账户名、状态和时间戳
- **EchoActionTests**: 测试回显功能，验证 payload 正确回显
- **LoginActionTests**: 测试登录动作，验证输出结构
- **IdleActionTests**: 测试空闲动作，验证持续参数处理
- 游戏访问类动作（redeem_key 等 14 个）的测试已随 GameAccess 插件拆分迁至 `tests/Vapor.Plugins.GameAccess.Tests`

### FakeSteam 确定性测试后端
- **FakeSteamClientManager**:实现 `ISteamClientManager` 传输 seam 的脚本化确定性双——登录结果队列（成功/邮件 Steam Guard/2FA/任意 EResult 失败）、connect 失败注入与挂起-放行、QR 挑战 URL 序列与批准结果、每账号 staged code/token 合并；调用日志（登录尝试、connect/disconnect 计数、token 暂存、代理暂存）供断言。全部行为在 arrange 段排定，不依赖时钟与真实 Steam 网络。
- **BotSessionStateMachineTests**:在上述 fake 上跑会话状态机六场景——登录（含 QR 变体）、断线重连（断线终结命令循环，重连=会话重建 token 重登）、挑战（auth code/2FA 暂存后自动重试）、超时（挂起 connect 的调用方取消、action 超预算）、错误（登录/连接拒绝→FatalError）、受限（RateLimitExceeded 无类型化异常，与其它登录失败同归 FatalError）。

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
dotnet test tests/Vapor.Steam.Core.Tests/Vapor.Steam.Core.Tests.csproj
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
| ActionRegistryTests | 13 |
| BotSessionTests | 22 |
| SessionManagerTests | 21 |
| SteamClientManagerTests | 15 |
| **总计** | **129**（历史小节口径；RedeemKeyActionTests 已迁往 GameAccess 插件测试工程） |

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

