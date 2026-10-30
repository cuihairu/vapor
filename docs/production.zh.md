简体中文 | English

# 生产部署

本指南讲的是用 Docker Compose 把 Vapor 部署到类生产环境。本地开发见 [running.md](running.md)；镜像细节见 [docker.md](docker.md)。

## 拓扑

```
            ┌────────────────────┐        WebSocket /v1/agent/ws
 clients ──▶│   Control plane    │◀──────────────────────────────┐
  (REST/SSE)│  REST + SSE + DB   │                               │
            │  SQLite (/app/data)│                    ┌──────────┴───┐
            └────────────────────┘                    │    Agent     │──▶ Steam network
                      ▲    ▲                          │ sessions,    │
                      │    └── Prometheus (:9090)      │ actions,     │
                      └──────── Grafana   (:3000)      │ plugins :9700│
                                                       └──────────────┘
```

- 控制平面：无状态 HTTP 服务；全部状态放在 `/app/data` 挂载的两个 SQLite 数据库（任务/审计）里。
- Agent：有状态 worker，持有 Steam 会话与本机凭据，存放在 `/app/.vapor`。它主动向外连接控制平面——除指标端点（`:9700`）外不需要任何入站端口。
- 可观测性（可选 profile）：Prometheus 抓取两个服务；Grafana 自带预配好的仪表盘。

## 配置

### 控制平面

| 变量 | 是否必填 | 默认值 | 说明 |
|----------|----------|---------|-------|
| `Vapor_ADMIN_API_KEY` | 是 | — | 管理 REST/SSE 端点的 Bearer 密钥；可选带 `@<ISO-8601>` 过期后缀（见 § 密钥） |
| `Vapor_AGENT_API_KEYS` | 是 | — | Agent 隧道接受的密钥，逗号分隔；每个密钥都可带 `@<ISO-8601>` 过期后缀 |
| `Vapor_VIEWER_API_KEY` | 否 | — | 只读控制台密钥：在 GET/HEAD/OPTIONS 上与管理密钥一样被接受；任何写操作（以及 Agent 隧道）仍返回 `401`。必须与管理密钥不同；不支持过期后缀——轮换靠重新部署 |
| `Vapor_DB_PATH` | 否 | `data/controlplane.db` | 主存储；compose 中挂载为卷 |
| `Vapor_AUDIT_DB_PATH` | 否 | (derived) | 审计日志存储 |
| `Vapor_TASK_LEASE_SECONDS` | 否 | `300` | 运行中任务的心跳停这么久即被重新入队 |
| `Vapor_TASK_MAX_DISPATCH_ATTEMPTS` | 否 | `10` | 无法派发任务的永久失败阈值；`0` 表示无限重试 |
| `Vapor_TASK_DISPATCH_RETRY_DELAY_MS` | 否 | `2000` | 未派发任务可被再次认领前的退避延迟 |
| `Vapor_RECONCILE_INTERVAL_SECONDS` | 否 | `15` | 账号编排器的收敛节奏；`<= 0` 关闭编排 |
| `Vapor_RECONCILE_MAX_ACCOUNTS_PER_AGENT` | 否 | `25` | 编排器给 Agent 分配账号时的容量上限 |
| `Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS` | 否 | `3` | 账号被限速前的连续登录失败次数（更新其 spec 可重置） |
| `Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS` | 否 | `60` | 指数重试冷却的基数（`base × 2^(n-1)`，封顶 15 分钟）；`0` 关闭 |
| `Vapor_RECONCILE_SESSION_STALENESS_SECONDS` | 否 | `120` | 比这更旧的会话快照在编排器眼中算过期 |
| `Vapor_RECONCILE_FARM_REFRESH_SECONDS` | 否 | `300` | `farm` 账号多久重新查询一次剩余卡片掉落并重建挂机队列；每账号的挂机策略（单游戏预算、队列顺序、优先应用）走 accounts API 写进账号 spec，不是环境变量 |
| `Vapor_RECONCILE_BOOST_REFRESH_SECONDS` | 否 | `1800` | `boost` 账号多久重新查询一次总游戏时长并重建未达标排期 |
| `Vapor_RECONCILE_TRADE_REFRESH_SECONDS` | 否 | `600` | 启用了自动接受交易策略的在线账号多久重新查询一次进行中的交易报价 |
| `Vapor_RECONCILE_STANDING_REFRESH_SECONDS` | 否 | `21600` | 编排器多久跑一次异常状态检查（`check_account_standing`）；结果是封禁就把该账号从交易派发中隔离，直到后续一次干净结果解除 |
| `Vapor_RECONCILE_DRY_RUN` | 否 | off | 只上报编排偏差（审计 + 指标），不派发任务 |
| `Vapor_WEBHOOK_NOTIFICATIONS_URL` | 否 | off | 以 JSON 接收任务/会话/认证事件的 webhook 端点；留空即关闭通知 |
| `Vapor_WEBHOOK_NOTIFICATIONS_SECRET` | 否 | — | HMAC-SHA256 密钥；设置后请求带上 `X-Vapor-Timestamp` 与 `X-Vapor-Signature: sha256=<hex>`，签名对象是 `"{timestamp}.{body}"` |
| `Vapor_WEBHOOK_NOTIFICATIONS_EVENTS` | 否 | all | 逗号分隔的事件类型白名单（例如 `task.completed,auth.challenge`） |
| `Vapor_WEBHOOK_NOTIFICATIONS_MAX_RETRIES` | 否 | `3` | 单个事件在计为失败并丢弃前的投递尝试次数 |
| `Vapor_WEBHOOK_NOTIFICATIONS_RETRY_BASE_DELAY_MS` | 否 | `500` | 重试退避基数（`base × 2^attempt`） |
| `Vapor_WEBHOOK_DB_PATH` | 否 | `data/webhook.db` | webhook 投递日志的 SQLite 文件（每次尝试一行；经 `GET /v1/notifications/deliveries` 按最新优先查询）。记录是尽力而为——日志损坏或写满都不会挡住投递；未投递的通知在重启后丢失（它是审计轨迹，不是重放队列） |
| `Vapor_PLUGIN_INDEX_URL` | 否 | off | PluginStore 索引源（JSON：`{plugins:[{id,name,version,apiVersion,url,sha256,...}]}`）；off 表示目录端点上报 `configured: false`。只应指向你自己掌控的索引 |
| `Vapor_API_RATE_LIMIT_PER_MINUTE` | 否 | `0` | `/v1` 各路由按 key 的滑动窗口限流（0 = 关闭）；被拒请求返回 429 与 Retry-After（见 api.md） |
| `Vapor_CRAWL_DB_PATH` | 否 | `data/crawl.db` | 采集计划与逐应用采集结果的 SQLite 文件 |
| `Vapor_CONFIG_DB_PATH` | 否 | `data/config.db` | 已声明挂机账号的 SQLite 文件：账号 spec 与设置写入即落盘，启动时再水合回来，于是控制平面重启不再需要重新声明账号（`:memory:` 恢复持久化之前的行为） |
| `VAPOR_SCRIPT_DB_PATH` | 否 | `data/scripts.db` | 操作员脚本库的 SQLite 文件（`ScriptRecord`：id、name、description、language、opaque content、时间戳）；只存身份与元数据——执行语义在 Agent 侧 |
| `Vapor_CRAWL_WORKER_TICK_SECONDS` | 否 | `5` | 采集 worker 的认领/轮询节奏；`<= 0` 关闭采集编排 |
| `Vapor_CRAWL_KEEP_RUNS` | 否 | `10` | 每个计划保留结果的最近运行数（更早的运行在完成时被清理） |
| `Vapor_CRAWL_MAX_APPS_PER_PLAN` | 否 | `500` | 每个采集计划的 app_ids 上限（更大的请求会被拒） |
| `Vapor_CRAWL_MAX_APPS_PER_TASK` | 否 | `200` | 每个已派发分片的应用数上限（收紧计划的 shard_size） |
| `Vapor_CRAWL_RUN_TIMEOUT_SECONDS` | 否 | `1800` | 一次采集运行的硬停止；未完成的分片被取消并记为失败 |
| `Vapor_CRAWL_INTERVAL_MS` | 否 | `500` | 批量分片内实时商店抓取之间的默认节奏 |
| `Vapor_ENABLE_SWAGGER` | 否 | off | 生产环境保持关闭 |
| `VAPOR_ENCRYPTION_KEY` | 推荐 | — | ≥32 字节；用于加密已存凭据（AES-GCM） |
| `VAPOR_ALLOW_INSECURE_DEFAULT_KEY` | 否 | off | 逃生门；生产环境不要开启 |

| `OTEL_EXPORTER_OTLP_ENDPOINT` | 否 | off | OTLP 端点（例如 `http://tempo:4317`）；开启分布式追踪导出 |

### Agent

| 变量 | 是否必填 | 默认值 | 说明 |
|----------|----------|---------|-------|
| `AGENT_ID` | 是 | — | 唯一且稳定的 id（会出现在 `/v1/agents` 里） |
| `AGENT_REGION` | 是 | — | 任务路由所用的区域 |
| `AGENT_CONTROLPLANE_WS_URL` | 是 | — | 例如 `ws://controlplane:8080/v1/agent/ws` |
| `AGENT_API_KEY` | 是 | — | 必须是 `Vapor_AGENT_API_KEYS` 中的一项 |
| `VAPOR_PLUGINS_DIR` | 否 | — | 插件目录；镜像已预装 Monitoring |
| `VAPOR_METRICS_HOST` / `VAPOR_METRICS_PORT` | 否 | `:9700` | Prometheus 端点，同时也是 agent 健康检查 |
| `VAPOR_REDIS` | 否 | off | StackExchange.Redis 连接串（例如 `redis:6379`）；把数据缓存从进程内切到 Redis |
| `AGENT_RECONNECT_INITIAL_DELAY_MS` | 否 | `500` | 重连退避的起点 |
| `AGENT_RECONNECT_MAX_DELAY_MS` | 否 | `10000` | 重连退避的上限 |
| `AGENT_RECONNECT_BACKOFF_FACTOR` | 否 | `2` | 指数因子 |
| `AGENT_RECONNECT_MAX_RETRIES` | 否 | `0` | `0` = 无限重试 |
| `AGENT_TASK_TIMEOUT_SECONDS` | 否 | `900` | 盯单个已派发任务的看门狗（取消该任务、上报结构化的 `task timeout` 失败，循环继续服务）；`<= 0` 关闭。它设在逐动作超时之上，后者先触发 |
| `AGENT_2FA_AUTO_SUBMIT` | 否 | off | `true` = 用已存的共享密钥（Steam TOTP）在本地应答 2FA 挑战；关闭则交给人工 SSE 通道 |
| `AGENT_MARKET_LISTINGS_ENABLED` | 否 | off | `true` = 允许创建真实市场挂单（`create_market_listing` 带 `send=true`）；每账号的开关也必须打开（见下文） |
| `VAPOR_STEAM_INVENTORY_BASE_URL` | 否 | `https://steamcommunity.com` | 覆盖库存读取所用的 Steam Community 主机（mock 支持，41-4）。仅在测试/验收时指向本地 mock，不设置即生产默认值 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | 否 | off | OTLP 端点；开启分布式追踪导出 |

#### 自动应答 2FA（需显式开启）

设了 `AGENT_2FA_AUTO_SUBMIT=true` 后，Agent 会监听 `TwoFactorCodeNeeded` 会话事件。对于手机验证器共享密钥已存在 Agent 凭据库中的账号（经 MobileAuthenticator 插件的 `save_shared_secret` 动作持久化，静态加密），Agent 在本地生成 Steam TOTP 并提交——全程无人参与。共享密钥永不出 Agent。没有存下密钥的账号（例如邮箱 Steam Guard 验证码，本地根本生成不出来）原样落到人工 SSE 挑战通道。同一账号的重复应答受每账号 60 秒冷却限流；Steam 服务器时间在启动时以及此后每小时同步一次，保证 TOTP 窗口始终有效。


#### 创建市场挂单（需显式开启，双重门控）

把物品挂上出售是市场链路里最敏感的一环，因此真实挂单（`POST /v1/accounts/{name}/market/listings` 且 `send=true`）需要两个显式开关，二者默认都是关的：账号 spec 的 `marketListingsEnabled` 标志（控制平面侧，经 `PUT /v1/accounts/{name}` 设置）与 Agent 的 `AGENT_MARKET_LISTINGS_ENABLED=true`。任一开关关闭，端点直接拒绝；绕过端点直接向 Agent 派发，则由 Agent 侧的检查挡下。不带 `send` 的调用是干运行：它只回报计入手续费的定价方案（卖家实得、Steam 手续费、发行商抽成、买家价），不发送任何东西。价格永远来自调用方——没有自动重定价。需要手机确认的挂单通过 `needs_mobile_confirmation` 回报；确认它是既有的市场确认环的职责（`POST /v1/accounts/{name}/confirmations/accept-all` 带 `type=market`）。


#### 缓存后端（Redis）

不设 `VAPOR_REDIS` 时，Agent 把商店数据（游戏信息、价格、市场挂单）缓存在进程内（`MemoryVaporCache`）。设了 `VAPOR_REDIS`，同一套 `IVaporCache` 契约就切到 `RedisVaporCache`，它：

- 在同一区域的多个 Agent 副本之间共享条目（`vapor:cache:<key>` JSON 信封，带绝对的 fresh/stale 过期时间戳），
- 用一把短命的 `SET NX PX` 锁（释放时校验 token）在各实例间去重 stale-while-revalidate 刷新，
- 按前缀用 `SCAN` 删键（`cache_invalidate` 的失效效果完全一样），并且绝不对服务器 `FLUSHDB`。

用 Docker 组网时，加一个 Redis 服务并把 Agent 指过去：

```yaml
services:
  redis:
    image: redis:7-alpine
  agent:
    environment:
      VAPOR_REDIS: "redis:6379"
```

连接以 `abortConnect=false` 建立，所以 Redis 短暂不可达时 Agent 照样能启动；此时缓存操作快速失败（动作退回实时抓取），直到连接恢复。

#### 插件

插件放在 `VAPOR_PLUGINS_DIR`（默认 `<app-base>/plugins`，即应用程序目录而非工作目录），每个插件一个子目录加一份 `plugin.json` 清单；镜像已预装官方的 Monitoring 与 GameAccess 插件。加载、信任门控、权限与清单格式见[插件开发指南](plugins.md)。运维须知：

- 加载失败是被隔离的——坏掉的插件记日志后跳过，Agent 照常启动；原因去启动日志的加载报告里查（API 版本不兼容、信任级别低于宿主下限、清单无效）。
- 宿主策略（`MinimumTrust`、`RequirePermissionsDeclared`）在构造 `PluginManager` 时由代码设定；插件在清单与能力里声明自己的信任级别与权限，未声明的一律剥掉（按最低信任对待）。
- 插件行为通过清单的 `configuration` 段配置，并可用环境变量覆盖，例如 Market Watch 插件读取 `market.check_interval_seconds`、`market.threshold_percent`、`market.country` 与 `market.webhook_url`（环境变量：`VAPOR_MARKETWATCH_INTERVAL_SECONDS`、`VAPOR_MARKETWATCH_THRESHOLD_PERCENT`、`VAPOR_MARKETWATCH_COUNTRY`、`VAPOR_MARKETWATCH_WEBHOOK_URL`）。
- 插件也可以在运行时经 `plugin_install` / `plugin_uninstall` / `plugin_list` 宿主动作安装——校验 SHA-256 后热加载，无需重启（见[插件开发指南](plugins.md)）。
- 控制平面的 PluginStore 从 `Vapor_PLUGIN_INDEX_URL` 读取插件索引（控制平面环境变量；未设置时目录端点上报 `configured: false`）。SHA-256 摘要要你自己钉住，并且只把它指向自己掌控的索引——摘要钉的是完整性，不是来源。

### 步骤

### 镜像

发布标签会把带版本号的镜像推到 GHCR（`ghcr.io/cuihairu/vapor/controlplane:<X.Y.Z>` 与 `.../agent:<X.Y.Z>`；正式发布还会同步 `:latest`）。想直接用它们而不本地构建，就新建一个 `docker-compose.override.yml`：

```yaml
services:
  controlplane:
    image: ghcr.io/cuihairu/vapor/controlplane:1.0.0
    build: null
  agent:
    image: ghcr.io/cuihairu/vapor/agent:1.0.0
    build: null
```

Compose 会自动合并这个 override；`build: null` 关掉本地构建，于是用已发布的镜像。生产环境钉死一个具体版本，回滚时改钉上一个版本即可。

### 密钥

1. 在 `docker-compose.yml` 旁边建一个 `.env`，填入高强度密钥：

   ```bash
   VAPOR_ADMIN_API_KEY=$(openssl rand -hex 32)
   VAPOR_AGENT_API_KEY=$(openssl rand -hex 32)
   VAPOR_ENCRYPTION_KEY=$(openssl rand -hex 32)
   ```

   把它们记进你的密钥管理器——加密密钥是读回已存凭据的前提，丢了就找不回来。

2. 把整栈拉起来：

   ```bash
   docker compose --env-file .env up -d
   # optional metrics stack:
   docker compose --env-file .env --profile observability up -d
   ```

3. 验证：

   ```bash
   curl -fsS localhost:8080/healthz                       # {"ok":true}
   curl -fsS -H "Authorization: Bearer $VAPOR_ADMIN_API_KEY" \
        localhost:8080/v1/agents                          # agent listed as online
   curl -fsS localhost:9700/metrics | head               # agent metrics
   ```

4. 在控制平面前面放一个终结 TLS 的反向代理，并把 `:8080` / `:9700` 限制在可信网络内。所有敏感端点都要求管理密钥；`/healthz` 是有意公开的。

### 密钥轮换

API 密钥支持可选的过期后缀，于是轮换是一次按计划推进的切换，而不是协调双方各重启一次：

```bash
# 1. Generate the new key and stage BOTH keys — the old one with a deadline:
VAPOR_AGENT_API_KEYS="new-$(openssl rand -hex 32),$(printf %s "$OLD_AGENT_KEYS")@$(date -u -d '+14 days' +%Y-%m-%dT%H:%M:%SZ)"

# 2. Restart the control plane (and move agents to the new key at leisure):
docker compose --env-file .env up -d controlplane
```

旧密钥一直可用到过期那一刻，之后开始以与未知密钥相同的 401 失败——对 Agent 是在隧道握手时，对管理密钥是在 REST 上。请在截止前把所有客户端切到新密钥；过了截止时间，在下次方便的重启时把变量里过期的那一项删掉。同样的后缀对 `VAPOR_ADMIN_API_KEY` 也有效。

## Web 控制台

控制平面自带三个静态页面（无构建步骤、无外部资源）。页面本身不做认证就提供——里面不含敏感数据——而每一次数据调用都走带操作员 bearer token 的已认证 `/v1` API：

| 页面 | 作用 | 写操作 |
|------|------|--------|
| `/admin.html`（默认落地页） | 完整运维控制台：账号全生命周期、交易与确认、市场与认领、采集计划、配置 | 有——与 admin REST 面一一对应；不可逆操作需通过显式确认对话框 |
| `/dashboard.html` | 以读为主的机群监控（统计、会话、任务、审计、SSE 实时流） | 一个白名单内的按账号调和动作——写面由契约测试锁死 |
| `/gamedata.html` | 游戏数据字典与采集结果浏览器 | 无——由契约测试锁死 |

管理界面只是 REST API 之上的一层薄客户端：服务端始终是唯一真相源，它的配置面板从不显示密码类设置值——未改动的脱敏输入框会原样提交已存的值。控制台能做的每一次写操作，管理 API 密钥是唯一的防线；请照此对待它（见下方清单）。

## 安全检查清单

- [ ] 随机生成的 `Vapor_ADMIN_API_KEY` / `Vapor_AGENT_API_KEYS`（32 位以上十六进制字符）
- [ ] 分阶段的轮换要在退场的密钥上加 `@<expiry>` 后缀（见 § 密钥轮换）
- [ ] 已设 `VAPOR_ENCRYPTION_KEY`，且未设 `VAPOR_ALLOW_INSECURE_DEFAULT_KEY`
- [ ] 未设 `Vapor_ENABLE_SWAGGER`
- [ ] 控制平面位于 TLS 之后；指标端口对互联网做了防火墙隔离
- [ ] 容器以非 root 运行（两个镜像的默认行为）
- [ ] 按你的合规要求保留审计日志（`Vapor_AUDIT_DB_PATH`）

## 账号安全态势

Steam 会把共用同一出口 IP 的账号关联起来一并标记——整群账号共用一个地址，就等于每个账号都跟每次封禁挂钩。Vapor 的缓解手段是三个需要配合使用的功能；它们都不自动，而且它们都不能让 game-coordinator 事务的自动化变得可以接受（这条边界见 `docs/game-plugins.md`）。

### 每账号出口代理

每个账号都可以钉定自己的出口代理（`http|https|socks5://[user:pass@]host:port`），它作用于该账号的整条出口链路：CM 连接、web API、交易与手机确认流量都从这里出去。有两条性质对机群安全很关键：

- 代理是凭据，不是设置。它以加密形式存放在账号钉定的那个 Agent 的凭据库里；控制平面什么都不存，只记录脱敏形式。用 `POST /v1/accounts/{name}/proxy` 按账号指派（或在管理界面的账号编辑器的代理字段里指派）；该端点会链上一个 `check_proxy` 校验，并回报实时出口 IP、Steam 可达性与时延。
- 钉一次，少改动。每改一次代理都会踢掉该账号的会话并从新出口重新登录——挂机账号上换 IP，正是这个功能要避免的那种关联事件。代理轮换是一个显式且留审计的动作（`account.proxy_set`）；设计上既没有代理池，也没有自动轮换。

### 状态检查与自动隔离

编排器会定期对每个已声明账号跑 `check_account_standing`（`GetPlayerBans`：VAC / 社区 / 交易 / 经济封禁，外加受限标记），节奏由 `Vapor_RECONCILE_STANDING_REFRESH_SECONDS` 决定，默认 6 小时。结果异常的账号会**被隔离，不参与交易派发**，直到后续一次干净检查把它放出来：调和器的自动调度（礼物报价查询 / 自动接受 / 确认环）直接跳过被隔离的账号，显式派发路径同样尊重隔离——手动 loot 拒绝被隔离的来源，仓库归集跳过被隔离的挂机来源并拒绝被隔离的仓库。两者都留了带审计的 `force=true` 逃生门（loot：强制派发，审计详情里写 `forced_quarantine`；collect：越过仓库限制，而被隔离的挂机来源永远跳过——要用 force 单独 loot 才够得到它）。每次隔离状态变化都以 `account.standing_alert` / `account.standing_released` 事件发布并记录（`standing_quarantined`）。从未收敛过的账号没有状态历史：未知按「没问题」处理，所以新声明不会被拦下——第一次周期检查就会在一个刷新周期内补上这个缺口。

### 挂机 / 仓库分离

账号声明一个 `role`：`farm`（默认）或 `storage`。挂机账号承接卡片掉落与游戏时长的活；仓库账号则是 `POST /v1/orchestration/storage/collect` 把可交易物品归集过去的仓库（按发送方做交易限流，报价在发送方一侧确认，进度走任务的 SSE 流）。把可交易库存放在指定的仓库账号上，能限定挂机账号一旦失守的损失范围，这也正是 ASF 运维者把「跑市场的 bot」与「囤货的 bot」分开的做法。角色在账号编辑器里指派（或用 `PUT /v1/accounts/{name}`）；归集与快照端点见 `docs/api.md`，完整走查见 `docs/getting-started.md`。

## 数据、备份与升级

状态存放在两个命名卷里：

| 卷 | 内容 |
|--------|---------|
| `controlplane-data`（`/app/data`） | `controlplane.db`（任务/子任务/Agent）、`audit.db`、`crawl.db`、`config.db`（已声明 spec + 设置）、`scripts.db`（操作员脚本库）、`webhook.db`（webhook 投递日志） |
| `agent-data`（`/app/.vapor`） | 每 Agent 的凭据与会话数据 |

用 SQLite 的在线 API 备份（控制平面照常运行也保持一致）。运行时镜像里没有 `sqlite3` CLI——跑官方 `alpine` 镜像并顺手装上 `sqlite` 包即可。挂载点必须可写——`.backup` 会把输出文件写进同一个卷——而且六个数据库都得进备份集：

```bash
for db in controlplane audit crawl config scripts webhook; do
  docker run --rm -v vapor_controlplane-data:/data alpine \
    sh -c "apk add --no-cache sqlite >/dev/null && sqlite3 /data/$db.db \".backup '/data/backup-$db-$(date +%F).db'\""
done
```

卷名会带上 compose 项目名前缀，默认取检出目录名——文档里那份 clone 产出的就是 `vapor_controlplane-data`。如果你的检出版本在别处，换成自己的前缀（`docker volume ls | grep controlplane-data`）。

Agent 卷要和它服务的挂机账号一起备份——里面是加密凭据与会话令牌；丢了就得给每个账号做一次交互式重新登录（可能还要过 2FA）：

```bash
docker run --rm -v vapor_agent-data:/data:ro -v "$PWD:/out" alpine \
  tar czf /out/agent-data-$(date +%F).tgz -C /data .
```

另一种做法是先停控制平面，再复制卷内容——已停止的 SQLite 数据库做文件级拷贝总是一致的。

仓库还把这套流程做成了一个 CLI：`tools/Vapor.Backup`——一个自包含的控制台应用，适合在挂载了数据卷、又不想拉 `alpine`+`sqlite` 镜像的宿主上使用：

```bash
dotnet run --project tools/Vapor.Backup -- backup  /data/controlplane.db /backups/controlplane-$(date +%F).db
dotnet run --project tools/Vapor.Backup -- verify  /backups/controlplane-$(date +%F).db
dotnet run --project tools/Vapor.Backup -- restore /backups/controlplane-$(date +%F).db /data/controlplane.db --force
```

`backup` 走 SQLite 的在线备份 API（控制平面照常运行也保持一致）；`restore` 先对备份跑 `PRAGMA integrity_check`，损坏的坚决不恢复，而且没有 `--force` 绝不覆盖已存在的文件；`verify` 就是跑一遍检查。退出码：0 成功，1 操作失败，2 用法错误。

### 恢复

1. `docker compose stop controlplane`——绝不要在运行中的实例上恢复。
2. 把备份的 `*.db` 文件复制进 `controlplane-data` 卷，并先删掉它们旁边残留的 `*.db-wal` / `*.db-shm`：那些文件属于正被替换掉的数据库，把陈旧 WAL 重放到已恢复的文件上会损坏数据或让它回退。
3. `docker compose up -d`。Schema 迁移在启动时幂等执行；已声明的挂机账号从 `config.db` 水合回来，任务与 Agent 从 `controlplane.db` 恢复，审计与采集历史各归各的文件，Agent 会自行重连并重新上报会话。

控制平面的四个文件各自独立备份，因此一次恢复可能混了不同时间点的数据。这种时间差是设计上就安全的：`config.db` 存的是*期望*状态，调和器会把会话重新收敛过去——`config.db` 稍旧一点，最坏不过是把操作员已经看过的一次收敛重放一遍；更新一点也只是多出几条声明。

升级：

1. 拉取新镜像（或本地重新构建），并读一遍 `CHANGELOG.md`。
2. `docker compose up -d`——SQLite schema 迁移在首次启动时自动应用；旧库原地加列（不需要 dump/恢复）。
3. 回滚办法是在 override 文件里改钉上一个镜像 tag，再重跑一次 `docker compose up -d`；注意，被新版本迁移过的数据库会保留新增的列，旧版本会安全地忽略它们。

## 横向扩容 Agent

每个 Agent 就是一个 `agent` 服务实例，带唯一的 `AGENT_ID` 与 `AGENT_REGION`。任务会路由到与该任务同区域、且声明了所需动作能力的 Agent 上。要加容量：

```yaml
  agent-2:
    build:
      context: .
      dockerfile: src/Vapor.Agent/Dockerfile
    image: vapor/agent:local
    restart: unless-stopped
    environment:
      AGENT_ID: agent-2
      AGENT_REGION: us-east
      AGENT_CONTROLPLANE_WS_URL: ws://controlplane:8080/v1/agent/ws
      AGENT_API_KEY: "${VAPOR_AGENT_API_KEY}"
      VAPOR_METRICS_HOST: 0.0.0.0
      VAPOR_METRICS_PORT: "9700"
    depends_on:
      controlplane:
        condition: service_healthy
```

扩容时要考虑的几点：

- Agent 彼此独立；某一个掉线只会暂停它的任务，直到控制平面的任务租约（`Vapor_TASK_LEASE_SECONDS`）过期、任务被重新入队。
- 无法派发的任务（没有具备相应能力的 Agent）会带延迟重试，尝试 `Vapor_TASK_MAX_DISPATCH_ATTEMPTS` 次后永久失败——其中 `GuardedWrite` / `NonIdempotent` / 未分级动作无论该设置是多少都封顶 2 次尝试（执行安全分级，round 50）——怎么调参见 [troubleshooting.md](troubleshooting.md)。

## 监控

启用可观测性 profile 后：

- Prometheus UI：`http://<host>:9090`
- Grafana：`http://<host>:3000`（默认允许匿名查看——对外暴露前先限制访问或配置认证）
- Agent 指标：`http://<agent>:9700/metrics`

随附的告警规则（`deploy/prometheus/alerts.yml`，由 compose 的 Prometheus 自动加载）覆盖两个服务：

- Agent：`VaporAgentDown`（critical）、`VaporAgentTargetMissing`（critical）、`VaporAgentScrapeSlow`（warning）。
- 控制平面：`VaporControlPlaneDown`（critical）、`VaporTasksQueuedBacklog`（任务排队 >15m，warning）、`VaporTasksStuckRunning`（任务运行 >30m，warning）。

把这些告警经你的 Alertmanager 路由到你惯用的呼叫通道。

控制平面在 `/metrics` 暴露 Prometheus 指标（和 Agent 的端点一样公开——靠网络层防护）：`vapor_controlplane_tasks_by_status{status="..."}`、`vapor_controlplane_agents_connected`、派发失败计数器 `vapor_controlplane_dispatch_failures_total{reason="no_capable_agent"|"enqueue_failed"|"attempts_exhausted"}`，以及故障注入指标族 `vapor_controlplane_fault_injections_total{kind,mode}` / `vapor_controlplane_faults_armed`（见下文）。

## 故障注入演练

仅管理员可用的 `/v1/faults` API（见 `api.md` §4.13）能在真实机制上布设运行时的错误/延迟演练——任务派发（重新入队/重试路径）或 `/v1` API 请求（边缘中间件）。几条运维经验法则：

- 每个故障都会自愈：一份预算（剩余注入次数，耗尽即自动移除）和一个 TTL（默认 15 分钟）共同圈住每次演练。你布下的东西不可能无限期地拖垮系统，而控制平面一重启就全部解除（状态在内存里）。
- 控制面本身永不被注入故障：`/v1/faults`、`/healthz` 与 `/metrics` 豁免于 API 请求注入，因此演练始终可观测、可叫停。
- 紧急停止按钮：`DELETE /v1/faults` 一次性解除全部，并回报当时布设了多少个。
- 每次布设/解除都有审计（`/v1/audit/logs` 里的 `faults.enable` / `faults.disable` / `faults.clear`），并体现在 `vapor_controlplane_fault_injections_total` 计数器与 `vapor_controlplane_faults_armed` gauge 上。
- 路由选择器是对路由模式做大小写不敏感的子串匹配，所以 `"/v1/jobs"` 会扩散到所有 jobs 端点——用能回答你问题的最窄选择器去布设。

## 分布式追踪

两个服务都通过 OpenTelemetry 上报 span（`Vapor.ControlPlane` 与 `Vapor.Agent` 两个 activity source）。追踪默认关闭；在任意一个（乃至两个）服务上设置标准的 `OTEL_EXPORTER_OTLP_ENDPOINT` 变量，就用标准的 OTLP 协议/头变量开启了导出。

一条 trace 会跨隧道端到端跟踪同一个任务：

```
POST /v1/jobs           → ASP.NET Core server span (control plane)
  task.dispatch         → producer span; traceparent injected into the WS message
    task.execute        → agent consumer span (parented via the tunnel traceparent)
      task.result       → control-plane span continued from the agent's returned traceparent
```

任何兼容 OTLP 的后端都行（Jaeger、Grafana Tempo、兼容 Zipkin 的收集器）。把两个服务指向同一个后端，就能在一条 trace 里看到完整链路；变量未设置时不注册任何 exporter，span 就是惰性的（除了被禁用的 ActivitySource 之外没有额外开销）。

`vapor_controlplane_http_requests_total` 的每个样本都带一个 Prometheus 文本格式的 **exemplar**，内含该序列中最近一次请求的 W3C trace id（`…status="503"} 1 # {trace_id="<32 hex>"} 1`）：从慢或出错的路由直接跳到具体 trace 的「指标→追踪」桥梁，两侧之间没有任何依赖（exemplar 只是抓取行上的一个注释；忽略它的抓取方看到的值完全一样）。要用它，就用 `--enable-feature=exemplar-storage` 启动 Prometheus，并在 Grafana 里配置数据源的「Exemplars → trace」链接（Tempo/Jaeger）——在请求速率面板上点一个点就会打开对应 trace。耗时摘要不带 exemplar（曝光格式只允许在 counter 与 histogram 上使用）。

需要尾部采样的话，它属于夹在服务与后端之间的 OTel Collector（`tailsampling` 策略处理器）：设了 `OTEL_EXPORTER_OTLP_ENDPOINT` 后两个服务会导出全部数据，采不采由 collector 决定。服务自身刻意不提供任何采样开关——在单人运维的规模下，后端的数据量是运维者的策略，不是应用的。
