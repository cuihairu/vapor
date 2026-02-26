# Contributing

## Prerequisites

- .NET SDK 8.x (recommended; 9.x can build via `global.json`)
  - Note: this repo targets `net8.0`, so running the built apps/tests requires the .NET 8 runtime installed.

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

## Pull requests

- Keep changes focused and easy to review.
- Add/update tests when changing behavior.
- Update docs when changing APIs or runtime behavior.

## Releases

See `docs/releasing.md`.

