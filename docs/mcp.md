# MCP server

`Vapor.Mcp` exposes the control plane to AI agents over the
[Model Context Protocol](https://modelcontextprotocol.io) (stdio transport,
official `ModelContextProtocol` C# SDK). The server is a **thin proxy**: every
tool call is an HTTP call to the running control plane's existing REST faces,
so MCP callers see exactly what the dashboard and REST clients see — there is
no second query path and no way around the control plane's admin
authentication or its audit log.

## Running it

```bash
dotnet run --project src/Vapor.Mcp -c Release
```

The process speaks MCP over stdin/stdout. The stdio transport owns stdout for
protocol frames — the host's entire log pipeline (and startup failures) go to
stderr, so redirection stays protocol-clean. Without an admin key the server
refuses to start (exit code 2): fail-closed, no anonymous read face.

## Configuration

| Variable | Meaning | Default |
|----------|---------|---------|
| `Vapor_MCP_BASE_URL` | Control plane base URL | `http://127.0.0.1:8080` |
| `Vapor_MCP_ADMIN_API_KEY` | Admin API key (Bearer credential for every tool call) | — (required) |
| `MCP_WRITE_ENABLED` | `1` or `true` (case-insensitive) registers the write tools; `Vapor_MCP_WRITE_ENABLED` is accepted as an alias | unset → read-only |

## Tool surface

### Read-only (always registered)

| Tool | Backing endpoint | Notes |
|------|------------------|-------|
| `list_agents` | `GET /v1/agents` | Declared agents, including offline ones. |
| `agent_status` | `GET /v1/agents/status` | One agent's connection row (region, capabilities, connected since); case-insensitive id match. |
| `list_accounts` | `GET /v1/accounts` | Optional `state` / `region` / `agent` filters. Output passes a whitelist sanitizer first — see below. |
| `list_plugins` | `GET /v1/plugins/installed` | Per-agent last-reported plugin inventory. |
| `plugin_status` | `GET /v1/plugins/installed` | Rows for one plugin id across agents. |
| `recent_events` | `GET /v1/audit/logs` | Audit/event log query; `limit` clamped to 1–500 (default 20), optional `action` / `account` / `agentId` filters. |
| `metrics_snapshot` | `GET /metrics` | Raw Prometheus text of the control plane metrics. |

### Gated writes (registered only when the gate is on)

| Tool | Backing endpoint | Notes |
|------|------------------|-------|
| `enable_account` | `POST /v1/accounts/{name}/enable` | Resumes orchestration toward the declared desired state; audited by the control plane. |
| `disable_account` | `POST /v1/accounts/{name}/disable` | Stops orchestration; desired state and config are kept; audited. |

The gate removes the tools from the registry entirely: a read-only server's
tools list simply has no `enable_account`/`disable_account` entries — they are
not registered-and-refusing, they do not exist.

Why not `start_agent`/`stop_agent`? In vapor's architecture agents dial **in**
to the control plane over WebSocket; the control plane has no channel to spawn
or stop agent processes. The honest write surface at this layer is the
account desired state, so that is what the gated tools drive. Agent process
lifecycle belongs to the host (systemd/Docker), not the control plane.

### Deliberately absent

Real Steam actions — case opening, consumption, trades — are not exposed over
MCP at all. Those stay behind the CsgoPlugin's own config gates
(`open.enabled`/`consume.enabled`, default off) and the 41-4 real-environment
acceptance work; MCP does not bypass any existing gate.

## Credential sanitization

`list_accounts` and the two write tools project account payloads onto an
explicit whitelist of `AccountSpec` fields (`accountName`, `enabled`,
`desiredState`, `idleApps`, `region`, `agentId`, `note`, `version`,
`marketListingsEnabled`, `boostTargets`, `tradePolicy`, `farmPolicy`, `role`,
`steamId`) before anything leaves the server. This is belt-and-suspenders on
top of the architecture — the control plane never stores credentials — but the
whitelist guarantees that even if a credential-shaped field ever appears
upstream (passwords, `maFile` paths, `shared_secret`/`identity_secret`,
refresh/access tokens), it cannot reach tool output: unknown fields are
dropped, not passed through. The whitelist is pinned by tests
(`AccountSanitizerTests.SafeFields_AreASubsetOfTheDeclaredAccountSpecWireShape`).

## Client configuration

Claude Code (`~/.claude.json` or project `.mcp.json`):

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

Cursor (`~/.cursor/mcp.json`) uses the same `mcpServers` shape. To pre-build
instead of using `dotnet run`, publish once
(`dotnet publish src/Vapor.Mcp -c Release -o /opt/vapor-mcp`) and point
`"command"` at the produced executable (`/opt/vapor-mcp/Vapor.Mcp` on Linux,
`/opt/vapor-mcp/Vapor.Mcp.exe` on Windows).

## Tests

`tests/Vapor.Mcp.Tests` (48 test methods, 83 runtime cases) covers the write
gate (registry has no write tools when off, exactly two when on), the
sanitizer (credential-shaped fields injected into upstream payloads never
appear in output), the REST proxy client (bearer header, error mapping,
transport failures, cancellation) and both tool faces end to end against a
stubbed HTTP handler.
