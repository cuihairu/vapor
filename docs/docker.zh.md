简体中文 | [English](../docker/)

# Docker 与 Compose

## 镜像

| 镜像 | Dockerfile | 说明 |
|-------|------------|-------|
| `vapor/controlplane` | `src/Vapor.ControlPlane/Dockerfile` | REST + SSE + WebSocket，SQLite 位于 `/app/data` |
| `vapor/agent` | `src/Vapor.Agent/Dockerfile` | 随镜像预装 Monitoring 与 GameAccess 插件，位于 `/app/plugins` |

两个镜像均为多阶段构建，基于 `mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`，以非 root 用户（`APP_UID`）运行，并以仓库根目录为构建上下文：

```bash
docker build -f src/Vapor.ControlPlane/Dockerfile -t vapor/controlplane:local .
docker build -f src/Vapor.Agent/Dockerfile -t vapor/agent:local .
```

## Compose

`docker-compose.yml` 定义了 `controlplane` 与 `agent`，外加一个带 Prometheus
与 Grafana 的可选 `observability` profile：

```bash
# Control plane + agent only
docker compose up -d

# With metrics stack (Prometheus at :9090, Grafana at :3000)
docker compose --profile observability up -d
```

Grafana 实例会自动预配 Prometheus 数据源与随附的
**Vapor Overview** 仪表盘（`deploy/grafana/dashboards/vapor-overview.json`），
覆盖动作吞吐/失败/耗时、会话状态、缓存命中率、
GC/线程池与内存。

### 环境变量 / 密钥

在本地开发之外运行前，先设置这些变量（通过 shell 环境或 `.env` 文件）：

| 变量 | 使用方 | Compose 中的默认值 |
|----------|---------|--------------------|
| `VAPOR_ADMIN_API_KEY` | control plane | `admin-dev-token` |
| `VAPOR_AGENT_API_KEY` / `VAPOR_AGENT_API_KEYS` | agent / control plane | `agent-dev-token` |
| `VAPOR_AGENT_ID`, `VAPOR_AGENT_REGION` | agent | `agent-1`, `eu-west` |
| `VAPOR_CONTROLPLANE_PORT` | 宿主端口映射 | `8080` |
| `VAPOR_AGENT_METRICS_PORT` | 宿主端口映射 | `9700` |
| `VAPOR_VIEWER_API_KEY` | control plane | — （禁用） |

`VAPOR_VIEWER_API_KEY` 是可选的**只读控制台密钥**：在安全方法
（GET/HEAD/OPTIONS）上，它对所有读端点的认证效果与管理密钥完全一致，
而任何写操作——以及 agent 隧道——仍会以 `401` 拒绝它。请让它与管理密钥
保持不同（取值相同即视为禁用）；它面向共享/演示的控制台访问（见 README
的演示站点一节）。

两个控制平面密钥变量都支持按 key 附加可选的 `@<ISO-8601>` 过期后缀
（例如 `old-key@2026-12-31T23:59:59Z`），用于计划轮换——
见 [production.md](production.md) § Key rotation。

卷在重启之间保留状态：`controlplane-data`（SQLite 数据库）与
`agent-data`（加密凭据存储，位于 `/app/.vapor`）。

## 指标端点

Monitoring 插件在 agent 上暴露 Prometheus 文本格式（版本 0.0.4）。配置优先级：环境变量 → `plugin.json`
的 `configuration` → 内置默认值。

| 环境变量 | plugin.json 键 | 默认值 |
|-----|-----------------|---------|
| `VAPOR_METRICS_HOST` | `metrics.host` | `127.0.0.1` |
| `VAPOR_METRICS_PORT` | `metrics.port` | `9700` |
| `VAPOR_METRICS_PATH` | `metrics.path` | `/metrics` |

导出的指标族：

| 指标 | 类型 | 标签 | 说明 |
|--------|------|--------|-------------|
| `vapor_action_executions_total` | counter | `action`, `status` | 按结果统计的动作执行次数 |
| `vapor_action_duration_seconds_sum` | counter | `action`, `status` | 累计执行秒数 |
| `vapor_actions_registered` | gauge | – | 当前已注册的动作数 |
| `vapor_sessions_active` | gauge | – | 被跟踪的 bot 会话数 |
| `vapor_sessions_by_state` | gauge | `state` | 按状态分组的会话数 |
| `vapor_session_events_total` | counter | `type` | 会话生命周期事件 |
| `vapor_cache_hits_total` / `vapor_cache_misses_total` | counter | – | 缓存统计 |
| `vapor_cache_stale_hits_total` | counter | – | 进程启动以来的 stale-while-revalidate 交付次数 |
| `vapor_cache_entries` | gauge | – | 存活缓存条目数 |
| `process_uptime_seconds`, `process_working_set_bytes` | gauge | – | 进程基础指标 |
| `dotnet_gc_heap_size_bytes`, `dotnet_gc_collections_total` | gauge/counter | `generation` | GC 状态 |
| `dotnet_threadpool_threads`, `dotnet_threadpool_pending_work_items` | gauge | – | 线程池 |

绑定失败（端口被占用、地址不可用）会记录日志且不致命：
插件继续工作，只有 HTTP 端点被禁用。

## 独立 Prometheus

```yaml
scrape_configs:
  - job_name: vapor-agent
    static_configs:
      - targets: ["agent-host:9700"]
```
