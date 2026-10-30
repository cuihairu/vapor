简体中文 | English

# 发布流程

本仓库采用语义化版本（SemVer），tag 形如 `vX.Y.Z`（以及可选的预发布版本，如 `vX.Y.Z-alpha.1`）。

## 检查清单

1. 确保 `main` 上 CI 全绿。
2. 更新 `CHANGELOG.md` 中 `## [Unreleased]` 之下。
3. 创建 tag：
   - `git tag -a vX.Y.Z -m "vX.Y.Z"`
4. 推送 tag：
   - `git push origin vX.Y.Z`
5. GitHub Actions 会为该 tag 构建并发布 GitHub Release：
   - `Vapor-ControlPlane-vX.Y.Z-<rid>.zip`
   - `Vapor-Agent-vX.Y.Z-<rid>.zip`
   - Docker 镜像推送至 GHCR：`ghcr.io/cuihairu/vapor/controlplane:X.Y.Z` 与
     `ghcr.io/cuihairu/vapor/agent:X.Y.Z`（正式 release 也会更新
     `:latest`；预发布版本不移动 `:latest`）

注意：
- 带预发布后缀的 tag（包含 `-`，例如 `v0.1.0-alpha.1`）会作为 GitHub **prerelease** 发布。
- 已部署环境的回滚：钉定上一个镜像 tag（见 `docs/production.md`）。被更新版本迁移过的数据库对旧二进制仍然可读——新增列会被忽略。

