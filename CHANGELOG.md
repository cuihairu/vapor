# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
