English | [简体中文](zh/data-dictionary/)

# Game Data Field Dictionary

> Authoritative source. The "Field Dictionary" panel in `src/Vapor.ControlPlane/wwwroot/gamedata.html` is maintained in sync with this file; when changing either side, the other side must be updated too (`DashboardStaticTests.GameDataHtml_DocumentsAllFiveModels` only locks the model-name list). Model definitions: `src/Vapor.Steam.Core/Models/GameModels.cs`.

## Data Sources and the No-Credential Constraint

- All data comes from **Steam public store/community endpoints** (`store.appdetails`, storesearch, community market),
  **reachable anonymously**: the related actions set `RequiresLogin: false`, and no secret is touched at any point
  during crawling.
- In multi-account sharded crawling, accounts serve only as **sharding/dispatch/audit attribution** (different
  accounts run on different agents/egresses, spreading rate limits).
- **Hard constraint**: crawl shard tasks are dispatched through the regular job queue; when the payload carries no
  credentials, the agent always replays that account's already-stored session (`AgentTaskExecutor`: no credentials
  → `TryRestoreSessionAsync`, and if nothing is stored the task fails with
  "No credentials provided and no stored session found"). Therefore, accounts in the crawl pool must first complete
  login and session storage on some agent; a single account's credential failure only fails that account's shards and
  does not block other shards.
- Bulk scraping of market listings is not performed (complementary with MarketWatch: alerting plane vs data plane);
  downstream consumers can subscribe to the `crawl.run_completed` event.

## Source Endpoints and Cache Tiers

`FetchCachedAsync` (fresh + stale-while-revalidate) manages all of it uniformly; payloads can override via
`cache_ttl_seconds` (`0` disables caching; the custom fresh stale window is 4×) and `force_refresh`.

| action | Steam Endpoint | Returned Model | fresh | stale | Cache Key |
|---|---|---|---|---|---|
| `get_game_info` | store.appdetails | `GameInfo` | 30 min | 2 h | `game:{appId}:{cc}` |
| `get_game_info_batch` | store.appdetails × N (≤200/batch) | `GameInfo[]` + per-item errors | 30 min | 2 h | Shares tier with single app |
| `search_games` | storesearch | `GameSearchResult[]` | 1 h | 6 h | `search:{term}:{limit}:{cc}` |
| `get_price` | store.appdetails (price section) | `PriceOverview` | 3 min | 15 min | `price:{appId}:{cc}` |
| `get_market_listings` | community market | `MarketListingsPage` | 5 min | 30 min | `market:{appId}:{start}:{count}` |

`get_game_info_batch` is the execution unit of crawling: a single item failure does not abort the whole batch
(per-item into `errors`), throttling applies between real HTTP calls at `interval_ms` (default 500 ms, clamped
0–5000), and cache hits incur zero wait. `term` and `cc` in cache keys are stored lowercase (the `search_games` key
also includes the `limit` segment).

## Model Fields

### GameInfo (`get_game_info` / `get_game_info_batch`)

| Field | JSON Key | Type | Meaning |
|---|---|---|---|
| AppId | `appId` | uint | Steam AppID |
| Name | `name` | string | Game display name |
| Type | `type` | string? | Content type: game / dlc / application / music etc. |
| Developer | `developer` | string? | Developer (merged when multiple) |
| Publisher | `publisher` | string? | Publisher (merged when multiple) |
| ReleaseDate | `releaseDate` | DateTimeOffset? | Release date (null when unknown) |
| IsFree | `isFree` | bool | Free to play |
| RequiresPurchase | `requiresPurchase` | bool | Purchase/subscription required to use |
| Price | `price` | PriceOverview? | Current price overview |
| MetacriticScore | `metacriticScore` | int? | Metacritic score (null when missing) |
| RecommendationsTotal | `recommendationsTotal` | long? | Total review count (positive + negative) |
| Genres | `genres` | string[] | Genre tags (RPG, Action, etc.) |
| Categories | `categories` | string[] | Functional categories (single-player/multi-player/cloud saves, etc.) |
| HeaderImage | `headerImage` | string? | Header image URL |
| SmallCapsuleImage | `smallCapsuleImage` | string? | Small capsule image URL |
| ShortDescription | `shortDescription` | string? | Short description (already truncated on the source side) |
| SupportedLanguages | `supportedLanguages` | string? | Supported languages (Steam's raw comma-separated string) |
| FetchedAt | `fetchedAt` | DateTimeOffset | Snapshot fetch time (cache freshness marker) |

### PriceOverview (`get_price` / `GameInfo.price`)

| Field | JSON Key | Type | Meaning |
|---|---|---|---|
| Currency | `currency` | string | Currency code (USD, CNY, etc.) |
| Final | `final` | decimal? | Current price after discount (in the main currency unit) |
| Initial | `initial` | decimal? | Original price before discount |
| DiscountPercent | `discountPercent` | int | Discount percentage (0–100) |
| FinalFormatted | `finalFormatted` | string? | Formatted current price as returned by Steam (for display) |

### GameSearchResult (`search_games`)

| Field | JSON Key | Type | Meaning |
|---|---|---|---|
| AppId | `appId` | uint | Steam AppID |
| Name | `name` | string | Game display name |
| Type | `type` | string? | Content type |
| IsFree | `isFree` | bool | Free or not |
| Price | `price` | PriceOverview? | Price overview (may be absent in search results) |
| HeaderImage | `headerImage` | string? | Header image URL |
| FetchedAt | `fetchedAt` | DateTimeOffset | Snapshot fetch time |

### MarketListing (`get_market_listings`)

| Field | JSON Key | Type | Meaning |
|---|---|---|---|
| ListingId | `listingId` | ulong | Listing ID (0 for search aggregation results) |
| Name | `name` | string? | Item display name |
| HashName | `hashName` | string? | Market hash name (stable identifier for search aggregation) |
| SellListings | `sellListings` | int? | Number of active sell listings behind a search aggregation |
| AppId | `appId` | uint | Owning app |
| AssetId | `assetId` | ulong | Asset ID (0 for search aggregation) |
| ClassId | `classId` | ulong | Class ID (maps to item description) |
| InstanceId | `instanceId` | ulong | Instance ID |
| TotalPrice | `totalPrice` | decimal? | Total price paid by the buyer (including fees) |
| CurrencyId | `currencyId` | int? | Steam currency ID (e.g. 2001 = USD) |
| FetchedAt | `fetchedAt` | DateTimeOffset | Snapshot fetch time |

### MarketListingsPage (`get_market_listings`)

| Field | JSON Key | Type | Meaning |
|---|---|---|---|
| AppId | `appId` | uint | Owning app |
| Listings | `listings` | MarketListing[] | Listing list for this page |
| TotalCount | `totalCount` | int | Total listings matching the query |
| Start | `start` | int | Start offset of this page |
| PageSize | `pageSize` | int | Page size |
| HasMore | `hasMore` | bool | Whether another page follows |

## Crawl Data Plane

The authoritative persistence of plans/runs/results lives in the control plane's separate DB (`Vapor_CRAWL_DB_PATH`,
default `data/crawl.db`); `CrawlRunWorker` handles due-claim → account-pool rotation sharding (explicit `overrides`
take priority) → per-shard dispatch of `get_game_info_batch` → per-app writes to `crawl_results`. REST (admin):

| Endpoint | Description |
|---|---|
| `GET /v1/crawl/plans` | Plan list |
| `POST /v1/crawl/plans` | Create (one-shot runs immediately by default; `cron`/`intervalSeconds` for periodic; `startNow=false` only schedules) |
| `GET/PUT/DELETE /v1/crawl/plans/{id}` | Get / partial update (omitted fields keep their values) / delete (results retained) |
| `POST /v1/crawl/plans/{id}/trigger` | Trigger immediately (400 when disabled; overlaps are skipped-and-counted by the worker) |
| `GET /v1/crawl/plans/{id}/runs?limit=` | Run aggregates (newest first, limit clamped 1–100, default 20) |
| `GET /v1/crawl/results?planId&runId&appId&account&ok&limit&offset` | Per-app result query (limit clamped 1–500, default 100) |

Each run's results are kept for `Vapor_CRAWL_KEEP_RUNS` (default 10) runs; a run exceeding
`VAPOR_CRAWL_RUN_TIMEOUT_SECONDS` (default 1800) is force-finalized and the whole shard is recorded as failed.
`wwwroot/gamedata.html` provides a read-only view of the above data.
