# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Aggregated internal status view (`GET /v1/system/status` + dashboard
  "内部状态总览" panel): one admin-only read-only report combining
  control-plane self health (DB availability/latency, job queue depth,
  scheduler heartbeat, reconciler last-pass outcome, recurring-job and plugin
  counters), proxy probe history from recent `check_proxy` tasks, connected
  agents and account desired-vs-actual state (mismatch list, pending auth
  challenges as counters only), with a derived overall verdict
  (`healthy`/`degraded`/`unhealthy` + reasons). Credentials and challenge
  codes never appear in the response; the dashboard panel refreshes off the
  existing SSE streams with a 2 s debounce instead of adding polling.

- Plugin ecosystem (todo §38 P4): plugins install at runtime through three new
  agent host actions — `plugin_install` (download url + mandatory sha256 →
  checksum → staging unpack with zip-slip protection and manifest-at-root
  validation → hot-load; reinstalling an installed id replaces it),
  `plugin_uninstall` (unload + retire directory; idempotent on unknown ids)
  and `plugin_list` — no bot session required. Host actions ride the existing
  job pipeline and are targeted with the new `agent:{id}` task-target prefix:
  the scheduler routes them directly to the named agent instead of a region
  pick, failing over via the usual dispatch-retry path when the agent is
  offline. Every action's output carries the agent's full installed list,
  which the ControlPlane mirrors per agent and exposes through
  `/v1/plugins/catalog` (index source from `Vapor_PLUGIN_INDEX_URL`, 60s
  cache), `/v1/plugins/installed`, `POST /v1/plugins/install` (by catalog
  `pluginId` or direct `url`+`sha256`; one targeted job per agent),
  `POST /v1/plugins/uninstall/{pluginId}` and
  `POST /v1/plugins/inventory/refresh`. The admin console gains a
  PluginStore panel: catalog browsing with per-agent targeting, one-click
  batch install, per-agent inventory chips with uninstall, and an inventory
  re-sync button. Trust boundary on record: the checksum pins integrity
  against the supplied digest, not package origin — `trust` remains
  manifest-declared and the host's `MinimumTrust` policy is unchanged.

- ASF-style smart-farming enhancements (todo §38 P3): farm accounts accept an
  optional per-account farm policy via the accounts API — a per-game hour
  budget (a fuse against dead farming: an app idled for the whole budget in
  the current session is skipped and the loop rotates), a queue priority
  order (cards descending — the report default — cards ascending, or app id
  ascending) and a priority-apps list pinned to the queue head in
  declaration order. Completed apps are detected by queue diff (the drops
  report only lists apps with remaining drops) and, together with
  budget-skipped apps, are never re-queued when a lagging report still lists
  them; draining the queue emits a one-shot completion audit entry and
  `account.farm_progress` broker event (kinds `app_completed`,
  `app_budget_exhausted`, `queue_empty`). Session card statistics
  (remaining / collected / cards-per-hour) accumulate monotonically across
  report totals and survive spec updates, as do completion and skip marks —
  a spec update is a policy edit, not a reset. New REST surface:
  `GET /v1/orchestration/farm` (per-account farm snapshot); the admin
  console's account cards show a live farm badge and the account editor
  carries the farm policy fields.

- Abnormal account standing detection (todo §38 P2): every account runs a
  periodic standing check through the orchestrator (default every 6h,
  `Vapor_RECONCILE_STANDING_REFRESH_SECONDS`) using the new
  `check_account_standing` action — the agent fetches its Steam Web API key
  from the logged-in web session, queries `GetPlayerBans/v1` (authoritative
  VAC/community/game/economy bans) and `GetSteamLevel/v1` (level 0 ⇒ limited
  profile; level failures degrade gracefully), and classifies the account as
  `clean`, `restricted` (economy probation) or `banned` (any ban flag). A
  `banned` result quarantines the account: the trade evaluation loop skips
  all further trade dispatches, a `standing_quarantined` audit entry and an
  `account.standing_alert` broker event (webhook-forwardable) are emitted,
  and a later clean result releases the quarantine symmetrically. New REST
  surface: `GET /v1/orchestration/standing` (per-account snapshot) and
  `POST /v1/accounts/{name}/standing-check` (force a check on the next
  reconcile pass through the orchestrator's own pipeline). The admin
  console's account cards show a standing badge (clean/restricted/banned/
  unchecked), a quarantine flag, and a manual "体检" button.

- Per-account proxy support (todo §38 P1): every account can route all Steam
  traffic — CM login (WebSocket-only via the SteamKit2 3.4.0
  `WithHttpClientFactory` path, socks5 remote DNS included), web API, trades
  and mobile confirmations — through its own http/https/socks5 proxy to avoid
  multi-account same-IP association bans. Proxy endpoints are stored
  encrypted per account (`FileCredentialStore.SaveProxyAsync`), the agent
  pre-parses payload proxies fail-fast before any session is touched,
  credentials are scrubbed from logs by the redactor (blacklist + URI
  pattern), and the new `check_proxy` action probes exit IP / Steam
  reachability / latency through the configured (or payload-provided) proxy
  with masked output. Compromise on record: the proxy applies to the active
  account (shared CM client, serial switching — egress changes drop the CM
  connection); parallel multi-account egress needs a per-account
  SteamClient pool (future direction).

- Achievement listing, unlock and reset (todo §33): achievements are listed
  via the community per-game stats page (`get_achievements` +
  `GET /v1/accounts/{name}/achievements?appId=`) — API names are inferred
  from icon file names, and unlock state comes from the "Unlocked <date>"
  blocks rather than icon color (progress-type achievements keep colored
  icons while locked). Writes go through the client stats protocol by hand
  (SteamKit2 has never shipped achievement wrappers): load user stats, map
  names to achievement ids via the unified GetGameAchievements listing
  (list index == id, cross-guarded by the unlock-block id range), patch the
  community-established bitmap encoding, store the merged blob, then read
  back and verify every bit — an unavailable read-back is reported as such
  (`verified: false`) instead of claimed success. Both writes are explicit
  single-shot REST actions (`POST /v1/accounts/{name}/achievements/unlock`
  and `/reset`): an explicit non-empty name list is required everywhere (no
  implicit full-batch path exists), reset additionally requires
  `confirm: true` at both the action and API layers, single-item failures
  never abort the batch and are reported per item, and every read/unlock/
  reset lands an audit entry carrying the full name list. The admin
  console's account panel gains an achievements block (pull list by appId,
  per-item and select-all checkboxes, confirm dialog for unlock, double
  confirmation for reset). Stat/achievement numeric editing stays
  out of scope (§11.5).

- Conservative whitelist auto-accept for trade offers (todo §32): per-account
  `TradePolicy` (`autoAcceptGifts` + partner whitelist, validated at PUT time —
  enabling requires a non-empty whitelist, and omitting the field clears the
  policy toward the safe side). Connected accounts with the policy on run a
  throttled evaluation loop (default every 600s, `Vapor_RECONCILE_TRADE_REFRESH_SECONDS`)
  that auto-accepts only incoming offers which are both from a whitelisted
  partner and gifts-only — offers asking anything in return are never accepted
  automatically, nor are our own counter-offers; unreadable give counts are
  treated as unusable. A required mobile confirmation chains automatically
  without the identity secret ever leaving the agent. Every scan persists a
  `trade.policy_evaluated` audit entry with a per-offer decision (including
  skip reasons), and a completed accept records `trade.auto_accepted` in the
  audit log and publishes it as a broker event the webhook pipeline forwards
  by type. The policy is off by default with no global switch.

- Full-featured admin console (todo §31): `admin.html` grows five panels
  over the admin REST surface — account lifecycle (desired-state editing,
  enable/disable, delete with name-match confirmation), trades &
  confirmations (offer accept/decline, confirmation batches, loot, 1:1
  duplicate-card swap plans), market & claiming (listing create with pricing
  preview, filtered cancellation with dry-run default, points-shop claiming
  with separate free/force paths, license additions), crawl plan management
  (create/edit/trigger/delete with inline validation), and configuration
  (global + per-account settings with whole-dictionary replace semantics).
  Password-class settings keys render masked and never round-trip through
  the DOM; every irreversible write gates on an explicit confirmation, both
  locked by contract tests. The read-only dashboard keeps its zero-write-verb
  contract untouched.

- Playtime boosting as a managed desired state (todo §29): the new `boost`
  account state carries `boostTargets` (appid → target hours, validated at
  PUT time — at least one target required, finite positive hours, conflicting
  per-app targets rejected). The reconciler periodically refreshes total
  playtime via the new `get_playtime` action (parses the embedded `rgGames`
  JSON on the profile games tab, stale-while-revalidate cached 30 min), idles
  every app below its target in a single multi-app `play_games` job (the
  IdleApps list still acts as an exclusion list), and stops idling — keeping
  the session online — once every target is met. Query failures mark a
  deviation without consuming the login failure budget, and an unusable
  report keeps the schedule idle instead of reading as "all targets met".
  Refresh cadence configurable via `Vapor_RECONCILE_BOOST_REFRESH_SECONDS`
  (default 1800s).

### Fixed

- Market fee math (`MarketFeeCalculator`, exposed via property tests):
  `FromBuyerPrice` started its search at the exact-15%-fees estimate
  `floor(target × 100 / 115)`, but the two floored fee components can
  undercut that estimate (and the one-cent minimum leaves up to two
  further cents on very low prices), so the largest feasible seller was
  systematically missed by up to three cents — a seller selling a typical
  sub-$1 card received one cent less than the buyer target allowed. The
  walk now starts at estimate+3 (a proven upper bound on the shortfall)
  and still steps down, so exact-fee targets like 115 → 100 are unchanged.
  Both entry points also guard prices above `MaxSellerProceedsCents`
  (`int.MaxValue / 15`, ~$1.43M) where the int fee multiplies previously
  overflowed unchecked and could wrap the buyer price negative.

### Documentation

- New docs: a zero-to-farming walkthrough (`docs/getting-started.md`),
  a REST API reference covering all 50 `/v1` endpoints
  (`docs/api.md`) and an actions catalog with payload/output fields for
  every job action (`docs/actions.md`).
- README rewritten as a proper front page: capability highlights,
  architecture diagram, quick start, complete documentation index.
- Feature matrix backfilled (runtime PluginStore, endpoint count 34→50);
  docs index quality numbers refreshed (2,614 tests at 100.0%); mkdocs
  nav includes the new pages.

## [0.1.0-alpha.2] - 2026-09-17

### Changed

- `Microsoft.Data.Sqlite` 8.0.24 → 10.0.12 to match the net10.0 target
  framework (the 8.0.x pin was a net8-era leftover); store schemas and
  behavior unchanged.

### Added

- Coverage backfill for the P7-3/P8/P9 feature waves (+53 tests, 2007→2060
  green): full value-shape coverage for `definition_ids` payloads (JsonElement
  round-trips, .NET lists of every numeric shape, scalar wrapping, invalid
  skips, dedup), market listing create/cancel action validation with
  cancellation and transport-failure passthrough, `SteamMarketClient`
  defensive parsing arms, `CrawlRunWorker` cancellation/failure/audit paths
  and mixed-shape batch output parsing, crawl store guards, planner
  empty-pool warnings, crawl-plan PUT merge semantics, and the account market
  / points-shop REST three-state contract (filter payload assembly, 202
  pending, 502 failure, claim validation and dedup). Overall line coverage
  restored to the repository baseline of 99.5% (Steam.Core 97.8%→99.4%,
  ControlPlane 98.8%→99.5%).
- Performance baselines (GA audit §14 #4): latency benchmarks for the read
  REST endpoints under concurrent load plus the job-creation write path for
  comparison (`ApiLatencyBenchmarks.cs`), per-operation managed-allocation
  baselines for the job store and cache layer, cache read/write throughput
  benchmarks, and `scripts/run-benchmarks.sh` to run them all. Measured
  numbers, environment and methodology notes live in `docs/performance.md`
  (the authoritative record with a dated archive); benchmark assertions only
  guard order-of-magnitude regressions.
- Game-data harvesting (P9): `get_game_info_batch` (up to 200 apps per batch,
  per-app errors isolated, shares the `game:{appId}:{cc}` cache tier with
  `get_game_info`) plus a crawl orchestration layer on the control plane —
  `CrawlShardPlanner` (round-robin over enabled accounts with explicit
  per-app overrides), `SqliteCrawlStore` (plans + per-app results, due-cursor
  claiming, run aggregation, per-plan pruning) and `CrawlRunWorker` (claims
  due plans, dispatches shards through the regular job queue, persists
  per-app outcomes, run timeout, one-shot/recurring cursors). Admin REST at
  `/v1/crawl/plans` CRUD + trigger + runs + results queries, `crawl.*` events
  on the existing webhook pipeline, and a read-only `gamedata.html` page with
  a field dictionary for the six store models (`docs/data-dictionary.md` is
  the authoritative source). Store fetches stay anonymous; accounts are
  dispatch/audit identity only and payloads never carry credentials.
- Points shop claiming (P8): `get_points_shop_summary` (balance plus reward
  definitions by id, `free_only` filter) and `claim_points_shop_items` (free
  definitions by default, paid ones need `force=true`; the batch is validated
  before the first redemption and single failures don't abort the rest) with
  REST endpoints `GET/POST /v1/accounts/{name}/points-shop/*`, backed by the
  SteamKit2 LoyaltyRewards unified service.
- Own market listings (P7-1): `get_my_market_listings` action plus
  `GET /v1/accounts/{name}/market/listings`; `SteamMarketClient` parses the
  login-gated mylistings page (listing id, hash name, buyer price, seller
  proceeds, asset summary) with a contract test pinning the response shape.
- Market batch cancel (P7-2): `cancel_market_listings` action plus
  `POST /v1/accounts/{name}/market/listings/cancel`. Filter by app, market
  hash name, inclusive buyer-price range or listing age; per-listing pacing
  (default 1s) as market-specific rate control; one failure does not abort
  the batch and per-listing results are reported back. `dry_run` defaults to
  previewing the would-cancel list, and a real run with no filter is refused
  both at the endpoint and inside the action itself.
- Market listing creation (P7-3): `create_market_listing` action plus
  `POST /v1/accounts/{name}/market/listings` with fee-aware pricing
  (`MarketFeeCalculator`: Steam 5% + publisher 10% on the seller amount,
  buyer/seller conversion both ways). Without `send` it is a dry run
  reporting the pricing plan only; a real listing is doubly gated by the
  per-account `marketListingsEnabled` switch and the agent's
  `AGENT_MARKET_LISTINGS_ENABLED`. Prices come from the caller on either
  side (`seller_proceeds_cents` / `buyer_price_cents`); a mobile/email
  confirmation requirement is reported back for the existing
  market-confirmations loop.
- Store data actions (P3): `get_game_info`, `search_games`, `get_price` and
  `get_market_listings`, all wired through the shared `IVaporCache` layer with
  per-call `cache_ttl_seconds` override (0 disables caching).
- `SteamStoreApiClient` (+ interface) covering appdetails, storesearch and
  community market endpoints; `MarketListing` / `MarketListingsPage` models.
- HTTP resilience for `SteamWebHandler`:
  - 429 responses honor the Retry-After header (bounded by config) while 5xx
    errors use exponential backoff; both are retried within the retry budget.
  - `HttpCircuitBreaker` (Closed/Open/HalfOpen with single half-open probe)
    rejects traffic after repeated failures until recovery.
  - `WebRequestMetrics` counters (successes, 429s, 5xx, 4xx, network failures,
    retries, circuit-breaker rejections) exposed as a snapshot.
  - Configurable `RateLimitIntervalMs`, `MaxRetryDelayMs`, `MaxRetryAfterSeconds`
    and circuit breaker thresholds.
- Trade safety layer (`Vapor.Steam.Core.Trading`):
  - `TradeOfferStateMachine`: legal offer transition checks (accept requires a
    received Active unexpired offer matching the expected partner; decline vs
    cancel direction validation; SteamID64 <-> account ID conversion).
  - `TradeAssetValidator`: pre-send ownership verification (asset exists, is
    tradable, off cooldown, sufficient quantity, duplicate references aggregated).
  - `TradeRateLimiter`: per-account sliding-window quota + concurrency gate
    with configurable options and injectable clock.
  - `ISteamTradeClient` interface extraction and `GetOwnSteamId` (steamlogin
    cookie parsing); trade URLs with 64-bit partner IDs are now parsed correctly.
  - Trade actions enforce validation by default; `skip_verification` and
    `verify_state=false` payload flags allow explicit bypass; Agent wires a
    shared rate limiter via DI.
- Full-chain log redaction: `RedactingLoggerProvider` wraps any logger sink and
  redacts messages, structured state values, scopes and exception content;
  Agent uses `AddRedactingConsole()`.
- Steam data models: `GameInfo`, `ItemInfo`, `PriceOverview`, `GameSearchResult`
  with cache key helpers and freshness markers.
- Cache layer: `IVaporCache` + `MemoryVaporCache` (per-entry TTL, LRU eviction,
  hit/miss counters, single-flight factory deduplication, injectable clock).
- Redis cache backend (`RedisVaporCache`, enabled via `VAPOR_REDIS`): JSON
  envelope with absolute fresh/stale timestamps, cross-instance single-flight
  lock (SET NX PX + token release), SCAN-based prefix invalidation.
- Plugin system (P4, `Vapor.Plugins.Core`): `plugin.json` manifest discovery,
  collectible `PluginLoadContext` isolation with unload verification, SemVer
  compatibility policy, trust/permission model (minimum-trust gate,
  least-privilege capability stripping, `LoadedPlugin.GrantedPermissions`),
  event subscription (`IEventPlugin` + `PluginEventDispatcher`), typed
  configuration extensions with env overrides; sample plugin and
  `docs/plugins.md` guide.
- Official plugins:
  - MobileAuthenticator: TOTP generation, confirmation listing/responding,
    `save_shared_secret` / `save_identity_secret`, `confirm_trade_offer`
    (creator matching with bounded retry) and `confirm_all_confirmations`
    (type filter + allow/cancel, per-item failure isolation).
  - Monitoring: Prometheus text exposition endpoint + `get_metrics` action
    covering action/session/cache/runtime metrics; Grafana dashboard template.
  - MarketWatch: price-threshold and free-game (`kind=free` edge detection)
    watches with webhook alerts (`market_watch_add/remove/list`).
- Account orchestration (P5): `/v1/accounts` resource CRUD + lifecycle
  (enable/disable/remove), `DesiredStateReconciler` driving
  `offline`/`online`/`idle`/`farm` desired states with login-failure cooldowns,
  agent-loss rebalancing and dry-run mode; aggregate account views, per-account
  job/session filters, reconcile metrics + audit records.
- Card farming (P6-1): `get_card_drops` action parsing the badge page
  (three-way fixture cross-validation), `farm` desired state with a CP-side
  farm queue (play next app by remaining drops, auto-rotate on completion),
  and idle-exclusion-list semantics in farm mode.
- Trading & confirmation loop (P6-2): `get_trade_offers` with synchronous REST
  bridging, accept/decline endpoints with automatic follow-up mobile
  confirmation, batch confirmation (`confirm_all_confirmations` + CP
  `/confirmations/accept-all`), `loot_inventory` (scannable inventory →
  tradable filter → rate-limited send) and 1:1 duplicate card swap
  (`find_duplicates` / `swap_duplicates` with dry-run default). Account-scoped
  endpoints: `/trade-offers`, `/confirmations/accept-all`, `/loot`,
  `/duplicates`, `/swap-offers`, `/inventory`.
- Interop & claiming (P6-3): `add_license` free-license claiming (app IDs via
  client protocol, sub IDs via store checkout), MarketWatch free-game alerts
  forming a watch→claim loop, inventory REST with multi-app scan +
  tradable/marketable filters, and `Vapor.Agent import-mafile` CLI importing
  SDA / steamguard-cli `.maFile` secrets into the encrypted credential store
  (agent-local by design: maFile contents never transit control-plane tasks).
- QR code sign-in (P6-4): `qr_login` login-task flag using SteamKit2 QR
  challenge + polling; challenge URL surfaces via session events with
  automatic rotation and challenge clearing.
- Read-only web dashboard (`wwwroot/dashboard.html`): stat cards, accounts,
  agents, sessions, jobs and audit log with dual SSE streams + polling
  fallback; write-verb contract tests guard its GET-only surface.
- Notifications & automation (P5): `INotificationSink` + HMAC-signed webhook
  sink with exponential-backoff retries and rule-based filtering
  (`Vapor_WEBHOOK_NOTIFICATIONS_*`); `TwoFactorAutoResponder` answering 2FA
  challenges locally from stored shared secrets (opt-in,
  `AGENT_2FA_AUTO_SUBMIT=true`, Steam server-time sync); recurring jobs via
  `schedule` (interval or 5-field cron) with missed/overlap policies.
- Protocol resilience (P5): `ISteamTransport` adapter layer isolating SteamKit2
  behind a protocol-agnostic surface (SteamResult mirrors wire EResult codes);
  recorded contract tests for Steam Web API responses (caught and fixed the
  market search render contract drift); WS tunnel protocol replay tests
  (serialization snapshots + forward compatibility).
- Observability: OpenTelemetry tracing with W3C traceparent propagated across
  the agent tunnel; dispatch-failure, notification, schedule-trigger and
  reconcile metrics; Prometheus alert rules.
- Terminal dispatch state: undispatchable tasks fail permanently after
  `Vapor_TASK_MAX_DISPATCH_ATTEMPTS` delayed retries instead of looping
  forever; task `output` is persisted end-to-end (agent → store → REST).

### Fixed

- Admin/dashboard pages 404 in real deployments: the Web SDK only copies
  wwwroot on publish (not into bin) and `UseStaticFiles()` resolved the web
  root from the process working directory, so launching the dll from any
  other cwd served no pages at all — while the test suite never requested a
  page and stayed green. wwwroot now mirrors into the build output and the
  static file provider anchors on `AppContext.BaseDirectory`; new E2E guard
  tests exercise the pages through the bare-dll launch shape (verified red
  without the fix, green with it).
- QR sign-in challenges never reached the admin pages: the challenge SSE
  streams did not listen for `qr_required` (so the rotating Steam QR URL
  shown on the page went stale) and the challenge card rendered a
  meaningless auth-code prompt. The admin UI now live-updates QR challenges
  with a copyable challenge URL, and the read-only dashboard surfaces the
  session state.
- Flaky `SessionManagerTests.SubscribeAllEvents_ReceivesEventsFromSessions` timeout.
- `TokenRefreshTests` async-without-await warnings breaking strict builds on .NET 8 SDK.
- Market search render contract drift (new `results[]`/`total_count` shape
  replaced `listinginfo`/`total_rowcount`), which made listing queries return
  null silently; parsing now prefers the new shape and falls back to the legacy
  one, and `MarketListing` gained aggregated `Name`/`HashName`/`SellListings`.
- Coverage pipeline: misplaced `MaxCpuCount` runsettings token silently
  disabling collection, stale-report cleanup ordering, and cross-report
  filename-prefix normalization for merged coverage totals.
- `MaFileParser` no longer leaks a raw `JsonException` on the rare
  wrong-password path where the garbage plaintext happens to pass PKCS7
  padding validation; it surfaces as the same decrypt `InvalidDataException`
  as every other failure on that route.
- `RedisVaporCache.CreateFromConnectionString` retries transient connection
  failures (3 attempts, short backoff) instead of failing outright on the
  first blip — a bare connection string had dropped the options'
  `abortConnect=false` default.
- Redis integration tests under the coverage CI job (coverlet instrumentation
  on a 4-core runner queues the thread pool enough to trip SE.Redis's 5s
  command timeout against the healthy local server): the test connection
  string now sets `syncTimeout`/`asyncTimeout` to 15s.

### Security

- Audit log persistence: `SqliteAuditStore` + `IAuditStore` with `GET /v1/audit/logs`
  (filters by action/account/jobId/time range, paging, redacted sensitive details).
- Dedicated audit records for login transitions (`session.login`) and sensitive
  task results (trade/redeem actions via `task.result.reported`).
- Credential store v2 format: AES-GCM encrypted tokens at rest, transparent
  v1 migration, atomic writes, `.bak` corruption recovery, and Unix permission
  tightening (owner-only 600).
- Master key configuration: `VAPOR_ENCRYPTION_KEY_BASE64` and
  `VAPOR_ENCRYPTION_KEY_FILE` sources (KMS/Docker secrets workflows) with
  priority over `VAPOR_ENCRYPTION_KEY`.
- Key rotation CLI (`tools/Vapor.KeyRotation`): re-encrypts the credential store
  between keys with `base64:`/`file:`/`env:` specs, `--dry-run`, and abort-on-failure safety.
- Explicit-key crypto APIs (`EncryptWithKey`/`DecryptWithKey`) for rotation tooling.
- Shared secrets (2FA TOTP) and identity secrets (confirmation signing) stored
  encrypted in the credential store; identity secrets never leave the agent —
  control-plane payloads are zero-secret by contract (test-asserted).
- QR sign-in keeps the request key agent-side; refresh tokens persist only
  through the existing encrypted store.
- Audit coverage extended to account lifecycle and orchestrator decisions,
  trade accept/decline/confirm, batch confirmations, loot, duplicate swaps and
  inventory reads, all redacted before persistence.
- Webhook notifications never carry verification codes (bool flag only) and
  are HMAC-signed with replay-resistant timestamps.

## [0.1.0-alpha.1] - 2025-12-28

### Added

- Control plane auth challenge tracking + endpoints (list, SSE, submit code).
- Session tracking endpoint and event plumbing for the admin UI.
- Admin UI improvements (auth challenges panel, job status aliases, favicon).
- CI workflow (build/test + Codecov upload) and release workflow (multi-RID zips).
- Test runner scripts with coverage support and roll-forward for `net8.0` tests.
- Repo meta: `CODE_OF_CONDUCT.md`, `CONTRIBUTING.md`, `SECURITY.md`, `SUPPORT.md`, templates, `global.json`, assets.

### Changed

- Coverage defaults to enabled on CI; local coverage is opt-in via scripts.

### Fixed

- Normalized session/auth event types and cleaned up stale auth prompts during session progress.

[Unreleased]: https://github.com/cuihairu/vapor/compare/v0.1.0-alpha.2...HEAD
[0.1.0-alpha.2]: https://github.com/cuihairu/vapor/compare/v0.1.0-alpha.1...v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/cuihairu/vapor/releases/tag/v0.1.0-alpha.1
