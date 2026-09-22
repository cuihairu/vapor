# Getting started

This walkthrough takes you from an empty machine to a farming Steam account in ten steps: start the stack, declare an account, log in (password or QR), handle a Steam Guard challenge, run your first jobs, then hand the account to the orchestrator for card farming or playtime boosting. Commands use the compose dev defaults; every step links to the reference docs for the production-grade path.

## 0. Prerequisites

- Docker + Docker Compose (recommended), **or** .NET 10 SDK for a local run
- `curl` (all examples) — or just use the admin UI; every step has a UI equivalent
- One Steam account to drive (a burner/alt is the wise choice while learning)

## 1. Start the stack

```bash
git clone https://github.com/cuihairu/vapor.git
cd vapor
docker compose up -d
```

This starts two containers: `controlplane` (REST API on `:8080`, SQLite in a named volume) and `agent` (WebSocket client, encrypted credential store in its own volume, Monitoring plugin preinstalled). Add `--profile observability` to also start Prometheus (`:9090`) and Grafana (`:3000`).

Verify:

```bash
curl -s http://127.0.0.1:8080/healthz
curl -s http://127.0.0.1:8080/v1/agents/status -H "Authorization: Bearer admin-dev-token"
```

The agent connects **outbound** to `ws://controlplane:8080/v1/agent/ws` and authenticates with an agent API key — the control plane never dials agents, so agents can sit behind NAT in any region. See [docker.md](docker.md) for the variable table and [production.md](production.md) for the real-deployment topology (TLS, key management, backups).

Prefer local processes? Follow [running.md](running.md) (`Vapor_ADMIN_API_KEY` / `Vapor_AGENT_API_KEYS` / `VAPOR_ENCRYPTION_KEY` and friends).

## 2. Open the consoles

| URL | What it is |
|-----|------------|
| `http://127.0.0.1:8080/admin.html` | Full management console: accounts, sessions, jobs, trade/market/claim operations, auth challenges, plugin store, config |
| `http://127.0.0.1:8080/dashboard.html` | Read-only dashboard: stats, agents, sessions, jobs, audit — dual SSE streams |
| `http://127.0.0.1:8080/gamedata.html` | Game data dictionary (cached store lookups) |

All API examples below work verbatim with the compose dev keys (`admin-dev-token`).

## 3. Declare an account

Accounts are *declared* on the control plane — identity only (name, desired state, placement). **Credentials are never stored here**; they live in the agent's AES-GCM encrypted store or arrive with a login job.

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/acct-1 \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"enabled":true,"desiredState":"Online","region":"eu-west","agentId":"agent-1","updatedBy":"me"}'
```

`desiredState` is the whole orchestration contract:

| State | Meaning |
|-------|---------|
| `Offline` | No session; agent assignment released |
| `Online` | Live, logged-on session |
| `Idle` | Logged on and idling `idleApps` (whitelist) |
| `Farm` | Smart card farming — idles games with remaining card drops, rotates as they run out; `idleApps` becomes an *exclusion* list |
| `Boost` | Playtime boosting — idles apps until each `boostTargets` hours target is met |

The reconciler keeps converging reality to this spec (see [architecture.md](architecture.md)); you change behavior by editing the declaration, not by pushing buttons.

## 4. Log in

### Option A: password in the job

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"login","region":"eu-west","targets":["acct-1"],"payload":{"password":"<steam-password>"}}'
```

### Option B: QR code (no password handling at all)

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"login","region":"eu-west","targets":["acct-1"],"payload":{"qrLogin":true}}'
```

The challenge URL surfaces as a session event (and as a clickable button in the admin session panel); scan it with the Steam mobile app. The QR flow times out after 3 minutes. Tokens persist in the agent's encrypted store, so restarts restore the session without re-login.

### Steam Guard challenges

If the account is protected, the session parks in a challenge state and the control plane publishes a challenge event. Submit the code once, from anywhere:

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"code":"12345","type":"email"}'   # or "type":"totp"
```

Watch for challenges in real time via the SSE stream:

```bash
curl -N http://127.0.0.1:8080/v1/auth/challenges/events -H "Authorization: Bearer admin-dev-token"
```

With the **MobileAuthenticator** plugin loaded (its maFile imported on the agent), TOTP codes are answered automatically — the code itself never crosses the wire: the control plane learns only a boolean. Existing SDA/steamguard-cli users can import their `.maFile` on the agent host (`dotnet Vapor.Agent.dll import-mafile <file>`); see [session-engine.md](session-engine.md).

## 5. Run your first jobs

Jobs are the universal currency: an action name, a JSON payload, and target accounts.

```bash
# is the session alive?
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"ping","region":"eu-west","targets":["acct-1"]}'

# how many card drops are left?
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"get_card_drops","region":"eu-west","targets":["acct-1"]}'

# read the result back
curl -sS http://127.0.0.1:8080/v1/jobs/<jobId> -H "Authorization: Bearer admin-dev-token"

# or watch it live (SSE)
curl -N http://127.0.0.1:8080/v1/jobs/<jobId>/events -H "Authorization: Bearer admin-dev-token"
```

The full action vocabulary — farming, trading, market, achievements, claims — is the [actions catalog](actions.md); the REST shortcuts that wrap the frequent ones are in the [API reference](api.md).

## 6. Turn on card farming

This is the payoff step — the ASF "smart farming" equivalent, driven by desired state:

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/acct-1 \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{
        "enabled":true,
        "desiredState":"Farm",
        "idleApps":[730],                 // exclusion list: never farm these
        "region":"eu-west","agentId":"agent-1",
        "farmPolicy":{"perGameHourBudget":4,"priorityOrder":"CardsDescending","priorityApps":[251570]},
        "updatedBy":"me"
      }'
```

The orchestrator then runs the loop: poll the badges page → idle games with remaining drops → rotate as drops run out → honor the per-game hour budget and priority order → stop when nothing is left. Watch it:

```bash
curl -sS http://127.0.0.1:8080/v1/orchestration/farm -H "Authorization: Bearer admin-dev-token"
```

A spec edit (new exclusion, new budget) resets only the affected bookkeeping — no redeploy, no restart.

## 7. Boost playtime (optional)

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/acct-1 \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"desiredState":"Boost","boostTargets":[{"appId":570,"targetHours":100}],"updatedBy":"me"}'
```

Playtime is measured from the profile games tab, so hours earned outside Vapor count toward the target. When every target is met the account falls back to `Idle`.

## 8. Watch it operate

- **Metrics**: agent Prometheus endpoint (compose default on host `:9700`) — action counters/durations, session states, cache stats; Grafana auto-provisions the *Vapor Overview* dashboard. The control plane exposes `/metrics` with orchestration-level gauges.
- **Audit**: every config change, task result, code submission and dispatch decision is persisted and queryable: `GET /v1/audit/logs` (redacted before storage).
- **Tracing**: set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces; the W3C `traceparent` flows through the agent tunnel, so a job is traceable end-to-end.

## 9. Add capabilities with plugins

The agent image ships with Monitoring preinstalled. Plugins are ALC-isolated, trust-checked, permission-scoped, and hot-loadable. Install one at runtime from a catalog or a direct URL+checksum:

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/plugins/install \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"pluginId":"vapor.marketwatch"}'      # resolves from the configured catalog index
```

Uninstall is symmetric (`POST /v1/plugins/uninstall/{pluginId}`), and `GET /v1/plugins/installed` shows per-agent state — the admin console's *PluginStore* panel does all of this with buttons. Packaging and the trust model: [plugins.md](plugins.md).

## 10. Where to next

- **Hardening for real use**: [production.md](production.md) — TLS, `VAPOR_ENCRYPTION_KEY` management, per-role API keys, backups, upgrades, rollback.
- **Something broke?**: [troubleshooting.md](troubleshooting.md) — symptom → diagnosis → fix checklists.
- **Writing plugins**: [plugins.md](plugins.md).
- **What Vapor does and doesn't do vs. ASF**: [feature-matrix.md](feature-matrix.md).

## Ground rules worth knowing from day one

1. **Credentials and Steam Guard codes never leave the agent.** Passwords go either per-job or into the agent's encrypted store; codes are consumed locally, only a boolean is reported.
2. **Destructive features are gated twice.** Market listing creation/cancels need `dry_run` explicitly disabled *plus* the per-account switch (`marketListingsEnabled`) *plus* the agent-side `AGENT_MARKET_LISTINGS_ENABLED`.
3. **Every write is audited.** Assume your actions are recorded — that's a feature.
4. **The API expects answers, not wishes.** Jobs report per-target success/failure with structured output; read the result, don't assume.
