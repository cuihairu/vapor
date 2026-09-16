# 游戏数据字段字典（data dictionary）

> 权威源。`src/Vapor.ControlPlane/wwwroot/gamedata.html` 的"字段字典"面板与本文件同步维护；
> 修改任一侧时必须同步另一侧（`DashboardStaticTests.GameDataHtml_DocumentsAllFiveModels` 只锁模型名清单）。
> 模型定义：`src/Vapor.Steam.Core/Models/GameModels.cs`。

## 数据来源与无凭证约束

- 全部数据来自 **Steam 公开商店/社区端点**（`store.appdetails`、storesearch、community market），
  **匿名可达**：相关 action `RequiresLogin: false`，抓取全程不接触任何 secret。
- 多账号分片采集（crawl）中，账号仅作为**分片/派发/审计归属**（不同账号在不同 agent/出口，分摊频控）。
- **硬约束**：crawl 分片任务经由常规作业队列派发，payload 不携带凭证时 agent 一律回放该账号的
  已入库会话（`AgentTaskExecutor`：no credentials → `TryRestoreSessionAsync`，未入库即任务失败
  "No credentials provided and no stored session found"）。因此采集池内的账号必须先在某个 agent
  完成登录入库；单个账号凭证失效只失败该账号的分片，不阻塞其他分片。
- 不做市场挂单批量抓取（与 MarketWatch 互补：告警面 vs 数据面）；下游可订阅 `crawl.run_completed` 事件。

## 来源端点与缓存层级

`FetchCachedAsync`（fresh + stale-while-revalidate）统一管理；载荷可用 `cache_ttl_seconds`
（`0` 禁用缓存，自定义 fresh 的 stale 窗口为 4 倍）与 `force_refresh` 覆盖。

| action | Steam 端点 | 返回模型 | fresh | stale | 缓存键 |
|---|---|---|---|---|---|
| `get_game_info` | store.appdetails | `GameInfo` | 30 min | 2 h | `game:{appId}:{cc}` |
| `get_game_info_batch` | store.appdetails × N（≤200/批） | `GameInfo[]` + 逐项错误 | 30 min | 2 h | 与单应用共享层级 |
| `search_games` | storesearch | `GameSearchResult[]` | 1 h | 6 h | `search:{term}:{cc}` |
| `get_price` | store.appdetails（价格段） | `PriceOverview` | 3 min | 15 min | `price:{appId}:{cc}` |
| `get_market_listings` | community market | `MarketListingsPage` | 5 min | 30 min | `market:{appId}:{start}:{count}` |

`get_game_info_batch` 是 crawl 的执行单元：单项失败不中断整批（逐项进 `errors`），
两次真实 HTTP 之间按 `interval_ms`（默认 500 ms，clamp 0–5000）节流，缓存命中零等待。

## 模型字段

### GameInfo（`get_game_info` / `get_game_info_batch`）

| 字段 | JSON 键 | 类型 | 语义 |
|---|---|---|---|
| AppId | `appId` | uint | Steam AppID |
| Name | `name` | string | 游戏显示名 |
| Type | `type` | string? | 内容类型：game / dlc / application / music 等 |
| Developer | `developer` | string? | 开发商（多个时合并） |
| Publisher | `publisher` | string? | 发行商（多个时合并） |
| ReleaseDate | `releaseDate` | DateTimeOffset? | 发行时间（未知为空） |
| IsFree | `isFree` | bool | 是否免费游玩 |
| RequiresPurchase | `requiresPurchase` | bool | 是否需要购买/订阅才能使用 |
| Price | `price` | PriceOverview? | 当前价格概览 |
| MetacriticScore | `metacriticScore` | int? | Metacritic 评分（缺失为空） |
| RecommendationsTotal | `recommendationsTotal` | long? | 评测总数（好评+差评） |
| Genres | `genres` | string[] | 类型标签（RPG、动作等） |
| Categories | `categories` | string[] | 功能分类（单人/多人/云存档等） |
| HeaderImage | `headerImage` | string? | 页头图 URL |
| SmallCapsuleImage | `smallCapsuleImage` | string? | 小胶囊图 URL |
| ShortDescription | `shortDescription` | string? | 简短描述（源侧已截断） |
| SupportedLanguages | `supportedLanguages` | string? | 支持语言（Steam 原始逗号串） |
| FetchedAt | `fetchedAt` | DateTimeOffset | 快照抓取时间（缓存新鲜度标记） |

### PriceOverview（`get_price` / `GameInfo.price`）

| 字段 | JSON 键 | 类型 | 语义 |
|---|---|---|---|
| Currency | `currency` | string | 币种代码（USD、CNY 等） |
| Final | `final` | decimal? | 折后现价（主货币单位） |
| Initial | `initial` | decimal? | 折扣前原价 |
| DiscountPercent | `discountPercent` | int | 折扣百分比（0–100） |
| FinalFormatted | `finalFormatted` | string? | Steam 返回的格式化现价（展示用） |

### GameSearchResult（`search_games`）

| 字段 | JSON 键 | 类型 | 语义 |
|---|---|---|---|
| AppId | `appId` | uint | Steam AppID |
| Name | `name` | string | 游戏显示名 |
| Type | `type` | string? | 内容类型 |
| IsFree | `isFree` | bool | 是否免费 |
| Price | `price` | PriceOverview? | 价格概览（搜索结果可能缺省） |
| HeaderImage | `headerImage` | string? | 页头图 URL |
| FetchedAt | `fetchedAt` | DateTimeOffset | 快照抓取时间 |

### MarketListing（`get_market_listings`）

| 字段 | JSON 键 | 类型 | 语义 |
|---|---|---|---|
| ListingId | `listingId` | ulong | 挂单 ID（搜索聚合结果为 0） |
| Name | `name` | string? | 物品显示名 |
| HashName | `hashName` | string? | 市场哈希名（搜索聚合的稳定标识） |
| SellListings | `sellListings` | int? | 搜索聚合背后的在售挂单数 |
| AppId | `appId` | uint | 所属应用 |
| AssetId | `assetId` | ulong | 资产 ID（搜索聚合为 0） |
| ClassId | `classId` | ulong | 类别 ID（映射物品描述） |
| InstanceId | `instanceId` | ulong | 实例 ID |
| TotalPrice | `totalPrice` | decimal? | 买方到手总价（含手续费） |
| CurrencyId | `currencyId` | int? | Steam 币种 ID（如 2001 = USD） |
| FetchedAt | `fetchedAt` | DateTimeOffset | 快照抓取时间 |

### MarketListingsPage（`get_market_listings`）

| 字段 | JSON 键 | 类型 | 语义 |
|---|---|---|---|
| AppId | `appId` | uint | 所属应用 |
| Listings | `listings` | MarketListing[] | 本页挂单列表 |
| TotalCount | `totalCount` | int | 符合查询的挂单总数 |
| Start | `start` | int | 本页起始偏移 |
| PageSize | `pageSize` | int | 页大小 |
| HasMore | `hasMore` | bool | 是否还有下一页 |

## 采集（crawl）数据面

计划/轮次/结果的权威落库在控制面独立 DB（`Vapor_CRAWL_DB_PATH`，默认 `data/crawl.db`），
`CrawlRunWorker` 负责到期认领 → 账号池轮转分片（显式 `overrides` 优先）→ 逐分片派发
`get_game_info_batch` → 逐应用落 `crawl_results`。REST（admin）：

| 端点 | 说明 |
|---|---|
| `GET /v1/crawl/plans` | 计划列表 |
| `POST /v1/crawl/plans` | 创建（one-shot 默认立即跑；`cron`/`intervalSeconds` 周期；`startNow=false` 只挂调度） |
| `GET/PUT/DELETE /v1/crawl/plans/{id}` | 单查/部分更新（省略字段保持）/删除（结果保留） |
| `POST /v1/crawl/plans/{id}/trigger` | 立即触发（disabled 400；重叠由 worker 跳过计数） |
| `GET /v1/crawl/plans/{id}/runs?limit=` | 轮次聚合（新→旧，limit clamp 1–100 默认 20） |
| `GET /v1/crawl/results?planId&runId&appId&account&ok&limit&offset` | 逐应用结果查询（limit clamp 1–500 默认 100） |

每轮结果保留 `Vapor_CRAWL_KEEP_RUNS`（默认 10）轮；运行时长超过 `Vapor_CRAWL_RUN_TIMEOUT_SECONDS`
（默认 1800）强制收尾并整片记失败。`wwwroot/gamedata.html` 提供上述数据的只读视图。
