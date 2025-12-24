# Steam Session Engine

## 概述

Steam Session Engine 实现了类似 ASF 的 Bot 会话架构，提供会话管理和动作执行功能。已集成 SteamKit2 用于真实的 Steam 网络通信。

## 核心组件

### BotSession
- 会话状态机：`Disconnected -> Connecting -> Connected` 等
- 命令队列处理
- 事件发布
- 错误处理和重连逻辑
- 与 SteamClientManager 集成

### SessionState 枚举
```
Disconnected, Connecting, ConnectingWaitAuthCode, ConnectingWait2FA,
Connected, Reconnecting, DisconnectedByUser, Disconnecting, FatalError
```

### IAction 接口
所有动作实现此接口：
```csharp
string Name { get; }
ActionMetadata Metadata { get; }
Task<ActionResult> ExecuteAsync(
    BotSession session,
    IReadOnlyDictionary<string, object?> payload,
    CancellationToken cancellationToken
);
```

### ActionRegistry
- 动作注册和查找
- 按名称不区分大小写查找

### SessionManager
- 管理多个 BotSession 实例
- 获取或创建会话
- 会话生命周期管理

### SteamClientManager
- 封装 SteamKit2 的 SteamClient
- 管理 Steam 回调和连接状态
- 处理登录、认证码和 2FA 流程
- 维护多账户登录状态

## 内置动作

| 动作名 | 说明 | 需要登录 | 超时 |
|--------|------|----------|------|
| `ping` | 心跳检测 | 否 | 10s |
| `echo` | 回显 payload | 否 | 10s |
| `login` | 登录 Steam | 否 | 60s |
| `idle` | 模拟在线 | 是 | 300s |
| `redeem_key` | 激活游戏 Key | 是 | 60s |

## 使用示例

### 注册自定义动作
```csharp
public sealed class MyAction : IAction
{
    public string Name => "my_action";
    public ActionMetadata Metadata => new ActionMetadata(
        Name, "Description", RequiresLogin: true, TimeoutSeconds: 60
    );

    public Task<ActionResult> ExecuteAsync(
        BotSession session,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        // 实现动作逻辑
        return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
    }
}

// 注册
actionRegistry.Register(new MyAction());
```

### 在 Agent 中使用
```csharp
var session = await sessionManager.GetOrCreateSessionAsync(
    accountName,
    new AccountCredentials(accountName, password),
    cancellationToken
);

var result = await session.ExecuteActionAsync(
    "ping",
    new Dictionary<string, object?>(),
    cancellationToken
);
```

## SteamKit2 集成

SteamClientManager 负责与 Steam 网络通信：
- 管理单个共享的 SteamClient 实例
- 通过 CallbackManager 处理 Steam 回调
- 支持多账户同时登录
- 处理认证码和 2FA 请求
- 支持 access token 和 refresh token 保存

### 认证流程

1. 初始登录需要密码
2. Steam 返回需要认证码或 2FA 时，Session 状态变为 `ConnectingWaitAuthCode` 或 `ConnectingWait2FA`
3. 通过 `ProvideAuthCode()` 或 `Provide2FACode()` 提供代码
4. 登录成功后，access token 和 refresh token 可保存用于后续登录

## 未来扩展

1. 实现更多实用动作：`play_game`, `trade`, `add_friend` 等
2. 支持会话持久化和恢复
3. 添加限流和重试策略
4. 添加 Steam 交易处理
5. 实现 Steam 社交功能（好友、群组等）
