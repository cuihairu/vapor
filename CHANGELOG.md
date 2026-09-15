# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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

### Fixed

- Flaky `SessionManagerTests.SubscribeAllEvents_ReceivesEventsFromSessions` timeout.
- `TokenRefreshTests` async-without-await warnings breaking strict builds on .NET 8 SDK.

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

- Ongoing development.

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

[Unreleased]: https://github.com/cuihairu/vapor/compare/v0.1.0-alpha.1...HEAD
[0.1.0-alpha.1]: https://github.com/cuihairu/vapor/releases/tag/v0.1.0-alpha.1
