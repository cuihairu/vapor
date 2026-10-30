简体中文 | English

# Vapor 路线图 —— 分布式系统能力地图

Vapor 的定位是一个 API 驱动的分布式系统：一个持有状态并对外暴露 REST/SSE 面的控制平面，加一支经出站隧道在贴近 Steam 区域端点处执行任务的 Agent 舰队。本地图盘点此类系统所需的能力，标注 Vapor 当前的站位（2026-09-25 普查，所有断言均已对照源码核验），并按优先级对剩余缺口排序。

配套文档：

- `feature-matrix.md` —— 与 ASF 的 Steam 功能对标（平台*做什么*）
- 本文件 —— 分布式系统基础设施（平台*怎么跑*）
- `architecture.md` —— 组件设计与取舍

图例：✅ 扎实 · 🟡 部分 · ❌ 缺失。优先级：**P0** = 价值最高，立即做 · **P1** = 其次 · **P2** = 稍后 / 重新评估。

---

## 1. 服务注册与发现 —— ✅（P2 残余）

**现状。** Agent 经出站隧道自注册（`agent_hello` 携带 id、region、能力清单）；控制平面在内存中维护 `AgentRegistry`，以心跳判定存活，断连即注销。能力兼任路由过滤器（`SupportsAction`），插件清单按 agent 从任务结果镜像。发现机制刻意以 CP 为中心：agent 之间互不发现，CP 端点是 agent 侧的静态配置。

**缺口。** 没有外部注册中心（Consul/etcd/DNS）。只有在控制平面实例多于一个之后，这才会变得相关（见 §9）。

**优先级依据。** 单一控制平面之下，内存注册表就是全部事实；外部注册中心只会新增依赖而不会移除依赖。

## 2. 路由与负载均衡 —— 🟡（P2）

**现状。** 任务派发按区域收拢范围，做能力匹配并选取负载最低的 agent（agent-id 平局打破）；期望状态收敛器在每 agent 容量上限之内选取负载最低且具备能力的 agent（按 agent id 确定性平局打破）；`agent:{id}` 目标直连路由（插件操作、钉住的账户）；agent 失联触发其账户的重新平衡。

**缺口。** 派发选择不感知延迟/健康；没有客户端侧负载均衡；没有一致性哈希。这些当前都不构成约束：账户由收敛器钉在各 agent 上，任务派发是队列而非热路径。

**优先级依据。** 当前规模下足够；若每 agent 队列深度成为调度约束则重新评估。

## 3. 超时、重试、熔断、限流 —— ✅（本轮完成；P1 残余：单 CP 规模下无实质项）

逐层盘点：

| 层 | 机制 | 状态 |
|-------|-----------|--------|
| agent → Steam Web | 重试，差异化处理 429（`Retry-After`，封顶）/ 5xx 退避 | ✅ |
| agent → Steam Web | 熔断器（Closed/Open/HalfOpen，单探针） | ✅ |
| agent → Steam Web | 请求配速（默认 1 req/s，可配置） | ✅ |
| 动作执行 | 每动作声明的 `TimeoutSeconds`（有界、结构化 `action timeout` 结果；树内全部会话 + 宿主动作均已声明，两条路径均强制执行） | ✅ |
| 动作执行 | agent 级任务看门狗（为未设时限的动作、宿主动作、会话恢复路径兜底） | ❌ → 本轮落地 |
| CP → agent 派发 | 派发尝试次数 + 重试延迟 + 租约回收 + 重入队 | ✅ |
| agent → CP 隧道 | 指数退避重连（500 ms → 10 s，可配置，默认不限次数） | ✅ |
| CP → webhook | 有界指数退避重试、10 s HTTP 超时、按 sink 隔离 | ✅ |
| 交易操作 | 每账户滑动窗口配额 + 并发闸门 | ✅ |
| 爬取 | 每次运行硬超时、每批次配速 | ✅ |
| CP 入站 REST | 按 key 限流 | ❌ → 本轮落地 |

**为何这两项是 P0。** 看门狗堵上了一个真实死锁：一个挂起的动作会永久占住 agent 的串行任务循环，而它的心跳又让 CP 租约一直存活——操作员以下的任何层都无法把它救回来。入站限流则是这个 API 优先系统缺失的保护面，而其控制平面在设计上就是唯一入口。

## 4. 认证与密钥管理 —— ✅（本轮完成；残余：无实质项）

**现状。** 按角色的 API key（admin / agent）守卫 REST、SSE 与隧道握手；HMAC-SHA256 签名的 webhook；AES-GCM 凭据存储，主密钥来源对 KMS 友好（`BASE64` / `FILE` / 明文），带轮换 CLI 与 v1→v2 透明迁移；静态脱敏的审计日志；输出级日志脱敏（消息、作用域、异常）；代理凭据端到端脱敏。API key 支持可选的 `@<ISO-8601>` 过期后缀（`Vapor_AGENT_API_KEYS` 逐 key，`Vapor_ADMIN_API_KEY` 同理）：key 严格在其过期时刻之前有效，自该时刻起如同未知 key 一样被拒绝——这把轮换变成一次分阶段切换（新 key 裸值、旧 key 带死线），而非协调好的双重重启——见 `production.md` § Key rotation。

**缺口。** 无实质项。没有 OIDC/mTLS 是设计使然，不是遗漏——见下方非目标：Vapor 是单操作员自托管系统，数据模型中没有租户概念。

**优先级依据。** 单操作员安全模型已然完整，轮换策略也在内；多租户仍明确在范围之外（见 `architecture.md` Non-goals）。

## 5. 协议与序列化 —— ✅（P2）

**现状。** 处处 JSON：REST（System.Text.Json、camelCase、枚举即字符串）、隧道用封闭的四消息 WebSocket 信封、SSE 事件、webhook JSON。协议记录经往返性质测试；插件 API 兼容性按 SemVer 门控；W3C trace context 随隧道透传（见 §6）。

**缺口。** 没有消息 schema 版本协商（信封是封闭的，这本身就是它的保护）；没有 protobuf/gRPC；没有 schema 注册表。

**优先级依据。** 对 API 优先系统而言，JSON + OpenAPI 是正确的默认；二进制协议评估过并暂被否决（见 `architecture.md` 取舍）。

## 6. 分布式追踪与指标 —— ✅（exemplar 本轮落地；残余：尾部采样，属 collector 侧策略）

**现状。**

- *追踪*：两个服务均启用 OpenTelemetry（经 `OTEL_EXPORTER_OTLP_ENDPOINT` 选择加入）。完整派发链跨隧道双向以 span 链接：`task.dispatch`（生产者，CP）→ `task.execute`（消费者，agent，以透传的 `traceparent` 为父）→ `task.result`（消费者，CP，以 agent 回传的 `traceparent` 为父）。ASP.NET Core instrumentation 覆盖 REST 面。
- *指标*：CP `/metrics` 导出业务计数器（按状态的任务、按原因的派发失败、收敛动作、计划结果、爬取结果、通知投递）；agent 经 Monitoring 插件在 `:9700` 暴露 Prometheus（动作、缓存、会话、运行时）；compose observability profile 附带 Grafana 仪表盘。

**缺口 → 本轮落地。** REST 面自身的 RED 指标：`vapor_controlplane_http_requests_total{method,route,status}` 与时长 sum/count——独立于可选 OTel 管线的请求级可见性。

**缺口 → 已落地（第二批）。** Exemplar：每个请求计数器样本都携带环境中的 W3C trace id（`# {trace_id="<32 hex>"} 1`），于是仪表盘上慢/出错的路由可直链到具体 trace；忽略 exemplar 的抓取器看到完全相同的值，两个面保持独立（见 `production.md` Monitoring）。

**残余（P2，有意为之）。** 尾部采样位于服务与后端之间的 OTel Collector——单操作员规模下有意不做成服务侧旋钮（`production.md` Distributed tracing）。

## 7. 配置分发与灰度 —— 🟡（P2）

**现状。** 期望状态 spec 是配置分发的主干：声明式、带版本（乐观并发）、由收敛器循环收敛。插件行为经 manifest + 环境变量覆盖配置；运行时插件安装可按 agent 定向，兼任一个粗粒度灰度杠杆（先装到一个 agent 上）。全局干运行模式预览编排决策而不派发。

**缺口。** 没有百分比发布，没有特性开关。

**优先级依据。** 期望状态模型已经覆盖「改配置、看它收敛」；分数发布的机器只有在舰队大得多时才划算。

## 8. 故障注入与演练 —— ✅（本轮完成；P2 残余：无实质项）

**现状。** 全仓遍布确定性测试缝（`ProbeOverride`、`FetchOverride`、fake Redis、可注入时钟、测试夹具中的 happens-before 闸门）；E2E 演练包括杀 agent 重平衡与派发失败路径，覆盖 stub 与真实进程。运行时故障注入 API 本轮落地：仅管理员可用的 `/v1/faults` 端点（`FaultInjector`）在两个面上布设错误/延迟注入——任务派发（认领之后，搭乘真实的重入队/重试机制）与 `/v1` API 请求（边缘中间件；故障端点、`/healthz` 与 `/metrics` 保持豁免，使演练始终可观测、可叫停）。每个故障经预算（耗尽即自动移除）与 TTL 自愈；紧急停止按钮（`DELETE /v1/faults`）一键解除全部；状态在内存，CP 重启即全部解除——有意为之（比操作员的注意力活得更久，正是这些边界要防止的事）。可经 `vapor_controlplane_fault_injections_total{kind,mode}` / `vapor_controlplane_faults_armed` 与 `faults.enable/disable/clear` 审计动作观测。

**缺口。** 单 CP 规模下无实质项（多实例拓扑会需要按面划定范围与全局解除屏障——随 §9 HA 一并重新评估）。

**优先级依据。** 测试缝纪律让 CI 里可以做确定性故障演练；运行时 API 则在不引入混沌框架的前提下，对真实机制做生产演练。

## 9. 状态同步与一致性 —— 🟡（模型已成文，声明态现已持久；建 HA 属 P2）

**现状。** 控制平面是唯一写者与唯一状态所有者——且其全部状态现在都能在重启后存活：SQLite 直接支撑 jobs、审计与爬取，而账户 spec 与设置（声明的农场）驻留内存的读路径经 `SqliteConfigStore` 写穿透持久化到 SQLite，并在启动时再水化（本轮落地）。`ConfigVersion` 是全量替换式 PUT 存储上的单调收敛触发器（单一写者使 CAS 不再必要）；任务认领基于租约，投递 at-least-once、上报按 attempt 围栏、每任务恰一条终态记录；agent 会话状态最终同步进 CP 的 `SessionTracker`；缓存为 stale-while-revalidate 加 single-flight 去重，启用 Redis 时跨实例。完整模型——保证、不保证、失败/重启语义——已成文于 [`consistency.md`](consistency.md)（本轮落地，§3 的每动作超时行在同一轮补齐）。

**缺口。** 单实例控制平面：无 HA、无水平写扩展，重启 = 短暂编排暂停（agent 经 token 恢复保住会话，声明的农场从 SQLite 再水化——无需手工重新声明）。

**优先级依据。** 单 CP 是一项明确成文的取舍（见 `architecture.md`）：换来事务性一致性与零协调开销。HA（外置 DB 或 leader election）是本地图上最大的未来项——刻意排在 P0 加固之后。

---

## 本轮落地（P0）

1. 树内每个动作现已声明 `TimeoutSeconds`，agent 增加了任务级看门狗（§3）：`AGENT_TASK_TIMEOUT_SECONDS`，默认 900 s，`<= 0` 禁用——它取消挂起的任务、向控制平面上报结构化失败，并让循环继续服务下一个任务。
2. 控制平面在 `/metrics` 为每个 REST 请求记录 RED 指标，并支持按 key 滑动窗口限流（§6 + §3）：`Vapor_API_RATE_LIMIT_PER_MINUTE`，默认关闭，超限返回 `429` 附 `Retry-After` 与拒绝计数器。
3. 投递与一致性保证——attempt 围栏下的 at-least-once、租约回收、收敛语义、失败/重启行为——已写入 [`consistency.md`](consistency.md)（§9）。
4. 每个已配置的 API key 都接受可选的 `@<ISO-8601>` 过期后缀（§4）：过期 key 与未知 key 同样以 401 失败关闭，admin REST/SSE 与 agent 隧道握手一视同仁——这让单操作员 key 轮换有了死线，而非一次协调好的重启。
5. 仅管理员可用的 `/v1/faults` 端点在任务派发与 API 请求两个面布设有界错误/延迟演练（§8）：预算 + TTL 自愈、紧急停止按钮、豁免的控制面、Prometheus 计数器与审计轨迹。
6. 每个 `vapor_controlplane_http_requests_total` 样本都以 Prometheus 文本格式 exemplar 携带环境中的 W3C trace id（§6），因此无论演练错误还是自然错误，都离具体 trace 只有一次点击；尾部采样已成文为 collector 侧策略。
7. 账户 spec 与设置写穿透持久化到 SQLite（`SqliteConfigStore`、`Vapor_CONFIG_DB_PATH`）并在启动时再水化（§9）：控制平面最后一块未持久化状态就此消失，重启不再抹掉农场声明，consistency.md 中设置「CP 重启后存活」的断言从愿景变成了代码事实。

## 明确非目标（目前）

- **任何形式的多租户**（按租户认证、配额、命名空间、OIDC/RBAC）——设计上就在范围之外，不是推迟：Vapor 是单操作员自托管系统，其威胁模型里恰好只有一名管理员。见 `architecture.md` Non-goals。
- gRPC 隧道迁移、外部消息队列骨干（NATS/Kafka）与多实例控制平面——待 P0 项浸泡一段时间后再重新评估。
- 服务间的 mesh 式 mTLS——在单一控制平面、key 认证的 agent 走仅出站隧道的形态下并无必要。
