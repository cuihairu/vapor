简体中文 | [English](../running/)

# 本地运行（开发环境）

## 控制平面

前置条件：
- .NET 10 运行时（推荐 SDK 10.x）
  - 运行二进制/测试需要与目标 TFM 匹配的运行时。

环境变量：
- `ASPNETCORE_URLS`（例如 `http://127.0.0.1:8080`）
- `Vapor_ADMIN_API_KEY`（管理 REST 调用必填）
- `Vapor_AGENT_API_KEYS`（逗号分隔；Agent 隧道必填）
- `Vapor_DB_PATH`（默认 `data/controlplane.db`；临时环境用 `:memory:`）
- `Vapor_TASK_LEASE_SECONDS`（默认 `300`；停止心跳的运行中任务会被重新入队）
- `Vapor_TASK_MAX_DISPATCH_ATTEMPTS`（默认 `10`；无法派发的任务——例如没有 Agent 支持的动作——带延迟重入队，超过此次数后永久失败；设 `0` 表示无限重试。自执行安全分级起，实际上限按动作收口：`GuardedWrite` / `NonIdempotent` / 未分级（`Unknown`）动作无论此值如何都封顶**共 2 次尝试**；`ReadOnly` / `Idempotent` 动作原样使用此值）
- `Vapor_TASK_DISPATCH_RETRY_DELAY_MS`（默认 `2000`；未派发任务可被再次认领前的延迟）
- `Vapor_ENABLE_SWAGGER`（设 `true` 暴露 `/swagger`）
- `VAPOR_ENCRYPTION_KEY`（生产环境推荐；自定义加密密钥，最少 32 字节）
- `VAPOR_ALLOW_INSECURE_DEFAULT_KEY`（生产环境的可选逃生门，不推荐）

凭据存储说明：
- 新写入的凭据使用 AES-GCM，带随机 nonce 与认证标签。
- 既有的 AES-CBC 凭据记录仍可读，保持向后兼容。

运行：

```bash
export ASPNETCORE_URLS=http://127.0.0.1:8080
export Vapor_ADMIN_API_KEY=dev-admin
export Vapor_AGENT_API_KEYS=dev-agent
export Vapor_DB_PATH=:memory:
export VAPOR_ENCRYPTION_KEY=0123456789abcdef0123456789abcdef
dotnet run --project src/Vapor.ControlPlane
```

## 管理界面

控制平面启动后，浏览器打开：

```
http://127.0.0.1:8080/
```

管理界面提供：
- 实时统计面板
- 任务创建与管理
- Agent 状态监控
- 交互式认证挑战处理
- 实时事件流

## Agent

环境变量：
- `AGENT_ID`（必填）
- `AGENT_REGION`（必填）
- `AGENT_CONTROLPLANE_WS_URL`（必填，例如 `ws://127.0.0.1:8080/v1/agent/ws`）
- `AGENT_API_KEY`（必填，必须与 `Vapor_AGENT_API_KEYS` 中某一项匹配）
- `VAPOR_ENCRYPTION_KEY`（生产环境推荐；自定义加密密钥，最少 32 字节）
- `VAPOR_ALLOW_INSECURE_DEFAULT_KEY`（生产环境的可选逃生门，不推荐）
- `AGENT_RECONNECT_INITIAL_DELAY_MS`（可选，默认 `500`）
- `AGENT_RECONNECT_MAX_DELAY_MS`（可选，默认 `10000`）
- `AGENT_RECONNECT_BACKOFF_FACTOR`（可选，默认 `2`）
- `AGENT_RECONNECT_MAX_RETRIES`（可选，默认 `0` 表示不限次数）

运行：

```bash
export AGENT_ID=agent-1
export AGENT_REGION=local
export AGENT_CONTROLPLANE_WS_URL=ws://127.0.0.1:8080/v1/agent/ws
export AGENT_API_KEY=dev-agent
export VAPOR_ENCRYPTION_KEY=0123456789abcdef0123456789abcdef
export AGENT_RECONNECT_MAX_RETRIES=10
dotnet run --project src/Vapor.Agent
```

## API 示例

### 提交任务

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"ping","region":"local","targets":["acct-1","acct-2"]}'
```

### 密码登录

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"login","region":"local","targets":["acct-1"],"payload":{"password":"<steam-password>"}}'
```

### 兑换游戏 Key

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"redeem_key","region":"local","targets":["acct-1"],"payload":{"key":"AAAAA-BBBBB-CCCCC"}}'
```

### 提交验证码（被挑战时）

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"code":"<email-code>","type":"email"}'
```

### 围观任务事件（SSE）

```bash
curl -N http://127.0.0.1:8080/v1/jobs/<jobId>/events -H "Authorization: Bearer dev-admin"
```

### 围观会话事件（SSE）

```bash
curl -N http://127.0.0.1:8080/v1/sessions/events?accountName=acct-1 -H "Authorization: Bearer dev-admin"
```

### 围观认证挑战事件（SSE）

```bash
curl -N http://127.0.0.1:8080/v1/auth/challenges/events -H "Authorization: Bearer dev-admin"
```

注意：内置管理界面使用 `EventSource`，浏览器中无法可靠附加自定义头；它改用 `?authorization=<token>` 传递令牌。

### 列出任务

```bash
curl -sS "http://127.0.0.1:8080/v1/jobs?limit=50" -H "Authorization: Bearer dev-admin"
```

### 列出 Agent

```bash
curl -sS http://127.0.0.1:8080/v1/agents/status -H "Authorization: Bearer dev-admin"
```

### 读取配置

```bash
curl -sS http://127.0.0.1:8080/v1/config -H "Authorization: Bearer dev-admin"
```

### 更新全局配置

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/config/global \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"settings":{"defaultRegion":"local","maxConcurrentJobs":20},"updatedBy":"local-dev"}'
```

### 更新账号配置

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/config/account/acct-1 \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"enabled":true,"region":"local","labels":["vip"]}'
```

`settings` 是自由形态的键值袋——原样存储与回显，控制平面不会对个别键做出任何动作。账号级**出口代理不是配置项**：它是 Agent 侧的凭据，通过 `POST /v1/accounts/{name}/proxy` 钉定（见 `docs/api.md` §4.x），该调用会向账号钉定的 Agent 派发 `set_proxy` 宿主动作，并链一个验证实际出口的 `check_proxy`。

### 取消任务

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs/<jobId>/cancel -H "Authorization: Bearer dev-admin"
```

## 认证挑战工作流

当 Steam 账号需要邮箱或 2FA 认证时：

1. 会话进入 `ConnectingWaitAuthCode` 或 `ConnectingWait2FA` 状态
2. 控制平面发布认证挑战事件
3. 管理界面显示通知
4. 通过以下方式提交验证码：
   - **管理界面**：在认证挑战面板输入验证码
   - **API**：`POST /v1/auth/challenges/{accountName}/code`
5. Agent 收到验证码后继续登录

### 验证码提交示例

```bash
# 邮箱验证码
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"code":"123456","type":"email"}'

# TOTP 验证码（Steam Authenticator）
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"code":"987654","type":"totp"}'
```
