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

## Submit a job

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer dev-admin" \
  -H "Content-Type: application/json" \
  -d '{"action":"ping","region":"local","targets":["acct-1","acct-2"]}'
```

## Watch job events (SSE)

```bash
curl -N http://127.0.0.1:8080/v1/jobs/<jobId>/events -H "Authorization: Bearer dev-admin"
```

## List jobs

```bash
curl -sS "http://127.0.0.1:8080/v1/jobs?limit=50" -H "Authorization: Bearer dev-admin"
```

## Cancel a job

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs/<jobId>/cancel -H "Authorization: Bearer dev-admin"
```
