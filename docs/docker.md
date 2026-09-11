# Docker & Compose

## Images

| Image | Dockerfile | Notes |
|-------|------------|-------|
| `vapor/controlplane` | `src/Vapor.ControlPlane/Dockerfile` | REST + SSE + WebSocket, SQLite under `/app/data` |
| `vapor/agent` | `src/Vapor.Agent/Dockerfile` | Ships with the Monitoring plugin preinstalled in `/app/plugins` |

Both images are multi-stage builds on `mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`, run as a non-root user (`APP_UID`), and take the repository root as build context:

```bash
docker build -f src/Vapor.ControlPlane/Dockerfile -t vapor/controlplane:local .
docker build -f src/Vapor.Agent/Dockerfile -t vapor/agent:local .
```

## Compose

`docker-compose.yml` defines `controlplane` and `agent`, plus an optional
`observability` profile with Prometheus and Grafana:

```bash
# Control plane + agent only
docker compose up -d

# With metrics stack (Prometheus at :9090, Grafana at :3000)
docker compose --profile observability up -d
```

The Grafana instance auto-provisions a Prometheus datasource and the bundled
**Vapor Overview** dashboard (`deploy/grafana/dashboards/vapor-overview.json`),
covering action throughput/failures/duration, session states, cache hit ratio,
GC/thread-pool and memory.

### Environment / secrets

Set these before running outside of local development (via shell env or a `.env` file):

| Variable | Used by | Default in compose |
|----------|---------|--------------------|
| `VAPOR_ADMIN_API_KEY` | control plane | `admin-dev-token` |
| `VAPOR_AGENT_API_KEY` / `VAPOR_AGENT_API_KEYS` | agent / control plane | `agent-dev-token` |
| `VAPOR_AGENT_ID`, `VAPOR_AGENT_REGION` | agent | `agent-1`, `eu-west` |
| `VAPOR_CONTROLPLANE_PORT` | host port mapping | `8080` |
| `VAPOR_AGENT_METRICS_PORT` | host port mapping | `9700` |

Volumes keep state across restarts: `controlplane-data` (SQLite databases) and
`agent-data` (encrypted credential store at `/app/.vapor`).

## Metrics endpoint

The Monitoring plugin exposes Prometheus text format (version 0.0.4) on the
agent. Configuration precedence: environment variable → `plugin.json`
`configuration` → built-in default.

| Env | plugin.json key | Default |
|-----|-----------------|---------|
| `VAPOR_METRICS_HOST` | `metrics.host` | `127.0.0.1` |
| `VAPOR_METRICS_PORT` | `metrics.port` | `9700` |
| `VAPOR_METRICS_PATH` | `metrics.path` | `/metrics` |

Exported metric families:

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `vapor_action_executions_total` | counter | `action`, `status` | Action executions by outcome |
| `vapor_action_duration_seconds_sum` | counter | `action`, `status` | Cumulative execution seconds |
| `vapor_actions_registered` | gauge | – | Actions currently registered |
| `vapor_sessions_active` | gauge | – | Tracked bot sessions |
| `vapor_sessions_by_state` | gauge | `state` | Sessions grouped by state |
| `vapor_session_events_total` | counter | `type` | Session lifecycle events |
| `vapor_cache_hits_total` / `vapor_cache_misses_total` | counter | – | Cache statistics |
| `vapor_cache_entries` | gauge | – | Live cache entries |
| `process_uptime_seconds`, `process_working_set_bytes` | gauge | – | Process basics |
| `dotnet_gc_heap_size_bytes`, `dotnet_gc_collections_total` | gauge/counter | `generation` | GC state |
| `dotnet_threadpool_threads`, `dotnet_threadpool_pending_work_items` | gauge | – | Thread pool |

A bind failure (port occupied, address unavailable) is logged and non-fatal:
the plugin keeps working, only the HTTP endpoint is disabled.

## Standalone Prometheus

```yaml
scrape_configs:
  - job_name: vapor-agent
    static_configs:
      - targets: ["agent-host:9700"]
```
