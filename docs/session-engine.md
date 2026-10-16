English | [简体中文](zh/session-engine/)

# Steam Session Engine

## Overview

The Session Engine implements ASF-style bot sessions on top of [SteamKit2](https://github.com/SteamRE/SteamKit): one long-lived session per account, with login, challenge handling, and action execution all converging in this layer. The control plane never touches the Steam protocol; actions run on the agent, and session state and credentials never leave the agent.

## Core Components

### BotSession
- Session state machine (ten states, including the QR login wait state)
- Command queue processing
- Event publishing
- Error handling and reconnection logic
- Integration with SteamClientManager

### SessionState Enum
```
Disconnected, Connecting, ConnectingWaitAuthCode, ConnectingWait2FA,
ConnectingWaitQr, Connected, Reconnecting, DisconnectedByUser,
Disconnecting, FatalError
```

### IAction Interface
All actions implement this interface:
```csharp
string Name { get; }
ActionMetadata Metadata { get; }
Task<ActionResult> ExecuteAsync(
    BotSession session,
    IReadOnlyDictionary<string, object?> payload,
    CancellationToken cancellationToken
);
```

`ActionMetadata` declares `RequiresLogin` and `TimeoutSeconds`, plus an init-only `Safety` (execution safety classification; the scheduler uses it to cap redispatch counts — actions like `send_trade_offer` whose repeated execution would double the external side effects always have a dispatch limit of 2).

### ActionRegistry
- Action registration and lookup
- Case-insensitive lookup by name

### SessionManager
- Manages multiple BotSession instances
- Gets or creates sessions
- Session lifecycle management

### SteamClientManager
- Wraps SteamKit2's SteamClient
- Manages Steam callbacks and connection state
- Handles login, auth code, and 2FA flows
- Maintains multi-account login state

## Actions

The full catalog of all 65 actions in the repo (payload fields, timeouts, safety classifications) is in the [actions catalog](actions.md). Here are 5 of the smallest actions as examples:

| Action | Description | Requires Login | Timeout |
|--------|-------------|----------------|---------|
| `ping` | Heartbeat check | No | 10s |
| `echo` | Echoes the payload | No | 10s |
| `login` | Logs in to Steam | No | 60s |
| `idle` | Simulates being online | Yes | 300s |
| `redeem_key` | Activates a game key | Yes | 60s |

## Usage Examples

### Registering a Custom Action
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
        CancellationToken cancellationToken
    )
    {
        // 实现动作逻辑
        var output = new Dictionary<string, object?>();
        return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
    }
}

// 注册
actionRegistry.Register(new MyAction());
```

### Using in an Agent
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

## SteamKit2 Integration

SteamClientManager handles all network communication with Steam:
- Manages a single shared SteamClient instance
- Handles Steam callbacks via CallbackManager
- Manages multi-account credentials and logs in one account at a time on the shared connection (a single client only performs one account's login at any moment)
- Handles auth code and 2FA requests
- Supports saving access token and refresh token

### Authentication Flow

1. Initial login requires a password
2. When Steam responds that an auth code or 2FA is required, the Session state becomes `ConnectingWaitAuthCode` or `ConnectingWait2FA`; QR code login goes through `ConnectingWaitQr`
3. Codes are provided via `ProvideAuthCode()` / `Provide2FACode()`, or delivered through the SSE channel from codes submitted in the admin panel; accounts configured with a mobile authenticator shared secret can be answered automatically by the agent-side `TwoFactorAutoResponder`
4. After a successful login, the access token and refresh token are saved for subsequent logins — after an agent restart, the session is restored from them without manual re-login

## Boundaries

Session credentials and Steam Guard codes never leave the agent (only booleans cross the boundary); social actions such as friends and groups are not implemented; cross-region scheduling is the control plane's responsibility and is out of scope for this layer.
