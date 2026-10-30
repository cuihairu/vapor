简体中文 | English

# 故障排查

面向 Vapor 运维的“症状 → 诊断 → 修复”参考。所有 `curl` 示例在需要认证处均假定
携带 `Authorization: Bearer $VAPOR_ADMIN_API_KEY`。

## 诊断工具箱

| 工具 | 能告诉你什么 |
|------|-------------------|
| `GET /healthz` | 控制平面进程已启动（公开，无需认证） |
| `GET /v1/agents` | 已注册的 Agent、能力、在线/离线 |
| `GET /v1/agents/status` | 实时 agent 会话/心跳状态 |
| `GET /v1/jobs/{id}` | Job 及各 task 的 `status`、`attempt`、`error`、`output` |
| `GET /v1/jobs/{id}/events` | SSE 流：`task.dispatch_failed`、`task.failed` 等 |
| `GET /v1/audit/logs` | 管理动作与 Job 动作的持久化审计轨迹 |
| Agent `:9700/metrics` | Agent 进程指标（也是它的健康检查） |
| 控制平面日志 | 带凭据脱敏的结构化控制台日志 |

## Agent 一直不显示在线

**症状**：`GET /v1/agents` 为空或 agent 一直离线；agent 日志显示不断重连尝试。

1. **密钥不匹配** — agent 日志显示 WebSocket 握手被 401 拒绝。`AGENT_API_KEY`
   必须是控制平面 `Vapor_AGENT_API_KEYS`（逗号分隔列表）中的一项。
2. **URL 不对** — `AGENT_CONTROLPLANE_WS_URL` 必须包含路径
   （`ws://controlplane:8080/v1/agent/ws`）。从 compose 网络之外访问时用
   发布的宿主地址。
3. **控制平面尚未就绪** — agent 服务有
   `depends_on: controlplane: service_healthy`；先检查
   `docker compose ps` 与 `GET /healthz`。

agent 默认无限重连（500ms → 10s 指数退避）。可用 `AGENT_RECONNECT_*`
变量调节；先修根因，而不是抬高重试上限。

## 任务卡在 `queued`

**症状**：`GET /v1/jobs/{id}` 显示 `status: "Queued"`，`attempt` 不再
前进；SSE 流上反复出现 `task.dispatch_failed` 事件。

这意味着当前没有**有能力处理（capable）**的 agent 可供该任务认领：

- 任务所在区域没有在线 agent，**或**
- 在线 agent 的能力都不包含该任务的动作（例如该动作未在任何已注册 agent 上
  存在——动作名拼错字就是这个样子），**或**
- 该任务刚经历派发失败，正在等待重试延迟结束。

历史备注：过去任务在这种情形下会永远停在 queued。现在它们会带上终态——
见下一节。

## 任务以 "no capable agent available" 失败

**症状**：task 达到 `status: "Failed"`，`error` 包含
`dispatch failed after N attempts: no capable agent available`，Job 显示
`failed`。

这就是派发终态按设计生效的表现：任务每次尝试后带延迟重新入队
（`Vapor_TASK_DISPATCH_RETRY_DELAY_MS`，默认 2s），并在
`Vapor_TASK_MAX_DISPATCH_ATTEMPTS`（默认 10；`0` = 无限重试）之后永久失败。

自执行安全分类（第 50 轮）起，实际上限按动作收口：`GuardedWrite` /
`NonIdempotent` 动作——以及任何未分级（`Unknown`）的动作（如第三方插件动作）
——无论配置上限是多少都封顶**共 2 次派发尝试**，因此可能使外部副作用翻倍的
重投递不会在配置上限处空转；`ReadOnly` / `Idempotent` 动作原样使用配置的
上限。一个在 `attempt: 2` 就失败、而你预期它会重试更久的任务，多半撞上了
这个封顶。

调优：

- agent 有较长维护窗口？提高尝试上限或设为 `0`（并依靠任务租约清理真正死掉的
  任务）。对处于保守封顶之下的动作，调高配置上限不起作用——封顶是设计使然，
  不是旋钮。
- 快速失败环境（CI/测试）？调低上限与延迟——E2E 套件用 `3` 次尝试 / `200ms`。

`error` 列与 `task.failed` SSE 事件都会带上原因；REST `GET /v1/jobs/{id}`
按 task 展示。

## 任务卡在 `running`

**症状**：`status: "Running"`，迟迟不完成；agent 中途死亡或失联。

控制平面会让心跳早于 `Vapor_TASK_LEASE_SECONDS`（默认 300s）的运行中任务过期：
任务重新入队，由任何有能力的 agent 接手。如果它确实仍在被网络分区的 agent 上
运行，其完成上报会作为过期尝试被拒绝——预期以重新入队的那次运行为准。

对于长时运行的动作，确保 agent 的心跳间隔远小于租约窗口。

## Job 因凭据/解密错误失败

**症状**：会话登录失败；agent 日志出现 AES/GCM 错误。

存储的凭据用 `VAPOR_ENCRYPTION_KEY`（AES-GCM，≥32 字节）加密。更换密钥会让
先前存储的凭据不可读。恢复原密钥或重新录入凭据。旧 AES-CBC 记录出于迁移目的
仍可读。

## 指标端点不可达

**症状**：Prometheus target 掉线；agent 健康检查失败（healthcheck 查询
`:9700/metrics`）。

- `VAPOR_METRICS_HOST`/`VAPOR_METRICS_PORT` 必须与发布的端口映射一致
  （`${VAPOR_AGENT_METRICS_PORT:-9700}:9700`）。
- 指标服务器在启动之后才绑定；头几秒内健康检查依赖 `start_period: 15s`。

## 控制平面启动失败

1. **数据库路径不可写** — 容器以非 root 运行；`/app/data` 卷必须可被
   `APP_UID` 写入。
2. **SQLite 文件损坏** — 从备份恢复（见 [production.md](production.md)）。
3. **缺少必需环境变量** — 未设置 `Vapor_ADMIN_API_KEY` /
   `Vapor_AGENT_API_KEYS` *不会*阻止进程启动：进程照常启动，但每个 admin 与
   隧道调用都被 401 拒绝（fail closed）。

## 直接从 SQLite 读 Job 状态

REST 不可用时，存储就是一个普通 SQLite 数据库：

```sql
SELECT id, status, attempt, error FROM tasks WHERE job_id = $job;
SELECT status, COUNT(*) FROM tasks GROUP BY status;
```

Schema 变更（新增列）会在控制平面启动时自动应用——旧二进制读取迁移过的数据库
时会忽略新列。
