<p align="center">
  <img src="docs/assets/vapor.svg" width="96" alt="Vapor" />
</p>

# Vapor (ASF-inspired)

[![CI](https://github.com/cuihairu/vapor/actions/workflows/ci.yml/badge.svg)](https://github.com/cuihairu/vapor/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/cuihairu/vapor/branch/main/graph/badge.svg)](https://codecov.io/gh/cuihairu/vapor)
[![release](https://img.shields.io/github/v/release/cuihairu/vapor?sort=semver)](https://github.com/cuihairu/vapor/releases)
[![license](https://img.shields.io/github/license/cuihairu/vapor)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-net10.0-512BD4)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![status](https://img.shields.io/badge/status-alpha-orange)](#development-status)

API-controlled, headless Steam automation platform designed for large-scale batch operations and multi-region deployment.

## Requirements

- Runtime: .NET 10 (apps/tests target `net10.0`)
- SDK: 10.x (recommended)
- Language: C# (via Directory.Build.props)

## Docs

- Architecture: `docs/architecture.md`
- Local run: `docs/running.md`
- Docker & Compose: `docs/docker.md`
- Production deployment: `docs/production.md`
- Troubleshooting: `docs/troubleshooting.md`

## Testing

- Run tests: `./scripts/run-tests.sh` (or `pwsh ./scripts/run-tests.ps1`)
- Coverage: `./scripts/run-tests.sh --coverage`
  - Tests require the .NET 10 runtime; `DOTNET_ROLL_FORWARD=Major` can bridge an older runtime.

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

