# Production deployment

This guide covers deploying Vapor with Docker Compose in a production-like
environment. For local development see [running.md](running.md); image details
are in [docker.md](docker.md).

## Topology

```
            ┌────────────────────┐        WebSocket /v1/agent/ws
 clients ──▶│   Control plane    │◀──────────────────────────────┐
  (REST/SSE)│  REST + SSE + DB   │                               │
            │  SQLite (/app/data)│                    ┌──────────┴───┐
            └────────────────────┘                    │    Agent     │──▶ Steam network
                      ▲    ▲                          │ sessions,    │
                      │    └── Prometheus (:9090)      │ actions,     │
                      └──────── Grafana   (:3000)      │ plugins :9700│
                                                       └──────────────┘
```

- **Control plane**: stateless HTTP service; all state lives in two SQLite
  databases (jobs/audit) mounted at `/app/data`.
- **Agent**: stateful worker holding Steam sessions and per-machine
  credentials at `/app/.vapor`. Connects out to the control plane — no
  inbound ports required except the metrics endpoint (`:9700`).
- **Observability** (optional profile): Prometheus scrapes both services;
  Grafana ships pre-provisioned dashboards.

## Configuration

### Control plane

| Variable | Required | Default | Notes |
|----------|----------|---------|-------|
| `Vapor_ADMIN_API_KEY` | yes | — | Bearer key for admin REST/SSE endpoints |
| `Vapor_AGENT_API_KEYS` | yes | — | Comma-separated keys the agent tunnel accepts |
| `Vapor_DB_PATH` | no | `data/controlplane.db` | Main store; volume-mounted in compose |
| `Vapor_AUDIT_DB_PATH` | no | (derived) | Audit log store |
| `Vapor_TASK_LEASE_SECONDS` | no | `300` | Running tasks whose heartbeat stops for this long are requeued |
| `Vapor_TASK_MAX_DISPATCH_ATTEMPTS` | no | `10` | Permanent failure threshold for undispatchable tasks; `0` retries forever |
| `Vapor_TASK_DISPATCH_RETRY_DELAY_MS` | no | `2000` | Backoff before an undispatched task may be claimed again |
| `Vapor_ENABLE_SWAGGER` | no | off | Keep off in production |
| `VAPOR_ENCRYPTION_KEY` | recommended | — | ≥32 bytes; encrypts stored credentials (AES-GCM) |
| `VAPOR_ALLOW_INSECURE_DEFAULT_KEY` | no | off | Escape hatch; do not enable in production |

| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | off | OTLP endpoint (e.g. `http://tempo:4317`); enables distributed tracing export |

### Agent

| Variable | Required | Default | Notes |

| Variable | Required | Default | Notes |
|----------|----------|---------|-------|
| `AGENT_ID` | yes | — | Unique, stable id (shows up in `/v1/agents`) |
| `AGENT_REGION` | yes | — | Region used for task routing |
| `AGENT_CONTROLPLANE_WS_URL` | yes | — | e.g. `ws://controlplane:8080/v1/agent/ws` |
| `AGENT_API_KEY` | yes | — | Must be one of `Vapor_AGENT_API_KEYS` |
| `VAPOR_PLUGINS_DIR` | no | — | Plugin directory; the image ships Monitoring preinstalled |
| `VAPOR_METRICS_HOST` / `VAPOR_METRICS_PORT` | no | `:9700` | Prometheus endpoint, also the agent healthcheck |
| `AGENT_RECONNECT_INITIAL_DELAY_MS` | no | `500` | Reconnect backoff start |
| `AGENT_RECONNECT_MAX_DELAY_MS` | no | `10000` | Reconnect backoff ceiling |
| `AGENT_RECONNECT_BACKOFF_FACTOR` | no | `2` | Exponential factor |
| `AGENT_RECONNECT_MAX_RETRIES` | no | `0` | `0` = retry forever |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | off | OTLP endpoint; enables distributed tracing export |

### Steps

### Images

Released tags publish versioned images to GHCR
(`ghcr.io/cuihairu/vapor/controlplane:<X.Y.Z>` and `.../agent:<X.Y.Z>`;
official releases also move `:latest`). To run them instead of building
locally, create a `docker-compose.override.yml`:

```yaml
services:
  controlplane:
    image: ghcr.io/cuihairu/vapor/controlplane:1.0.0
    build: null
  agent:
    image: ghcr.io/cuihairu/vapor/agent:1.0.0
    build: null
```

Compose merges the override automatically; `build: null` disables the
local build so the published image is used. Pin a specific version in
production and roll back by pinning the previous one.

### Secrets

1. Create a `.env` next to `docker-compose.yml` with strong secrets:

   ```bash
   VAPOR_ADMIN_API_KEY=$(openssl rand -hex 32)
   VAPOR_AGENT_API_KEY=$(openssl rand -hex 32)
   VAPOR_ENCRYPTION_KEY=$(openssl rand -hex 32)
   ```

   Record these in your secret manager — the encryption key is required to
   read back stored credentials and cannot be recovered.

2. Bring the stack up:

   ```bash
   docker compose --env-file .env up -d
   # optional metrics stack:
   docker compose --env-file .env --profile observability up -d
   ```

3. Verify:

   ```bash
   curl -fsS localhost:8080/healthz                       # {"ok":true}
   curl -fsS -H "Authorization: Bearer $VAPOR_ADMIN_API_KEY" \
        localhost:8080/v1/agents                          # agent listed as online
   curl -fsS localhost:9700/metrics | head               # agent metrics
   ```

4. Put a TLS-terminating reverse proxy in front of the control plane and
   restrict `:8080` / `:9700` to trusted networks. All sensitive endpoints
   require the admin key; `/healthz` is intentionally public.

## Security checklist

- [ ] Random `Vapor_ADMIN_API_KEY` / `Vapor_AGENT_API_KEYS` (32+ hex chars)
- [ ] `VAPOR_ENCRYPTION_KEY` set, `VAPOR_ALLOW_INSECURE_DEFAULT_KEY` unset
- [ ] `Vapor_ENABLE_SWAGGER` unset
- [ ] Control plane behind TLS; metrics ports firewalled from the internet
- [ ] Containers run as non-root (default in both images)
- [ ] Audit log (`Vapor_AUDIT_DB_PATH`) retained per your compliance policy

## Data, backup and upgrades

State lives in two named volumes:

| Volume | Content |
|--------|---------|
| `controlplane-data` (`/app/data`) | `controlplane.db` (jobs/tasks/agents), `audit.db` |
| `agent-data` (`/app/.vapor`) | Per-agent credentials and session data |

Back up with SQLite's online API (consistent while the control plane keeps
running), using any SQLite image that mounts the same volume:

```bash
docker run --rm -v vapor_controlplane-data:/data:ro alpine/sqlite3 \
  /data/controlplane.db ".backup '/data/backup-controlplane-$(date +%F).db'"
```

The runtime image itself ships no `sqlite3` CLI. Alternatively, stop the
control plane first and copy the volume contents — file-level copies of a
stopped SQLite database are always consistent.

Upgrades:

1. Pull the new images (or rebuild locally) and review `CHANGELOG.md`.
2. `docker compose up -d` — SQLite schema migrations are applied
   automatically on first start; old databases gain new columns in place
   (no dump/restore needed).
3. Roll back by pinning the previous image tag in the override file and
   re-running `docker compose up -d`; note that databases migrated by a
   newer version keep the added columns, which older versions ignore
   safely.

## Scaling out agents

Each agent is one `agent` service instance with a unique `AGENT_ID` and
`AGENT_REGION`. Tasks are routed to agents in the job's region that
advertise the required action capability. To add capacity:

```yaml
  agent-2:
    build:
      context: .
      dockerfile: src/Vapor.Agent/Dockerfile
    image: vapor/agent:local
    restart: unless-stopped
    environment:
      AGENT_ID: agent-2
      AGENT_REGION: us-east
      AGENT_CONTROLPLANE_WS_URL: ws://controlplane:8080/v1/agent/ws
      AGENT_API_KEY: "${VAPOR_AGENT_API_KEY}"
      VAPOR_METRICS_HOST: 0.0.0.0
      VAPOR_METRICS_PORT: "9700"
    depends_on:
      controlplane:
        condition: service_healthy
```

Scaling considerations:

- Agents are independent; one going offline only pauses its tasks until the
  control plane's task lease (`Vapor_TASK_LEASE_SECONDS`) expires and the
  task is requeued.
- Undispatchable tasks (no capable agent) retry with a delay and fail
  permanently after `Vapor_TASK_MAX_DISPATCH_ATTEMPTS` attempts — see
  [troubleshooting.md](troubleshooting.md) for tuning.

## Monitoring

With the observability profile enabled:

- Prometheus UI: `http://<host>:9090`
- Grafana: `http://<host>:3000` (anonymous viewer by default — restrict or
  configure auth before exposing)
- Agent metrics: `http://<agent>:9700/metrics`

Bundled alert rules (`deploy/prometheus/alerts.yml`, auto-loaded by the
compose Prometheus) cover both services:

- Agent: `VaporAgentDown` (critical), `VaporAgentTargetMissing` (critical),
  `VaporAgentScrapeSlow` (warning).
- Control plane: `VaporControlPlaneDown` (critical),
  `VaporTasksQueuedBacklog` (tasks queued >15m, warning),
  `VaporTasksStuckRunning` (tasks running >30m, warning).

Route these via your Alertmanager to whatever paging channel you use.

The control plane exposes Prometheus metrics at `/metrics` (public like
the agent's endpoint — protect at the network layer):
`vapor_controlplane_tasks_by_status{status="..."}`,
`vapor_controlplane_agents_connected`, and dispatch failure counters
`vapor_controlplane_dispatch_failures_total{reason="no_capable_agent"|"enqueue_failed"|"attempts_exhausted"}`.

## Distributed tracing

Both services emit spans through OpenTelemetry (`Vapor.ControlPlane` and
`Vapor.Agent` activity sources). Tracing is off by default; setting the
standard `OTEL_EXPORTER_OTLP_ENDPOINT` variable on either service (or both)
enables OTLP export with the standard OTLP protocol/headers variables.

The trace follows one task end to end across the tunnel:

```
POST /v1/jobs           → ASP.NET Core server span (control plane)
  task.dispatch         → producer span; traceparent injected into the WS message
    task.execute        → agent consumer span (parented via the tunnel traceparent)
      task.result       → control-plane span continued from the agent's returned traceparent
```

Any OTLP-compatible backend works (Jaeger, Grafana Tempo, Zipkin-compatible
collectors). Point both services at the same backend to see the full chain
in one trace; with the variable unset no exporter is registered and the
spans are inert (no overhead beyond disabled ActivitySources).
