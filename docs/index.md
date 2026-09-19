---
hide:
  - navigation
  - toc
---

# Vapor (ASF-inspired)

**API-controlled, headless Steam automation platform** designed for large-scale batch operations and multi-region deployment.

[![CI](https://github.com/cuihairu/vapor/actions/workflows/ci.yml/badge.svg)](https://github.com/cuihairu/vapor/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/cuihairu/vapor/branch/main/graph/badge.svg)](https://codecov.io/gh/cuihairu/vapor)
[![release](https://img.shields.io/github/v/release/cuihairu/vapor?sort=semver)](https://github.com/cuihairu/vapor/releases)
[![license](https://img.shields.io/github/license/cuihairu/vapor)](https://github.com/cuihairu/vapor/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-net10.0-512BD4)](https://dotnet.microsoft.com/)

<div class="grid cards" markdown>

-   :material-server-network:{ .lg .middle } **Control plane + regional agents**

    ---

    A single public HTTP API submits jobs; regional agents execute them close to Steam, partners and users — with idempotency, retries and partial-success accounting.

    [:arrow-right: Architecture](architecture.md)

-   :material-robot:{ .lg .middle } **ASF-style session engine**

    ---

    Steam session logic ("Bot" sessions + pluggable **Actions**) stays isolated and reusable: login with SteamGuard/2FA challenges, card farming, trading, market listings, inventory scanning and more.

    [:arrow-right: Session engine](session-engine.md)

-   :material-puzzle:{ .lg .middle } **Plugin platform**

    ---

    Load-context isolated plugins with manifest, SemVer API contract, permission grants and an event dispatcher — Monitoring and MobileAuthenticator ship in-tree.

    [:arrow-right: Plugin development](plugins.md)

-   :material-shield-lock:{ .lg .middle } **Secure by default**

    ---

    API-key auth, audit logs, AES-GCM encrypted credential stores, redacting log pipeline and circuit-broken Steam web access.

    [:arrow-right: Security model](architecture.md#security-model-public-internet-api)

</div>

## Why Vapor?

Vapor reuses the proven ASF concepts — bot sessions, actions, farming — but re-shapes the delivery model: instead of one process per machine, Vapor splits into a **centralized control plane** (public API, SQLite-backed orchestration) and **headless regional agents** (WebSocket-connected executors). That makes it a fit for fleet-style, multi-region operation where ASF's single-box model stops scaling. A detailed comparison lives in the [feature matrix](feature-matrix.md).

## Quick start

=== "Docker Compose"

    ```bash
    git clone https://github.com/cuihairu/vapor.git
    cd vapor
    docker compose up -d                  # control plane + agent
    docker compose --profile observability up -d   # + Prometheus & Grafana
    ```

    See [Docker & Compose](docker.md) for the full topology and variables.

=== "Local .NET"

    ```bash
    dotnet build Vapor.sln
    export VAPOR_ENCRYPTION_KEY=0123456789abcdef0123456789abcdef
    dotnet run --project src/Vapor.ControlPlane    # control plane
    # separate shell — agents need their identity wired:
    export AGENT_ID=agent-1 AGENT_REGION=local
    export AGENT_CONTROLPLANE_WS_URL=ws://127.0.0.1:8080/v1/agent/ws
    export AGENT_API_KEY=dev-agent
    dotnet run --project src/Vapor.Agent           # agent
    ```

    Full variable reference: [Local run](running.md).

## Documentation

| Section | Contents |
|---------|----------|
| **Getting Started** | [Local run](running.md) · [Docker & Compose](docker.md) · [Production deployment](production.md) |
| **Design** | [Architecture](architecture.md) · [Session engine](session-engine.md) · [Plugins](plugins.md) · [Feature matrix](feature-matrix.md) · [Performance](performance.md) |
| **Reference** | [Data dictionary](data-dictionary.md) · [Testing](testing.md) · [Releasing](releasing.md) · [Troubleshooting](troubleshooting.md) |

## Status

Vapor is currently **alpha** — breaking changes are expected. Quality gates: 3-OS × dual-configuration CI matrix, 2,263 tests at 99.6% line coverage, strict static analysis (0-warning build), coverage gate and property-based tests. See [releasing](releasing.md) for the versioning policy.

## License

Apache-2.0. See [LICENSE](https://github.com/cuihairu/vapor/blob/main/LICENSE), [contributing](https://github.com/cuihairu/vapor/blob/main/CONTRIBUTING.md) and [security policy](https://github.com/cuihairu/vapor/blob/main/SECURITY.md).
