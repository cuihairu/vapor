English | 简体中文

# Performance Baseline

> Authoritative source. This file is the single record of performance baseline numbers in the repo; assertions
> inside the benchmarks only check functional correctness + wide order-of-magnitude upper bounds (to guard against
> CI jitter flakes) — **the printed numbers are the baseline**. When refreshing the baseline, move the current table
> as a whole into the archive section at the end of the file, then re-run and transcribe in the new environment (same
> archive pattern as the coverage baseline in `tests/TESTING.md`).

## Measurement Environment (2026-09-15 measured)

| Item | Value |
|---|---|
| CPU | Intel i9-10880H, 8 cores |
| RAM | 30 GiB |
| OS | Ubuntu 26.04 (Linux 7.0.0-31-generic) |
| .NET | SDK 10.0.112, runtime 10.0.12 |
| Build Configuration | Release, `RunConfiguration.MaxCpuCount=2` |

Numbers are strongly tied to the measuring machine: re-run and refresh after changing machines or runtimes; no
cross-environment comparison.

## How to Run

```bash
./scripts/run-benchmarks.sh                # full suite of both benchmark projects
./scripts/run-benchmarks.sh -f <filter>    # append --filter
```

Or manually (`--logger` must be detailed, otherwise xunit does not show the benchmark-printed numbers):

```bash
dotnet test tests/Vapor.ControlPlane.Tests -c Release \
  --logger "console;verbosity=detailed" \
  --filter "FullyQualifiedName~Performance" -- RunConfiguration.MaxCpuCount=2
dotnet test tests/Vapor.Steam.Core.Tests -c Release \
  --logger "console;verbosity=detailed" \
  --filter "FullyQualifiedName~Performance" -- RunConfiguration.MaxCpuCount=2
```

Benchmarks are not hooked into CI (following the "local dev machine measured numbers are authoritative" stance; CI
only guarantees tests pass, not the numbers).

## Measurement Conventions

- **Latency benchmarks** go through `WebApplicationFactory`'s in-memory handler: covering framework pipeline +
  serialization + storage costs, **excluding the real network stack** — what's recorded is a regression baseline of
  the code under test, not end-to-end network latency.
- The latency host uses a real `:memory:` store and removes all hosted background services
  (`RemoveAll<IHostedService>`) to keep background polling out of the measurement window; seed data: 50 accounts /
  200 jobs / 100 audit entries / 5 crawl plans × 40 results.
- **Allocation** uses `GC.GetTotalAllocatedBytes(precise: false)` process-level deltas ÷ operation count (async code
  migrates between thread-pool threads, so per-thread counters don't apply); absolute values include test-pipeline
  noise — the use is watching order-of-magnitude regressions, not splitting percentages.
- The concurrency-correctness benchmark (`Vapor.Steam.Core.Tests/Performance/ConcurrencyTests.cs`) only asserts
  behavioral correctness; it prints no throughput numbers and is not recorded here.

## Baseline (2026-09-15, measured on this machine)

### Latency — read-only endpoints (8 concurrency × 200 requests) + job-dispatch write-path comparison

| Endpoint | p50 | p95 | max | Throughput |
|---|---|---|---|---|
| `GET /healthz` | 0 ms | 4 ms | 18 ms | 5445 req/s |
| `GET /v1/accounts` | 1 ms | 8 ms | 24 ms | 2851 req/s |
| `GET /v1/crawl/plans` | 4 ms | 10 ms | 21 ms | 1405 req/s |
| `GET /v1/crawl/results?limit=100` | 13 ms | 31 ms | 40 ms | 501 req/s |
| `GET /v1/jobs?limit=100` | 13 ms | 36 ms | 40 ms | 477 req/s |
| `GET /v1/audit/logs?limit=100` | 16 ms | 47 ms | 52 ms | 420 req/s |
| `POST /v1/jobs` (200 serial creates, 202 Accepted) | 1 ms | 3 ms | 20 ms | 498 req/s |

Source: `tests/Vapor.ControlPlane.Tests/Performance/ApiLatencyBenchmarks.cs`.

### Throughput — component level

| Benchmark | Scale | Measured |
|---|---|---|
| Job queue: job creation | 500 jobs | 776 ms (644/s) |
| Job queue: claim + finish loop | 500 tasks | 1463 ms (342/s) |
| Concurrent claimer (4 contenders) | 200 tasks | 198 ms (1010/s), 0 duplicate dispatches |
| EventBroker fan-out | 500 jobs + 100 global subscribers × 100 events | 38 ms all delivered |
| SSE concurrent connections | 50 connections | 723 ms all received events |
| In-memory cache writes (`GetOrSetAsync` fill) | 5000 distinct keys | 73 ms (67,759 writes/s) |
| In-memory cache hot reads (`GetOrSetStaleWhileRevalidateAsync`, all fresh hits) | 4000 reads / 100 keys / 8 workers | timing window <1 ms (>10⁶ reads/s), 0 factory callbacks |

Source: `ControlPlaneBenchmarks.cs` (first 5 items), `tests/Vapor.Steam.Core.Tests/Performance/CacheBenchmarks.cs`
(last 2 items).

### Resource Footprint — managed allocation per operation

| Benchmark | Measured |
|---|---|
| `SqliteJobStore` create + claim + finish full cycle | 68,860 B/operation (200 cycles, 892 ms) |
| `MemoryVaporCache` hot read | 252 B/read |

Source: `tests/Vapor.ControlPlane.Tests/Performance/ResourceFootprintBenchmarks.cs`, `CacheBenchmarks.cs`.

### Capacity Profile — 100 / 1k / 10k (2026-10-11, measured on this machine)

The same workload against 100 / 1,000 / 10,000 existing rows (jobs, audit entries) and same-scale burst events.
**How to read it**:

- The SQLite read path does not degrade with existing volume (indexed queries): `ListJobs(100)` p95 stays ≤21 ms at
  all three scales, audit `QueryAsync` p50 goes 0→0→1 ms; `GetJob` is constantly 0 ms. Seed time grows linearly with
  scale (write amplification is visible in the 10k range), but that's the one-time cost of building the DB, not a
  query-path regression signal. claim + finish ×100 at the first scale (100) is slow due to in-process JIT
  warm-up — the same method gets faster the more it runs, and all three scales are well within budget.
- EventBroker's bounded channel (capacity 256, DropOldest) = the documented at-most-once contract: when bursts
  exceed 256, the subscriber's deterministic delivery floor is min(256, scale) — measured: bursts of 100 / 1,000 /
  10,000 delivered 100 / 256 / 256. When consumption can't keep up with publication, early events are dropped and
  subscribers get the most recent 256 retained by the channel; bursts ≤ 256 are delivered in full.

| Benchmark | 100 | 1,000 | 10,000 |
|---|---|---|---|
| Job seed (full write) | 854 ms | 1050 ms | 13389 ms |
| `ListJobs(100)` p50 / p95 | 1 / 19 ms | 4 / 21 ms | 1 / 15 ms |
| `GetJob` p50 | 0 ms | 0 ms | 0 ms |
| claim + finish ×100 | 1128 ms | 834 ms | 430 ms |
| Audit seed | 98 ms | 204 ms | 4275 ms |
| `QueryAsync(AuditQuery)` p50 / p95 | 0 / 13 ms | 0 / 4 ms | 1 / 7 ms |
| EventBroker burst publish | 17 ms, delivered 100 (= scale) | 19 ms, delivered 256 (floor) | 152 ms, delivered 256 (floor) |

Source: `tests/Vapor.ControlPlane.Tests/Performance/CapacityProfileBenchmarks.cs`
(`./scripts/run-benchmarks.sh -f CapacityProfile`).

## Archive

> **2026-09-15: initial baseline** (measured on this machine; environment at the top of the file). This table
> supersedes the old hand-copied numbers in the performance section of `tests/TESTING.md` (those old numbers came
> from an unrecorded environment and survive only in git history; the queue-creation ~5,200/s and concurrent-claimer
> ~1,760/s there differ from this machine's measurements due to different environments, not as regression signals).
