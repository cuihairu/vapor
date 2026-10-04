# Steam Session Engine

## 概述

Session Engine 基于 [SteamKit2](https://github.com/SteamRE/SteamKit) 实现 ASF 式的 Bot 会话：每个账号一个长连会话，登录、质询、动作执行都收敛在这层。控制面不碰 Steam 协议，动作在 agent 上跑，会话状态与凭据也不出 agent。

## 核心组件

### BotSession
- 会话状态机（十态，含 QR 登录等待态）
- 命令队列处理
- 事件发布
- 错误处理和重连逻辑
- 与 SteamClientManager 集成

### SessionState 枚举
```
Disconnected, Connecting, ConnectingWaitAuthCode, ConnectingWait2FA,
ConnectingWaitQr, Connected, Reconnecting, DisconnectedByUser,
Disconnecting, FatalError
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

`ActionMetadata` 声明 `RequiresLogin` 与 `TimeoutSeconds`，另有 init-only 的 `Safety`（执行安全分类，调度器据此限制重派次数——`send_trade_offer` 这类重复执行会产生双倍外部副作用的动作，派发上限恒为 2）。

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

## 动作

全仓 57 个动作的完整目录（payload 字段、超时、安全分类）见 [actions catalog](actions.md)。这里列 5 个最小的作示例：

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
    )
    { Safety = ActionSafety.ReadOnly };

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
2. Steam 返回需要认证码或 2FA 时，Session 状态变为 `ConnectingWaitAuthCode` 或 `ConnectingWait2FA`；扫码登录走 `ConnectingWaitQr`
3. 通过 `ProvideAuthCode()` / `Provide2FACode()` 提供代码，或由 SSE 通道送达 admin 面板提交的代码；配置了移动认证器共享密钥的账号可由 agent 侧 `TwoFactorAutoResponder` 自动应答
4. 登录成功后，access token 和 refresh token 保存用于后续登录——agent 重启后凭此恢复会话，无需人工重登

## 边界

会话凭据和 Steam Guard 码不离开 agent（过线只有布尔值）；好友、群组等社交动作未实现；跨区域调度是控制面的职责，本层不管。
