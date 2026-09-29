# Contributing

## Prerequisites

- .NET SDK 10.x (recommended; `global.json` allows any newer SDK)
  - Note: this repo targets `net10.0`, so running the built apps/tests requires the .NET 10 runtime installed.

## Build

```bash
dotnet restore Vapor.sln
dotnet build Vapor.sln -c Release
```

## Test

```bash
./scripts/run-tests.sh
./scripts/run-tests.sh --coverage
```

## Dependencies

Every `PackageReference` version in the repo is registered in one table,
[`docs/dependencies.md`](docs/dependencies.md), which is the single source
of truth. When you add or bump a dependency:

- Edit the table **and** the `.csproj` files in the same commit.
- Run `python3 scripts/verify-dependency-versions.py` before pushing.
- Major (semver-major) upgrades follow the checklist in that page; they
  are never merged automatically.

The build treats NuGet audit advisories as errors
(`NuGetAuditMode=all`), so a vulnerable package does not compile.

## Pull requests

- Keep changes focused and easy to review.
- Add/update tests when changing behavior.
- Update docs when changing APIs or runtime behavior.

## Releases

See `docs/releasing.md`.

