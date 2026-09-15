# 性能基线（performance baseline）

> 权威源。本文件是仓库唯一的性能基线数字记录处；基准测试内的断言只做功能正确性
> + 宽数量级上限校验（防 CI 抖动 flake），**打印出来的数字才是基线**。刷新基线时
> 把当前表整体移入文末归档区，再以新环境重跑转录（归档范式同 `tests/TESTING.md`
> 覆盖率基线）。

## 测量环境（2026-09-15 实采）

| 项 | 值 |
|---|---|
| CPU | Intel i9-10880H，8 核 |
| 内存 | 30 GiB |
| OS | Ubuntu 26.04（Linux 7.0.0-31-generic） |
| .NET | SDK 10.0.112，runtime 10.0.12 |
| 构建配置 | Release，`RunConfiguration.MaxCpuCount=2` |

数字与测量机器强相关：换机器或换运行时后重跑刷新，不做跨环境横向比较。

## 运行方法

```bash
./scripts/run-benchmarks.sh                # 两个基准项目全套
./scripts/run-benchmarks.sh -f <filter>    # 追加 --filter
```

或手动（`--logger` 必须用 detailed，否则 xunit 不显示基准打印的数字）：

```bash
dotnet test tests/Vapor.ControlPlane.Tests -c Release \
  --logger "console;verbosity=detailed" \
  --filter "FullyQualifiedName~Performance" -- RunConfiguration.MaxCpuCount=2
dotnet test tests/Vapor.Steam.Core.Tests -c Release \
  --logger "console;verbosity=detailed" \
  --filter "FullyQualifiedName~Performance" -- RunConfiguration.MaxCpuCount=2
```

基准不挂 CI（延续"本地开发机实测为准"口径，CI 只保证测试通过不保证数字）。

## 口径说明

- **时延基准**走 `WebApplicationFactory` 的 in-memory handler：覆盖框架管道 +
  序列化 + 存储成本，**不含真实网络栈**——记录的是被测代码的回归基线，不是端到端
  网络时延。
- 时延宿主用真实 `:memory:` 存储并移除全部托管后台服务（`RemoveAll<IHostedService>`），
  避免后台轮询混入测量窗口；种子数据：50 账号 / 200 作业 / 100 审计条目 /
  5 采集计划 × 40 结果。
- **分配量**用 `GC.GetTotalAllocatedBytes(precise: false)` 进程级差值 ÷ 操作数
  （异步代码在线程池线程间迁移，per-thread 计数器不适用）；绝对值含测试管道噪声，
  用途是盯数量级回归，不抠百分比。
- 并发正确性基准（`Vapor.Steam.Core.Tests/Performance/ConcurrencyTests.cs`）只断言
  行为正确，不打印吞吐数字，不在此记录。

## 基线（2026-09-15，本机实测）

### 时延 — 只读端点（8 并发 × 200 请求）+ 任务派发写入口对照

| 端点 | p50 | p95 | max | 吞吐 |
|---|---|---|---|---|
| `GET /healthz` | 0 ms | 4 ms | 18 ms | 5445 req/s |
| `GET /v1/accounts` | 1 ms | 8 ms | 24 ms | 2851 req/s |
| `GET /v1/crawl/plans` | 4 ms | 10 ms | 21 ms | 1405 req/s |
| `GET /v1/crawl/results?limit=100` | 13 ms | 31 ms | 40 ms | 501 req/s |
| `GET /v1/jobs?limit=100` | 13 ms | 36 ms | 40 ms | 477 req/s |
| `GET /v1/audit/logs?limit=100` | 16 ms | 47 ms | 52 ms | 420 req/s |
| `POST /v1/jobs`（串行 200 次创建，202 Accepted） | 1 ms | 3 ms | 20 ms | 498 req/s |

来源：`tests/Vapor.ControlPlane.Tests/Performance/ApiLatencyBenchmarks.cs`。

### 吞吐 — 组件级

| 基准 | 规模 | 实测 |
|---|---|---|
| 任务队列：job 创建 | 500 jobs | 776 ms（644/s） |
| 任务队列：claim + finish 循环 | 500 tasks | 1463 ms（342/s） |
| 并发 claimer（4 竞争者） | 200 tasks | 198 ms（1010/s），0 重复派发 |
| EventBroker 扇出 | 500 job + 100 全局订阅者 × 100 事件 | 38 ms 全量送达 |
| SSE 并发连接 | 50 连接 | 723 ms 全部收到事件 |
| 内存缓存写入（`GetOrSetAsync` 填充） | 5000 distinct keys | 73 ms（67,759 写/s） |
| 内存缓存热读（`GetOrSetStaleWhileRevalidateAsync`，全 fresh 命中） | 4000 读 / 100 keys / 8 worker | 计时窗口 <1 ms（>10⁶ 读/s），0 次 factory 回调 |

来源：`ControlPlaneBenchmarks.cs`（前 5 项）、`tests/Vapor.Steam.Core.Tests/Performance/CacheBenchmarks.cs`（后 2 项）。

### 资源占用 — 每操作托管分配量

| 基准 | 实测 |
|---|---|
| `SqliteJobStore` create + claim + finish 全周期 | 68,860 B/操作（200 cycles，892 ms） |
| `MemoryVaporCache` 热读 | 252 B/读 |

来源：`tests/Vapor.ControlPlane.Tests/Performance/ResourceFootprintBenchmarks.cs`、`CacheBenchmarks.cs`。

## 归档

> **2026-09-15：初始基线**（本机实测，环境见文首）。本表取代 `tests/TESTING.md`
> 性能章节内的旧手抄数字（旧数字出处环境未记录，仅存于 git 历史；其中队列创建
> ~5,200/s 与并发 claimer ~1,760/s 与本机实测差异属环境不同，不构成回归信号）。
