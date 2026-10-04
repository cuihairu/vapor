# Dependency upgrade policy

Vapor ships a headless automation runtime that talks to Steam over
SteamKit2, stores everything in SQLite, and is hardened to an unusual
degree for an alpha project: the build treats every analyzer warning
and every NuGet audit advisory as an error, and CI refuses to merge a
drop below 100% line and branch coverage. Dependencies are the one input
where that strictness can quietly rot, so this page states the rules
explicitly and pins them mechanically.

This page is the **single source of truth** for every `PackageReference`
version in the repository. `scripts/verify-dependency-versions.py` parses
the table below out of this file and fails the build if any `.csproj`
disagrees with it, if a package is used without being registered here, or
if the audit configuration is relaxed. Edit this table and the projects
together; the script is the referee, not the other way round.

## 1. Audit posture

`Directory.Build.props` sets:

```xml
<NuGetAuditMode>all</NuGetAuditMode>   <!-- direct *and* transitive -->
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

`NuGetAuditMode=all` widens the audit from direct references to the whole
transitive closure, and `TreatWarningsAsErrors` promotes the resulting
`NU1901`–`NU1904` advisories from warnings to build failures. Together
they mean a known-vulnerable package cannot be built, let alone
shipped — there is no "audit warning backlog" to work through later,
because it never compiles.

The baseline is zero advisories across every project, direct and
transitive:

```bash
for p in $(find src tools tests -name '*.csproj' -not -path '*/obj/*'); do
  dotnet list "$p" package --vulnerable --include-transitive | tail -1
done
```

Run it per project, not with `dotnet list Vapor.sln package`: the
solution-level form aborts with a project-collection conflict on this
solution layout (two projects resolve to identical global properties),
so the per-project loop above is the supported spelling.

The other half of the same posture is the `dependency-review` workflow
(`.github/workflows/dependency-review.yml`), which runs
`dependency-review-action` on every pull request and fails when a PR
*introduces* a vulnerable or GPL-licensed dependency. The NuGet audit
governs what is in the tree; dependency review governs what is allowed to
enter the tree in the first place.

Both guards are configuration, not folklore: the script in §5 re-asserts
`NuGetAuditMode` and `TreatWarningsAsErrors` on every push, so quietly
setting `NuGetAuditMode` back to `direct` (or turning warnings back into
warnings) fails CI rather than silently weakening this section.

## 2. The version fleet

Every `PackageReference` in `src/`, `tools/` and `tests/` is registered
here. `Scope` is one of:

| Scope | Meaning |
|-------|---------|
| `test-fleet` | Every test project **must** declare it, at exactly this version |
| `any` | A project that declares it **must** use exactly this version (declaring it at all is optional) |

A *test project* here is a `*.Tests` project, and every one of the 13 of
them also sets `<IsTestProject>true</IsTestProject>` — the script checks
that property too, because it is what `Directory.Build.props` keys the
coverage defaults off. Three plugin test projects were created without
it in earlier rounds; since the property is also the natural way to
*detect* a test project, that gap would have silently exempted them from
the fleet check below, so the script accepts a project as a test project
on either signal and fails when the two disagree.

`test-fleet` exists because the test toolchain has to behave identically
in all 13 test projects: a single project left on an older runner is how
you get a test that passes locally and reports nothing, or a coverage
denominator that quietly changes shape. The canonical example is in
`todo.md` — `Vapor.ControlPlane.Tests` sat on xunit 2.6.2 / test SDK
17.8.0 / runner 2.5.4 for the entire life of the project while the other
11 sat on 2.9.3 / 17.14.1 / 2.8.2, and nothing was red.

<!-- verify-dependency-versions:packages -->

| Package | Version | Scope | Notes |
|---------|---------|-------|-------|
| `xunit` | 2.9.3 | test-fleet | Test framework. 2.6.2 → 2.9.3 tightened assertion diagnostics; keep the fleet on one minor line. |
| `xunit.runner.visualstudio` | 2.8.2 | test-fleet | VSTest adapter. Must ship `<PrivateAssets>all</PrivateAssets>` (checked mechanically) — it drags in the whole `Microsoft.TestPlatform.*` set plus build assets. |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | test-fleet | Test host + discovery. Drives `--blame-hang`, which the coverage job relies on. |
| `coverlet.collector` | 6.0.4 | test-fleet | Coverage collector. `PrivateAssets` is deliberately *not* required here: test projects are `IsPackable=false` and nothing references them, so there is no dependency graph for it to leak into. |
| `FsCheck.Xunit` | 2.16.6 | any | Property-based testing. Only 5 of 13 test projects use it. |
| `Moq` | 4.21.0 | any | Mocking. Only 3 test projects use it. |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | any | `WebApplicationFactory` host for the control-plane tests. |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 | any | Also a `src/` package; one version for the whole tree. |
| `Microsoft.Extensions.Logging.Console` | 10.0.12 | any | `src/Vapor.Agent`, `src/Vapor.Steam.Core`. |
| `Microsoft.Extensions.DependencyInjection` | 10.0.12 | any | `src/Vapor.Agent`. |
| `Microsoft.Data.Sqlite` | 10.0.12 | any | Storage layer. Its `SqliteJobStore.Dispose` teardown is a known CI flake site — see §4. |
| `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.5 | any | Native SQLite bundle; version line is independent of the .NET one. |
| `Cronos` | 0.13.0 | any | Cron parsing for scheduled jobs. |
| `Swashbuckle.AspNetCore` | 10.2.3 | any | OpenAPI/Swagger, opt-in via `Vapor_ENABLE_SWAGGER`. |
| `StackExchange.Redis` | 3.3.1 | any | Optional cache backend. |
| `SteamKit2` | 3.4.0 | any | The Steam protocol client. Upgrades here are Steam-behaviour changes, not library hygiene — see §4. |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.19.1 | any | OTLP exporter. |
| `OpenTelemetry.Extensions.Hosting` | 1.19.1 | any | OTel host integration. |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.19.0 | any | OTel ASP.NET Core instrumentation. Independently versioned upstream — 1.19.0 vs 1.19.1 is upstream's split, not local drift. |

<!-- /verify-dependency-versions:packages -->

## 3. What is automated

`.github/dependabot.yml` runs weekly against the repository root for the
`nuget` ecosystem, opening up to 10 pull requests at a time and **ignoring
all `version-update:semver-major`**:

```yaml
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 10
    ignore:
      - dependency-name: "*"
        update-types: ["version-update:semver-major"]
```

So patch and minor bumps arrive as ordinary pull requests, are reviewed
by a human, and go through the normal gate — Release build with zero
warnings, the serial coverage round, and the double-100% coverage gate.
Dependabot bumps are not special-cased anywhere: they are treated exactly
like any other change, which is the point.

Three things are deliberately **not** automated, because a bot cannot
judge them:

- **GitHub Actions versions** — every action is SHA-pinned (see
  `docs/releasing.md` conventions and the pin comments in the workflows).
  Bumping a SHA is a supply-chain decision, not a version bump.
- **Docker base images and the .NET SDK version** — these set the runtime
  floor for the published images and are changed deliberately.
- **Semver-major dependency bumps** — the blanket `ignore` above. A major
  bump is a behaviour change that needs the procedure in §4, not a
  green-PR-shaped change.

## 4. Major upgrades (the manual path)

A semver-major bump, or any change to a dependency with a large blast
radius, is done by hand against this checklist:

1. Edit the row in the §2 table **and** every `.csproj` that declares the
   package, in the same commit. The script fails on a half-applied bump
   — that is the intended friction, not a bug.
2. `dotnet build Vapor.sln -c Release --no-incremental` and read the
   output. Expect analyzer and nullable warnings to move: a new major
   frequently tightens nullability annotations or adds analyzer rules,
   and with `AnalysisLevel=latest-all` plus `TreatWarningsAsErrors` those
   surface as build errors. Treat each one as real work, not noise — the
   previous Moq 4.20.72 → 4.21.0 minor bump landed already-red on `main`
   with three CS8602 sites.
3. `./scripts/collect-coverage-serial.sh` then
   `python3 scripts/coverage-summary.py TestResults/coverage-serial --min 100 --min-branch 100`.
   A tooling bump can move the coverage *denominator* (a new test
   assembly, a differently-shaped generated file). A silent drop here
   means the gate is now measuring something else.
4. `dotnet format Vapor.sln --verify-no-changes`.
5. `python3 scripts/verify-testing-docs.py` — test counts must match
   `tests/TESTING.md` exactly.
6. `python3 scripts/verify-dependency-versions.py` — self-check, so a
   broken script is caught before CI.
7. Note the change in `CHANGELOG.md` under `## [Unreleased]`.

Packages with protocol or data-model exposure get extra scrutiny, because
their tests cannot prove the upgrade safe:

- **SteamKit2** — the Steam protocol surface. The suite pins wire-level
  behaviour with known vectors (see the TOTP and confirmation-hash
  property tests in `Vapor.Steam.Core`); a major bump that changes login
  challenge routing or trade state transitions is a product change.
- **Microsoft.Data.Sqlite** — owns the four SQLite databases. Migrations
  are idempotent-on-start and additive (see `docs/production.md` § Data,
  backup and upgrades), so an upgrade that adds a column is safe, but it
  has the repo's only known CI flake signature:
  `StorageCollectRunnerTests.DispatchCollectAsync_ConfirmationSettledWithoutConfirmation_ReportsNotConfirmed`
  can throw a `NullReferenceException` from `SqliteJobStore.Dispose`
  during teardown on the macOS leg. Re-run the failed job before
  investigating it as a regression.

## 5. Mechanical guards

Two workflows enforce the above, and neither requires anyone to remember
anything:

| Guard | Where | Enforces |
|-------|-------|----------|
| `verify-dependency-versions.py` | `.github/workflows/ci.yml` (format job) | §2 table ⇄ every `.csproj`; `test-fleet` completeness; every `*.Tests` project sets `IsTestProject`; `<PrivateAssets>all</PrivateAssets>` on the runner; `NuGetAuditMode=all` and `TreatWarningsAsErrors` still set |
| `dependency-review-action` | `.github/workflows/dependency-review.yml` (pull requests) | No vulnerable or GPL-licensed dependency introduced by a PR |

Run the version guard locally with:

```bash
python3 scripts/verify-dependency-versions.py
```

It exits 0 with an `OK:` summary when the tree matches this page, and 1
with one line per violation otherwise. A new dependency is expected to
fail it once — that failure is the prompt to add a row here, which is
what keeps this page from silently rotting into fiction.
