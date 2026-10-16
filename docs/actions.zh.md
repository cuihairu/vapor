简体中文 | [English](../actions/)

# 动作目录

动作是 Agent 执行的工作单位：`POST /v1/jobs` 指定一个动作、一个目标集合和一个可选的 `payload`，每个目标账号（或 Agent）在自己的会话上运行它。本目录文档化全部 **65 个内置动作** —— 20 个来自 Steam.Core 程序集的宿主动作、16 个来自 Game Access 插件、9 个来自 Mobile Authenticator 插件、3 个来自 Market Watch、1 个来自 Monitoring、4 个来自 Game Data、3 个来自 CS2/CSGO 插件、3 个来自 Dota 2 插件，以及 6 个 Agent 宿主动作 —— 连同它们的 payload 键与输出字典，均按实现原样抄录。

这件事的 HTTP 侧（任务信封、派发、调度、经 REST/SSE 读结果）在 [api.md](api.md) 的 "Jobs & tasks" 一节；导览见 [getting-started.md](getting-started.md)。下面的 payload 示例针对 `POST /v1/jobs` 请求体。

源码地图，供参考：宿主 Steam 域动作位于 `src/Vapor.Steam.Core/Actions/*.cs`，游戏访问动作位于官方 `src/Vapor.Plugins.GameAccess/` 插件（打进 Agent 镜像；名称相同），Agent 宿主动作位于 `src/Vapor.Agent/HostActions/*.cs`，其余插件动作位于 `src/Vapor.Plugins.*`，任务信封位于 `src/Vapor.ControlPlane/Program.cs` + `src/Vapor.Protocol/Models.cs`。下面的字段名从 payload 解析 / 输出构建代码中原样抄录。

## 约定

- Payload 键命名：控制平面信封是 camelCase（`action`、`payload`、`targets`、…），但 `payload` *内部的键* 在动作代码中写作 **snake_case**（`app_id`、`steam_id`、`items_to_give`、…）。多数查找经由 `PayloadReader`，它**大小写不敏感**地匹配键，因此 `appIds` 也能解析到 `app_id`。例外：`send_trade_offer` 的 `items_to_give` / `items_to_receive`（及其嵌套字段）以精确、**大小写敏感**的字典查找读取。本目录按代码原样记录确切的键。
- 输出键命名：输出字典的键是字面量（多为 snake_case；少数动作用 camelCase 键 —— 逐动作注明）。内嵌 .NET 模型对象的值（如 `GameInfo`、`PriceOverview`）按标准 camelCase JSON 策略序列化。
- Payload 值类型：经 SQLite/WebSocket 往返后，payload 值以 `JsonElement` 到达；除另有说明外，每个解析器都接受数字、字符串、数组或单个裸值。
- 元数据：每个动作声明 `RequiresLogin` 与 `TimeoutSeconds`；记作 `login: yes/no`、`timeout: Ns`。`login: yes` 动作需要该账号的会话已登录；把它们派发给离线账号会以登录错误使子任务失败。
- 执行安全：每个动作声明一个 `ActionSafety` 分级（每个标题行记作 `safety:`）—— `ReadOnly`（从不改动任何东西）、`Idempotent`（重复执行收敛到同一状态）、`GuardedWrite`（有改动，但有界的重试是安全的）、`NonIdempotent`（重复执行使外部副作用翻倍）。真相来源是每个动作旁的 `IAction.Metadata.Safety` 注解；控制平面在 `ActionSemantics.cs` 中镜像它并据此限制再派发：`GuardedWrite`/`NonIdempotent`（以及未分级/`Unknown` 的动作，例如第三方插件）无论配置上限如何都把派发尝试封顶为 2，而 `ReadOnly`/`Idempotent` 动作原样使用配置上限。三个面由 `scripts/verify-actions-safety.py` 保持一致。

---

## 通用任务调用信封

创建任务 —— `POST /v1/jobs`（管理员认证头；`src/Vapor.Protocol/Models.cs` 中的 `CreateJobRequest`）：

```json
{
  "action": "get_inventory",
  "region": "eu",
  "targets": ["accountA", "accountB"],
  "payload": { "app_id": "730", "tradable_only": true },
  "meta": { "requested_by": "ops" },
  "schedule": { "intervalSeconds": 300, "cron": "*/5 * * * *", "missed": "skip", "overlap": "skip" }
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `action` | string | 是 | 确切的动作名（见下方目录）；留空 → 400。 |
| `targets` | string[] | 是（≥1） | **每个目标**扇出一个子任务。目标通常是**账号名**；宿主动作则是机器选择器 `"agent:{agentId}"`。 |
| `region` | string? | 否 | 省略时为空字符串；调度器只派发给该区域内的 Agent。 |
| `payload` | object? | 否 | 交给动作的自由形态字典；键见下方各动作。 |
| `meta` | object? | 否 | 与任务一起存储的字符串值元数据。 |
| `schedule` | object? | 否 | 出现时任务成为**循环模板**（状态 `scheduled`）：`intervalSeconds` 或 `cron`（5 字段、UTC；cron 优先）。`missed`：`skip`（默认）| `runOnce`；`overlap`：`skip`（默认）| `allow`。 |

响应：`202 Accepted`，`Location: /v1/jobs/{id}`，体 `{ "job": { id, action, region, targets, meta, status, createdAt, updatedAt, schedule?, nextRunAt? } }`。状态枚举：`queued | running | scheduled | finished | failed | canceled`。

派发：`TaskSchedulerService` 每 250 ms 轮询一次；账号目标的子任务交给区域内已连接、且在 hello `capabilities` 中宣告了该动作的 Agent 中的最空闲者（运行中子任务最少，Agent id 决胜）；`agent:{id}` 目标交给该确切 Agent（做 capability 校验）。

读结果 —— `GET /v1/jobs/{jobId}` 返回 `JobWithTasks`：

```json
{
  "job": { "id": "...", "action": "...", "status": "finished", "...": "..." },
  "tasks": [
    {
      "id": "...", "jobId": "...", "target": "accountA", "action": "get_inventory",
      "region": "eu", "payload": { }, "status": "finished", "attempt": 1,
      "createdAt": "...", "updatedAt": "...",
      "error": null,
      "output": { "steam_id": "7656...", "items": [ ] }
    }
  ]
}
```

子任务的 `status`：`queued | running | finished | failed | canceled`；`output` 是下方文档化的动作结果字典；失败时 `error` 携带动作的错误字符串。配套端点：`GET /v1/jobs`（列表；`limit` 钳制 1–500，默认 50；`account` 过滤器）、`GET /v1/jobs/{jobId}/events`（SSE：`job.created`、`task.dispatched`、`task.*`）、`POST /v1/jobs/{jobId}/cancel`。

`/v1/accounts/{name}/...` 下的大多数 REST 包装都很薄：它们为其中一个动作构造 payload 并派发一个单目标任务，返回 `202 { job_id, status }`（或当它快速完成时内联返回子任务输出）。

## 注册表 / 命名空间说明

- `ActionRegistry`（`src/Vapor.Steam.Core/IAction.cs`）是一个单一的扁平、大小写不敏感字典。核心动作在 Agent 启动时注册；插件动作经 `PluginManager.PluginLoaded`（`actionRegistry.Register(action)`）注册进*同一个*字典，并在 `PluginUnloading` 时移除。
- 没有命名空间前缀。插件动作名（`market_watch_add`、`get_metrics`、…）与核心名（`get_inventory`、…）并排共存。冲突不被检测：`Name` 等于某核心动作名的插件动作会**静默覆盖**它（后写者胜出），且当插件卸载时，该名字被整体注销 —— 被遮蔽的核心动作不会回来。约定：插件给名字加前缀（`market_watch_*`、`get_metrics`），内置插件没有一个与核心名冲突。
- Agent 宿主动作（`plugin_install` / `plugin_uninstall` / `plugin_list` / `plugin_update_check` / `script_exec`）*不*在注册表里；它们位于一个单独的字典，在 Agent 的任务执行器中先于注册表被检查，并像其他动作一样在 hello capabilities 中宣告。它们额外要求任务目标恰好是 `agent:{thisAgentId}`（纵深防御 —— 错误路由的投递会响亮地失败，而不是改动错误机器的插件目录）。

---

# Steam.Core 动作（`Vapor.Steam.Core` 程序集，`src/Vapor.Steam.Core/Actions/`）


### `login` —— `LoginAction.cs`（login: no, timeout: 60s, safety: Idempotent）
把该账号的会话登录到 Steam。无 payload 字段。
输出：`account`、`state`、`action`。

### `echo` —— `EchoAction.cs`（login: no, timeout: 10s, safety: ReadOnly）
把 payload 原样回显。Payload：任意（直通）。
输出：`echo`（payload 字典）、`account`。

### `ping` —— `PingAction.cs`（login: no, timeout: 10s, safety: ReadOnly）
存活探针。无 payload 字段。
输出：`pong`（恒为 `true`）、`account`、`state`、`timestamp`（unix 秒）。

### `idle` —— `IdleAction.cs`（login: yes, timeout: 300s, safety: Idempotent）
模拟在线一段时间（立即返回；会话保持挂机）。
Payload：`duration` int，可选，默认 `60`（秒）。
输出：`action`（`"idle"`）、`duration`、`state`。


### `get_trade_offers` —— `GetTradeOffersAction.cs`（login: yes, timeout: 30s, safety: ReadOnly）
列出收到与发出的交易报价（IEconService）。交易循环的读侧。
Payload：`active_only` bool，可选，默认 `true`。
输出：`active_only`、`sent_count`、`received_count`、`sent_offers[]` / `received_offers[]` `{ trade_offer_id, partner_steam_id, is_our_offer, state, items_to_give_count, items_to_receive_count, time_created, items_to_give[], items_to_receive[], message?, time_expires? }`，其中每个资产条目为 `{ app_id, context_id, asset_id, class_id, instance_id, amount, is_currency }`。

### `send_trade_offer` —— `TradeOfferActions.cs`（`SendTradeOfferAction`）（login: yes, timeout: 60s, safety: NonIdempotent）
向另一个 Steam 用户发送交易报价。
Payload：
- `partner_steam_id` string，可选 —— 交易对方的 SteamID64（**它与 `trade_url` 二者必居其一**）。
- `trade_url` string，可选 —— 带 token 的交易 URL（让 Steam 投递给非好友）；对方 id 与 `token` 从中提取。
- `token` string，可选 —— 使用 `partner_steam_id` 时的交易 token。
- `message` string，可选 —— 报价留言。
- `items_to_give` array，可选 —— 条目 `{ app_id, context_id, asset_id, amount }`；每条默认：`app_id` 730、`context_id` 2、`amount` 1；`asset_id` 必填（缺失/为零 `asset_id` 的条目被静默丢弃）。此处的键按**大小写敏感**匹配（普通字典读取）。
- `items_to_receive` array，可选 —— 条目形状相同。
- `skip_verification` bool，可选（默认 `false`）—— **安全门**：跳过发送前的所有权核验（物品存在、可交易、不在冷却中、发送方库存数量充足）。
输出：`trade_offer_id`、`partner_steam_id`、`requires_mobile_confirmation`、`ownership_verified`（bool）。
安全：每账号**交易限流器**（`TradeRateLimiter` 租约；拒绝错误 "Trade rate limit exceeded for this account, try again later"）；核验分页封顶 50 页。

### `accept_trade_offer` —— `TradeOfferActions.cs`（`AcceptTradeOfferAction`）（login: yes, timeout: 30s, safety: GuardedWrite）
接受一份收到的交易报价。
Payload：`trade_offer_id` string **必填**（ulong）；`partner_steam_id` string **必填**（预期对方）；`verify_state` bool，可选，默认 `true` —— 设 `false` 可**绕过**状态机核验（报价 Active、收到而非发出、未过期、来自预期对方）。
输出：`trade_offer_id`、`requires_mobile_confirmation`、`state_verified`。限流同 `send_trade_offer`。

### `decline_trade_offer` —— `TradeOfferActions.cs`（`DeclineTradeOfferAction`）（login: yes, timeout: 30s, safety: GuardedWrite）
拒绝一份收到的报价。
Payload：`trade_offer_id` string **必填**；`verify_state` bool 可选默认 `true`（核验报价是收到的且 Active；`false` 绕过）。
输出：`trade_offer_id`、`state_verified`。限流。

### `cancel_trade_offer` —— `TradeOfferActions.cs`（`CancelTradeOfferAction`）（login: yes, timeout: 30s, safety: GuardedWrite）
取消该账号发出的一份报价。
Payload：`trade_offer_id` string **必填**；`verify_state` bool 可选默认 `true`（核验报价是我们发出的且仍 Active；`false` 绕过）。
输出：`trade_offer_id`、`state_verified`。限流。


### `get_my_market_listings` —— `GetMyMarketListingsAction.cs`（login: yes, timeout: 30s, safety: ReadOnly）
列出该账号自己的市场挂单（登录门控的 mylistings 页 —— 自有挂单 id 与手续费拆分的唯一来源）。每次派发一页；继续派发直到 `start` 达到 `total_count` 即完成翻页。
Payload：`start` int 可选默认 `0`；`count` int 可选默认 `100`。
输出：`start`、`count`、`total_count`、`active_count`、`on_hold_count`、`to_be_confirmed_count`、`listings[]` `{ listing_id, app_id, context_id, asset_id, class_id, market_hash_name, market_name, game_name, price_cents, fee_cents, seller_proceeds_cents, currency_id, icon_url, time_created, cancel_requested }`。
（区别于 `get_market_listings` —— 公开的按应用市场搜索。）

### `create_market_listing` —— `CreateMarketListingAction.cs`（login: yes, timeout: 120s, safety: NonIdempotent）
把一件库存物品挂上出售 —— 市场循环的 ToS 灰色地带核心，三重门控：
1. `send` 默认 `false` → **干运行**，只计算计入手续费的定价方案（对 Steam 零请求）。
2. 真实挂单额外要求本 Agent 显式 opt-in 的环境变量 `AGENT_MARKET_LISTINGS_ENABLED=true` —— 没有它，带 `send=true` 的直接派发会被拒绝。
3. 以及每账号开关（控制平面账号规格 `marketListingsEnabled=true`，由 `POST /v1/accounts/{name}/market/listings` 在派发前强制执行；默认关闭）。
Payload：
- `app_id` int **必填**（正数）。
- `context_id` string **必填**（如社区库存用 `"6"`）。
- `asset_id` string **必填**（来自 `get_inventory` / `get_my_market_listings` 输出）。
- `amount` int 可选默认 `1`（必须 ≥ 1）。
- `seller_proceeds_cents` int / `buyer_price_cents` int —— **恰好其一必填**（≥ 1）。买方价会被下探到 Steam 手续费模型允许的最大卖方所得；低于下限（3 分买方价）则失败。
- `send` bool 可选默认 `false` —— `true` 执行真实挂单。
输出（干运行）：`dry_run: true`、`would_list: true`、`asset` `{ app_id, context_id, asset_id, amount }`、`pricing` `{ seller_proceeds_cents, steam_fee_cents, publisher_fee_cents, buyer_price_cents }`。
输出（真实）：`dry_run: false`、`asset`、`pricing`、`success`、`requires_confirmation`、`needs_mobile_confirmation`、`needs_email_confirmation`、`message?`、`email_domain?`。
安全：价格只来自调用方（无自动重定价）；Steam 侧限流以子任务错误呈现，完整回复保留在输出中；需要手机/邮箱确认会被回报 —— 确认它是 `confirm_all_confirmations`（type `market`）的职责。

### `cancel_market_listings` —— `CancelMarketListingsAction.cs`（login: yes, timeout: 600s, safety: GuardedWrite）
按过滤器批量取消该账号自己的挂单；每条匹配挂单逐一尝试（单条失败不中止批次）。
Payload：
- `app_id` int 可选；`market_hash_name` string 可选；`min_price_cents` / `max_price_cents` int 可选（非负，min ≤ max）；`older_than_seconds` int 可选（>0）—— 只取消创建时间早于现在减去该值的挂单。
- `dry_run` bool 可选，**默认 `true`** —— 干运行只报告*将会*取消什么；真实运行（`dry_run=false`）**完全不带过滤器会被拒绝**（防手滑护栏）。
- `delay_ms` int 可选默认 `1000` —— 取消之间的节流间隔（对市场页的保守；干运行不适用）。
输出：`dry_run`、`matched`、`scanned`、`succeeded`（干运行中为 null）、`failed`（干运行中为 null）、`listings[]` `{ listing_id, market_hash_name, price_cents, would_cancel | succeeded }`。
安全：20 页 × 500 挂单的防御性抓取上限。

### `get_game_info` —— `DataActions.cs`（`GetGameInfoAction`）（login: no, timeout: 30s, safety: ReadOnly）
一个游戏的完整商店详情。
Payload：`app_id` string **必填**（正数）；`cc` string 可选默认 `"us"`；`cache_ttl_seconds`；`force_refresh`。
输出：`game`（GameInfo 对象 —— camelCase JSON：`appId`、`name`、`type`、`developer`、`publisher`、`releaseDate`、`isFree`、`requiresPurchase`、`price` {currency, final, initial, discountPercent, finalFormatted}、`metacriticScore`、`recommendationsTotal`、`genres`、`categories`、`headerImage`、`smallCapsuleImage`、`shortDescription`、`supportedLanguages`、`fetchedAt`）、`cache_key`。

### `get_game_info_batch` —— `DataActions.cs`（`GetGameInfoBatchAction`）（login: no, timeout: 240s, safety: ReadOnly）
一个批次的商店详情；**单应用错误不中止批次**（≥ 1 个应用解析成功即成功）。
Payload：`app_ids` **必填** —— CSV 字符串或数组（数字/字符串），最多 **200** 条，零值/无效条目使整个调用失败；`cc` 可选默认 `"us"`；`interval_ms` int 可选默认 `500`（钳制 0–5000；仅在真实商店抓取之间起节流间隔作用，缓存命中时跳过）；`cache_ttl_seconds`；`force_refresh`。
输出：`total_count`、`fetched`、`failed`、`games[]`（GameInfo）、`errors[]` `{ app_id, error }`、`cc`。

### `search_games` —— `DataActions.cs`（`SearchGamesAction`）（login: no, timeout: 30s, safety: ReadOnly）
搜索商店目录。
Payload：`term` string **必填**；`limit` int 可选默认 `20`；`cc` 可选；缓存项。
输出：`term`、`total_count`、`results[]`（GameSearchResult：`appId`、`name`、`type`、`isFree`、`price`、`headerImage`、`fetchedAt`）。

### `get_price` —— `DataActions.cs`（`GetPriceAction`）（login: no, timeout: 30s, safety: ReadOnly）
一个游戏的当前价格概览。
Payload：`app_id` string **必填**；`cc` 可选默认 `"us"`；缓存项。
输出：`app_id`、`price`（PriceOverview：`currency`、`final`、`initial`、`discountPercent`、`finalFormatted`）、`cache_key`。

### `get_market_listings` —— `DataActions.cs`（`GetMarketListingsAction`）（login: no, timeout: 30s, safety: ReadOnly）
一个游戏的公开社区市场挂单。
Payload：`app_id` string **必填**；`start` int 可选默认 `0`；`count` int 可选默认 `20`（钳制 1–100）；缓存项。
输出：`app_id`、`total_count`、`start`、`page_size`、`has_more`、`listings[]`（MarketListing 对象：`listingId`、`name`、`hashName`、`sellListings`、`appId`、`assetId`、`classId`、`instanceId`、`totalPrice`、`currencyId`、`fetchedAt`）。

### `cache_invalidate` —— `DataActions.cs`（`InvalidateCacheAction`）（login: no, timeout: 10s, safety: Idempotent）
按键前缀失效缓存的商店数据或清空全部（让任务无需重启 Agent 即可让过期层失效）。
Payload：`prefix` string 可选（如 `"price:730"`）；`clear_all` bool 可选（`true` 清空整个缓存）。二者必居其一。
输出：`prefix`（清空全部时为 null）、`cleared_all`、`removed`（条目数）。Agent 未配置缓存时失败。


### `check_account_standing` —— `CheckAccountStandingAction.cs`（login: yes, timeout: 60s, safety: ReadOnly）
封禁/信誉状态的健康探针（GetPlayerBans + 受限账号标记）。编排器依据 `standing` 把账号从交易/市场工作中隔离。
Payload：`steam_id` string 可选（SteamID64，≥ 76561197960265728）；默认为会话自身的。
输出（**注意 camelCase 键**）：`account`、`steamId`、`standing`（`"banned"` | `"restricted"` | `"clean"` —— 任何 VAC/社区/游戏/经济封禁 → banned；经济观察期 → restricted）、`vacBanned`、`numberOfVacBans`、`numberOfGameBans`、`daysSinceLastBan`、`communityBanned`、`economyBan`、`limited`、`steamLevel`。

### `check_proxy` —— `CheckProxyAction.cs`（login: no, timeout: 30s, safety: ReadOnly）
该账号出口代理的连通性自检：打开会话所用的同一代理 HTTP 栈，探测出口 IP + Steam 可达性。
Payload：`proxy` string 可选 —— 代理 URL；默认为会话配置的代理。两者皆无时动作以 "no proxy" 成功。
输出（无代理）：`proxyEnabled: false`、`account`。
输出：`proxyEnabled`、`proxy`（**恒为脱敏形式 —— 凭据从不离开 Agent**）、`scheme`、`account`、`exitIp`、`steamReachable`、`error`、`latencyMs`（测得时）。
注意：出口 IP 经 `api.ipify.org`，再测 `steamcommunity.com` 可达性；成功要求两者皆过。

---

# Game Access 插件动作（官方插件 `Vapor.Plugins.GameAccess`，`src/Vapor.Plugins.GameAccess/`）

这十四个游戏访问动作曾内置于宿主直至插件拆分；动作名与 payload 模式不变，Agent Docker 镜像打包该插件，没有它的宿主只是在 hello 中停止宣告这些名字（派发以 capability 不匹配 / `action not found` 失败，从不静默空操作）。拆分契约见插件 README。

## 挂机 / 时长

### `play_games` —— `src/Vapor.Plugins.GameAccess/PlayGamesAction.cs`（login: yes, timeout: 30s, safety: Idempotent）
在会话上开始或停止玩游戏。
Payload：
- `games` string，可选 —— AppID 的 CSV；也接受 `id/12345` 逐条格式（`"12345,67890"`、`"id/220,730"`）。
- `action` string，可选 —— `play`（默认）| `stop` | `idle`。`idle` 与 `stop` 都停止所有游戏。`games`/`action` 至少其一必填。
输出：`action`（`"play"` | `"stop"`）、`games`（AppID 集合，仅 play）、`account`。

### `get_card_drops` —— `src/Vapor.Plugins.GameAccess/GetCardDropsAction.cs`（login: yes, timeout: 120s, safety: ReadOnly）
列出仍有剩余卡牌掉落的游戏（社区徽章页）；stale-while-revalidate 缓存（默认 TTL 10 分钟，过期窗口 30 分钟）。
Payload：
- `steam_id` string，可选 —— SteamID64；默认为从会话 cookie 解析出的其自身 SteamID。
- `cache_ttl_seconds` int，可选 —— `0` 完全禁用缓存；`>0` 覆盖 TTL（过期窗口推导得出）。
- `force_refresh` bool，可选（默认 `false`）—— 绕过缓存并重新填充它。
输出：`steam_id`、`apps_with_drops`、`total_drops_remaining`、`drops[]` `{ app_id, name, drops_remaining }`（按掉落降序、再按 app id 排序）。

### `get_playtime` —— `src/Vapor.Plugins.GameAccess/GetPlaytimeAction.cs`（login: yes, timeout: 120s, safety: ReadOnly）
拥有游戏及其总时长（个人资料游戏页签）；缓存（默认 TTL 30 分钟，过期窗口 60 分钟）。
Payload：`steam_id`（可选，同上）、`games` string 可选（CSV AppID 过滤器，与 `play_games` 同一解析器）、`cache_ttl_seconds`、`force_refresh`（语义同 `get_card_drops`）。
输出：`steam_id`、`games_count`、`total_hours`、`playtimes[]` `{ app_id, name, hours }`。

## 许可证与 Key

### `add_license` —— `src/Vapor.Plugins.GameAccess/AddLicenseAction.cs`（login: yes, timeout: 120s, safety: Idempotent）
在账号上认领免费 Steam 内容（addlicense 流程）。App ID 走客户端协议（免费按需应用）；sub ID 走网站 "Add to account" 按钮所用的商店结账端点。
Payload：
- `app_ids` uint 数组，可选 —— 客户端协议免费许可证（需要已连接的 Steam 客户端）。
- `sub_ids` uint 数组，可选 —— 商店结账（需要 web 会话）。二者至少其一必填；条目必须 `> 0`、去重；接受单个裸值。
输出：`app_ids`、`sub_ids`、`apps_result`（Steam 结果字符串）、`granted_app_ids`、`granted_package_ids`（授予时）、`purchases[]` `{ id, success, detail }`（sub 路径）。

### `redeem_key` —— `src/Vapor.Plugins.GameAccess/RedeemKeyAction.cs`（login: yes, timeout: 60s, safety: GuardedWrite）
兑换一个 Steam 产品 Key；瞬时失败最多重试 3 次（250 ms × 次数退避）。
Payload：`key` string，**必填**。
输出：`action`、`key`（**已脱敏** —— 中段以 `*` 遮蔽）、`result`（Steam 结果名）、`resultCode`（int）、`attempts`、`success`、`requestId`、`durationMs`、`grantedAppIds`、`grantedPackageIds`、`receiptDetails`（非空/非 null 时各自出现）。
安全：`RateLimitExceeded` 呈现为 "Too many key redemption attempts. Please try again later."；`AlreadyOwned` 与 `DuplicateRequest` 计为成功。

## 成就

### `get_achievements` —— `src/Vapor.Plugins.GameAccess/GetAchievementsAction.cs`（login: yes, timeout: 120s, safety: ReadOnly）
列出一个游戏的成就及解锁状态（社区统计页）。只读；`api_name` 值是 `unlock_achievements` 的写侧标识符。
Payload：`app_id` string，**必填**（正数）；`steam_id` string 可选（默认会话）。
输出：`steam_id`、`app_id`、`unlocked_count`、`total_count`、`achievements[]` `{ api_name, display_name, description, unlocked, icon_url }`。

### `unlock_achievements` —— `src/Vapor.Plugins.GameAccess/UnlockAchievementsAction.cs`（login: yes, timeout: 180s, safety: Idempotent）
经客户端统计协议把具名成就设为已解锁。**不存在 "unlock everything" 路径** —— 名称必须显式。
Payload：`app_id` string **必填**（正数）；`names` string 数组 **必填、非空**（大小写不敏感去重；接受单个字符串）。
输出：`app_id`、`unlock`（`true`）、`requested_count`、`results[]` `{ name, success, detail }`、`succeeded_count`、`failed_count`、`verified`。单条失败从不中止其余。

### `reset_achievements` —— `src/Vapor.Plugins.GameAccess/UnlockAchievementsAction.cs`（`ResetAchievementsAction` 类）（login: yes, timeout: 180s, safety: GuardedWrite）
清除具名成就。**破坏性；双重门控**：要求显式非空 `names` 清单**且** `confirm: true`，在动作层*和*控制平面 API 独立强制执行。
Payload：`app_id`（同上）、`names`（同上）、`confirm` bool —— 必须显式 `true`。
输出：与 `unlock_achievements` 同形，但 `unlock: false`。

## 库存（插件半边）

### `get_inventory` —— `src/Vapor.Plugins.GameAccess/GetInventoryAction.cs`（login: yes, timeout: 60s, safety: ReadOnly）
读取一个 Steam 库存。两种模式：经典单应用或 `app_ids` 多应用扫描（最多 5 个应用；context 规则：753 → 6，否则 2）。
Payload：
- `steam_id` string，可选（默认会话）。
- `app_ids` uint 数组，可选 —— 多应用扫描；优先于经典二元组；每次调用最多 5 个。
- `app_id` string，可选 —— 默认 `730`（CS2）。
- `context_id` string，可选 —— 默认 `2`。
- `tradable_only` bool，可选（默认 `false`）—— 只保留当前即可交易的物品。
- `marketable_only` bool，可选（默认 `false`）—— 只保留可上市物品。
输出（经典）：`steam_id`、`app_id`、`context_id`、`total_count`、`items[]` `{ asset_id, class_id, instance_id, app_id, amount, name, market_name, market_hash_name, type, tradable, marketable }`。
输出（多应用）：`steam_id`、`total_count`、`items[]`（同形）、`apps[]` `{ app_id, context_id, item_count }`。分页防御性封顶 50 000 件物品。

### `get_game_inventory` —— `src/Vapor.Plugins.GameAccess/GetGameInventoryAction.cs`（login: yes, timeout: 60s, safety: ReadOnly）
单个应用库存的游戏经济视图：与 `get_inventory` 同源分页，然后把物品折叠成按 (class, instance) 的堆 —— 数量、可交易/可上市拆分 —— 从大到小。只读：从不发起任何交易或市场调用。
Payload：
- `steam_id` string，可选（默认会话）。
- `app_id` string，可选 —— 默认 `730`（CS2）；Dota 2 是 `570`。
- `context_id` string，可选 —— 默认 `2`。
- `tradable_only` / `marketable_only` bool，可选（默认 `false`）—— 聚合前先过滤。
- `value` bool，可选（默认 `false`）—— 附带市场估值。
- `currency` string，可选 —— priceoverview 货币 id，估值时透传。
- `cc` string，可选 —— 应用级价格的 country code（默认 `us`）。
输出：`steam_id`、`app_id`、`context_id`（string）、`total_items`、`stack_count`、`stacks[]` `{ class_id, instance_id, name, market_name, market_hash_name, type, count, tradable_count, marketable_count, amount_total }`；带 `value=true` 时还有 `valuation` `{ currency, priced_stacks, unpriced_stacks, truncated, items[] { market_hash_name, count, lowest_price, median_price, volume, error }, game_price }`。价格按堆来自社区市场 `priceoverview` 端点 —— 缓存在 `SteamCacheTtl.Price` 层，封顶最大的 50 个可上市堆（超限的堆被跳过而非失败）—— 并按 Steam 的格式原样返回（含货币符号，从不解析成数字）。`game_price` 镜像 `get_price`（同端点、同 `price:{appId}:{cc}` 缓存键，因此两个动作共享一份热缓存）且是补充性的：商店失败落入 `game_price.error`，不会拖垮按堆估值。

### `get_item_details` —— `src/Vapor.Plugins.GameAccess/GetItemDetailsAction.cs`（login: yes, timeout: 60s, safety: ReadOnly）
特定库存物品的逐件元数据与估值：给出 `asset_ids`（确切物品）或 `class_ids`（某类的全部物品），动作把库存分页扫一遍并返回匹配条目，可选附带市场价格。只读：从不发起任何交易或市场调用。
Payload：
- `steam_id` string，可选（默认会话）。
- `app_id` string，可选 —— 默认 `730`；`context_id` string，可选 —— 默认 `2`。
- `asset_ids` / `class_ids` uint64 列表（接受单个标量）—— 至少一个 id 必填；不可解析或非正条目被静默丢弃，重复项去重。
- `value` bool，可选（默认 `false`）—— 每件附带市场价格；`currency` string，可选。
输出：`steam_id`、`app_id`、`context_id`（string）、`matched_count`、`items[]` `{ asset_id, class_id, instance_id, app_id, amount, name, market_name, market_hash_name, type, tradable, marketable }`（64 位 id 以字符串表示）；带 `value=true` 时还有 `valuation` `{ currency, priced_count, failed_count, items[] { asset_id, market_hash_name, lowest_price, median_price, volume, error } }` —— 不可上市条目带 `error: "not marketable"` 且不做价格查询；价格同 `get_game_inventory`。

### `loot_inventory` —— `src/Vapor.Plugins.GameAccess/LootAction.cs`（`LootInventoryAction`）（login: yes, timeout: 120s, safety: GuardedWrite）
把该账号当前可交易的全部物品发给一个对方（ASF/Watt 的 "loot" 流程）。
Payload：
- `partner_steam_id` string 或 `trade_url` string —— 其一**必填**。
- `message` string，可选。
- `app_ids` uint 数组，可选 —— **默认 `[753]`**（Steam 社区物品 —— 交易卡牌落处）；最多 5 个应用；每应用 50 库存页上限。
输出：`trade_offer_id`、`partner_steam_id`、`item_count`、`apps_scanned[]` `{ app_id, context_id, tradable_items }`、`requires_mobile_confirmation`。
安全：只发*现在*可交易的物品；发送前先取限流器租约；没有找到任何可交易物品时失败。

### `find_duplicates` —— `src/Vapor.Plugins.GameAccess/FindDuplicatesAction.cs`（login: yes, timeout: 60s, safety: ReadOnly）
扫描库存找重复物品并报告超出 `keep` 的可交易份数（TradeMatcher 式分析；不发送任何报价）。
Payload：`app_ids` uint 数组可选（默认 `[753]`，最多 5）；`keep` int 可选，默认 `1`，必须 1–100。
输出：`steam_id`、`keep`、`apps_scanned[]` `{ app_id, context_id, scanned_items }`、`duplicates[]` `{ app_id, class_id, instance_id, name, total, excess_count, excess_asset_ids }`、`excess_count`。

### `swap_duplicates` —— `src/Vapor.Plugins.GameAccess/SwapDuplicatesAction.cs`（login: yes, timeout: 120s, safety: NonIdempotent）
把该账号的重复份与对方手中该账号所缺卡牌的重复份配对（反之亦然）；**默认干运行**，带 `send=true` 才发送对称的 1:1 报价。
Payload：
- `partner_steam_id` string 或 `trade_url` string —— 其一**必填**；必须与账号自身 SteamID 不同。
- `message` string，可选。
- `send` bool，可选（默认 `false`）—— **干运行开关**；`false` 只报告匹配，`true` 发送报价。
- `app_ids` uint 数组可选（默认 `[753]`，最多 5）；`keep` int 1–100 默认 `1`；`max_swaps` int 1–100 默认 `25`。
输出：`partner_steam_id`、`keep`、`matches[]` `{ give: {app_id, context_id, asset_id, class_id, instance_id, name}, receive: {…} }`、`give_count`、`receive_count`、`dry_run`；发送后还有 `trade_offer_id`、`requires_mobile_confirmation`。
安全：默认干运行；发送时走限流器。

## 积分商店

### `get_points_shop_summary` —— `src/Vapor.Plugins.GameAccess/GetPointsShopSummaryAction.cs`（login: yes, timeout: 60s, safety: ReadOnly）
积分商店余额与奖励定义（`claim_points_shop_items` 的发现源；免费可领项是 `point_cost == 0`）。
Payload：`definition_ids` uint 数组可选（查特定定义）；`free_only` bool 可选默认 `false`（客户端侧过滤器；`items_total` 仍报告 Steam 返回的数量）。
输出：`points`、`points_earned`、`points_spent`、`items[]` `{ defid, app_id, point_cost, active, type?, description?, free_until? }`（可选键在非默认时出现）、`items_total`。

### `claim_points_shop_items` —— `src/Vapor.Plugins.GameAccess/ClaimPointsShopItemsAction.cs`（login: yes, timeout: 120s, safety: GuardedWrite）
兑换积分商店奖励定义（`add_license` 的积分商店变体；对应 ASF 的 "RP"）。
Payload：
- `definition_ids` uint 数组 **必填**。
- `force` bool 可选默认 `false` —— **安全门**：不带 `force` 时先校验整批，**任何付费定义（`point_cost != 0`）或未知 id 都会在兑换任何东西之前拒绝整批**；带 `force=true` 时付费项也被兑换。
输出：`force`、`requested`、`succeeded`、`failed`、`results[]` `{ defid, success, result, community_item_id? }`（64 位 id 保留为字符串）、`points_after`（尽力而为的余额刷新）。
# 插件贡献的动作


### `generate_totp`（login: no, timeout: 15s, safety: ReadOnly）
由 base64 共享密钥生成当前 Steam 手机令牌验证码（可用时使用已同步的 Steam 时间偏移）。
Payload：`shared_secret` string **必填**（别名 `sharedSecret`）；`time` long 可选（unix 秒，用于测试）。
输出：`code`、`seconds_remaining`、`time`、`time_synced`。

### `generate_confirmation_hash`（login: no, timeout: 15s, safety: ReadOnly）
面向 /mobileconf 端点的 HMAC-SHA1 确认哈希。
Payload：`identity_secret` string **必填**（别名 `identitySecret`）；`tag` string 可选默认 `"conf"` —— 必须是 `conf | details | allow | cancel` 之一；`time` long 可选。
输出：`hash`、`tag`、`time`、`time_synced`。

### `sync_steam_time`（login: no, timeout: 30s, safety: Idempotent）
查询 Steam 的 QueryTime 端点并存储本地/服务器时钟偏移（所有 TOTP/哈希生成都用它）。无 payload 字段。
输出：`offset_seconds`、`steam_time`、`synced_at`。

### `get_trade_confirmations`（login: yes, timeout: 30s, safety: ReadOnly）
列出会话账号待处理的手机交易/市场确认。
Payload：`identity_secret` string **必填**（别名 `identitySecret`）。
输出：`count`、`confirmations[]` `{ id, nonce, creator_id, headline, summary }`（64 位 id 以字符串表示）。

### `respond_trade_confirmation`（login: yes, timeout: 30s, safety: NonIdempotent）
接受（`allow`）或取消一条待处理手机确认。
Payload：`identity_secret` string **必填**；`confirmation_id` **必填**（正整数）；`nonce` **必填**（正整数）；`operation` string 可选默认 `"allow"` —— `allow | cancel`。
输出：`confirmation_id`、`operation`。

### `save_shared_secret`（login: no, timeout: 10s, safety: GuardedWrite）
把该账号的令牌器共享密钥存入 Agent 的**加密凭据存储**，以便 2FA 挑战可以本地应答（同时供 `AGENT_2FA_AUTO_SUBMIT` 自动应答器使用）。
Payload：`shared_secret` string **必填**。
输出：`account`、`stored: true`。
安全：密钥只留在 Agent 侧。

### `save_identity_secret`（login: no, timeout: 10s, safety: GuardedWrite）
存储身份密钥用于本地交易确认签名。该密钥从不离开 Agent —— 确认子任务的派发不带它，存储在执行时才被读取。
Payload：`identity_secret` string **必填**。
输出：`account`、`stored: true`。

### `confirm_trade_offer`（login: yes, timeout: 60s, safety: GuardedWrite）
**用已存储的身份密钥**完成交易报价的手机确认步骤（绝不来自 payload，绝不出现在任务记录里）。列出待处理确认，匹配 creator id 等于交易报价 id 的那一条（最多轮询 6 × 250 ms —— Steam 比报价接受晚几秒），然后应答。
Payload：`trade_offer_id` **必填**（正整数）；`operation` string 可选默认 `"allow"` —— `allow | cancel`。
输出：`trade_offer_id`、`confirmation_id`、`operation`、`confirmed`（当且仅当 operation 为 `allow` 时为 true）。

### `confirm_all_confirmations`（login: yes, timeout: 120s, safety: NonIdempotent）
一次性应答每一条待处理手机确认（Watt/ASF 的 "accept all"）；逐条处理 —— 单条失败不中止批次。
Payload：`operation` string 可选默认 `"allow"` —— `allow | cancel`；`type` string 可选 —— `all`（默认）| `trade` | `market` 过滤器。
输出：`operation`、`total`、`succeeded`、`failed`、`results[]` `{ confirmation_id, type, succeeded, error? }`。


### `market_watch_add`（login: no, timeout: 10s, safety: Idempotent）
开始监视一个游戏：`kind=price` 在越过阈值时告警（默认），`kind=free` 在游戏转为免费时告警。后台轮询间隔/阈值默认来自插件配置（`market.check_interval_seconds` 300、`market.threshold_percent` 10、`market.country` "us"）。
Payload：`app_id` string **必填**（正数）；`kind` string 可选默认 `"price"` —— `price | free`；`threshold_percent` number 可选（默认来自插件配置，必须 > 0，仅对 `price` 有意义）；`cc` string 可选（默认来自插件配置，转小写）。
输出：`app_id`、`kind`、`threshold_percent`、`cc`、`watched`（总监视数）。该应用已被监视时失败。

### `market_watch_remove`（login: no, timeout: 10s, safety: Idempotent）
停止监视一个游戏的价格。
Payload：`app_id` string **必填**。
输出：`app_id`、`watched`（剩余数）。未被监视时失败。

### `market_watch_list`（login: no, timeout: 10s, safety: ReadOnly）
列出被监视的游戏及其基线与最后观测价。无 payload 字段。
输出：`watches[]` `{ app_id, kind, threshold_percent, cc, currency, baseline, last_price, last_known_free, last_checked_at, alerts }`、`count`、`interval_seconds`。


### `get_metrics`（login: no, timeout: 10s, safety: ReadOnly）
返回当前指标快照，让控制平面能通过正常任务执行拉取监控数据（该插件也在自己的 HTTP 端点上提供它）。无 payload 字段。
输出：`format`（`"prometheus"`）、`metrics`（Prometheus exposition 文本）、`summary`（JSON 摘要字符串）。

## CS2/CSGO 插件（`vapor.csgo`，`src/Vapor.Plugins.Csgo/CsgoPlugin.cs`）

干运行模拟器，加上**带门控**的真实游戏协调器开箱（`case_open`，现分级为 `NonIdempotent`：一次门控的真实开箱消耗一个箱子 + 一把钥匙 —— 安全分级正是为这种最坏副作用而生）、把背包分类为箱子/钥匙/其他的 CS2 实时背包读取，以及一个门控的消耗台账（运维实际开箱后录入其中）。两道门，默认**关闭**：`open.enabled` 解锁真实 GC 开箱路径（Steam 客户端传输 + CS2 握手 + `OpenCrate`）；`consume.enabled` 只解锁台账记账。每个输出都带 `mode` 字段（`"dry-run"`、`"gc"`、`"readonly"`、`"ledger"`）。插件边界：[plugins.md](plugins.md)。

### `case_open` —— `src/Vapor.Plugins.Csgo/CsgoPlugin.cs`（`CaseOpenAction`）（login: no, timeout: 180s, safety: NonIdempotent）
不带 `crateAssetId`/`keyAssetId`（且门关闭）时运行本地干运行模拟器：对箱子的物品池采样并追加一个模拟结果 —— 无 Steam 流量、无 capability。带数字 asset id 或 `open.enabled=true` 时动作走真实路径，它在门关闭时**拒绝运行**并给出 `open.enabled` / `VAPOR_CSGO_OPEN_ENABLED` 的指引；一次门控的真实开箱每个请求恰好消耗一个箱子 + 一把钥匙（`count != 1` 被拒绝 —— 要多开就重复调用）。
Payload：`caseId` string **必填**（必须存在于插件目录中）；`crateAssetId`/`keyAssetId` 可选数字（ulong 以数字、字符串或 JSON 数值表示 —— 要么都给要么都不给，非零；显式 id 跳过开箱前背包扫描）；`count` integer 可选默认 `1`（仅模拟）。
输出：包裹在 `payload` 键下一层。模拟内层形状：`mode`（`"dry-run"`）、`caseId`、`opened`、`rarityCounts[]` `{ rarity, count }`、`results[]`。真实 `gc` 内层形状：`mode`（`"gc"`）、`case_id`、`case_name`、`crate_asset_id`、`key_asset_id`、`prizes[]` `{ asset_id, name (null when the backpack lagged past the retries), matched }`、`ledger` `{ entries, cases, keys }`。真实路径执行 CS2 GC 握手（PlayGames → `ClientHello` → `ClientWelcome`），发送 `OpenCrate`（2534），最多等 120 秒等带请求 `1007` 的 `ItemCustomizationNotification`，然后重读背包（最多 3 次尝试）把奖品 asset id 映射到市场名，并把消耗记入台账。分级 `NonIdempotent`（最坏情况：一次门控调用花掉一个箱子和一把钥匙）—— 派发重试是运维的责任。

### `case_inventory` —— `src/Vapor.Plugins.Csgo/CaseActions.cs`（`CaseInventoryAction`）（login: yes, timeout: 60s, safety: ReadOnly）
经共享库存分页器（app `730`，context `2`）翻页 CS2 背包并分类：市场哈希名以 `"Case"` 结尾的是箱子容器（容器名与已知箱子匹配时带目录箱 id），以 `"Key"` 结尾的是钥匙（"Case Key" 是钥匙），其余归为其他。可堆叠数量求和；分组按名称排序。
Payload：`steam_id` string 可选 —— 默认为会话自身的（来自其 web cookie；无 web 会话或 cookie 不含 SteamID 时以明确指引失败）。
输出：`mode`（`"readonly"`）、`steam_id`、`app_id`、`context_id`、`total_count`、`cases[]` `{ name, case_id (null when unmatched), count }`、`key_count`、`other_count`、`ledger` `{ entries, cases, keys }` —— 台账合计正是把这次读取变成一次收敛的东西（背包减去已记录消耗即应该还在的东西）。

### `case_consume` —— `src/Vapor.Plugins.Csgo/CaseActions.cs`（`CaseConsumeAction`）（login: no, timeout: 30s, safety: NonIdempotent）
向插件台账记一条消耗条目（箱子 + 钥匙 + 奖励）。**`consume.enabled` 关闭（默认）时拒绝运行**，并给出配置键及其环境覆盖的指引；开启该门不授予任何 Steam 能力 —— 运维在 CS2 里完成开箱并在此记录。要求 login `no`：不读取也不触碰任何会话。
Payload：`caseId` string **必填**（必须存在于插件目录中）；`count` integer 可选默认 `1`（钳制到 1..100）；`keys` integer 可选（默认为 `count` —— 每箱一钥 —— 钳制到 1..100）；`awards` string 数组可选（运维报告收到的奖励，回显进条目）。
输出：`mode`（`"ledger"`）、`caseId`、`cases`、`keys`、`awards`、`ledger` `{ entries, cases, keys }`（记录后的合计）。分级 `NonIdempotent`：每次调用追加一行台账。台账在内存中保留最近 1000 条，配置了 `ledger.path` 时把每条作为一行 JSON 追加（归档 I/O 错误按设计被吞掉）。

## Dota 2 插件（`vapor.dota2`，`src/Vapor.Plugins.Dota2/Dota2Plugin.cs`）

三个把游戏无关的交易基础（交易报价、库存分页、限流）限定到 Dota 2（app `570`，context `2`）的动作。该插件只提供动作 —— 插件 web 路由不带 bot 会话，因此没有插件 web API；Mobile Authenticator 插件的确认动作原样覆盖 Dota 2 交易确认。严格 payload 解析：不可解析条目与外来 app id 都是错误，从不静默丢弃。**带门控**的发送，默认**关闭**：`trade.enabled`（或 `VAPOR_DOTA2_TRADE_ENABLED`）解锁 `dota2_send_trade`；读动作无门。所有权核验对着实时 Dota 2 库存运行，且每次拒绝都点名 Steam 的 7 天市场交易冷却。合成、勇士令状自动化与局内自动化是已文档化的非目标。插件边界：[game-plugins.md](game-plugins.md)。

### `dota2_inventory` —— `src/Vapor.Plugins.Dota2/Dota2InventoryAction.cs`（`Dota2InventoryAction`）（login: yes, timeout: 60s, safety: ReadOnly）
经共享分页器（app `570`，context `2`）翻页 Dota 2 库存并分入三类：可交易、处于 Steam 7 天市场交易冷却（`TradabilityDate` 在未来 —— 解锁时间逐件列出）、以及锁定（`Tradable=false` 且无未来解锁时间上报 —— Steam 永不标记为可交易的赠送或活动锁定物品）。
Payload：`steam_id` string 可选 —— 默认为会话自身的（来自其 web cookie；无 web 会话或 cookie 不含 SteamID 时以明确指引失败）。
输出：`mode`（`"readonly"`）、`steam_id`、`app_id`、`context_id`、`total_count`、`ready_count`、`cooldown_count`、`locked_count`、`checked_at`、`cooldown_items[]` `{ asset_id, name, cooldown_until }` 与 `locked_items[]` `{ asset_id, name }` —— 两个列表都封顶 50 条，而计数保持诚实总数。

### `dota2_trades` —— `src/Vapor.Plugins.Dota2/Dota2TradesAction.cs`（`Dota2TradesAction`）（login: yes, timeout: 30s, safety: ReadOnly）
列出会话账号的交易报价并聚焦 Dota 2 的那些。默认只列出实际触及 app-`570` 资产的报价（两个方向都有 `dota` 计数）；`include_all=true` 保留外来报价可见，标记 `dota_involved=false`。
Payload：`active_only` bool 可选默认 `true`（透传给 Steam 传输层）；`include_all` bool 可选默认 `false`。
输出：`mode`（`"readonly"`）、`app_id`、`dota_only`、`active_only`、`received` / `sent` —— 各为 `{ total, dota, offers[] }`，其中 `offers[]` `{ trade_offer_id, partner_steam_id, state, is_our_offer, message (null when none), time_created, time_expires (null when none), dota_involved, items_to_give[] / items_to_receive[] { asset_id, app_id, context_id, amount } }`。

### `dota2_send_trade` —— `src/Vapor.Plugins.Dota2/Dota2SendTradeAction.cs`（`Dota2SendTradeAction`）（login: yes, timeout: 60s, safety: NonIdempotent）
发送一份 Dota 2 交易报价（给、收或两者）。**`trade.enabled` 关闭（默认）时拒绝运行**，并给出配置键及其环境覆盖的指引。对方来自 `trade_url`（按共享交易 URL 规则解析；token 自动提取）或 `partner_steam_id`。发送前，给出侧对着实时库存核验：物品必须是拥有的 app-`570` 物品且堆数量充足，任何处于 Steam 7 天市场交易冷却（或根本不可交易）的物品都会使调用失败并点名解锁时间 —— 错误在任何报价创建之前到达。
Payload：`trade_url` string 或 `partner_steam_id` string —— 其一必填（两者都在时 `trade_url` 优先；未显式给 `token` 时其 token 自动补上）；`items_to_give` / `items_to_receive` 为 `{ asset_id, amount? (default 1), app_id? (must be 570), context_id? (must be 2) }` 的数组 —— 两个清单合计至少一件，每份报价至多 100 件（批量或自动化交易是非目标 —— 要更多报价就重复调用）；`token` string 可选（使用 `partner_steam_id` 时的交易报价 token）；`message` string 可选。
输出：`mode`（`"trade"`）、`app_id`、`trade_offer_id`（Steam 未返回时为 null）、`partner_steam_id`、`requires_mobile_confirmation`、`ownership_verified`（给出侧经实时库存核验时为 true）、`items_to_give`、`items_to_receive`。分级 `NonIdempotent`：每次调用创建一份新报价 —— 派发重试会使报价翻倍，因此重试是运维的责任；宿主提供时由共享的每账号限流器门控发送。

## Game Data 插件（`vapor.game-data`，`src/Vapor.Plugins.GameData/GameDataPlugin.cs`）

四个基于官方 Steam Web API（`api.steampowered.com`，带 key 认证的 GET）的只读动作。API key **在 Agent 侧**存于插件配置（`webapi.key`）或 `VAPOR_GAME_DATA_WEBAPI_KEY` 环境变量 —— 控制平面从不见它 —— 缺 key 时每个动作都以明确指引失败而不是伪造数据。每个动作把传输（`HttpRequestException`）与解析（`JsonException`）失败捕获成带操作名的失败结果。投影是紧凑摘要，不是原始 payload。各游戏插件边界：[game-plugins.md](game-plugins.md)。

### `dota2_match_history`（login: no, timeout: 30s, safety: ReadOnly）
抓取公开的 Dota 2 比赛历史（`IDOTA2Match_570/GetMatchHistory/v1/`）。
Payload：`heroId` number 可选；`gameMode` number 可选；`matchesRequested` number 可选（钳制到 1..100）。不可解析的值被忽略而非拒绝。
输出：`status`（Web API 结果状态，缺失时为 0）、`matchCount`、`totalResults`、`matches[]` `{ matchId, matchSeqNum, startTime, lobbyType, gameMode }` —— `matchId`/`matchSeqNum` 是 int64（真实比赛 id 超出 int32）。

### `dota2_heroes`（login: no, timeout: 30s, safety: ReadOnly）
抓取 Dota 2 英雄目录（`IEconDOTA2_570/GetHeroes/v1/`）。无 payload 字段。
输出：`status`、`count`、`items[]` `{ id, name, localizedName, legs }`。

### `dota2_game_items`（login: no, timeout: 30s, safety: ReadOnly）
抓取 Dota 2 物品目录（`IEconDOTA2_570/GetGameItems/v1/`）。无 payload 字段。
输出：`status`、`count`、`items[]` `{ id, name, localizedName, cost }`。

### `econ_item_schema`（login: no, timeout: 60s, safety: ReadOnly）
抓取 TF2/CS2 物品模式摘要（`IEconItems_{appid}/GetSchema/v1/`）。
Payload：`appid` number **必填** —— 只接受 `440`（TF2）与 `730`（CS2）；其他值失败并明示白名单。
输出：`status`、`count`（模式物品数）、`items[]` `{ defIndex, name }`、`appId`。完整模式有数 MB 且留在 Web API —— 只返回摘要。

---

# Agent 宿主动作（`Vapor.Agent` 程序集，`src/Vapor.Agent/HostActions/`）

全部六个都要求任务目标恰好是 `"agent:{agentId}"`（见 `HostTaskTarget`）；它们以宿主为作用域运行 —— 无 bot 会话 —— 并走常规子任务管线（重试/审计/任务面板都适用）。`pluginId` 拼写：`pluginId` 与 `plugin_id` 都被接受（别名查找）。

### `plugin_install`（login: no, timeout: 300s, safety: Idempotent）
按 URL 安装一个插件包（zip），带**强制 SHA-256 校验和**，对照清单校验并热加载它。
Payload：`url` string **必填**（包位置）；`sha256` string **必填**（64 位十六进制摘要；别名 `sha_256`）；`pluginId` string 可选（别名 `plugin_id`；省略时从清单推导）；`version` string 可选。
输出：`pluginId`、`version`、`replaced`（bool —— 覆盖了既有安装）、`actions`（贡献的动作名）、`plugins`（完整已加载插件列表，让控制平面能镜像 Agent 的库存）。失败时输出仍带 `url` + `plugins`。

### `plugin_uninstall`（login: no, timeout: 60s, safety: Idempotent）
按 id 卸载插件并移除其目录（先改名挪开再尽力删除 —— Windows 上的 CLR 文件锁不得阻塞发现）。**幂等**：卸载一个未加载的插件以 `removed=false` 成功。
Payload：`pluginId` string **必填**（别名 `plugin_id`）。
输出：`pluginId`、`removed`、`plugins`（剩余列表）。

### `plugin_list`（login: no, timeout: 15s, safety: ReadOnly）
报告当前已加载的插件与插件根目录。无 payload 字段。
输出：`directory`（插件根路径）、`count`、`plugins`（已加载插件列表；每个条目带 `id`、`name`、`version`、`apiVersion`、`trust`、`permissions`、`publisher`（清单的参考性发布者，未声明时缺失）、`resources`（清单的参考性运行时资源声明，未声明时为空）、`actions`）。

### `plugin_update_check`（login: no, timeout: 15s, safety: ReadOnly）
把来自插件索引的目录候选与该 Agent 实际加载的插件比较，并逐候选给出判定。按设计只读 —— 这里不下载也不替换任何东西；运维用 `plugin_install` 决定装什么（以及装在哪）。
Payload：`candidates` 数组可选 —— 来自索引的 `{ id, version }` 对象（非对象条目被跳过；无 `id` 的条目被跳过）；省略时得到空报告（已加载插件镜像仍随行）。
输出：`count`、`updates`（逐候选：`id`、`catalogVersion`、`installedVersion`（未安装时为 null）、`status` —— `updateAvailable`（目录严格更新，两侧版本都能解析为 `System.Version`）/ `upToDate`（相等或已安装领先）/ `notComparable`（任一侧不可解析 —— 从不强转）/ `notInstalled`）、`plugins`（完整已加载插件列表，让控制平面能镜像 Agent 的库存）。

### `set_proxy`（login: no, timeout: 120s, safety: GuardedWrite）
指派（或清除）一个账号的出口代理：校验端点，把它持久化进**本 Agent 的**凭据存储，且存在活跃会话时经新出口重建该会话（移除 → 恢复 → 令牌重登）。重建失败不是错误：指派已持久化，下次登录会拾起它（`sessionRestarted=false`）。
Payload：`account` string **必填**；`proxy` string 可选 —— 端点（`http|https|socks5://[user:pass@]host:port`）；**省略/留空即清除**（REST 层强制显式性，意外空调用无法剥掉钉定的出口）。
输出：`account`、`proxy`（**仅脱敏形式** —— `socks5://user:<redacted>@host:port`；凭据从不离开 Agent）、`cleared`（bool）、`sessionRestarted`（bool）。
注意：该动作以宿主为作用域（`agent:{id}` 目标）但以账号为键 —— ControlPlane 拒绝为未钉定 Agent 的账号指派，否则端点会持久化进认领该任务的任何一个 Agent。搭配 `check_proxy` 验证实际出口。理由：Steam 按 IP 关联登录；账号的出口钉定一次、很少更改。

### `script_exec`（login: no, timeout: 300s, safety: NonIdempotent）
在本 Agent 机器上运行来自控制平面脚本仓库的运维脚本。仓库在控制平面侧，因此 payload 携带完整脚本正文；Agent 从 `language` 解析解释器，捕获 stdout/stderr 加退出码，并把三者回报到子任务结果上。唯一入口就是这个宿主动作 —— `POST /v1/scripts/{id}/execute` 是运维侧入口，负责暂存 payload。
Payload：`content` string **必填**（完整脚本正文）；`language` string 可选 —— `shell`（默认，`/bin/sh -c`）/ `python`（`python3 -c`）/ `powershell`（`pwsh -NoProfile -NonInteractive -Command`；解释器缺失时以启动错误使执行失败）；`timeoutSeconds` number 可选（1–300，默认 120 —— 只能缩短 300s 的元数据上限）；`scriptId` / `name` 可选溯源，由控制平面回显用于任务识别。
输出：`exitCode`（被杀时为 null）、`timedOut`（bool）、`stdout` / `stderr`（各封顶 65536 字符并带 `stdoutTruncated` / `stderrTruncated` 标志 —— 截断被报告而非隐藏），再无其他 —— 该动作刻意不回显脚本正文。非零退出是携带 `script exited with code N` 的失败结果；超时是携带 `script timed out after Ns` 的失败结果并杀掉进程树。分级 `NonIdempotent`：重跑任意脚本可能使外部副作用翻倍，因此派发封顶 2 次尝试。

---

## 动作数量汇总

| 提供方 | 数量 |
|---|---|
| Steam.Core 宿主动作（`src/Vapor.Steam.Core/Actions/`） | 20 |
| Game Access 插件（`vapor.game-access`） | 16 |
| Mobile Authenticator 插件（`vapor.mobile-authenticator`） | 9 |
| Market Watch 插件（`vapor.market-watch`） | 3 |
| CS2/CSGO 插件（`vapor.csgo`） | 3 |
| Dota 2 插件（`vapor.dota2`） | 3 |
| Monitoring 插件（`vapor.monitoring`） | 1 |
| Game Data 插件（`vapor.game-data`） | 4 |
| Agent 宿主（`Vapor.Agent/HostActions/`） | 6 |
| **合计** | **65** |

这些计数是已分级（已镜像）的动作。CS2/CSGO 插件的三个动作全部已分级；`case_open` 带 `NonIdempotent`，因为其门控真实路径每次调用消耗一个箱子 + 一把钥匙，而其模拟路径保持本地干运行。Dota 2 插件的三个动作全部已分级；`dota2_send_trade` 带 `NonIdempotent`，因为每次调用创建一份新报价（重试会使它翻倍），而其默认关门的拒绝发生在触碰任何东西之前。
