简体中文 | [English](../api/)

# REST API 参考

控制平面把每一项运维能力都作为 HTTP API 暴露在`/v1`下 —— 68 条路由（84 个操作）—— 外加`/`、`/healthz`与`/metrics`。管理控制台用的也是同一套接口，因此 UI 能做的一切，这份参考都用`curl`演示了对应做法。当`Vapor_ENABLE_SWAGGER=true`时，可在`/swagger`取到 OpenAPI 文档。

compose 开发环境下的基础 URL：`http://127.0.0.1:8080`。真实部署的密钥管理、TLS 与网络加固见 [production.md](production.md)；把这些端点串成一套能跑起来的挂机农场的完整走查见 [getting-started.md](getting-started.md)。

## 1. 通用约定

### 基础 URL

- 没有任何硬编码的基础 URL；控制平面就是一个普通的 ASP.NET Core `WebApplication`（监听地址用标准的`ASPNETCORE_URLS`）。
- Docker 镜像`EXPOSE 8080`，`docker-compose.yml`映射`${VAPOR_CONTROLPLANE_PORT:-8080}:8080`，因此`http://<host>:8080`实际上是基础 URL。
- Swagger UI（当`Vapor_ENABLE_SWAGGER=true`）挂载在`/swagger`，文档标题为 "Vapor Control Plane API"（`v1`）。
- `GET /`以 302 跳转到`/admin.html`（管理界面是`wwwroot`下的静态文件）。

### 认证

三套静态 bearer 令牌，全部通过环境变量配置（`Config.LoadFromEnvironment`）：

| 角色 | 环境变量 | 格式 |
|---|---|---|
| 管理员 | `VAPOR_ADMIN_API_KEY` | 单个令牌（compose 开发默认`admin-dev-token`） |
| Agent | `VAPOR_AGENT_API_KEYS` | 逗号分隔的令牌列表（开发默认`agent-dev-token`） |
| 只读 | `VAPOR_VIEWER_API_KEY` | 单个令牌；只读 —— 在`/v1`的全部安全方法（GET/HEAD/OPTIONS）上受理，任何写操作都返回`401`拒绝 |

请求头约定（对应 OpenAPI 安全方案`bearer`）：`Authorization: Bearer <token>`。

- `Auth.TryAdmin`—— 令牌与`AdminApiKey`完全相等（ordinal 比较）。
- `Auth.TryAgent`—— 令牌是`AgentApiKeys`的成员（ordinal 比较）。
- 密钥过期（`Config.ParseApiKey`）：任何已配置的密钥都可以带一个
  `@<ISO-8601>`后缀 —— 例如`VAPOR_AGENT_API_KEYS=new-key,old-key@2026-12-31T23:59:59Z`。
  该后缀会从凭据中剥离（只有日期才会被解析为过期时间；最后一个`@`之后
  不是日期的密钥，整段都算字面密钥）。密钥在过期时刻之前严格有效；从那一刻
  起，bearer 令牌会像未知密钥一样被拒绝，返回同样空体的 401。
  过期机制正是密钥轮换的手段：把新密钥与旧密钥一起下发并带上
  截止时间，旧密钥就会按计划失效，不必再重启一次
  （见`production.md` § 密钥）。
- 查询字符串回退（`GetAuthorization`）：当`Authorization`请求头缺失时，接受`?authorization=`查询参数，带不带`Bearer `前缀都行（缺失时补上）。这是给无法设置请求头的 SSE/WebSocket 客户端准备的。
- 免认证（公开）：`GET /`、`GET /healthz`、`GET /metrics`、`wwwroot`下的静态文件（`/admin.html`等）、`/swagger`（启用时）。
- 双认证端点（**管理员或 Agent 密钥**）：`POST /v1/sessions/events`、`GET /v1/auth/challenges/events`（Agent 看到的是过滤后的视图）、`GET /v1/agent/ws`（仅 Agent 密钥）。
- 其余所有`/v1`端点都要求**管理员**密钥。

未授权响应：`Results.Unauthorized()` / `TypedResults.Unauthorized()`，以及 SSE 上只写状态码 —— 都是**空体的 401**（部分路由上确实挂着`.Produces<ErrorResponse>(401)`元数据，但处理器不输出任何响应体）。

### JSON 约定

`ConfigureHttpJsonOptions`与`Vapor.Protocol.JsonDefaults.Options`（SSE 负载和 WebSocket 帧都用它）完全一致：

- `PropertyNamingPolicy = CamelCase` → record 的属性按 camelCase 绑定/序列化（例如`PartnerSteamId` → `partnerSteamId`）。
- `DefaultIgnoreCondition = WhenWritingNull` → 值为 null 的属性在响应中被**省略**。
- `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` → 枚举序列化为 camelCase 字符串（`desiredState: "offline"`、`status: "queued"`、`priorityOrder: "cardsDescending"`、`missed: "runOnce"`、`overlap: "allow"`、`passwordFormat: "plainText"`）。
- 若干内联响应体刻意使用 **snake_case** 键 —— 它们是在 C# 匿名对象里逐字写死的（`job_id`、`app_id`、`market_listings`、`points_shop`、`mobile_confirmation`）—— 下面按原样抄录；camelCase 策略不会给它们改名（它只会把首字母小写）。

### 错误体结构

`ErrorResponse(string Error)` → `{ "error": "<message>" }`。用于 400/404/409 的响应体。`401`没有响应体（见上文）。账号任务类端点还有一种情形：派发的 Agent 子任务以非队列态的失败状态收场时，会另外返回一个专用的 **502** 响应体`{ "job_id": "...", "error": "..." }`。

### 限流（默认关闭）

在控制平面上设置`Vapor_API_RATE_LIMIT_PER_MINUTE`（> 0）即可为所有`/v1`路由启用按密钥计数的滑动窗口限流。计数键取`Authorization`请求头的原始值（未认证的请求共用`anonymous`桶）；`GET /`、`/healthz`、`/metrics`以及静态控制台页面从不限流。被拒绝时返回 **429**，响应体是标准的`{ "error": "..." }`，另带`Retry-After`请求头（距离窗口内最早那条请求过期还有多少秒）。限流默认关闭；被拒次数计入`/metrics`上的`vapor_controlplane_rate_limited_total`。

### 路由参数约束

路由上没有声明任何 ASP.NET 路由约束（`{name}`、`{offerId}`、`{jobId}`、`{accountName}`、`{pluginId}`、`{id}`都是普通字符串）；校验在处理器内部完成（例如`offerId`必须能解析为`ulong > 0`，`appId`查询参数必须能解析为`uint > 0`）。

---

## 2. SSE 事件端点 —— 机制

以下端点共用同一套机制：`GET /v1/jobs/{jobId}/events`、`GET /v1/jobs/events`、`GET /v1/sessions/events`、`GET /v1/auth/challenges/events`：

- 响应头：`Content-Type: text/event-stream`、`Cache-Control: no-cache`、`Connection: keep-alive`。
- 首帧立即发出：`event: ready\ndata: {}\n\n`。
- 其后每个事件都是`event: <type>\ndata: <json>\n\n`，JSON 由`JsonDefaults.Options`序列化（camelCase、枚举序列化为 camelCase 字符串、省略 null）。
- 认证走标准 bearer 校验 —— 浏览器用`?authorization=`查询参数回退。
- 底层是`EventBroker`的内存有界通道（容量 256，`DropOldest`）：消费慢的订阅方可能悄悄丢事件。不做重放，也不留历史。
- 查询过滤：会话流与挑战流接受可选的`accountName`查询参数（不传值 = 所有账号，键为`*`）。

事件负载结构：

- 任务事件（`Event` record）：`{ id, jobId, type, ts, payload? }`，其中`payload`是开放的`Dictionary<string, object?>`。帧的`event:`名等于事件的`type`。代码实际发布的事件类型：
  - `job.created`（payload 为`action`、`targets`）、`job.canceled`（无 payload）
  - `task.dispatched`（`taskId`、`agentId`）、`task.finished`（`taskId`、`success`、`job`）、`task.failed`（`taskId`、`error`、`job`）、`task.dispatch_failed` / `task.enqueue_failed`（`taskId`、`attempt`、`error`、`agentId?`）
  - `job.scheduled_triggered` / `job.scheduled_skipped` / `job.scheduled_completed`（循环调度器）
  - `trade.auto_accepted`、`account.farm_progress`、`account.standing_alert`、`account.standing_released`、`account.reconciled`（编排器；部分带`jobId`，部分为全局）
  - `agent.connected` / `agent.disconnected`（`agentId`、`region`；`jobId`为 null）
  - `crawl.run_triggered`、`crawl.run_completed`、`crawl.run_skipped`（抓取 worker；payload 为`plan_id`、`run_id`、`status`（`completed`|`failed`|`timeout`|`skipped`）、`total`、`ok`、`failed`—— 全是 snake_case 键）。注意：`crawl.run_failed`（派发异常）只写审计日志，绝不进入 SSE broker。
- 权威名称表 —— 每个名称、所属的 Domain/Operational 层以及发布者 ——
  位于 [`domain-model.md`](domain-model.md) 的 "SSE event names" 一节；
  `scripts/verify-sse-event-names.py`负责让这三个面保持一致。
- 会话事件（`SessionEvent` record）：帧为`event: session.<eventType>`；payload 是`{ id, accountName, eventType, state, message?, timestamp }`。
- 认证挑战事件（`AuthChallengeEvent` record）：帧为`event: auth.<challengeType>`；payload 是`{ id, accountName, challengeType, message?, code?, timestamp, jobId?, attempt }`（`attempt`是该账号在跟踪器中的重提代数，首次提出时为 1）。访问规则：
  - 管理员：看到全部挑战，但`code_provided_*`事件在发出前会剥掉`code`（置`null`）。
  - Agent：只看得到`code_provided_*`事件，`code`完整保留（这就是投递给 Agent 的通道）。
  - `challengeType`取值：`auth_code_required`、`2fa_required`、`qr_required`、`code_provided_email`、`code_provided_totp`、`code_provided_2fa`。

---

## 3. Agent WebSocket —— 认证握手

`GET /v1/agent/ws`—— Agent 隧道。认证顺序：

1. bearer 令牌必须匹配`VAPOR_AGENT_API_KEYS`中的某一项（`Auth.TryAgent`）—— 走请求头或`?authorization=`回退；否则返回 401（空体）。
2. 必须是 WebSocket 升级请求，否则 400 `{ "error": "websocket required" }`。
3. 查询参数`agentId`与`region`都必填，否则 400 `{ "error": "agentId and region are required" }`。
4. 第一帧 WebSocket 消息必须是 JSON `{ "type": "hello", "hello": { "agentId": "...", "region": "...", "capabilities": { "<action>": true|false, ... }, "meta": { ... } } }`（`WSMessage`/`AgentHello`），且`hello.agentId` / `hello.region`必须与查询参数相等；否则连接以关闭状态`PolicyViolation`、消息 "hello required" 断开。
5. 注册完成后，服务端会推送`{type: "task", task: JobTask, traceHeaders?}`帧和`{type: "task_cancel", taskCancel: {taskId, attempt, ts, reason?}}`帧，并接收上行发来的`task_result`（`{taskId, success, error?, output?, finishedAt, attempt}`）与`task_heartbeat`（`{taskId, attempt, ts}`）帧（未知或已过期的心跳直接忽略，不算错误）。

---

## 4. 按资源域划分的端点

账号任务模式的图例：许多`/v1/accounts/{name}/...`端点会向账号所在的 Agent 派发一个单目标任务，并**阻塞轮询最多 30 秒**等待子任务（`AccountTaskRunner.WaitWindow`，轮询间隔 200 毫秒）：

- 子任务`Finished` → 返回`200`，内嵌子任务输出。
- 窗口结束时仍是`Queued` → 返回`202`，带`Location: /v1/jobs/{jobId}`，响应体`{ "job_id": "<id>", "status": "pending" }`（调用方继续轮询`GET /v1/jobs/{jobId}`）。
- 子任务`Failed`/`Canceled`（或任何非队列态的终态）→ 返回`502`，响应体`{ "job_id": "<id>", "error": "<msg>" }`。
- 账号未声明 → `404 { "error": "account '<name>' is not declared" }`。

---

### 4.1 认证与挑战

#### `GET /v1/auth/challenges`
- 用途：列出内存跟踪器中当前挂起的登录挑战（2FA / 验证码提示）。
- 认证：管理员。
- 请求体：无。查询参数：无。
- 200：`{ "challenges": [ AuthChallengeEvent ] }`——`AuthChallengeEvent = { id, accountName, challengeType, message?, code?, timestamp, jobId?, attempt }`，最新在前；`attempt`按账号计重提代数（1 = 首次提出）。
- 错误：401。

#### `POST /v1/auth/challenges/{accountName}/code`
- 用途：为某个账号提交登录挑战验证码；清掉挂起的挑战，并发布一个`code_provided_<type>`认证挑战事件，供 Agent 经 SSE 消费。
- 认证：管理员。
- 请求体：自由形式的 JSON 对象（绑定为`Dictionary<string, string?>`）：
  - `code`—— string，**必填**（留空 → 400 `code is required`）；会去掉首尾空白。
  - `type`—— string，可选；取`email` | `totp` | `2fa`之一（转小写）；**默认`email`**；其他取值 → 400 `type must be one of: email, totp, 2fa`。
- 200：`{ "ok": true, "accountName": "<name>", "type": "<email|totp|2fa>" }`
- 错误：400（缺验证码 / 类型非法）、401。
- 路由参数：`{accountName}`—— 普通字符串。
- 审计：`auth.code.submitted`（`code`值会写进 details 字典，但落库前由`SensitiveDataRedactor`脱敏 —— 存下来的审计记录里永远不会出现明文验证码）。

#### `GET /v1/auth/challenges/events` (SSE)
- 用途：登录挑战的实时流；管理员看到全部（`code_provided_*`上的验证码已脱敏），Agent 只看得到`code_provided_*`（带验证码）。
- 认证：**管理员或 Agent**。
- 查询参数：`accountName`（string，可选）—— 过滤到单个账号。
- 200：`text/event-stream`，帧为`event: auth.<challengeType>`，负载是`AuthChallengeEvent` JSON（见 §2）。首帧是`event: ready`。
- 错误：401（无响应体）。

#### `POST /v1/qr`
- 用途：为控制台把文本渲染成二维码 SVG —— 管理页面把它嵌进`qr_required`挑战卡片，让 Steam 手机 App 直接扫描轮换中的挑战 URL。渲染器是第一方实现（仓库内的二维码编码器，字节模式 / ECC M / 版本 1-6，≤ 106 个 UTF-8 字节）：登录令牌始终留在控制平面内，不牵涉任何第三方图像服务（todo §18 红线）。
- 认证：管理员。
- 请求体：`{ "text": "<string>" }`—— 必填（留空 → 400 `text is required`；超过 106 个 UTF-8 字节 → 400，消息中写明容量）。
- 200：`image/svg+xml`—— 自包含的`<svg>`，带 4 模块宽的静区（无状态渲染）。
- 错误：400（文本为空 / 超容量 / 请求体无法解析）、401。
- 审计：无 —— 渲染无状态、无副作用。

---

### 4.2 账号（已声明的挂机账号、交易、市场、库存、重复物品、成就、积分商店、战利品、状态、许可证、确认（移动端））

账号规格 record ——`AccountSpec`：`{ accountName, enabled, desiredState, idleApps?, region?, agentId?, note?, version?, marketListingsEnabled, boostTargets?, tradePolicy?, farmPolicy?, role, steamId? }`，其中
`version = { version: int, updatedAt, updatedBy? }`（`ConfigVersion`）、
`boostTargets = [ { appId: uint, targetHours: double } ]`（`BoostTarget`）、
`tradePolicy = { autoAcceptGifts: bool, partnerWhitelist?: ulong[] }`（`TradePolicy`）、
`farmPolicy = { perGameHourBudget?: double, priorityOrder: "cardsDescending"|"cardsAscending"|"appIdAscending", priorityApps?: uint[] }`（`FarmPolicy`）、
`role = "farm"|"storage"`（`AccountRole`，默认`farm`—— 挂机账号是战利品来源，仓库账号则是收取任务的目标；这个默认值让该字段出现之前就已持久化的规格保持原样）、
`steamId?`—— 账号自身的 SteamID64，也就是仓库账号被收取时对端的地址（校验为正的 64 位数；留空归一化为 null —— 先声明角色、稍后才拿到 id 没问题，没有它收取端点会拒绝运行）。

#### `GET /v1/accounts`
- 用途：按过滤器列出已声明的账号。
- 认证：管理员。
- 查询参数：`state`（可选；`AccountDesiredState`名称，大小写不敏感——`offline` | `online` | `idle` | `farm` | `boost`；无法解析 → 400 `unknown desired state '<state>' (expected offline, online or idle)`）、`region`（可选，大小写不敏感匹配）、`agent`（可选，按`agentId`大小写不敏感匹配）。
- 200：`{ "accounts": [ AccountSpec ] }`
- 错误：400、401。

#### `GET /v1/accounts/{name}`
- 用途：单个账号的聚合视图：规格、实时会话快照、编排器视图、挂起挑战、最近 10 条子任务。
- 认证：管理员。
- 请求体：无。
- 200：`{ "spec": AccountSpec, "session": SessionSnapshot|null, "observed": string, "reason": string, "orchestration": AccountOrchestrationView|null, "pendingChallenge": AuthChallengeEvent|null, "recentTasks": [ JobTask ] }`（省略 null）。`observed`是最新的会话快照状态（把`session.state`提到顶层；字段缺席 = 没有快照），`reason`是收敛器对该账号最近一次做出的判定（投影自`orchestration.lastDeviation`；缺席 = 还没有任何一轮记录过判定）。
- 错误：404 `account '<name>' is not declared`、401。

#### `PUT /v1/accounts/{name}`
- 用途：声明一条账号规格或整体替换它（新建或全量更新；更新时版本号自增；省略`marketListingsEnabled`则沿用旧值）。
- 认证：管理员。
- 请求体（`PutAccountRequest`，除特别说明外全部字段可选）：
  - `enabled`—— bool，默认`true`
  - `desiredState`—— 字符串枚举，默认`"offline"`（`offline`|`online`|`idle`|`farm`|`boost`）
  - `idleApps`—— 数字 app id 的 string[]，可选（非数字条目 → 400）
  - `region`—— string，可选
  - `agentId`—— string，可选
  - `note`—— string，可选
  - `updatedBy`—— string，可选（审计元数据）
  - `marketListingsEnabled`—— bool，可选（为 null 时更新保留原值）
  - `boostTargets`——`[{ appId: uint, targetHours: number }]`，可选；当`desiredState = "boost"`时必须非空（否则 → 400）；同一 app 重复出现但 targetHours 不同 → 400
  - `tradePolicy`——`{ autoAcceptGifts: bool (default false), partnerWhitelist?: string(ulong)[] }`，可选；`autoAcceptGifts=true`但白名单为空 → 400
  - `farmPolicy`——`{ perGameHourBudget?: number, priorityOrder?: string, priorityApps?: uint[] }`，可选；预算非有限或 ≤ 0 → 400
  - `role`—— 字符串枚举，默认`"farm"`（`farm`|`storage`；其他取值 → 400 `account role must be farm or storage, got <value>`）
  - `steamId`—— string（SteamID64），可选；出现时必须能解析为`ulong > 0`（否则 400 `steam_id must be a positive 64-bit SteamID, got '<value>'`）；留空归一化为 null
- 200：`{ "spec": AccountSpec }`
- 错误：400（名称为空，或校验失败并回传`ArgumentException`消息）、401。
- 审计：`account.spec.updated`。

#### `DELETE /v1/accounts/{name}`
- 用途：删除一条已声明的账号规格。
- 认证：管理员。
- 请求体：无。
- 204：空。
- 错误：404、401。审计：`account.spec.removed`。

#### `POST /v1/accounts/{name}/enable` / `POST /v1/accounts/{name}/disable`
- 用途：翻转`enabled`（恢复 / 暂停编排），期望状态与配置保持不变。
- 认证：管理员。请求体：无。
- 200：`{ "spec": AccountSpec }`。错误：404、401。审计：`account.enabled` / `account.disabled`。

#### `POST /v1/accounts/{name}/standing-check`
- 用途：在下一轮收敛时安排一次对该账号立即执行的 Steam 状态检查。
- 认证：管理员。请求体：无。
- 202：`Location: /v1/accounts/{name}`，响应体`{ "scheduled": true }`。
- 错误：404、409 `{ "error": "standing check not scheduled: a job is active or standing checks are disabled" }`、401。审计：`standing_check_requested`。

#### `POST /v1/accounts/{name}/reconcile`
- 用途：立即跑一轮收敛，不必等下一个周期节拍 —— 这一轮跑的就是既有的引擎逻辑；端点只负责触发，并把由此得出的判定记进审计轨迹。
- 认证：管理员。请求体：无。
- 200：`{ "status": "reconciled" }`。错误：404 `account '<name>' is not declared`、401。审计：`orchestration.reconcile`（details 含`desiredState`与本轮结束后的`reason`）。

#### `GET /v1/accounts/{name}/trade-offers`
- 用途：列出该账号收到的交易报价（派发`get_trade_offers`，有界等待）。
- 认证：管理员。
- 查询参数：`activeOnly`—— bool，可选，**默认`true`**。
- 200：`{ "job_id", "account", "offers": <agent task output> }`。
- 202 / 502：账号任务模式。错误：404、401。审计：`trade_offers.read`。

#### `POST /v1/accounts/{name}/trade-offers/{offerId}/accept`
- 用途：接受一份收到的交易报价（派发`accept_trade_offer`）；当输出标记`requires_mobile_confirmation`且`autoConfirm`不为 false 时，自动派发`confirm_trade_offer`。
- 认证：管理员。
- 路由参数：`{offerId}`—— 必须能解析为`ulong > 0`，否则 400 `offerId must be a positive integer`。
- 请求体（`TradeOfferDecisionRequest`）：
  - `partnerSteamId`—— string（SteamID64），**必填**（必须能解析为`ulong > 0`，否则 400 `partner_steam_id is required (the offer's partner_steam_id from the trade-offers listing)`）
  - `verifyState`—— bool，可选，默认`true`（以`verify_state`转发给 Agent）
  - `autoConfirm`—— bool，可选，默认`true`（移动端确认的自动确认开关）
- 200：`{ "job_id", "account", "result": <agent output>, "mobile_confirmation": { "attempted": true, "job_id", "confirmed"?: bool, "error"?: string } | null }`（为 null 时整个省略；确认子任务仍在队列中时`confirmed`/`error`缺席）。
- 202 / 502：账号任务模式。错误：400、404、401。审计：`trade_offer.accept`（外加`trade_offer.confirm`）。

#### `POST /v1/accounts/{name}/trade-offers/{offerId}/decline`
- 用途：拒绝一份收到的交易报价（派发`decline_trade_offer`）。
- 认证：管理员。
- 路由参数：`{offerId}`——`ulong > 0`，否则 400。
- 请求体（`TradeOfferDecisionRequest`）：
  - `partnerSteamId`—— 拒绝时**不要求**（忽略）。
  - `verifyState`—— bool，可选，默认`true`。
  - `autoConfirm`—— 未使用（拒绝永远不会自动确认）。
- 200：`{ "job_id", "account", "result", "mobile_confirmation" }`（与 accept 同形；此处`mobile_confirmation`恒为 null）。202 / 502 模式。错误：400、404、401。审计：`trade_offer.decline`。

#### `POST /v1/accounts/{name}/confirmations/accept-all`
- 用途：批量应答所有待处理的移动端确认（派发`confirm_all_confirmations`；身份密钥留在 Agent 侧）。
- 认证：管理员。
- 请求体（`ConfirmationsBatchRequest`）：
  - `operation`—— string，可选，默认`"allow"`；必须是`allow` | `cancel`，否则 400 `operation must be 'allow' or 'cancel'`。
  - `type`—— string，可选，默认`"all"`；必须是`all` | `trade` | `market`，否则 400 `type must be 'all', 'trade' or 'market'`。
- 200：`{ "job_id", "account", "result" }`。202 / 502 模式。错误：400、404、401。审计：`trade_confirmations.accept_all`。

#### `GET /v1/accounts/{name}/inventory`
- 用途：读取该账号的库存（派发`get_inventory`）；支持按应用/context 过滤。
- 认证：管理员。
- 查询参数：`appIds`（逗号分隔的 uint 列表，每项 > 0；任一条目非法 → 400 `app_ids entries must be positive app ids (got '<part>')`；空列表 → 400）、`appId`（string，可选）、`contextId`（string，可选）、`steamId`（string，可选）、`tradableOnly`（bool，可选）、`marketableOnly`（bool，可选）。
- 200：`{ "job_id", "account", "inventory": <agent output> }`。202 / 502 模式。错误：400、404、401。审计：`inventory.read`。

#### `GET /v1/accounts/{name}/duplicates`
- 用途：找出超出保留份数的重复物品（交易卡牌）（派发`find_duplicates`）。
- 认证：管理员。
- 查询参数：`appIds`（逗号分隔的 uint 列表，校验同库存）、`keep`（int，可选；必须 1–100，否则 400 `keep must be an integer between 1 and 100`）。
- 200：`{ "job_id", "account", "duplicates": <agent output> }`。202 / 502 模式。错误：400、404、401。审计：`inventory.duplicates`。

#### `GET /v1/accounts/{name}/achievements`
- 用途：列出该账号在某个游戏上的成就（派发`get_achievements`）。
- 认证：管理员。
- 查询参数：`appId`—— **必填**，正 uint，否则 400 `appId must be a positive app id`。
- 200：`{ "job_id", "account", "app_id": <uint>, "achievements": <agent output> }`。202 / 502 模式。错误：400、404、401。审计：`achievement.read`。

#### `POST /v1/accounts/{name}/achievements/unlock`
- 用途：解锁某个游戏的指定成就（派发`unlock_achievements`）；显式的名称清单就是全部契约（没有批量路径）。
- 认证：管理员。
- 请求体（`AchievementWriteRequest`）：
  - `appId`—— uint，**必填**，> 0（否则 400 `appId is required (positive app id)`）
  - `names`—— 成就 API 名称的 string[]，去空白去重后**必须非空**（否则 400 `names is required: an explicit non-empty list of achievement API names (no implicit full-batch path exists)`）
  - `confirm`—— 解锁时不使用。
- 200：`{ "job_id", "account", "app_id", "result": <agent output> }`。202 / 502 模式。错误：400、404、401。审计：`achievement.unlock`。

#### `POST /v1/accounts/{name}/achievements/reset`
- 用途：重置（清除）指定成就（派发`reset_achievements`）；门控与解锁相同，另加一次显式的破坏性操作确认。
- 认证：管理员。
- 请求体：同上`AchievementWriteRequest`，**另加**`confirm`—— bool，**必须显式为`true`**（否则 400 `confirm must be explicitly true for a reset (destructive operation)`）。
- 200：`{ "job_id", "account", "app_id", "result" }`。202 / 502 模式。错误：400、404、401。审计：`achievement.reset`。

#### `GET /v1/accounts/{name}/market/listings`
- 用途：列出该账号自己的社区市场挂单（派发`get_my_market_listings`）。
- 认证：管理员。
- 查询参数：`start`（int，可选，默认 0，钳制 ≥ 0）、`count`（int，可选，默认 100，钳制 1–500）。
- 200：`{ "job_id", "account", "market_listings": <agent output> }`。202 / 502 模式。错误：404、401。审计：`market_listings.read`。

#### `POST /v1/accounts/{name}/market/listings`
- 用途：创建一条市场挂单；默认只干运行（给出定价方案报告）；真实挂单需要`send=true`，外加账号规格里的 opt-in `marketListingsEnabled`（以及 Agent 自己的`AGENT_MARKET_LISTINGS_ENABLED`开关）。
- 认证：管理员。
- 请求体（`MarketCreateListingRequest`，全部可选）：
  - `appId`—— uint，可选（> 0 才会转发）
  - `contextId`—— string，可选
  - `assetId`—— string，可选
  - `amount`—— int，可选（> 0 才会转发）
  - `sellerProceedsCents`—— int，可选（两侧价格 —— 恰好其一）
  - `buyerPriceCents`—— int，可选
  - `send`—— bool，可选，**默认`false`**（干运行）；未 opt-in 的账号带`send=true` → 400 `refusing to list: this account has not enabled market listings (set marketListingsEnabled=true on the account spec)`
- 200：`{ "job_id", "account", "result" }`。202 / 502 模式。错误：400、404、401。审计：`market_listings.create`。

#### `POST /v1/accounts/{name}/market/listings/cancel`
- 用途：按过滤器取消该账号自己的市场挂单；`dry_run=true`（默认）只预览匹配到的挂单；不带过滤器的真实运行会被拒绝。
- 认证：管理员。
- 请求体（`MarketCancelRequest`，全部可选）：
  - `appId`—— uint，可选
  - `marketHashName`—— string，可选
  - `minPriceCents`—— int，可选
  - `maxPriceCents`—— int，可选
  - `olderThanSeconds`—— int，可选（> 0 才会转发）
  - `dryRun`—— bool，可选，**默认`true`**
  - 规则：`dryRun=false`且四个过滤器一个都没设 → 400 `refusing to cancel with no filter: pass at least one filter, or keep dry_run=true to preview`。
- 200：`{ "job_id", "account", "result" }`。202 / 502 模式。错误：400、404、401。审计：`market_listings.cancel`。

#### `GET /v1/accounts/{name}/points-shop/summary`
- 用途：读取该账号的积分商店余额以及（可选）指定奖励定义（派发`get_points_shop_summary`）。
- 认证：管理员。
- 查询参数：`definitionIds`（逗号分隔的 uint 列表；任一项无法解析 → 400 `invalid definition_ids value '<part>'`）、`freeOnly`（bool，可选；转发`free_only: true`）。
- 200：`{ "job_id", "account", "points_shop": <agent output> }`。202 / 502 模式。错误：400、404、401。审计：`points_shop.summary`。

#### `POST /v1/accounts/{name}/points-shop/claim`
- 用途：兑换积分商店的奖励定义（派发`claim_points_shop_items`）；默认只兑免费定义，付费定义需要`force`。
- 认证：管理员。
- 请求体（`PointsShopClaimRequest`）：
  - `definitionIds`—— uint[]，**必须非空**（否则 400 `definition_ids is required`）；条目必须 > 0（否则 400 `definition_ids must be positive`）；派发前去重。
  - `force`—— bool，可选，默认`false`（`true`时会兑换付费定义并花费积分；只有为 true 才会转发）。
- 200：`{ "job_id", "account", "result" }`。202 / 502 模式。错误：400、404、401。审计：`points_shop.claim`。

#### `POST /v1/accounts/{name}/proxy`
- 用途：通过账号钉定的 Agent 的`set_proxy`宿主动作钉定（或清除）该账号的出口代理，随后用链式的`check_proxy`验证实际出口。该端点**只在 Agent 侧持久化**（控制平面不存任何凭据），因此前提是该账号已钉定自己的 Agent。
- 认证：管理员。
- 错误（派发前）：400 `proxy is required (an explicit empty string clears the account's pinned exit)`；400 `account has no pinned agent (agentId); a proxy persists agent-side and requires one`；404 `pinned agent '{id}' is not connected; connect it and retry`；404 账号未知；401。
- 请求体（`SetProxyRequest`）：
  - `proxy`—— string，**必填字段**（传`null` / 缺省会被拒绝，这样无请求体的 POST 绝不会抹掉已钉定的出口）；显式传**空字符串表示清除**代理（直连）；其余情况为`http|https|socks5://[user:pass@]host:port`。端点的校验发生在 Agent 侧；非法端点会让子任务失败并给出一条固定消息（不含凭据）。
- 派发：在`agent:{agentId}`上派发一个宿主导向的`set_proxy`子任务（按区域挑 Agent 会是错的 —— 密钥必须落到钉定那个 Agent 的存储里），成功后再跟一个账号导向的`check_proxy`验证子任务（无 payload；Agent 按该账号当前的配置去探测）。验证失败**只上报、不回滚** —— 指派依然生效，后续由运维决定。
- 200：`{ "job_id", "account", "result", "verification" }`，其中`result`是`set_proxy`的输出（`{ account, proxy (masked), cleared, sessionRestarted }`），`verification`是`{ job_id, status, output? | error? }`（`status`为`finished`时带探测输出 —— 出口 IP、Steam 可达性、时延；或为`pending` / `failed`）。
- 202 / 502 模式（`set_proxy`子任务本身失败时返回 502；此时不会执行验证）。审计：`account.proxy_set`（端点以脱敏形式记录，或记作`(cleared)`）+ `account.proxy_check`。

#### `POST /v1/accounts/{name}/loot`
- 用途：把该账号所有可交易物品发给一个对方（派发`loot_inventory`）；Steam 要求时自动确认移动端步骤。
- 认证：管理员。
- 请求体（`LootRequest`）：
  - `partnerSteamId`—— string（SteamID64），可选；出现时必须能解析为`ulong > 0`（否则 400 `partner_steam_id must be a positive 64-bit SteamID`）
  - `tradeUrl`—— string，可选（带 token 的 URL 可以投递给非好友）
  - 两者至少其一**必填**（否则 400 `either partner_steam_id or trade_url is required (a token-bearing trade URL reaches non-friends)`）；两者都给时`trade_url`优先。
  - `message`—— string，可选（交易留言）
  - `appIds`—— int[]（record 中为`int[]? AppIds`），可选，限定发送哪些游戏的物品
  - `force`—— bool，默认`false`；当该账号被状态检查隔离时（§38-P2，检测到封禁），loot 会被拒绝并返回 400 `account '<name>' is quarantined by the standing check (standing=<summary>); pass force=true to override`，除非传了`force=true`—— 这是清空已被 Steam 标记的账号时唯一留痕的抢救通道。从未收敛过的账号（没有状态历史）永远不会被拦。
- 200：`{ "job_id", "account", "result", "mobile_confirmation" }`（`mobile_confirmation`与接受交易报价时同形）。202 / 502 模式。错误：400、404、401。审计：`account.loot`（外加`trade_offer.confirm`）；details 含`forced_quarantine`（只有凭`force`穿过状态隔离完成派发时才为 true）。

#### `POST /v1/accounts/{name}/swap-offers`
- 用途：把重复物品与对方手里的重复份配对，并提出 1:1 对换（派发`swap_duplicates`）；不带`send=true`时只干运行；实际发送时会自动确认移动端步骤。
- 认证：管理员。
- 请求体（`SwapOfferRequest`）：
  - `partnerSteamId`—— string，可选（校验与规则同 loot；对方 id 与交易 URL 至少其一必填）
  - `tradeUrl`—— string，可选
  - `message`—— string，可选
  - `appIds`—— int[]，可选
  - `keep`—— int，可选（每种物品保留的份数）
  - `maxSwaps`—— int，可选
  - `send`—— bool，**默认`false`**（record 中不可为空；省略时为干运行）
- 200：`{ "job_id", "account", "result", "mobile_confirmation" }`。202 / 502 模式。错误：400、404、401。审计：`trade.swap_offer`（外加`trade_offer.confirm`）。

#### `POST /v1/accounts/{name}/licenses`
- 用途：在该账号上认领免费 Steam 内容（派发`add_license`；`app_ids`走客户端协议，`sub_ids`走商店结账）。
- 认证：管理员。
- 请求体（`LicenseRequest`）：
  - `appIds`—— int[]，可选（条目必须 > 0，否则 400 `app_ids must be positive`）
  - `subIds`—— int[]，可选（条目必须 > 0，否则 400 `sub_ids must be positive`）
  - 两者至少其一**必填**（否则 400 `either app_ids or sub_ids is required`）。
- 200：`{ "job_id", "account", "result" }`。202 / 502 模式。错误：400、404、401。审计：`account.add_license`。

---

### 4.3 编排

#### `GET /v1/orchestration/standing`
- 用途：编排器跟踪的每个账号的状态快照。
- 认证：管理员。请求体：无。查询参数：无。
- 200：`{ "standing": [ AccountStandingView ] }`——`AccountStandingView = { accountName, standing?: string, quarantined: bool, checkedAt? }`。
- 错误：401。

#### `GET /v1/orchestration/farm`
- 用途：每个被跟踪账号的挂机循环快照（队列、进度、效率计数器）。
- 认证：管理员。请求体：无。查询参数：无。
- 200：`{ "farm": [ FarmAccountView ] }`——`FarmAccountView = { accountName, farmingAppId?: uint, queue?: uint[], cardsRemaining?: int, cardsCollected: int, cardsPerHour?: double, completedApps?: uint[], skippedApps?: uint[], queueRefreshedAt?, statsStartedAt? }`。
- 错误：401。

#### `POST /v1/orchestration/storage/collect`
- 用途：把整支挂机舰队的可交易物品收取到一个已声明的仓库账号（§39 P-b）：一个任务，每个来源账号一个`loot_inventory`子任务（`partner_steam_id` = 仓库账号声明的 SteamId64），有界等待，然后对 Steam 要求确认的报价在发送侧链式执行`confirm_trade_offer`子任务（未确认的报价永远送不到仓库对端）。该任务已有的 SSE 流就是进度通道。节奏天生是限流感知的：Agent 侧按发送方计的`TradeRateLimiter`负责压住报价创建速度（这才是 Steam 真正的约束）；跨来源并发是刻意为之（每个挂机账号都有自己的会话和速率预算）。
- 认证：管理员。
- 请求体（`StorageCollectRequest`）：
  - `storage`—— string，**必填**（留空 / 缺省 → 400 `storage (target account name) is required`）；必须指向一个已声明的账号（否则 404 `account '<name>' is not declared`），且`role = "storage"`（否则 400 `account '<name>' is not declared as a storage account (role=storage)`），并已声明 SteamId64（否则 400 `storage account '<name>' has no steam_id declared; set steamId on the account before collecting`）
  - `message`—— string，可选（每份报价都会带上的交易留言）
  - `appIds`—— int[]，可选，限定发送哪些游戏的物品
  - `force`—— bool，默认`false`；用于覆盖**仓库目标**上的状态检查隔离（否则 400 `storage account '<name>' is quarantined by the standing check (standing=<summary>); pass force=true to override`）。被隔离的挂机**来源**永远跳过（不接受 force 覆盖）—— 要够到它就用 loot 端点的`force`单独收。
- 来源：已启用且未声明离线的挂机账号（`role=farm`、`enabled=true`）—— 离线的账号已经登出，没什么可收的。没有可用来源 → 400 `no enabled farm accounts to collect from (sources are enabled farm accounts not declared offline)`。被隔离的来源（状态检查检测到封禁）不参与派发，并记入`skipped_quarantined`；来源全被隔离 → 400 `all <n> farm account(s) are quarantined by the standing check; loot one individually with force=true or wait for a clean re-check`。从未收敛过的账号没有状态历史，因此永远不会被隔离。
- 200：`{ "job_id", "storage", "storage_steam_id", "farm_accounts", "skipped_quarantined", "results": [ CollectTaskResult ] }`，其中`farm_accounts`列出已派发的来源，`skipped_quarantined`列出被状态检查排除的来源，`CollectTaskResult = { account, status, error?, itemCount?, tradeOfferId?, confirmed?: bool, confirmJobId?, confirmError? }`—— 每个子任务都在等待窗口内定案；单个来源的失败在`results`中逐条上报，绝不整体返回 502。
- 202：`Location: /v1/jobs/{id}`，响应体`{ "job_id", "storage", "storage_steam_id", "farm_accounts", "skipped_quarantined", "status": "pending" }`—— 窗口结束时仍有子任务在飞；继续轮询该任务。没收取完的部分会在下次收取时重跑：未接受的报价会过期，物品原地不动。
- 错误：400、404、401。审计：`storage.collect`（details 含`skippedQuarantined`与`forcedQuarantine`）。

#### `GET /v1/orchestration/storage/snapshot`
- 用途：每个账号上一次收取的统计，由近期的收取任务重建（没有专门的仓库表 —— 任务本身就是记录）。每个来源账号以最近一次 loot 子任务为准；与之匹配（目标相同且 payload 里带同一个报价 id）的确认任务提供`confirmed`标志。
- 认证：管理员。请求体：无。查询参数：无。
- 200：`{ "accounts": [ CollectSnapshotEntry ] }`——`CollectSnapshotEntry = { account, storage?, jobId, at, status, error?, itemCount?, tradeOfferId?, confirmed?: bool, confirmJobId?, confirmError? }`（`at` = loot 子任务的`updatedAt`）。
- 错误：401。

（另见内嵌在`GET /v1/accounts/{name}`中的`AccountOrchestrationView`：`{ assignedAgent?, activeJobId?, activeJobAction?, loginAttempts, nextAttemptAt?, idling, farmingAppId?, farmQueue?, farmQueueCheckedAt?, boostUnmetApps?, boostCheckedAt?, tradeOffersToAccept?: ulong[], tradeOfferCheckedAt?, lastAction?, lastActionAt?, lastDeviation?, standing?, standingQuarantined, standingCheckedAt?, farmCompletedApps?, farmSkippedApps?, farmAppStartedAt?, farmStatsStartedAt?, farmCardsRemaining?, farmCardsCollected, farmCardsPerHour? }`。）

---

### 4.4 Agent

#### `GET /v1/agents`
- 用途：列出已注册的 Agent（摘要是这么写的 "including offline ones"；实际上注册表里就是当前已注册的 hello）。
- 认证：管理员。请求体：无。
- 200：`{ "agents": [ AgentHello ] }`——`AgentHello = { agentId, region, capabilities?: { "<action>": bool }, meta?: { string: string } }`。
- 错误：401。

#### `GET /v1/agents/status`
- 用途：列出当前已连接的 Agent 及其区域与能力。
- 认证：管理员。请求体：无。
- 200：`{ "agents": [ { "id", "region", "capabilities", "connected": true, "connectedAt" } ] }`。
- 错误：401。

#### `GET /v1/agent/ws` (WebSocket)
- 用途：用于子任务派发、结果回传与心跳的 Agent 隧道。
- 认证：**Agent 密钥**（完整握手见 §3：`agentId` + `region`查询参数，首个`hello`帧必须吻合）。
- 401（无响应体）/ 400 `{ "error": "websocket required" }`或`{ "error": "agentId and region are required" }`；首帧不合法时以关闭码`PolicyViolation`（"hello required"）断开。

---

### 4.5 任务（+ 事件）

任务 record：`Job = { id, action, region?, targets: string[], meta?: {string:string}, status, createdAt, updatedAt, schedule?, nextRunAt? }`（status：`queued|running|scheduled|finished|failed|canceled`）；
`JobTask = { id, jobId, target, action, region?, payload?, status, attempt, createdAt, updatedAt, error?, output? }`（子任务状态：`queued|running|finished|failed|canceled`）；
`JobWithTasks = { job, tasks: [JobTask] }`；
`JobSchedule = { intervalSeconds: int (default 0), cron?: string, missed: "skip"|"runOnce" (default "skip"), overlap: "skip"|"allow" (default "skip") }`——`intervalSeconds`（≥ 5）与`cron`（5 字段、UTC）必须恰好设置其一；都给 → 400 `schedule accepts intervalSeconds or cron, not both`，都不给 → 400 `schedule requires intervalSeconds > 0 or a cron expression`。

#### `POST /v1/jobs`
- 用途：创建一个任务（由调度器派发给该任务所在区域内具备相应能力的 Agent；带`schedule`的任务会成为循环模板）。
- 认证：管理员。
- 请求体（`CreateJobRequest`）：
  - `action`—— string，**必填**（留空 → 400 `action is required`）
  - `region`—— string，可选（为 null / 缺省时交给调度器挑）
  - `targets`—— string[]，**必须非空**（否则 400 `targets is required`）
  - `payload`—— object（字符串键的开放映射），可选
  - `meta`—— object（`string → string`），可选
  - `schedule`——`JobSchedule`，可选；节奏非法 → 400 并附上`ScheduleClock.Validate`的消息
- 202：`Location: /v1/jobs/{id}`，响应体`CreateJobResponse` = `{ "job": Job }`。
- 错误：400（`ErrorResponse`）、401（空体）。同时发布 SSE `job.created`与审计`job.created`。

#### `GET /v1/jobs`
- 用途：列出近期的任务。
- 认证：管理员。
- 查询参数：`limit`（int，可选，默认 50，钳制 1–500）、`account`（string，可选过滤器）。
- 200：`{ "jobs": [ Job ] }`。
- 错误：401（空体）。

#### `GET /v1/jobs/{jobId}`
- 用途：一个任务及其子任务（逐子任务的状态、尝试次数、错误、输出）。
- 认证：管理员。请求体：无。
- 200：`JobWithTasks`。
- 错误：404 `{ "error": "job not found" }`、401（空体）。

#### `POST /v1/jobs/{jobId}/cancel`
- 用途：取消一个任务下所有处于队列中 / 运行中的子任务（同时向已连接的 Agent 推送`task_cancel`帧）。
- 认证：管理员。请求体：无。
- 200：`{ "ok": true }`。
- 错误：404 `{ "error": "job not found" }`、401。同时发布 SSE `job.canceled`与审计`job.canceled`。

#### `GET /v1/jobs/{jobId}/events` (SSE)
- 用途：该任务生命周期事件的 SSE 流。
- 认证：管理员。查询参数：无（任务范围由路由决定）。
- 200 `text/event-stream`（帧格式见 §2；只含该任务的`task.*`、`job.*`）。
- 错误：401（无响应体）、404 `{ "error": "job not found" }`（在流开启之前）。

#### `GET /v1/jobs/events` (SSE)
- 用途：跨全部任务的生命周期事件 SSE 流（broker 键为`*`；也承载`jobId`为 null 的 Agent / 账号 / 抓取事件）。
- 认证：管理员。请求体：无。查询参数：无。
- 200 `text/event-stream`。错误：401（无响应体）。

---

### 4.6 会话（+ 事件）

`SessionSnapshot = { accountName, state, eventType, message?, updatedAt }`。

#### `GET /v1/sessions`
- 用途：列出已知会话及其最新状态。
- 认证：管理员。
- 查询参数：`account`（string，可选；按账号名过滤，大小写不敏感）。
- 200：`{ "sessions": [ SessionSnapshot ] }`。
- 错误：401。

#### `POST /v1/sessions/events`
- 用途：接收来自 Agent（或管理员）的会话事件上报；更新跟踪器、喂给 SSE 流，并提出 / 清除认证挑战提示（事件类型`auth_code_required`/`2fa_required`/`qr_required`或状态`ConnectingWaitAuthCode`/`ConnectingWait2FA`/`ConnectingWaitQr`会创建挑战；其余一律清除该账号挂起的挑战）。与登录有关的迁移还会另外落一条`session.login`审计记录。
- 认证：**管理员或 Agent**。
- 请求体（`SessionEventRequest`）：
  - `accountName`—— string，**必填**（留空 → 400 `accountName is required`）
  - `eventType`—— string，可选；会做归一化（例如`StateChanged` → `state_changed`、`AuthCodeNeeded` → `auth_code_required`、`TwoFactorCodeNeeded` → `2fa_required`，CamelCase → snake_case）；留空 → `state_changed`
  - `state`—— string，可选；留空 → `"unknown"`
  - `message`—— string，可选
- 200：`{ "ok": true }`。
- 错误：400、401（空体）。审计：`session.event.received`（外加`session.login`）。

#### `GET /v1/sessions/events` (SSE)
- 用途：会话事件的 SSE 流。
- 认证：管理员。
- 查询参数：`accountName`（string，可选过滤器）。
- 200 `text/event-stream`，帧为`event: session.<eventType>`，负载是`SessionEvent`。
- 错误：401（无响应体）。

---

### 4.7 审计

#### `GET /v1/audit/logs`
- 用途：查询已持久化的审计日志。
- 认证：管理员。
- 查询参数：`limit`（int，默认 100，钳制 1–500）、`offset`（int，默认 0，钳制 ≥ 0）、`action`（string，可选）、`account`（string，可选）、`jobId`（string，可选）、`agentId`（string，可选）、`taskId`（string，可选）、`fromMs`（long，unix 毫秒，可选）、`toMs`（long，unix 毫秒，可选）；`fromMs > toMs` → 400 `fromMs must not be greater than toMs`。
- 200：`{ "logs": [ AuditEntry ], "total": int, "limit": int, "offset": int }`——`AuditEntry = { id, timestamp, action, actor, remoteIp?, accountName?, jobId?, taskId?, attempt?, agentId?, sessionId?, traceId?, details? }`（执行 id 字段遵循`docs/architecture.md`中 "Execution ID convention" 一节的约定；`traceId`解析自入站的 W3C `traceparent`请求头；所有 null 字段都从响应中省略）。
- 错误：400、401。

---

### 4.8 配置

`GlobalConfig = { version: ConfigVersion, settings?: object }`（record 上有`encryptionKey?`，但这些端点从不设置它，因此不会出现）。`AccountConfig = { accountName, enabled, region?, labels?, settings?, version? }`（`password`/`passwordFormat`从不由 API 设置，因此响应里省略）。

#### `GET /v1/config`
- 用途：获取全局配置与各账号配置。
- 认证：管理员。请求体：无。
- 200：`{ "global": GlobalConfig, "accounts": [ AccountConfig ] }`。
- 错误：401。

#### `PUT /v1/config/global`
- 用途：替换全局设置（版本号自增）。
- 认证：管理员。
- 请求体（`PutGlobalConfigRequest`）：
  - `settings`—— object（开放映射），可选（null → 空映射）
  - `updatedBy`—— string，可选
- 200：`GlobalConfig`。
- 错误：401。审计：`config.global.updated`。

#### `PUT /v1/config/account/{name}`
- 用途：替换单个账号的设置（enabled、region、labels、settings）；版本号自增。
- 认证：管理员。
- 路由参数：`{name}`—— 必填且不能是空白（全空白 → 400 `account name is required`）。
- 请求体（`PutAccountConfigRequest`）：
  - `enabled`—— bool，默认`true`
  - `region`—— string，可选
  - `labels`—— string[]，可选
  - `settings`—— object，可选
  - `updatedBy`—— string，可选
- 200：`AccountConfig`。
- 错误：400、401。审计：`config.account.updated`。

---

### 4.9 采集

`CrawlPlan = { id, name, appIds: uint[], accounts?: string[], overrides?: { "<appId>": "<account>" }, shardSize, intervalMs, cc, cron?, intervalSeconds, enabled, createdAt, updatedAt, runCount, lastRunId?, lastRunAt?, nextRunAt? }`;
`CrawlRunSummary = { runId, planId, startedAt, finishedAt?, total, ok, failed }`;
`CrawlResultRow = { id: long, planId, runId, appId: uint, account, jobId?, ok, error?, data?, fetchedAt }`.

#### `GET /v1/crawl/plans`
- 用途：列出全部采集计划。
- 认证：管理员。请求体：无。
- 200：`{ "plans": [ CrawlPlan ] }`。错误：401。

#### `POST /v1/crawl/plans`
- 用途：创建一个采集计划（默认单次执行；通过`cron`或`intervalSeconds`转为循环；`startNow=false`表示启用循环排程但不立即跑第一次）。
- 认证：管理员。
- 请求体（`CreateCrawlPlanRequest`，除特别注明外全部可选 —— 由`ValidateCrawlPlanRequest`校验）：
  - `name`：string，**必填**（否则 400 `name is required`）
  - `appIds`：应用 id 的 string[]，**必填且非空**（`app_ids is required`）；最多`CRAWL_MAX_APPS_PER_PLAN`（默认 500）项；每项必须能解析为 uint > 0
  - `accounts`：已声明账号名的 string[]，可选（名字未知 → 400 `unknown account '<name>'`）
  - `overrides`：`"<appId uint>" → "<accountName>"`形式的映射，可选（键非法或账号未知 → 400）
  - `shardSize`：int，可选（1–200 → `shard_size must be between 1 and 200`）
  - `intervalMs`：int，可选（0–60000）
  - `cc`：string，可选（国家代码；留空时默认`"us"`）
  - `cron`：string，可选（5 字段，UTC；按任务调度器的那套规则校验）
  - `intervalSeconds`：int，可选
  - `startNow`：bool，可选（单次执行总是立即跑；循环执行认`false`）
  - `enabled`：bool，可选，默认`true`
- 202：`Location: /v1/crawl/plans/{id}`，请求体 = `CrawlPlan`。
- 错误：400、401。审计：`crawl.plan.created`。

#### `GET /v1/crawl/plans/{id}`
- 用途：读取一个采集计划。
- 认证：管理员。请求体：无。
- 200：`CrawlPlan`（裸对象，无外层包装）。错误：404 `{ "error": "crawl plan not found" }`、401。

#### `PUT /v1/crawl/plans/{id}`
- 用途：更新采集计划；未给出的字段保持原值（合并语义）；`startNow=true`会立刻重新设定下一次运行。
- 认证：管理员。
- 请求体（`UpdateCrawlPlanRequest`）：与`CreateCrawlPlanRequest`字段完全相同，全部可选。
- 200：`CrawlPlan`。错误：400、404、401。审计：`crawl.plan.updated`。

#### `DELETE /v1/crawl/plans/{id}`
- 用途：删除一个采集计划（已持久化的结果保留）。
- 认证：管理员。请求体：无。
- 204：空。错误：404、401。审计：`crawl.plan.deleted`（`resultsKept: true`）。

#### `POST /v1/crawl/plans/{id}/trigger`
- 用途：立即触发一个采集计划（把下一次运行设为此刻；重叠的运行会被 worker 跳过）。
- 认证：管理员。请求体：无。
- 202：`Location: /v1/crawl/plans/{id}`，请求体 = `CrawlPlan`。
- 错误：400 `{ "error": "crawl plan is disabled" }`、404、401。审计：`crawl.plan.triggered`。

#### `GET /v1/crawl/plans/{id}/runs`
- 用途：列出某个计划最近的运行（聚合结果，最新在前）。
- 认证：管理员。
- 查询参数：`limit`（int，默认 20，钳制到 1–100）。
- 200：`{ "runs": [ CrawlRunSummary ] }`。错误：404、401。

#### `GET /v1/crawl/results`
- 用途：查询已持久化的按应用采集结果。
- 认证：管理员。
- 查询参数：`planId`（string，可选）、`runId`（string，可选）、`appId`（long，可选，> 0）、`account`（string，可选）、`ok`（bool，可选）、`limit`（int，默认 100，钳制到 1–500）、`offset`（int，默认 0）。
- 200：`{ "results": [ CrawlResultRow ], "total": int, "limit": int, "offset": int }`。
- 错误：401。

---

### 4.10 插件

`PluginIndexEntry = { id, name, version, apiVersion, url, sha256, description?, trust?, publisher?, permissions: string[], signing?: { publicKey, signature } }`（只有索引把两半都写出来时才会声明`signing`—— 只声明一半的块会被丢弃，绝不会被当作半截的信任指令下发）；
`PluginInventoryEntry = { id, name, version, apiVersion, trust?, publisher?, permissions: string[], actions: string[], resources: string[] }`（`resources`镜像清单里仅作提示的运行时资源声明，未声明时为空；`publisher`镜像清单声明的发布者标签，未声明时不出现）。

#### `GET /v1/plugins/catalog`
- 用途：浏览插件索引源（缓存 60 秒）。
- 认证：管理员。请求体：无。
- 200（索引未配置）：`{ "configured": false, "entries": [] }`。
- 200（已配置）：`{ "configured": true, "source": "<url>", "fetchedAt": <ts>, "error"?: string, "entries": [ PluginIndexEntry ] }`。
- 错误：401。

#### `GET /v1/plugins/installed`
- 用途：每个 Agent 最近一次上报的插件清单（镜像自`plugin_*`子任务的输出）。
- 认证：管理员。请求体：无。
- 200：`{ "agents": [ { "agentId", "reportedAt", "plugins": [ PluginInventoryEntry ], "updates": [ PluginUpdateStatus ]? } ] }`（按`agentId`排序；`PluginUpdateStatus = { id, catalogVersion, installedVersion?, status: "updateAvailable"|"upToDate"|"notComparable"|"notInstalled", publisherChanged?: true }`—— 只有当已安装清单与目录候选都声明了发布者、且两者忽略大小写后仍然不同时，`publisherChanged`才会出现；Agent 上次跑过`plugin_update_check`之后才有它）。
- 错误：401。

#### `POST /v1/plugins/install`
- 用途：向指定 Agent 派发插件安装（每个 Agent 一个`plugin_install`任务；目录模式按`pluginId`从索引解析出`url`/`sha256`）。
- 认证：管理员。
- 请求体（`PluginInstallRequest`）：
  - `agentIds`：string[]，**必填且非空**（否则 400 `agentIds must name at least one agent`）
  - `pluginId`：string，可选（目录模式的键）
  - `url`：string，可选（直连模式）
  - `sha256`：string，可选；直连模式下必填，须为 64 位十六进制摘要（否则 400 `sha256 must be a 64-character hex digest of the package`）
  - `version`：string，可选
  - 规则：`url`与`pluginId`都不给 → 400 `either url or pluginId is required`；`pluginId`不在目录中 → 404 `plugin '<id>' is not in the catalog`。
  - 目录收敛：目录模式把索引条目自己声明的`trust`/`permissions`/`apiVersion`/`publisher`以`expectedTrust`/`expectedPermissions`/`expectedApiVersion`/`expectedPublisher`写进子任务载荷（只写条目实际声明的字段；直连模式一个都不写）。Agent 在热加载前拿它们与包内嵌清单收敛 —— 任何不一致都会让安装失败，并给出一条聚合错误，点名全部对不上的字段（`package manifest does not match the catalog entry: …`），索引宣传的元数据就这样和内容一起被核验。已声明的`signing`块以两半的形式下发（`expectedSigningPublicKey`/`expectedSigningSignature`），由 Agent 在校验和通过之后、zip 解包之前，对照自己钉定的密钥（`VAPOR_PLUGIN_TRUST_KEYS`）核验；一个密钥都没钉定时，已声明的签名会失败关闭，而不是跳过不验。
- 202：`Location: /v1/plugins/installed`，请求体`{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`。
- 错误：400、404、401。审计：`plugin_install_dispatched`（details 带`signed`，只有解析到的目录条目声明了 signing 块时才为 true）。

#### `POST /v1/plugins/uninstall/{pluginId}`
- 用途：向指定 Agent 派发插件卸载（每个 Agent 一个`plugin_uninstall`任务）。
- 认证：管理员。
- 路由参数：`{pluginId}`—— 会去掉首尾空白；为空 → 400 `pluginId is required`。
- 请求体（`PluginUninstallRequest`），其中`agentIds`：string[]，**必填且非空**（否则 400 `agentIds must name at least one agent`）。
- 202：`Location: /v1/plugins/installed`，请求体`{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`。
- 错误：400、401。审计：`plugin_uninstall_dispatched`。

#### `POST /v1/plugins/inventory/refresh`
- 用途：在指定（或全部已连接）Agent 上重跑`plugin_list`，重新同步清单镜像；不在目标范围内的 Agent，其过期镜像条目会被丢弃。
- 认证：管理员。
- 请求体（`RefreshInventoryRequest`，可选 —— 可以整个省略），其中`agentIds`：string[]，可选（默认：全部已连接的 Agent）。
- 202：`Location: /v1/plugins/installed`，请求体`{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`。
- 错误：409 `{ "error": "no connected agents to refresh" }`、401。

#### `POST /v1/plugins/update-check`
- 用途：向指定（或全部已连接）Agent 派发`plugin_update_check`—— 每个 Agent 拿整个索引与自身已加载的插件逐一比对，为每个候选给一个结论；不会下载或替换任何东西。声明的发布者与已安装清单不一致的候选（双方都必须声明了发布者），结论里还会额外冒出`publisherChanged: true`。
- 认证：管理员。
- 请求体（`PluginUpdateCheckRequest`，可选 —— 可以整个省略），其中`agentIds`：string[]，可选（默认：全部已连接的 Agent）。
- 错误：400 `{ "error": "plugin index is not configured (set Vapor_PLUGIN_INDEX_URL)" }`、409 `{ "error": "no connected agents to check" }`、401。
- 202：`Location: /v1/plugins/installed`，请求体`{ "jobs": [ { "agentId", "jobId", "taskId" } ] }`。审计：`plugin_update_check_dispatched`。

---

### 4.11 脚本

`ScriptRecord = { id, name, description, language, content, createdAtMs, updatedAtMs }`—— 控制平面的脚本仓库（SQLite，路径`Vapor_SCRIPT_DB_PATH`，默认`data/scripts.db`）。它只为 CRUD 接口提供存储：身份、元数据和字节都归仓库管；执行走下面那个专用派发端点。

#### `GET /v1/scripts`
- 用途：列出脚本仓库（按名称排序）。
- 认证：管理员。请求体：无。
- 200：`{ "scripts": [ ScriptRecord ] }`。错误：401。

#### `GET /v1/scripts/{id}`
- 用途：取一个脚本连同它的内容。
- 认证：管理员。请求体：无。
- 200：`ScriptRecord`。错误：404 `{ "error": "script '<id>' does not exist" }`、401。

#### `POST /v1/scripts`
- 用途：存入一个新脚本。
- 认证：管理员。
- 请求体（`UpsertScriptRequest`）：
  - `name`：string，**必填且非空白**（否则 400 `name is required`）
  - `description`：string，可选（默认`""`）
  - `language`：string，可选；去首尾空白并转小写（默认`"shell"`）
  - `content`：string，**必填且非空白**（否则 400 `content is required`）
- 201：`ScriptRecord`，`Location: /v1/scripts/{id}`。错误：400、401。审计：`script_created`（`scriptId`、`name`、`language`、`bytes`）。

#### `PUT /v1/scripts/{id}`
- 用途：替换一个已存脚本（整体更新；创建时间戳保留）。
- 认证：管理员。
- 请求体（`UpsertScriptRequest`）：字段规则同`POST /v1/scripts`。
- 规则：id 未知 → 404（在校验之前就检查）。
- 200：`ScriptRecord`。错误：400、404、401。审计：`script_updated`（`scriptId`、`name`、`language`、`bytes`）。

#### `DELETE /v1/scripts/{id}`
- 用途：删除一个已存脚本。
- 认证：管理员。请求体：无。
- 204：空。错误：404 `{ "error": "script '<id>' does not exist" }`、401。审计：`script_deleted`（`scriptId`）。

#### `POST /v1/scripts/{id}/execute`
- 用途：经`script_exec`宿主动作把一个已存脚本派发到某个已连接的 Agent（目标是以宿主为作用域的`agent:{id}`；分级为`NonIdempotent`，所以派发封顶 2 次尝试）。
- 认证：管理员。
- 请求体（`ExecuteScriptRequest`）：
  - `agentId`：string，**必填**（以宿主为作用域的派发只认一台机器；空白 → 400）
  - `timeoutSeconds`：number，可选（1–300；越界 → 400；Agent 侧默认 120）
- 规则：脚本未知 → 还没碰载荷就 404；校验错误排在连通性检查之前；Agent 未知或未连接 → 404 `agent '<id>' is not connected; connect it and retry`。
- 200：`{ "job_id", "script", "result" }`—— 子任务在等待窗口内完成时的`script_exec`输出（`exitCode`、`timedOut`、带截断标记的`stdout`/`stderr`）。
- 202：`{ "job_id": "...", "status": "pending" }`，`Location: /v1/jobs/{job_id}`—— 等待窗口内 Agent 还没回报；轮询任务端点（§2）直到终态。
- 错误：子任务失败时 502 `{ "job_id", "error" }`、400、404、401。审计：`script_exec_dispatched`（`scriptId`、`name`、`agentId`、`bytes`、`outcome`、job id）。

---

### 4.12 脚本流

对脚本仓库的有序编排：一个脚本流是一串步骤，每步把一个已存脚本、一个目标 Agent 和一条失败策略配成一对。运行时控制平面按顺序走完各步，每步经`script_exec`宿主动作派发脚本，并持久化每一步的状态。

`ScriptFlowRecord = { id, name, description, steps, createdAtMs, updatedAtMs, schedule?, nextRunAtMs? }`，其中`steps: [ { scriptId, agentId, onFailure } ]`（`onFailure`：`"stop"`表示这一步失败就中止整次运行，`"continue"`表示继续往下走；默认`"stop"`）。`schedule`是可选的循环排程 —— 和定时任务模板用的是同一种`JobSchedule`形状（§4.5）；一旦设置，脚本流的循环调度器就按该计划触发整条流，以`nextRunAtMs`作到期游标（运行以审计身份`scheduler`执行，missed/overlap 策略的表现与循环任务调度器完全一致）。不设 schedule 就意味着这条流只能走手动运行入口；这两个字段在缺失时不出现在 JSON 里。`ScriptFlowRunRecord = { id, flowId, flowName, status, steps, startedAtMs, finishedAtMs? }`；运行状态为`completed`（每步都成功）、`completed_with_failures`（带着失败继续走完）、`failed`（被 stop 策略的失败中止）或`pending`（某一步的任务在等待窗口内始终没落定 —— 该任务仍可在任务接口上轮询）。每个运行步骤是`{ index, scriptId, agentId, status, jobId?, error?, output? }`，步骤状态为`succeeded`、`failed`、`pending`或`skipped`（运行在走到它之前就中止了，所以根本没尝试）。运行与脚本仓库共用同一个数据库；删除一条流，它的运行记录作为历史留下。

#### `GET /v1/script-flows`
- 用途：列出脚本流（按名称排序）。
- 认证：管理员。请求体：无。
- 200：`{ "flows": [ ScriptFlowRecord ] }`。错误：401。

#### `GET /v1/script-flows/{id}`
- 用途：取一条脚本流连同它有序的步骤。
- 认证：管理员。请求体：无。
- 200：`ScriptFlowRecord`。错误：404 `{ "error": "flow '<id>' does not exist" }`、401。

#### `POST /v1/script-flows`
- 用途：存入一条新脚本流。
- 认证：管理员。
- 请求体（`UpsertFlowRequest`）：
  - `name`：string，**必填且非空白**（否则 400 `name is required`）
  - `description`：string，可选（默认`""`）
  - `steps`：数组，**必填且 1–50 项**（否则 400 `at least one step is required` / `a flow supports at most 50 steps`）；每一项都要有非空白的`scriptId`与`agentId`（否则 400 `every step needs a scriptId` / `every step needs an agentId`），可选的`onFailure`会去首尾空白并转小写（默认`"stop"`；填别的 → 400 `onFailure must be 'stop' or 'continue'`）
  - `schedule`：`JobSchedule`，可选；节奏非法 → 400 并带上`ScheduleClock.Validate`的消息，cron 再也不会匹配 → 400 `schedule cron never matches again`（cron 已死的已布防流永远不会触发）。第一个触发点在写入时算出，并以`nextRunAtMs`返回。
- 规则：形状在写入时校验；步骤的前置条件（脚本存在、Agent 已连接）在运行开始时才检查。
- 201：`ScriptFlowRecord`，`Location: /v1/script-flows/{id}`。错误：400、401。审计：`flow_created`（`flowId`、`name`、`steps`、`scheduled`）。

#### `PUT /v1/script-flows/{id}`
- 用途：替换一条脚本流（整体更新；创建时间戳保留）。
- 认证：管理员。
- 请求体（`UpsertFlowRequest`）：字段规则同`POST /v1/script-flows`。
- 规则：id 未知 → 404（在校验之前就检查）。PUT 替换的是整份定义，排程也在其中：带上`schedule`就从此刻重新设定游标；不带则把脚本流退回纯手动（此时响应里不含那两个排程字段）。
- 200：`ScriptFlowRecord`。错误：400、404、401。审计：`flow_updated`（`flowId`、`name`、`steps`、`scheduled`）。

#### `DELETE /v1/script-flows/{id}`
- 用途：删除一条脚本流定义（过往的运行记录保留）。
- 认证：管理员。请求体：无。
- 204：空。错误：404 `{ "error": "flow '<id>' does not exist" }`、401。审计：`flow_deleted`（`flowId`）。

#### `POST /v1/script-flows/{id}/run`
- 用途：运行一条脚本流 —— 按顺序走各步，每步经`script_exec`把已存脚本派发到它的目标 Agent，等待窗口与单脚本执行入口相同。
- 认证：管理员。请求体：无。
- 规则：脚本流未知 → 任何东西开始跑之前就 404。预检先看每一步 —— 一次运行要么在前置条件全部满足之后才起步，要么干脆不起步；每一步先查脚本是否存在，再查 Agent 是否连通（`step <i> references script '<id>' which does not exist` / `step <i> targets agent '<id>' which is not connected; connect it and retry`，两者都是 404，且都发生在碰到任何机器之前、运行记录存在之前）。
- 语义：每一步都走`script_exec`派发（以宿主为作用域的`agent:{id}`目标，`NonIdempotent`封顶）。策略为`stop`的步骤失败即终止本次运行（其余步骤`skipped`）；策略为`continue`则继续往下走（出现过失败就记为`completed_with_failures`）。某一步的任务若在等待窗口内始终没落定，本次运行以`pending`结束 —— 后面的步骤不能乱序执行，因此被跳过；那个未落定的任务仍可在任务接口上轮询。
- 200：终态的`ScriptFlowRunRecord`（执行器就在这个请求里）。错误：404、401。审计：`flow_run_started`（`flowId`、`flowName`、`runId`、`steps`）、每个已执行步骤各一条`flow_step_completed` / `flow_step_failed` / `flow_step_pending`（`runId`、`index`、`scriptId`、`agentId`、job id、失败时带`error`），以及`flow_run_finished`（`runId`、`status`、`succeeded`/`failed`/`pending`/`skipped`计数）。

#### `GET /v1/script-flows/{id}/runs`
- 用途：列出某条脚本流的运行（最近一次在前）。
- 认证：管理员。请求体：无。
- 200：`{ "runs": [ ScriptFlowRunRecord ] }`。错误：401。

#### `GET /v1/script-flows/runs/{runId}`
- 用途：取一次运行连同它逐步的状态。
- 认证：管理员。请求体：无。
- 200：`ScriptFlowRunRecord`。错误：404 `{ "error": "flow run '<runId>' does not exist" }`、401。

---

### 4.13 系统（公开）

#### `GET /`
- 用途：302 重定向到`/admin.html`（管理界面）。
- 认证：无。请求体：无。302。

#### `GET /healthz`
- 用途：存活探针（公开，免认证）。
- 认证：无。请求体：无。
- 200：`{ "ok": true }`。

#### `GET /metrics`
- 用途：Prometheus 抓取端点（公开；防护要放在网络层）。计数器/仪表：`vapor_controlplane_tasks_by_status`、`vapor_controlplane_agents_connected`、`vapor_controlplane_dispatch_failures_total{reason=...}`、`vapor_controlplane_accounts_by_desired_state{state=...}`、`vapor_controlplane_reconcile_actions_total{action=...}`、`vapor_controlplane_schedule_triggers_total{outcome=...}`、`vapor_controlplane_crawl_runs_total` / `crawl_apps_total` / `crawl_tasks_dispatched` / `crawl_overlap_skips`、`vapor_controlplane_notifications_total{sink,outcome}` / `vapor_controlplane_notification_retries_total{sink}`。
- 认证：无。请求体：无。
- 200：`text/plain; version=0.0.4; charset=utf-8`（Prometheus 文本格式）。

#### `GET /v1/system/status`
- 用途：聚合的内部状态视图 —— 一份只读报告，把控制平面自身健康、代理探测历史、已连接 Agent 以及账号的期望状态与实际状态合到一起。仪表盘的 "内部状态总览" 面板渲染的就是这份报告。
- 认证：管理员。请求体：无。
- 200：`SystemStatusReport`：
  - `overall`：`{ status: "healthy"|"degraded"|"unhealthy", reasons: string[] }`。任务存储不可达时为 unhealthy；上一次收敛失败、已声明账号存在却没有 Agent 在线、或仍有认证挑战待处理时为 degraded；其余为 healthy。
  - `controlPlane`：
    - `db`：`{ available: bool, latencyMs: long, error?: string }`；一次`GetTaskStatusCounts`往返兼作存活探针，它的墙钟时间就是这里的时延。
    - `jobs`：按状态分的子任务数量：`{ queued, running, finished, failed, canceled }`。
    - `scheduler`：`{ lastTickAt?: timestamp, dispatchNoCapableAgent, dispatchEnqueueFailed, dispatchAttemptsExhausted }`；`lastTickAt`是派发循环的心跳（第一次 tick 之前不出现）。
    - `reconciler`：`{ lastPassAt?: timestamp, lastPassDurationMs?: long, lastPassFailed: bool, loginsDispatched, playsDispatched, cardDropsDispatched, playtimesDispatched, tradeAcceptsDispatched, rebalances, unassignments, throttledSkips, noAgentSkips, dryRunDeviations }`。
    - `recurringJobs`：`{ triggered, overlapSkipped, missedDropped, missedCatchUps }`。
    - `plugins`：`{ agentsReporting, entries, byTrust: { "<trust>": count } }`。
  - `proxies`：`{ probesOk, probesFailed, proxyDisabled, recent: [ { account, success, proxy?, exitIp?, latencyMs?, error?, checkedAt } ] }`。由最近的`check_proxy`子任务输出聚合而来（最近 100 个任务）。控制平面没有代理池注册表 —— 代理是每账号的 Agent 侧配置；`proxy`值如果出现，就是 Agent 侧脱敏后的端点。代理已禁用的、以及尚未跑完的任务都算进这些计数，但不会在明细里逐条列出。
  - `agents`：`{ connected: int, regions: string[], entries: [ { id, region, connectedAt, capabilities } ] }`。
  - `accounts`：`{ total, byDesiredState: { "<state>": count }, mismatches: [ { account, desiredState, actualSessionState?, assignedAgent?, reason? } ], sessionsTracked, pendingChallenges, challengeTypes: { "<type>": count } }`。`reason`带上收敛器对该账号最近一次的收敛决策（还没有任何一轮记录过时缺席）。所谓不一致，是指声明的期望状态与最新的会话快照对不上（快照缺失按"没有会话"算；`unknown`的会话状态绝不会被当作会话活着的正面证据）。
  - 脱敏：凭据、代理密码（Agent 侧已掩码）与 Steam Guard / 2FA 挑战**验证码**绝不出现 —— 挑战只贡献计数。
- 错误：401。

---

### 4.14 故障（故障注入演练）

用于韧性演练的运行时故障注入（路线图 §8）。四个端点都只对管理员开放，而且自身豁免于 api-request 注入 —— 演练总能被观察、被停掉。每个已布防的故障都有两道边界：一个**预算**（还能注入几次；用尽后故障自行移除）和一个**TTL**（`expiresAt`，1..3600 秒，默认 900）—— 被人忘掉的演练会自愈。状态在内存里：控制平面一重启，一切解除布防（这是刻意为之）。审计动作：`faults.enable`、`faults.disable`、`faults.clear`。指标：`vapor_controlplane_fault_injections_total{kind,mode}`计数器与`vapor_controlplane_faults_armed`仪表。

两个平面，由`kind`决定：

- `task-dispatch`：在调度器认领子任务之后、挑选 Agent 之前注入。选择器：`action`（精确匹配子任务动作）、`region`（精确匹配区域）；为 null 或省略即不限。error 模式复用真实的不可派发路径（重新入队加重试，发出`task.dispatch_failed`事件，错误为`fault injection <id>: dispatch failed`）；delay 模式以`max(configured retry delay, delayMs)`重新入队，错误为`fault injection <id>: dispatch delayed by <N>ms`。
- `api-request`：只对`/v1`路由在边缘中间件里注入（`/v1/faults`、`/healthz`、`/metrics`豁免）。选择器：`route`（对路由模式做忽略大小写的子串匹配 ——`"/v1/jobs"`同时匹配`/v1/jobs`与`/v1/jobs/{jobId}`）、`method`（大小写随意）；为 null 即不限。error 模式返回配置的状态码，响应体为`{ "error": "fault injection <id>" }`（和其他响应一样记进 RED 系列）；delay 模式额外加上`delayMs`的时延 —— 而且时间戳取自注入点之前，所以被撑大的耗时会计入`vapor_controlplane_http_request_duration_seconds_sum`。

与平面不匹配的选择器一律拒绝（400）：给`task-dispatch`配`route`/`method`，给`api-request`配`action`/`region`—— 一个永远不可能触发的已布防故障是演练失败，不是特性。

#### `GET /v1/faults`
- 用途：列出已布防的故障注入。
- 认证：管理员。请求体：无。
- 200：`FaultView[]`（没有已布防故障时为空数组）：

```json
[{
  "id": "3f0c…32hex",
  "kind": "api-request",
  "mode": "error",
  "action": null, "region": null,
  "route": "/v1/agents", "method": null,
  "httpStatus": 503,
  "delayMs": 1000,
  "budgetRemaining": 1, "fired": 0,
  "createdAt": "…", "expiresAt": "…"
}]
```

#### `POST /v1/faults`
- 用途：布防一个故障注入。
- 认证：管理员。请求体（`*`为必填）：

```json
{ "kind": "api-request", "mode": "error",
  "action": null, "region": null, "route": "/v1/agents", "method": null,
  "httpStatus": 503, "delayMs": 1000, "budget": 1, "ttlSeconds": 900 }
```

- `kind`*：`"task-dispatch"`或`"api-request"`（忽略大小写）。
- `mode`*：`"error"`或`"delay"`（忽略大小写）。
- 选择器 —— 按平面区分，见上文；为 null 或省略即不限；只有空白等同 null。
- `httpStatus`：400..599（默认 500），仅 error 模式。
- `delayMs`：1..60000（默认 1000），仅 delay 模式。
- `budget`：1..1000（默认 1；每次触发消耗一个，归零后自动移除）。
- `ttlSeconds`：1..3600（默认 900）。
- 201：已布防的`FaultView`（与列表项同形），`Location: /v1/faults/{id}`。
- 错误：400（kind / mode / 边界 / 选择器非法）、401。

#### `DELETE /v1/faults/{faultId}`
- 用途：解除一个故障注入的布防。
- 认证：管理员。请求体：无。
- 200：空响应体。错误：404（`fault <id> is not armed`—— 从未布防、已经解除，或被预算/TTL 自行移除）、401。

#### `DELETE /v1/faults`
- 用途：解除所有故障注入的布防 —— 那个"急停按钮"。
- 认证：管理员。请求体：无。
- 200：`{ "removed": <int> }`（原本布防了多少个）。错误：401。

---

### 4.15 通知（webhook 投递日志）

webhook 投递端把每次投递尝试都记进一个只追加的 SQLite 日志（`Vapor_WEBHOOK_DB_PATH`，默认`data/webhook.db`）。记录是尽力而为的：日志损坏或写满都绝不阻塞投递 —— 写入失败会被吞掉并记进日志。这份日志是"发生过什么"的审计线索，不是重放队列：没送出去的通知在重启后就丢了。

#### `GET /v1/notifications/deliveries`
- 用途：查询 webhook 投递日志 —— 每次尝试一行，所以一条重试后才送达的通知会先看到它的失败尝试，紧跟着是送达那条。
- 认证：管理员。
- 查询参数：`notificationId`（string，可选）、`outcome`（string，可选；`delivered`或`failed`）、`limit`（int，默认 100，钳制到 1–500）、`offset`（int，默认 0，钳制到 ≥ 0）。
- 200：`{ "deliveries": [ WebhookDeliveryRecord ], "total": int, "limit": int, "offset": int }`——`WebhookDeliveryRecord = { notificationId, category, type, jobId?, accountName?, attempt, outcome, statusCode?, error?, attemptedAtMs }`（为 null 的字段不出现；按`attemptedAtMs`最新在前；`attempt`是从 1 开始的尝试序号，`statusCode`是 HTTP 层失败时的 HTTP 状态码，传输层错误时为 null）。
- 错误：401。

---

## 5. 审计动作参考（由这些端点写入）

`account.spec.updated`、`account.spec.removed`、`account.enabled`、`account.disabled`、`account.loot`、`account.proxy_set`、`account.proxy_check`、`account.add_license`、`storage.collect`、`standing_check_requested`、`trade_offers.read`、`trade_offer.accept`、`trade_offer.decline`、`trade_offer.confirm`、`trade.swap_offer`、`trade_confirmations.accept_all`、`inventory.read`、`inventory.duplicates`、`achievement.read`、`achievement.unlock`、`achievement.reset`、`market_listings.read`、`market_listings.create`、`market_listings.cancel`、`points_shop.summary`、`points_shop.claim`、`job.created`、`job.canceled`、`session.event.received`、`session.login`、`auth.code.submitted`、`config.global.updated`、`config.account.updated`、`plugin_install_dispatched`、`plugin_uninstall_dispatched`、`crawl.plan.created`、`crawl.plan.updated`、`crawl.plan.deleted`、`crawl.plan.triggered`，外加任务结果审计（敏感动作会写`task.result.reported`：`SendTradeOffer*`、`AcceptTradeOffer*`、`DeclineTradeOffer*`、`CancelTradeOffer*`、`GetInventory*`、`RedeemKey*`），以及来自 worker 的采集运行审计。

Actor 优先取`X-Forwarded-For`头，没有则取远端 IP；details 在落库前先脱敏（`SensitiveDataRedactor`）。
