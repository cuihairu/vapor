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
   - `SteamControl-ControlPlane-vX.Y.Z-<rid>.zip`
   - `SteamControl-Agent-vX.Y.Z-<rid>.zip`
