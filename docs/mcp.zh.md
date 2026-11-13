# MCP server

`Vapor.Mcp` 通过 [Model Context Protocol](https://modelcontextprotocol.io)
（stdio 传输，官方 `ModelContextProtocol` C# SDK）把控制面开放给 AI agent。
该服务器是一个**薄代理**：每个工具调用就是对运行中控制面既有 REST 面的一次
HTTP 调用——MCP 调用方看到的与仪表盘和 REST 客户端完全一致，不存在第二套
查询路径，也绕不开控制面的 admin 鉴权与审计账。

## 启动

```bash
dotnet run --project src/Vapor.Mcp -c Release
```

进程通过 stdin/stdout 说 MCP 协议。stdio 传输独占 stdout 承载协议帧——宿主
的全部日志管线（以及启动失败信息）走 stderr，因此重定向不会污染协议。没有
admin key 时服务器拒绝启动（退出码 2）：fail-closed，不存在匿名读面。

## 配置

| 变量 | 含义 | 缺省 |
|----------|---------|---------|
| `Vapor_MCP_BASE_URL` | 控制面 base URL | `http://127.0.0.1:8080` |
| `Vapor_MCP_ADMIN_API_KEY` | Admin API key（每次工具调用的 Bearer 凭据） | —（必需） |
| `MCP_WRITE_ENABLED` | `1` 或 `true`（忽略大小写）时注册写工具；也接受别名 `Vapor_MCP_WRITE_ENABLED` | 未设 → 只读 |

## 工具面

### 只读（始终注册）

| 工具 | 对应端点 | 说明 |
|------|------------------|-------|
| `list_agents` | `GET /v1/agents` | 声明的 agent 清单，含离线。 |
| `agent_status` | `GET /v1/agents/status` | 单个 agent 的连接状态行（region/capabilities/connectedAt）；id 大小写无关。 |
| `list_accounts` | `GET /v1/accounts` | 可选 `state` / `region` / `agent` 过滤。输出先过白名单脱敏——见下。 |
| `list_plugins` | `GET /v1/plugins/installed` | 各 agent 最后上报的插件清单。 |
| `plugin_status` | `GET /v1/plugins/installed` | 跨 agent 收集某插件 id 的上报行；每行带上报 agent、上报时间、插件条目与该 agent 的更新判定。 |
| `recent_events` | `GET /v1/audit/logs` | 审计/事件账查询；`limit` 钳制 1–500（缺省 20），可选 `action` / `account` / `agentId` 过滤。 |
| `metrics_snapshot` | `GET /metrics` | 控制面指标的 Prometheus 原文。 |

### 门控写（仅在门开启时注册）

| 工具 | 对应端点 | 说明 |
|------|------------------|-------|
| `enable_account` | `POST /v1/accounts/{name}/enable` | 恢复朝声明态的编排；控制面记审计。 |
| `disable_account` | `POST /v1/accounts/{name}/disable` | 停止编排；声明态与配置保留；记审计。 |

门控把工具从注册表里彻底移除：只读服务器的工具清单里根本没有
`enable_account`/`disable_account`——不是「注册了但拒绝」，是不存在。

为什么不是 `start_agent`/`stop_agent`？vapor 的架构里 agent 主动**拨入**
控制面（WebSocket），控制面没有启动/停止 agent 进程的通道。这一层诚实的
写面就是账号声明态，门控写工具驱动的是它。agent 进程生命周期归宿主管
（systemd/Docker），不归控制面。

### 刻意不做

真实 Steam 动作——开箱、消耗、交易——完全不经 MCP 暴露。它们仍归
CsgoPlugin 自身的配置门控（`open.enabled`/`consume.enabled`，默认关）与
41-4 真实环境验收管；MCP 不绕过任何既有门控。

## 凭据脱敏

`list_accounts` 与两个写工具在输出前把账号载荷投影到 `AccountSpec` 字段的
显式白名单（`accountName`、`enabled`、`desiredState`、`idleApps`、`region`、
`agentId`、`note`、`version`、`marketListingsEnabled`、`boostTargets`、
`tradePolicy`、`farmPolicy`、`role`、`steamId`）。这是对架构的再加一层保险
——控制面本就不存凭据——但白名单保证即使上游将来出现凭据形状的字段（密码、
`maFile` 路径、`shared_secret`/`identity_secret`、refresh/access token），
它也无法抵达工具输出：未知字段被丢弃，而非透传。白名单由测试钉死
（`AccountSanitizerTests.SafeFields_AreASubsetOfTheDeclaredAccountSpecWireShape`）。

## 客户端配置

Claude Code（`~/.claude.json` 或项目 `.mcp.json`）：

```json
{
  "mcpServers": {
    "vapor": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/vapor/src/Vapor.Mcp", "--no-build", "-c", "Release", "--"],
      "env": {
        "Vapor_MCP_BASE_URL": "http://127.0.0.1:8080",
        "Vapor_MCP_ADMIN_API_KEY": "admin-dev-token"
      }
    }
  }
}
```

Cursor（`~/.cursor/mcp.json`）使用同样的 `mcpServers` 形状。若想预构建而非
`dotnet run`：先 `dotnet publish src/Vapor.Mcp -c Release -o /opt/vapor-mcp`，
再把 `"command"` 指到产物可执行文件（Linux `/opt/vapor-mcp/Vapor.Mcp`，
Windows `/opt/vapor-mcp/Vapor.Mcp.exe`）。

## 测试

`tests/Vapor.Mcp.Tests`（48 个测试方法 / 运行时 83 例）覆盖：写门（关时注册
表无写工具，开时恰两个）、脱敏器（向上游载荷注入凭据形状字段后输出永不出现）、
REST 代理客户端（Bearer 头/错误映射/传输失败/取消）与两个工具面经 stub
HTTP handler 的端到端。
