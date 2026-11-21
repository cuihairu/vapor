English | 简体中文

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
| REST API | ✅(IPC) | ➖ | ➖ | ➖ | ✅(69 `/v1` routes / 85 operations + OpenAPI) |
| Web UI | ✅(ASF-ui) | ✅ | ✅ | ➖ | ✅(read-first dashboard (writes converge to a single whitelisted write) + full-featured admin console + game data dictionary page, statically hosted, zero build chain) |
| Plugin system | ✅ | ✅ | ➖ | ➖ | ✅(ALC isolation + SemVer + trust/permissions + runtime PluginStore: directory source → agent-targeted install/uninstall/hot-reload, ASF plugins require manual placement) |
| Multi-node fleet (CP centralized scheduling) | ❌(single process) | ❌ | ❌ | ❌ | ✅(**unique**) |
| Observability (metrics/tracing/audit) | ⚠️ | ➖ | ➖ | ➖ | ✅(Prometheus + OTel + audit) |
| Encrypted credential storage | ✅ | ✅ | ✅ | ✅ | ✅(AES + key management + shared/identity secret encryption) |
| Per-account dedicated egress proxy (binding + console assignment) | ✅(per-bot WebProxy) | ❌ | ❌ | ➖ | ✅(§38-P1 credential library persistence + CM/Web full-chain proxying + `check_proxy` self-check; §39-P-a console per-account assignment: pinned agent targeted dispatch + masked audit + verification chain, changing egress = an explicit audit action, aligned with the "fix once, then rarely change" risk-control stance) |
| Anomalous account detection and auto-isolation (standing check) | ➖ | ➖ | ➖ | ➖ | ✅(§38-P2 `check_account_standing` periodic health checks: VAC/community/trade/economy bans + limited flag; anomalies auto-isolated from trade scheduling, manual loot/warehouse consolidation gated by the same fence — `force` exemption goes through audit, `account.standing_alert/released` events) |
| Credentials/captcha never leave the system (desensitization contract) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ✅(red line: captcha sends only a bool, never enters notifications/logs) |

## 4. Gap analysis and P6 proposals

Matrix conclusion: the platform layer (fleet orchestration / plugins / observability / REST) is the one layer Vapor has and the other four benchmarked products do not; the gaps concentrate in **Steam feature depth** — precisely the "inventory/trading, basic farming" items of GA exit criterion #2. Below, ordered by recommended priority (checkboxes for item-by-item tracking; tick when done).

> **2026-09-13 close-out**: P6-1 / P6-2 / P6-3 all landed; GA exit criterion #2 (inventory/trading, basic farming) closed. The remaining differences are only explicitly deferred items (listing-creation ToS grey area, weak demand for achievement management) and out-of-scope non-adoptions. See git history for landing details.
> **2026-09-14**: P7 chartered (market loop — listing read / batch cancel / listing creation); the two listing items are promoted from P6-4 deferral to a chartered phase — "requires inventory/pricing prerequisites" was filled in by P6, and the ToS grey area is mitigated by phasing + dry_run default + per-account switch; achievement management stays deferred. See git history.
> **2026-09-14**: P7-1 listing read landed (read-only zero-risk increment) — `get_my_market_listings` + `GET /v1/accounts/{name}/market/listings`; mylistings parsing is pinned by a synthetic fixture + three-way cross-verified contract test, and feeds data to P7-2 batch cancel. See git history.
> **2026-09-14**: P7-2 batch cancel landed — `cancel_market_listings` + `POST /v1/accounts/{name}/market/listings/cancel`; filters (app/name/price range/listing age) + per-item pacing + per-item failures do not abort and are faithfully summarized; dry_run by default, real cancel without filters rejected at two layers (CP 400 + independent action rejection), and a market-specific conservative rate limit runs independently of the trading rate-limit budget. See git history.
> **2026-09-14**: P7-3 listing creation landed — `create_market_listing` + `POST /v1/accounts/{name}/market/listings` + `MarketFeeCalculator` (Steam 5% + publisher 10% charged on seller proceeds, converted for both buy and sell sides; the contract is cross-verified against four sources including the official economy_v2.js/market_multisell.js); `send` defaults to dry run and emits only a pricing plan, while a real listing requires both the account's `marketListingsEnabled` and the agent's `AGENT_MARKET_LISTINGS_ENABLED` switches; prices come only from the caller, and market confirmations reuse the existing loop. All three phases of the P7 market loop complete. See git history.
> **2026-09-15**: P8 points shop claiming landed — `get_points_shop_summary` + `claim_points_shop_items` + `GET/POST /v1/accounts/{name}/points-shop/*` (SteamKit2 LoyaltyRewards unified service, semantics aligned with ASF RP commands): free definitions are claimed by default, paid ones require `force=true` and are rejected up front when missing as a whole batch, and defid comes only from explicit caller input (no automation that scans everything claimable). This closes the matrix's last gap that is neither deferred nor out of scope; §3.6 now leaves only the deferred achievement management. See git history.
> **2026-09-18**: Achievement unlock/management landed (§33, P1 achievement read + P2 write channel + P3 admin panel), closing §3.6's last deferred item. Writes are explicit single-shot REST (an explicit names list, no implicit bulk path; reset requires double confirm), with boolean set-only — achievement/stat numeric editing stays "explicitly not adopted". See git history.
> **2026-09-22**: Two matrix backfills — ① the §3.7 plugin-system row gains the runtime PluginStore (§37: directory source → agent-targeted install/uninstall/hot-reload + admin panel; ASF plugins require manual placement); ② the REST API row goes 34→50 `/v1` routes (58 operations; §31 admin write endpoints + §33 achievements + §35/§36 orchestration + §37 plugin-ecosystem five endpoints, cumulative). See git history.
> **2026-09-30**: Two matrix backfills — ① the §3.4 inventory-management row's Vapor column "no UI" is corrected (round 36 already delivered the admin backpack panel including the duplicates table; API + UI + action all three present); ② the §3.7 REST API row goes routes 50→70, operations 58→57 (round 37 QR endpoints + rounds after 38, cumulative; 57 after round 42's two game-economy actions). See git history.
> **2026-10-01**: Matrix errata + REST counting convention settled (round 47, maintenance round) — the previous entry's (2026-09-30) "routes 50→70, operations 58→57" mixed up the fields: 70 came from verify-api-docs' full set of Map* method+path pairs (including the three non-`/v1` entries `/`, `/healthz`, `/metrics`), and 57 was actually the distinct `/v1` path count. The approved convention matches the api.md introduction (route = distinct `/v1` path, operation = `/v1` method+path pair, including 7 multi-method paths); the true values are **57 routes / 67 operations**; the api.md introduction's "50 routes (58 operations)" was refreshed as stale too. See git history.

> **2026-10-13 follow-up errata**: The **57 routes / 67 operations** settled by the previous entry (2026-10-01) was pushed higher again by later landings — current value **69 routes / 85 operations** (same convention: route = distinct `/v1` path, operation = `/v1` method+path pair). This matrix's REST row follows the latest true values.

### P6-1 Card farming loop (GA exit criterion, the largest platform leverage) ✅ all complete

- [x] **Badge page parsing**: fetch the player's badge/drop page and derive "which apps still have card drops remaining, and how many" (✅ 2026-09-12 `SteamBadgesClient` + `get_card_drops`, SWR cache, fixture-based three-way parser cross-verification).
- [x] **Smart farming scheduling**: apps with remaining drops > 0 enter the idle queue and farming switches to the next one automatically when a queue head finishes; integrated with account orchestration DesiredState (✅ 2026-09-12 `Farm` desired state: the orchestrator periodically dispatches queries → builds FarmQueue → `play_games` puts the head at the front, auto-switching when the head finishes; a spec change resets the queue).
- [x] **Idle exclusion list**: IdleApps gains exclusion semantics (blacklist), aligned with ASF `Blacklist` (✅ 2026-09-12 IdleApps is an exclusion list under Farm mode and remains a whitelist under Idle mode).

### P6-2 Trading and confirmation loop (GA exit criterion, security base already in place) ✅ all complete

- [x] **Trade offer read**: pull incoming/outgoing offer lists into the CP (REST-ified), reusing the existing desensitization and audit (✅ 2026-09-12 `get_trade_offers` + `GET /v1/accounts/{name}/trade-offers` synchronous endpoint, three-state 200/202/502).
- [x] **Offer accept/decline**: built on MobileAuthenticator's existing confirmation hash/response capability; **auto-accept must be enabled by an explicit per-account policy** (✅ 2026-09-12/13 manual-path REST accept/decline + automatic continuation of mobile confirmation after accept; the 2026-09-13 evaluation concluding that orchestrator-side auto-accept would not be implemented was overturned and landed on 2026-09-18 as the §32 conservative allowlist version: explicit per-account enablement, partner allowlist, gifts-only pure receive, full decision audit; decision records in git history).
- [x] **Batch confirmation action**: batch processing of trade/market confirmations (benchmarking Watt's batch confirmation) (✅ 2026-09-13 `confirm_all_confirmations` + CP `/confirmations/accept-all`, type filter + allow/cancel).
- [x] **Offer sending (loot)**: transfer inventory to specified friends (✅ 2026-09-13 `loot_inventory` + CP `/loot`, inventory scan → tradable filter → rate-limited sending → mobile confirmation continuation).
- [x] **1:1 card swap (STM/TradeMatcher equivalent)**: landed (2026-09-13, `find_duplicates` + `swap_duplicates` + CP duplicates/swap-offers endpoints, see git history).

### P6-3 Interop and claiming (lowering migration/usage cost) ✅ all complete

- [x] **.maFile import**: support SDA/steamguard-cli maFile (parse shared_secret/identity_secret/device ID, store through the existing encrypted storage) — zero-cost migration for existing 2FA users (✅ 2026-09-13 local `Vapor.Agent import-mafile` CLI import; maFile content never lands in CP task records).
- [x] **Free license claiming**: an addlicense-equivalent action (sub/add page parsing); free-game alerts come from extending MarketWatch's watch types (✅ 2026-09-13 `add_license` (apps via protocol, subs via store checkout) + MarketWatch `kind=free` edge alert, forming a "notify → claim" loop).
- [x] **Inventory REST-ification**: GetInventoryAction deepened into `/v1/accounts/{name}/inventory` (filter by app/type), feeding the trading/market features (✅ 2026-09-13 multi-app scan + tradable_only/marketable_only filters, steam_id reverse-resolved from the session cookie).

### P6-4 Benchmark-aligned but deferred (recorded pending, not promised)

- [x] Web Dashboard read-first panels (benchmarking ASF-ui's read-only portion): `wwwroot/dashboard.html` landed — stats cards/accounts/agents/sessions/jobs/audit + dual SSE streams + polling fallback, writes converge to a single whitelisted write (guarded by contract tests: PUT/DELETE permanently forbidden, exactly one POST reconcile); management features live in admin.html (expanded on 2026-09-18 as §31 into a full-featured management console: account lifecycle / trading and confirmations / market and claiming / crawler schedules / configuration management, five panels, full write-endpoint coverage, destructive operations double-confirmed, guarded by contract tests).
- [x] Listing creation / batch cancel (benchmarking SGI): ToS grey area + requires inventory/pricing prerequisites, deferred. (2026-09-14 prerequisites filled in by P6; chartered as the P7 market loop, see git history)
- [x] QR code login (benchmarking SGI/steamguard-cli): SteamKit2 QR challenge + polling landed; the challenge URL surfaces through session events for a human to scan, and neither request_key nor token leaves the agent; the admin console's session panel has a built-in "QR code login" button for one-click triggering (✅ 2026-09-16 UI button landed, deferred item closed).
- [x] Achievement unlock/management (benchmarking SGI): weak demand, deferred. (2026-09-18 chartered as §33 and landed: reads via the community achievement page, unlock/reset via explicit single-shot REST over manually composed client stats protocol messages — explicit names + reset double confirm + read-back verification after each write; boolean set-only, numeric editing stays not adopted, see git history)

### Explicitly not adopted (out of scope)

- Network acceleration (Watt is a different category), local account switching (a client-side concept), a general TOTP vault (off-core), in-game scripting / achievement numeric editing (high-risk grey area).

## 5. References

- [ASF Wiki](https://github.com/JustArchiNET/ArchiSteamFarm/wiki) / [ASF FAQ](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/faq)
- [Watt Toolkit user guide](https://xtsat.github.io/SteamTools-Guide/) / [Microsoft Store page](https://apps.microsoft.com/detail/9mtcfhs560ng)
- [Steam Game Idler](https://github.com/zevnda/steam-game-idler) / [author's design article](https://dev.to/zevnda/how-i-built-an-open-source-alternative-to-archisteamfarm-and-why-you-should-care-37od)
- [steamguard-cli](https://github.com/dyc3/steamguard-cli) / [Idle Master Extended](https://github.com/jonas-med-ett-s/idle_master_extended)
