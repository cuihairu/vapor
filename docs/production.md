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
| `Vapor_RECONCILE_INTERVAL_SECONDS` | no | `15` | Account orchestrator reconcile cadence; `<= 0` disables orchestration |
| `Vapor_RECONCILE_MAX_ACCOUNTS_PER_AGENT` | no | `25` | Capacity cap when the orchestrator assigns accounts to agents |
| `Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS` | no | `3` | Consecutive failed logins before an account is throttled (reset by updating its spec) |
| `Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS` | no | `60` | Base of the exponential retry cooldown (`base × 2^(n-1)`, capped at 15 min); `0` disables |
| `Vapor_RECONCILE_SESSION_STALENESS_SECONDS` | no | `120` | Session snapshots older than this are treated as stale by the orchestrator |
| `Vapor_RECONCILE_FARM_REFRESH_SECONDS` | no | `300` | How often `farm` accounts re-query remaining card drops and rebuild the play queue; the per-account farm policy (per-game budget, queue ordering, priority apps) is part of the account spec via the accounts API, not an env knob |
| `Vapor_RECONCILE_BOOST_REFRESH_SECONDS` | no | `1800` | How often `boost` accounts re-query total playtime and rebuild the below-target schedule |
| `Vapor_RECONCILE_TRADE_REFRESH_SECONDS` | no | `600` | How often connected accounts with an auto-accept trade policy re-query active trade offers |
| `Vapor_RECONCILE_STANDING_REFRESH_SECONDS` | no | `21600` | How often the orchestrator runs the abnormal-standing check (`check_account_standing`); a banned result quarantines the account from trade dispatches until a later clean result releases it |
| `Vapor_RECONCILE_DRY_RUN` | no | off | Report orchestration deviations (audit + metric) without dispatching jobs |
| `Vapor_WEBHOOK_NOTIFICATIONS_URL` | no | off | Webhook endpoint that receives job/session/auth events as JSON; empty disables notifications |
| `Vapor_WEBHOOK_NOTIFICATIONS_SECRET` | no | — | HMAC-SHA256 secret; when set, requests carry `X-Vapor-Timestamp` + `X-Vapor-Signature: sha256=<hex>` over `"{timestamp}.{body}"` |
| `Vapor_WEBHOOK_NOTIFICATIONS_EVENTS` | no | all | Comma-separated event-type allowlist (e.g. `task.completed,auth.challenge`) |
| `Vapor_WEBHOOK_NOTIFICATIONS_MAX_RETRIES` | no | `3` | Per-event delivery attempts before the failure is counted and dropped |
| `Vapor_WEBHOOK_NOTIFICATIONS_RETRY_BASE_DELAY_MS` | no | `500` | Retry backoff base (`base × 2^attempt`) |
| `Vapor_CRAWL_DB_PATH` | no | `data/crawl.db` | SQLite file for crawl plans and per-app harvest results |
| `Vapor_CRAWL_WORKER_TICK_SECONDS` | no | `5` | Crawl worker claim/poll cadence; `<= 0` disables crawl orchestration |
| `Vapor_CRAWL_KEEP_RUNS` | no | `10` | Recent runs whose results are kept per plan (older runs pruned on completion) |
| `Vapor_CRAWL_MAX_APPS_PER_PLAN` | no | `500` | Upper bound on app_ids per crawl plan (rejects larger requests) |
| `Vapor_CRAWL_MAX_APPS_PER_TASK` | no | `200` | Upper bound on apps per dispatched shard (clamps plan shard_size) |
| `Vapor_CRAWL_RUN_TIMEOUT_SECONDS` | no | `1800` | Hard stop for a crawl run; outstanding shards are canceled and recorded as failures |
| `Vapor_CRAWL_INTERVAL_MS` | no | `500` | Default pacing between live store fetches inside a batch shard |
| `Vapor_ENABLE_SWAGGER` | no | off | Keep off in production |
| `VAPOR_ENCRYPTION_KEY` | recommended | — | ≥32 bytes; encrypts stored credentials (AES-GCM) |
| `VAPOR_ALLOW_INSECURE_DEFAULT_KEY` | no | off | Escape hatch; do not enable in production |

| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | off | OTLP endpoint (e.g. `http://tempo:4317`); enables distributed tracing export |

### Agent

| Variable | Required | Default | Notes |
|----------|----------|---------|-------|
| `AGENT_ID` | yes | — | Unique, stable id (shows up in `/v1/agents`) |
| `AGENT_REGION` | yes | — | Region used for task routing |
| `AGENT_CONTROLPLANE_WS_URL` | yes | — | e.g. `ws://controlplane:8080/v1/agent/ws` |
| `AGENT_API_KEY` | yes | — | Must be one of `Vapor_AGENT_API_KEYS` |
| `VAPOR_PLUGINS_DIR` | no | — | Plugin directory; the image ships Monitoring preinstalled |
| `VAPOR_METRICS_HOST` / `VAPOR_METRICS_PORT` | no | `:9700` | Prometheus endpoint, also the agent healthcheck |
| `VAPOR_REDIS` | no | off | StackExchange.Redis connection string (e.g. `redis:6379`); switches the data cache from in-memory to Redis |
| `AGENT_RECONNECT_INITIAL_DELAY_MS` | no | `500` | Reconnect backoff start |
| `AGENT_RECONNECT_MAX_DELAY_MS` | no | `10000` | Reconnect backoff ceiling |
| `AGENT_RECONNECT_BACKOFF_FACTOR` | no | `2` | Exponential factor |
| `AGENT_RECONNECT_MAX_RETRIES` | no | `0` | `0` = retry forever |
| `AGENT_2FA_AUTO_SUBMIT` | no | off | `true` = answer 2FA challenges locally from stored shared secrets (Steam TOTP); off leaves them to the manual SSE channel |
| `AGENT_MARKET_LISTINGS_ENABLED` | no | off | `true` = allow real market listing creation (`create_market_listing` with `send=true`); the per-account switch must be on too (see below) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | off | OTLP endpoint; enables distributed tracing export |

#### Automatic 2FA answering (opt-in)

With `AGENT_2FA_AUTO_SUBMIT=true` the agent listens for
`TwoFactorCodeNeeded` session events. For accounts whose mobile
authenticator shared secret is stored in the agent's credential store
(persisted via the MobileAuthenticator plugin's `save_shared_secret`
action, encrypted at rest), it generates a Steam TOTP locally and
submits it — no human in the loop. The shared secret never leaves the
agent. Accounts without a stored secret (email Steam Guard codes, for
instance, which cannot be generated locally) fall through to the manual
SSE challenge channel unchanged. Repeat answers for the same account
are rate-limited by a 60-second per-account cooldown, and Steam server
time is synchronized at startup and hourly so TOTP windows stay valid.


#### Market listing creation (opt-in, doubly gated)

Putting an item up for sale is the sensitive end of the market loop, so a
real listing (`POST /v1/accounts/{name}/market/listings` with `send=true`)
requires two explicit switches, both default off: the account spec's
`marketListingsEnabled` flag (control plane, set via `PUT /v1/accounts/{name}`)
and the agent's `AGENT_MARKET_LISTINGS_ENABLED=true`. Either switch off, the
endpoint refuses; a direct agent dispatch is guarded by the agent-side check.
Without `send` the call is a dry run: it reports the fee-aware pricing plan
(seller proceeds, Steam fee, publisher fee, buyer price) and sends nothing.
Prices always come from the caller — no auto-repricing. A listing that needs
a mobile confirmation is reported back via `needs_mobile_confirmation`;
confirming it is the existing market-confirmations loop's job
(`POST /v1/accounts/{name}/confirmations/accept-all` with `type=market`).


#### Cache backend (Redis)

Without `VAPOR_REDIS` the agent caches store data (game info, prices, market
listings) in-process (`MemoryVaporCache`). Setting `VAPOR_REDIS` switches the
same `IVaporCache` contract to `RedisVaporCache`, which:

- shares entries across agent replicas in a region (`vapor:cache:<key>` JSON
  envelopes with absolute fresh/stale expiry timestamps),
- deduplicates stale-while-revalidate refreshes across instances via a
  short-lived `SET NX PX` lock with token-checked release,
- removes keys by prefix with `SCAN` (invalidation via `cache_invalidate`
  works identically), and never `FLUSHDB`es the server.

When composing with Docker, add a Redis service and point the agent at it:

```yaml
services:
  redis:
    image: redis:7-alpine
  agent:
    environment:
      VAPOR_REDIS: "redis:6379"
```

The connection is opened with `abortConnect=false`, so the agent starts even
while Redis is briefly unreachable; cache operations then fail fast (actions
fall through to the live fetch) until the connection recovers.

#### Plugins

Plugins live in `VAPOR_PLUGINS_DIR` (default `./plugins`), one subdirectory
per plugin with a `plugin.json` manifest; the image ships the official
Monitoring plugin preinstalled. Loading, trust gating, permissions and the
manifest format are documented in the
[plugin development guide](plugins.md). Operational notes:

- Load failures are isolated — a broken plugin is logged and skipped, the
  agent still starts; check the startup log's load report for reasons
  (incompatible API version, trust below the host's floor, invalid manifest).
- The host policy (`MinimumTrust`, `RequirePermissionsDeclared`) is set in
  code when constructing the `PluginManager`; plugins declare their trust and
  permissions in the manifest and capabilities without a declaration are
  stripped (minimal trust).
- Plugin behaviour is configured through the manifest `configuration` section
  with environment-variable overrides, e.g. the Market Watch plugin reads
  `market.check_interval_seconds`, `market.threshold_percent`,
  `market.country` and `market.webhook_url` (env:
  `VAPOR_MARKETWATCH_INTERVAL_SECONDS`, `VAPOR_MARKETWATCH_THRESHOLD_PERCENT`,
  `VAPOR_MARKETWATCH_COUNTRY`, `VAPOR_MARKETWATCH_WEBHOOK_URL`).

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

## Web consoles

Three static pages ship with the control plane (no build step, no external
assets). The pages themselves are served without authentication — they embed
no sensitive data — while every data call goes through the authenticated
`/v1` API with the operator's bearer token:

| Page | Role | Writes |
|------|------|--------|
| `/admin.html` (default landing page) | Full operations console: account lifecycle, trades & confirmations, market & claiming, crawl plans, configuration | Yes — mirrors the admin REST surface; irreversible actions gate on explicit confirmation dialogs |
| `/dashboard.html` | Read-only fleet monitoring (stats, sessions, jobs, audit, SSE live feed) | None — locked by contract test |
| `/gamedata.html` | Game data dictionary & crawl results browser | None — locked by contract test |

The admin console is a thin client over the REST API: the server remains the
single source of truth, and its configuration panel never displays
password-class settings values — masked inputs left untouched resubmit the
stored value. The admin API key is the only line of defense for every write
the console can perform; treat it accordingly (see the checklist below).

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
