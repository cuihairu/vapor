# Running (local dev)

## Control plane

Prerequisites:
- .NET SDK 8+

Environment variables:
- `ASPNETCORE_URLS` (e.g. `http://127.0.0.1:8080`)
- `STEAMCONTROL_ADMIN_API_KEY` (required for admin REST calls)
- `STEAMCONTROL_AGENT_API_KEYS` (comma-separated; required for agent tunnel)
- `STEAMCONTROL_DB_PATH` (default `data/controlplane.db`; use `:memory:` for ephemeral)
- `STEAMCONTROL_TASK_LEASE_SECONDS` (default `300`; requeues stale running tasks)
- `STEAMCONTROL_ENABLE_SWAGGER` (set `true` to expose `/swagger`)

Run:

```bash
export ASPNETCORE_URLS=http://127.0.0.1:8080
export STEAMCONTROL_ADMIN_API_KEY=dev-admin
export STEAMCONTROL_AGENT_API_KEYS=dev-agent
export STEAMCONTROL_DB_PATH=:memory:
dotnet run --project src/SteamControl.ControlPlane
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
- `AGENT_API_KEY` (required, must match one entry in `STEAMCONTROL_AGENT_API_KEYS`)

Run:

```bash
export AGENT_ID=agent-1
export AGENT_REGION=local
export AGENT_CONTROLPLANE_WS_URL=ws://127.0.0.1:8080/v1/agent/ws
export AGENT_API_KEY=dev-agent
dotnet run --project src/SteamControl.Agent
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

### List jobs

```bash
curl -sS "http://127.0.0.1:8080/v1/jobs?limit=50" -H "Authorization: Bearer dev-admin"
```

### List agents

```bash
curl -sS http://127.0.0.1:8080/v1/agents/status -H "Authorization: Bearer dev-admin"
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
