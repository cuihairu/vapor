# Running (local dev)

## Control plane

Prerequisites:
- .NET 8 runtime (SDK 8.x recommended)
  - You may build with a newer SDK (e.g. .NET 9), but running `net8.0` binaries still requires the .NET 8 runtime (or `DOTNET_ROLL_FORWARD=Major`).

Environment variables:
- `ASPNETCORE_URLS` (e.g. `http://127.0.0.1:8080`)
- `Vapor_ADMIN_API_KEY` (required for admin REST calls)
- `Vapor_AGENT_API_KEYS` (comma-separated; required for agent tunnel)
- `Vapor_DB_PATH` (default `data/controlplane.db`; use `:memory:` for ephemeral)
- `Vapor_TASK_LEASE_SECONDS` (default `300`; requeues running tasks that stop heartbeating)
- `Vapor_ENABLE_SWAGGER` (set `true` to expose `/swagger`)

Run:

```bash
export ASPNETCORE_URLS=http://127.0.0.1:8080
export Vapor_ADMIN_API_KEY=dev-admin
export Vapor_AGENT_API_KEYS=dev-agent
export Vapor_DB_PATH=:memory:
dotnet run --project src/Vapor.ControlPlane
```

## Admin UI

After starting the control plane, open your browser to:

```
http://127.0.0.1:8080/
```

The admin UI provides:
- Dashboard with real-time statistics
- Job creation and management
- Agent status monitoring
- Interactive auth challenge handling
- Real-time event streaming

## Agent

Environment variables:
- `AGENT_ID` (required)
- `AGENT_REGION` (required)
- `AGENT_CONTROLPLANE_WS_URL` (required, e.g. `ws://127.0.0.1:8080/v1/agent/ws`)
- `AGENT_API_KEY` (required, must match one entry in `Vapor_AGENT_API_KEYS`)

Run:

```bash
export AGENT_ID=agent-1
export AGENT_REGION=local
export AGENT_CONTROLPLANE_WS_URL=ws://127.0.0.1:8080/v1/agent/ws
export AGENT_API_KEY=dev-agent
dotnet run --project src/Vapor.Agent
```

## API Examples

### Submit a job

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"ping","region":"local","targets":["acct-1","acct-2"]}'
```

### Login with password

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"login","region":"local","targets":["acct-1"],"payload":{"password":"<steam-password>"}}'
```

### Redeem a game key

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"redeem_key","region":"local","targets":["acct-1"],"payload":{"key":"AAAAA-BBBBB-CCCCC"}}'
```

### Submit auth code (when challenged)

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"code":"<email-code>","type":"email"}'
```

### Watch job events (SSE)

```bash
curl -N http://127.0.0.1:8080/v1/jobs/<jobId>/events -H "Authorization: Bearer dev-admin"
```

### Watch session events (SSE)

```bash
curl -N http://127.0.0.1:8080/v1/sessions/events?accountName=acct-1 -H "Authorization: Bearer dev-admin"
```

### Watch auth challenge events (SSE)

```bash
curl -N http://127.0.0.1:8080/v1/auth/challenges/events -H "Authorization: Bearer dev-admin"
```

Note: the built-in Admin UI uses `EventSource`, which cannot reliably attach custom headers in browsers; it passes the token via `?authorization=<token>` instead.

### List jobs

```bash
curl -sS "http://127.0.0.1:8080/v1/jobs?limit=50" -H "Authorization: Bearer dev-admin"
```

### List agents

```bash
curl -sS http://127.0.0.1:8080/v1/agents/status -H "Authorization: Bearer dev-admin"
```

### Read config

```bash
curl -sS http://127.0.0.1:8080/v1/config -H "Authorization: Bearer dev-admin"
```

### Update global config

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/config/global \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"settings":{"defaultRegion":"local","maxConcurrentJobs":20},"updatedBy":"local-dev"}'
```

### Update account config

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/config/account/acct-1 \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"enabled":true,"region":"local","labels":["vip"],"settings":{"proxy":"auto"}}'
```

### Cancel a job

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs/<jobId>/cancel -H "Authorization: Bearer dev-admin"
```

## Auth Challenge Workflow

When a Steam account requires email or 2FA authentication:

1. The session enters `ConnectingWaitAuthCode` or `ConnectingWait2FA` state
2. Control Plane publishes an auth challenge event
3. Admin UI displays a notification
4. Submit the code via:
   - **Admin UI**: Enter code in the auth challenge panel
   - **API**: `POST /v1/auth/challenges/{accountName}/code`
5. Agent receives the code and continues login

### Example auth code submission

```bash
# Email code
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"code":"123456","type":"email"}'

# TOTP code (Steam Authenticator)
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"code":"987654","type":"totp"}'
```

