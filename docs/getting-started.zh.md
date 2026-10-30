简体中文 | English

# 上手指南

本指南带你从一台空机器到挂机中的 Steam 账号，共十步：启动堆栈、声明账号、登录（密码或二维码）、处理 Steam Guard 挑战、运行首批任务，然后把账号交给编排器做卡牌挂机或时长提升。命令使用 compose 开发默认值；每一步都链接到参考文档的生产级路径。

## 0. 前置条件

- Docker + Docker Compose（推荐），**或**本地运行的 .NET 10 SDK
- `curl`（所有示例）——或者直接用管理界面；每一步都有等价的 UI 操作
- 一个要驱动的 Steam 账号（学习阶段用一个备用小号是明智的选择）

## 1. 启动堆栈

```bash
git clone https://github.com/cuihairu/vapor.git
cd vapor
docker compose up -d
```

这会启动两个容器：`controlplane`（REST API 在 `:8080`，SQLite 在命名卷中）和 `agent`（WebSocket 客户端，加密凭据存储在独立卷中，预装 Monitoring 插件）。加 `--profile observability` 可同时启动 Prometheus（`:9090`）与 Grafana（`:3000`）。

验证：

```bash
curl -s http://127.0.0.1:8080/healthz
curl -s http://127.0.0.1:8080/v1/agents/status -H "Authorization: Bearer admin-dev-token"
```

Agent **主动向外**连接 `ws://controlplane:8080/v1/agent/ws` 并用 agent API key 认证——控制平面从不回拨 Agent，所以 Agent 可以在任意区域躲在 NAT 后面。变量表见 [docker.md](docker.md)，真实部署拓扑（TLS、密钥管理、备份）见 [production.md](production.md)。

更偏好本地进程？按 [running.md](running.md) 操作（`Vapor_ADMIN_API_KEY` / `Vapor_AGENT_API_KEYS` / `VAPOR_ENCRYPTION_KEY` 等变量）。

## 2. 打开控制台

| URL | 说明 |
|-----|------------|
| `http://127.0.0.1:8080/admin.html` | 完整管理台：账号、会话、任务、交易/市场/认领操作、认证挑战、插件商店、配置 |
| `http://127.0.0.1:8080/dashboard.html` | 只读优先面板：统计、Agent、会话、任务、审计——双 SSE 流，外加一个白名单内的按账号调和动作 |
| `http://127.0.0.1:8080/gamedata.html` | 游戏数据字典（走缓存的查询） |

下文所有 API 示例在 compose 开发密钥（`admin-dev-token`）下原样可用。

## 3. 声明账号

账号在控制平面上是*声明式*的——只有身份信息（名称、期望状态、放置位置）。**这里绝不存凭据**；凭据保存在 Agent 的 AES-GCM 加密存储中，或随登录任务送达。

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/acct-1 \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"enabled":true,"desiredState":"Online","region":"eu-west","agentId":"agent-1","updatedBy":"me"}'
```

`desiredState` 就是整个编排契约：

| 状态 | 含义 |
|-------|---------|
| `Offline` | 无会话；释放 Agent 分配 |
| `Online` | 存活且已登录的会话 |
| `Idle` | 已登录并挂 `idleApps`（白名单） |
| `Farm` | 智能卡牌挂机——挂仍有卡牌掉落的游戏，掉完自动轮换；`idleApps` 变为*排除*名单 |
| `Boost` | 时长提升——挂应用直到 `boostTargets` 中每个小时数目标达成 |

调和器持续把现实收敛到这份声明（见 [architecture.md](architecture.md)）；你通过修改声明改变行为，而不是按按钮。

## 4. 登录

### 方式 A：密码随任务下发

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"login","region":"eu-west","targets":["acct-1"],"payload":{"password":"<steam-password>"}}'
```

### 方式 B：二维码（完全不碰密码）

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"login","region":"eu-west","targets":["acct-1"],"payload":{"qrLogin":true}}'
```

挑战链接会以会话事件出现（在管理台会话面板里也是可点击按钮）；用 Steam 手机 App 扫码即可。二维码流程 3 分钟超时。令牌持久化在 Agent 的加密存储中，重启后无需重新登录即可恢复会话。

### Steam Guard 挑战

如果账号有保护，会话会停在挑战状态，控制平面发布挑战事件。随时随地提交一次验证码即可：

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/auth/challenges/acct-1/code \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"code":"12345","type":"email"}'   # 或 "type":"totp"
```

通过 SSE 流实时关注挑战：

```bash
curl -N http://127.0.0.1:8080/v1/auth/challenges/events -H "Authorization: Bearer admin-dev-token"
```

加载 **MobileAuthenticator** 插件（在 Agent 上导入其 maFile）后，TOTP 验证码会自动应答——验证码本身从不跨越网络：控制平面只知道一个布尔值。SDA/steamguard-cli 的老用户可以在 Agent 宿主机上导入 `.maFile`（`dotnet Vapor.Agent.dll import-mafile <file>`）；见 [session-engine.md](session-engine.md)。

## 5. 运行首批任务

任务是通用货币：一个动作名、一个 JSON 载荷、一组目标账号。

```bash
# 会话还活着吗？
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"ping","region":"eu-west","targets":["acct-1"]}'

# 还剩多少卡牌掉落？
curl -sS -X POST http://127.0.0.1:8080/v1/jobs \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"action":"get_card_drops","region":"eu-west","targets":["acct-1"]}'

# 读回结果
curl -sS http://127.0.0.1:8080/v1/jobs/<jobId> -H "Authorization: Bearer admin-dev-token"

# 或实时围观（SSE）
curl -N http://127.0.0.1:8080/v1/jobs/<jobId>/events -H "Authorization: Bearer admin-dev-token"
```

完整动作词汇表——挂机、交易、市场、成就、认领——见[动作目录](actions.md)；包裹高频动作的 REST 快捷方式在 [API 参考](api.md)。

## 6. 开启卡牌挂机

这是收获的一步——等价于 ASF 的"smart farming"，由期望状态驱动：

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/acct-1 \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{
        "enabled":true,
        "desiredState":"Farm",
        "idleApps":["730"],               // 排除名单：永不挂这些
        "region":"eu-west","agentId":"agent-1",
        "farmPolicy":{"perGameHourBudget":4,"priorityOrder":"CardsDescending","priorityApps":[251570]},
        "updatedBy":"me"
      }'
```

编排器随后运转循环：轮询徽章页 → 挂仍有掉落的游戏 → 掉完轮换 → 遵守单游戏时长预算与优先顺序 → 没有剩余即停。围观：

```bash
curl -sS http://127.0.0.1:8080/v1/orchestration/farm -H "Authorization: Bearer admin-dev-token"
```

改声明（新排除项、新预算）只重置受影响的记账——不需要重新部署、不需要重启。

## 7. 提升时长（可选）

```bash
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/acct-1 \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"desiredState":"Boost","boostTargets":[{"appId":570,"targetHours":100}],"updatedBy":"me"}'
```

时长以个人资料游戏页签的口径计量，所以在 Vapor 之外获得的小时数也计入目标。全部目标达成后账号回落到 `Idle`。

## 8. 归集物品到仓库号

卡牌掉落与其他可交易物品可以从所有挂机号统一扫进一个仓库号。先声明仓库（它自己的 SteamID64 就是 loot 报价的收件地址），再跑一次归集：

```bash
# 仓库号：role=storage + 它自己的 SteamID64（不是凭据）
curl -sS -X PUT http://127.0.0.1:8080/v1/accounts/warehouse \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"enabled":true,"desiredState":"Offline","role":"storage","steamId":"76561197960265728","updatedBy":"me"}'

# 一个任务，每个启用中的挂机号一个 loot 子任务，mobile 确认自动续派
curl -sS -X POST http://127.0.0.1:8080/v1/orchestration/storage/collect \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"storage":"warehouse","message":"storage sweep"}'
```

整个运行是一个任务，所以 `GET /v1/jobs/<jobId>/events` 实时流出进度。Steam 要求确认的报价会在发送侧自动确认；超出等待窗口的运行以 `202` 附任务 id 应答，未完成的部分在下次归集时直接重新 loot（未接受的报价会过期，物品原地不动）。`GET /v1/orchestration/storage/snapshot` 显示每个源账号的上次归集。管理台的*仓库归集*面板做同样的事。

## 9. 观察运行

- 指标：Agent 的 Prometheus 端点（compose 默认宿主 `:9700`）——动作计数/耗时、会话状态、缓存统计；Grafana 自动 provisioning *Vapor Overview* 面板。控制平面暴露 `/metrics`，带编排层仪表。
- 审计：每次配置变更、任务结果、验证码提交与调度决策都持久化且可查询：`GET /v1/audit/logs`（存储前脱敏）。
- 追踪：设置 `OTEL_EXPORTER_OTLP_ENDPOINT` 导出追踪；W3C `traceparent` 流经 Agent 隧道，任务可端到端追踪。

## 10. 用插件扩展能力

Agent 镜像预装 Monitoring。插件 ALC 隔离、信任校验、权限收窄、支持热加载。运行时从目录或直链 URL+校验和安装：

```bash
curl -sS -X POST http://127.0.0.1:8080/v1/plugins/install \
  -H "Authorization: Bearer admin-dev-token" -H "Content-Type: application/json" \
  -d '{"pluginId":"vapor.marketwatch"}'      # 从配置的目录索引解析
```

卸载是对称的（`POST /v1/plugins/uninstall/{pluginId}`），`GET /v1/plugins/installed` 显示每个 Agent 的状态——管理台的 *PluginStore* 面板用按钮完成所有这些。打包与信任模型见 [plugins.md](plugins.md)。

## 11. 下一步去哪

- 真实使用前的加固：[production.md](production.md)——TLS、`VAPOR_ENCRYPTION_KEY` 管理、按角色 API Key、备份、升级、回滚。
- 出问题了？[troubleshooting.md](troubleshooting.md)——症状 → 诊断 → 修复清单。
- 写插件：[plugins.md](plugins.md)。
- Vapor 与 ASF 的能与不能：[feature-matrix.md](feature-matrix.md)。

## 从第一天就该知道的底线

1. **凭据与 Steam Guard 验证码永不离开 Agent。**密码要么随任务下发，要么进 Agent 的加密存储；验证码在本地消费，只上报一个布尔值。
2. **破坏性功能双重门控。**市场挂单创建/撤单需要显式关闭 `dry_run`，*加上*账号级开关（`marketListingsEnabled`），*再加上* Agent 侧的 `AGENT_MARKET_LISTINGS_ENABLED`。
3. **每次写入都有审计。**假定你的操作都会被记录——这是特性。
4. **API 给的是答案，不是愿望。**任务按目标上报成功/失败与结构化输出；读结果，别臆测。
