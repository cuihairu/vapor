English | [简体中文](zh/feature-matrix/)

# Vapor Feature Comparison Matrix (2026-09-13)

> Method: select five representative competing products, normalize them by capability domain, and compare item by item to identify gaps and convert them into a checkable P6 candidate list.
> Status legend: ✅ yes / ⚠️ partial / ❌ no / ➖ out of scope, not adopted / 📋 planned, pending implementation (see the corresponding P phase in `todo.md`). The Vapor column's current status is based on the repository code and `todo.md`.

## 1. Trade-off Principles

The matrix is aligned to "capability domains" rather than copying features one by one. What gets included in P6 is filtered by three criteria:

1. Positioning fit: Vapor is a multi-account large-scale operations platform (CP + Agent fleet + REST/plugins/orchestration); it does not do single-machine toolbox features (network acceleration, local account switching, in-game scripting).
2. GA exit criteria first (see git history): login and 2FA are already closed; **inventory/trading and basic farming are explicitly unclosed exit criteria**, so gap analysis gives them the highest weight.
3. Platform leverage: features that reuse the existing orchestration/notification/plugin/observability foundation take priority (low marginal cost, amplifies existing investment).

## 2. Products at a Glance

| Product | Form | Account Model | Positioning |
|------|------|----------|------|
| [ArchiSteamFarm (ASF)](https://github.com/JustArchiNET/ArchiSteamFarm/wiki) | C# daemon | Concurrent multi-account bots | Gold standard for card idling + 2FA + STM card trading + IPC/ASF-ui + plugins |
| [Watt Toolkit (Steam++)](https://github.com/BeyondDimension/SteamTools) | Desktop/mobile toolbox | Multiple local accounts | Token management, account switching, batch confirmation, network acceleration (embeds ASF card idling) |
| [Steam Game Idler (SGI)](https://github.com/zevnda/steam-game-idler) | Tauri desktop app | Multi-account | Two-phase card engine (32 concurrent), achievement unlock/management, playtime boost, inventory & market listings, free game claiming |
| [steamguard-cli](https://github.com/dyc3/steamguard-cli)(SDA family) | CLI | Single account × multiple maFiles | 2FA code generation, trade/market confirmation handling, maFile interop |
| Idle Master Extended | Desktop app | Single account | Single-account card idling, relies on browser cookie login, marginalized (reference only) |

## 3. Feature Matrix

### 3.1 Login and Identity

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| Credential login (incl. access token refresh) | ✅ | ✅ | ✅ | ✅ | ✅(BotSession + token refresh) |
| 2FA code generation (TOTP) | ✅ | ✅ | ✅ | ✅ | ✅(SteamTotp + time sync) |
| Automatic response to 2FA login challenges | ✅ | ✅ | ✅ | ✅ | ✅(TwoFactorAutoResponder, explicit opt-in) |
| Trade/market **confirmation** acceptance | ✅ | ✅(batch) | ✅ | ✅ | ✅(mobile confirmation closed loop + `confirm_all` batch; identity secret never leaves the agent) |
| QR code login | ❌ | ✅ | ✅ | ✅ | ✅(login task `qr_login` trigger, challenge URL surfaced; admin panel embeds a first-party QR renderer) |
| .maFile import interop (SDA/steamguard-cli) | ✅(ASF 2FA) | ✅ | ➖ | ✅ | ✅(agent-local CLI `import-mafile`, compatible with SDA nested / steamguard-cli flat layouts) |
| Local Steam client account switching | ➖ | ✅ | ✅ | ➖ | ➖(not applicable to a server-side architecture) |

### 3.2 Idling / Farming

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| Concurrent multi-account idling | ✅ | ✅(embeds ASF) | ✅ | ➖ | ✅(Agent fleet, naturally concurrent) |
| **Remaining card drop detection** (badge page parsing) | ✅ | ✅ | ✅ | ➖ | ✅(`get_card_drops` badge page parsing) |
| Smart farming schedule (auto-switch when drops exhausted) | ✅ | ✅ | ✅(two-phase engine) | ➖ | ✅(`farm` desired state, CP orchestration auto-switches when exhausted) |
| Idling blacklist/whitelist | ✅ | ✅ | ✅ | ➖ | ✅(Idle whitelist / Farm exclusion list) |
| Playtime boost | ✅ | ➖ | ✅ | ➖ | ✅(`boost` desired state, playtime-target scheduling, parallel multi-app idling until the target is reached) |

### 3.3 Trading

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| Trade offer reading | ✅ | ✅ | ✅ | ✅ | ✅(`get_trade_offers` + `GET /v1/accounts/{name}/trade-offers`) |
| Offer accept / decline | ✅ | ✅ | ✅ | ✅ | ✅(REST accept/decline, mobile confirmation auto re-dispatch; manual by default; orchestrator-side conservative whitelist auto-accept — explicitly enabled per account + partner whitelist + gifts-only, see §32) |
| Offer sending (loot / transfer) | ✅ | ➖ | ➖ | ➖ | ✅(`loot_inventory` + `POST /v1/accounts/{name}/loot`) |
| 1:1 auto card swap (STM / TradeMatcher) | ✅ | ➖ | ➖ | ➖ | ✅(2026-09-13 `swap_duplicates`, strictly bidirectional complementary pairing + dry_run by default) |
| Batch confirmation | ✅ | ✅ | ✅ | ✅ | ✅(`confirm_all_confirmations`, type filtering + allow/cancel) |

### 3.4 Inventory

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| Inventory reading | ✅ | ✅ | ✅ | ✅ | ✅(`GET /v1/accounts/{name}/inventory`, multi-app scan + tradable/marketable filtering) |
| Inventory management (API/UI/duplicate list) | ⚠️ | ✅ | ✅ | ➖ | ✅(duplicate list API: `GET /v1/accounts/{name}/duplicates` + admin backpack panel UI; `find_duplicates`/`swap_duplicates` actions) |
| Consolidation into a unified warehouse (loot to a common warehouse) | ❌(loot archived per account) | ➖ | ➖ | ➖ | ✅(§39-P-b account `role=farm/storage` declaration + `POST /v1/orchestration/storage/collect` consolidates all enabled farm accounts in one shot: a single job runs per-source `loot_inventory` (SSE progress) + sender-side mobile confirmation chain + agent-side per-sender rate limiting + a snapshot endpoint reads back the most recent consolidation per source account; no dedicated warehouse table, jobs are the record) |

### 3.5 Market

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| Market data reading (app details/pricing/search) | ⚠️ | ✅ | ✅ | ➖ | ✅(contract-test-locked Web client + caching) |
| Price monitoring alerts | ➖ | ➖ | ✅ | ➖ | ✅(MarketWatch plugin + webhook) |
| Listing reading (own mylistings) | ➖ | ➖ | ✅ | ➖ | ✅(`GET /accounts/{name}/market/listings`, P7-1, contract-test-locked parsing) |
| Listing creation (incl. fee-aware pricing) | ➖ | ➖ | ✅ | ➖ | ✅(`POST /accounts/{name}/market/listings`, P7-3, dry_run by default + account/agent dual switches + fee-aware pricing) |
| Listing management / batch cancellation | ➖ | ➖ | ✅ | ➖ | ✅(`POST /accounts/{name}/market/listings/cancel`, P7-2, dry_run by default + double rejection for unfiltered real cancellation) |

### 3.6 Automation / Orchestration

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| Task system (dispatch/retry/terminal state/output) | ⚠️(commands + timers) | ➖ | ✅(task chains) | ➖ | ✅(job/task + periodic tasks cron/interval + execution safety-classified dispatch caps: GuardedWrite/NonIdempotent/unclassified actions always capped at 2, round fifty) |
| Desired-state orchestration / node-loss rebalancing | ❌ | ❌ | ❌ | ❌ | ✅(DesiredStateReconciler, **unique**) |
| Event notifications (webhooks, etc.) | ⚠️(Steam messages) | ➖ | ✅ | ➖ | ✅(HMAC webhook + rule filtering) |
| Free game alerts + license claiming | ✅(addlicense) | ➖ | ✅(alerts + auto-claim) | ➖ | ✅(`add_license` + MarketWatch `kind=free` edge alerts, alert→claim closed loop) |
| Points shop claiming | ✅ | ➖ | ➖ | ➖ | ✅(`get_points_shop_summary` + `claim_points_shop_items`, P8, free items claimed by default + paid requires force + defid only from the caller) |

### 3.7 Platform / Security

| Capability | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| REST API | ✅(IPC) | ➖ | ➖ | ➖ | ✅(68 `/v1` routes / 84 operations + OpenAPI) |
| Web UI | ✅(ASF-ui) | ✅ | ✅ | ➖ | ✅(read-first dashboard (writes converge to a single whitelisted write) + full-featured admin console + game data dictionary page, statically hosted, zero build chain) |
| Plugin system | ✅ | ✅ | ➖ | ➖ | ✅(ALC isolation + SemVer + trust/permissions + runtime PluginStore: directory source → agent-targeted install/uninstall/hot-reload, ASF plugins require manual placement) |
| Multi-node fleet (CP centralized scheduling) | ❌(single process) | ❌ | ❌ | ❌ | ✅(**unique**) |
| Observability (metrics/tracing/audit) | ⚠️ | ➖ | ➖ | ➖ | ✅(Prometheus + OTel + audit) |
| Encrypted credential storage | ✅ | ✅ | ✅ | ✅ | ✅(AES + key management + shared/identity secret encryption) |
| Per-account dedicated egress proxy (binding + console assignment) | ✅(per-bot WebProxy) | ❌ | ❌ | ➖ | ✅(§38-P1 credential library persistence + CM/Web full-chain proxying + `check_proxy` self-check; §39-P-a console per-account assignment: pinned agent targeted dispatch + masked audit + verification chain, changing egress = an explicit audit action, aligned with the "fix once, then rarely change" risk-control stance) |
| Anomalous account detection and auto-isolation (standing check) | ➖ | ➖ | ➖ | ➖ | ✅(§38-P2 `check_account_standing` periodic health checks: VAC/community/trade/economy bans + limited flag; anomalies auto-isolated from trade scheduling, manual loot/warehouse consolidation gated by the same fence — `force` exemption goes through audit, `account.standing_alert/released` events) |
| Credentials/captcha never leave the system (desensitization contract) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ✅(red line: captcha sends only a bool, never enters notifications/logs) |
