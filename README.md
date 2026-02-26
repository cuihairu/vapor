<p align="center">
  <img src="docs/assets/icon.svg" width="96" alt="Vapor" />
</p>

# Vapor (ASF-inspired)

[![CI](https://github.com/cuihairu/vapor/actions/workflows/ci.yml/badge.svg)](https://github.com/cuihairu/vapor/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/cuihairu/vapor/branch/main/graph/badge.svg)](https://codecov.io/gh/cuihairu/vapor)
[![release](https://img.shields.io/github/v/release/cuihairu/vapor?sort=semver)](https://github.com/cuihairu/vapor/releases)
[![license](https://img.shields.io/github/license/cuihairu/vapor)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-net8.0-512BD4)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![status](https://img.shields.io/badge/status-alpha-orange)](#development-status)

API-controlled, headless Steam automation platform designed for large-scale batch operations and multi-region deployment.

## Requirements

- Runtime: .NET 8 (apps/tests target `net8.0`)
- SDK: 8.x (recommended; newer SDKs like 9.x can build)
- Language: C# 12 (via .NET 8)

## Docs

- Architecture: `docs/architecture.md`
- Local run: `docs/running.md`

## Testing

- Run tests: `./scripts/run-tests.sh` (or `pwsh ./scripts/run-tests.ps1`)
- Coverage: `./scripts/run-tests.sh --coverage`
  - Note: tests target `net8.0` (recommended: install the .NET 8 runtime). If you only have a newer runtime, set `DOTNET_ROLL_FORWARD=Major` (the scripts do this by default).

## Releases

- Versions: SemVer tags `vX.Y.Z` (see `docs/releasing.md`)
- Changelog: `CHANGELOG.md`

## Development status

Vapor is currently **alpha** (breaking changes expected).

## License

Apache-2.0, see `LICENSE`.

## Contributing

See `CONTRIBUTING.md` and `SECURITY.md`.

## Support

See `SUPPORT.md`.

