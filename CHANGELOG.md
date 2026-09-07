# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Audit log persistence: `SqliteAuditStore` + `IAuditStore` with `GET /v1/audit/logs`
  (filters by action/account/jobId/time range, paging, redacted sensitive details).
- Dedicated audit records for login transitions (`session.login`) and sensitive
  task results (trade/redeem actions via `task.result.reported`).
- Credential store v2 format: AES-GCM encrypted tokens at rest, transparent
  migration from legacy v1 plain files, atomic writes, `.bak` backup recovery,
  and Unix permission tightening (owner-only 600).
- Master key configuration: `VAPOR_ENCRYPTION_KEY_BASE64` and
  `VAPOR_ENCRYPTION_KEY_FILE` sources (KMS/Docker secrets workflows) with
  priority over `VAPOR_ENCRYPTION_KEY`.
- Key rotation CLI (`tools/Vapor.KeyRotation`): re-encrypts the credential store
  between keys with `base64:`/`file:`/`env:` specs, `--dry-run`, and abort-on-failure safety.
- Explicit-key crypto APIs (`EncryptWithKey`/`DecryptWithKey`) for rotation tooling.

### Fixed

- Flaky `SessionManagerTests.SubscribeAllEvents_ReceivesEventsFromSessions` timeout.
- `TokenRefreshTests` async-without-await warnings breaking strict builds on .NET 8 SDK.

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
