# Troubleshooting

Symptom → diagnosis → fix reference for operating Vapor. All `curl` examples
assume `Authorization: Bearer $VAPOR_ADMIN_API_KEY` where required.

## Diagnostic toolbox

| Tool | What it tells you |
|------|-------------------|
| `GET /healthz` | Control plane process is up (public, no auth) |
| `GET /v1/agents` | Registered agents, capabilities, online/offline |
| `GET /v1/agents/status` | Live agent session/heartbeat state |
| `GET /v1/jobs/{id}` | Job + per-task `status`, `attempt`, `error`, `output` |
| `GET /v1/jobs/{id}/events` | SSE stream: `task.dispatch_failed`, `task.failed`, … |
| `GET /v1/audit/logs` | Persisted audit trail of admin/job actions |
| Agent `:9700/metrics` | Agent process metrics (also its healthcheck) |
| Control plane logs | Structured console logs with credential redaction |

## Agent never shows online

**Symptoms**: `GET /v1/agents` empty or agent stays offline; agent logs show
reconnect attempts.

1. **Key mismatch** — agent log shows the WebSocket handshake being rejected
   with 401. `AGENT_API_KEY` must be one of the control plane's
   `Vapor_AGENT_API_KEYS` (comma-separated list).
2. **Wrong URL** — `AGENT_CONTROLPLANE_WS_URL` must include the path
   (`ws://controlplane:8080/v1/agent/ws`). From outside a compose network use
   the published host address.
3. **Control plane not healthy yet** — the agent service has
   `depends_on: controlplane: service_healthy`; check
   `docker compose ps` and `GET /healthz` first.

The agent reconnects forever by default (500ms → 10s exponential backoff).
Tune with `AGENT_RECONNECT_*` variables; fix the root cause rather than
raising the retry ceiling.

## Task stuck in `queued`

**Symptoms**: `GET /v1/jobs/{id}` shows `status: "Queued"`, `attempt` not
moving; repeated `task.dispatch_failed` events on the SSE stream.

This means no **capable** agent is currently claimable for the task:

- No agent online in the task's region, **or**
- No online agent's capabilities include the task's action (e.g. the action
  exists on no registered agent — a typo'd action name looks exactly like
  this), **or**
- The task recently failed dispatch and is waiting out its retry delay.

Historical note: tasks used to stay queued forever in this situation. They
now carry terminal state — see the next section.

## Task failed with "no capable agent available"

**Symptoms**: task reaches `status: "Failed"`, `error` contains
`dispatch failed after N attempts: no capable agent available`, job shows
`failed`.

This is the dispatch terminal state working as designed: the task was
requeued with a delay (`Vapor_TASK_DISPATCH_RETRY_DELAY_MS`, default 2s)
after each attempt and permanently failed after
`Vapor_TASK_MAX_DISPATCH_ATTEMPTS` (default 10; `0` = retry forever).

Tuning:

- Long agent maintenance window? Raise the attempt limit or set `0` (and
  rely on the task lease to clean up genuinely dead tasks).
- Fast-fail environments (CI/tests)? Lower the limit and the delay — the
  E2E suite uses `3` attempts / `200ms`.

The `error` column and `task.failed` SSE event carry the reason; REST
`GET /v1/jobs/{id}` shows it per task.

## Task stuck in `running`

**Symptoms**: `status: "Running"`, no completion, agent died or lost
connectivity mid-task.

The control plane expires running tasks whose heartbeat is older than
`Vapor_TASK_LEASE_SECONDS` (default 300s): the task is requeued and picked
up by any capable agent. If it is genuinely still running on a partitioned
agent, completion will be rejected as a stale attempt — expect the requeued
run to be authoritative.

For long-lived actions, make sure the agent's heartbeat interval is well
under the lease window.

## Job fails with credential/decryption errors

**Symptoms**: sessions fail to log in; agent logs show AES/GCM errors.

Stored credentials are encrypted with `VAPOR_ENCRYPTION_KEY` (AES-GCM, ≥32
bytes). A key change makes previously stored credentials unreadable.
Restore the original key or re-enter credentials. Legacy AES-CBC records
remain readable for migration purposes.

## Metrics endpoint unreachable

**Symptoms**: Prometheus target down; agent healthcheck failing (the
healthcheck queries `:9700/metrics`).

- `VAPOR_METRICS_HOST`/`VAPOR_METRICS_PORT` must match the published port
  mapping (`${VAPOR_AGENT_METRICS_PORT:-9700}:9700`).
- The metrics server binds after startup; during the first seconds the
  healthcheck relies on `start_period: 15s`.

## Control plane fails to start

1. **Database path not writable** — the container runs as non-root; the
   `/app/data` volume must be writable by `APP_UID`.
2. **Corrupt SQLite file** — restore from backup (see
   [production.md](production.md)).
3. **Missing required env** — `Vapor_ADMIN_API_KEY` /
   `Vapor_AGENT_API_KEYS` unset causes a startup failure with an explicit
   message.

## Reading job state from SQLite directly

When REST is unavailable, the store is a plain SQLite database:

```sql
SELECT id, status, attempt, error FROM tasks WHERE job_id = $job;
SELECT status, COUNT(*) FROM tasks GROUP BY status;
```

Schema changes (new columns) are applied automatically on control plane
start — an out-of-date binary reading a migrated database ignores the new
columns.
