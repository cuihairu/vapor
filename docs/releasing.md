# Releasing

This repo uses **Semantic Versioning** with tags in the form `vX.Y.Z` (and optional prereleases like `vX.Y.Z-alpha.1`).

## Checklist

1. Ensure CI is green on `main`.
2. Update `CHANGELOG.md` under `## [Unreleased]`.
3. Create a tag:
   - `git tag -a vX.Y.Z -m "vX.Y.Z"`
4. Push the tag:
   - `git push origin vX.Y.Z`
5. GitHub Actions will build and publish a GitHub Release for the tag:
   - `Vapor-ControlPlane-vX.Y.Z-<rid>.zip`
   - `Vapor-Agent-vX.Y.Z-<rid>.zip`
   - Docker images to GHCR: `ghcr.io/cuihairu/vapor/controlplane:X.Y.Z` and
     `ghcr.io/cuihairu/vapor/agent:X.Y.Z` (official releases also update
     `:latest`; prereleases do not move `:latest`)

Notes:
- Tags with a prerelease suffix (contains `-`, e.g. `v0.1.0-alpha.1`) are published as GitHub **prereleases**.
- Rollback for deployed environments: pin the previous image tag (see `docs/production.md`). Databases migrated by a newer version remain readable by older binaries — added columns are ignored.

