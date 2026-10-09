# Actions catalog

Actions are the unit of work an agent executes: `POST /v1/jobs` names an action, a target set and an optional `payload`, and every target account (or agent) runs it on its own session. This catalog documents all **58 shipped actions** — 20 host actions from the Steam.Core assembly, 16 from the Game Access plugin, 9 from the Mobile Authenticator plugin, 3 from Market Watch, 1 from Monitoring, 4 from Game Data and 5 agent host actions — with their payload keys and output dictionaries, as read from the implementation.

The HTTP side of this (job envelope, dispatch, scheduling, reading results over REST/SSE) is in [api.md](api.md), "Jobs & tasks" section; a guided tour is in [getting-started.md](getting-started.md). Payload examples below are for `POST /v1/jobs` bodies.

Source map, for reference: host Steam-domain actions live in `src/Vapor.Steam.Core/Actions/*.cs`, the game-access actions in the official `src/Vapor.Plugins.GameAccess/` plugin (bundled into the agent image; same names), agent host actions in `src/Vapor.Agent/HostActions/*.cs`, the remaining plugin actions in `src/Vapor.Plugins.*`, and the job envelope in `src/Vapor.ControlPlane/Program.cs` + `src/Vapor.Protocol/Models.cs`. Field names below are copied verbatim from the payload-parsing / output-building code.

## Conventions

- Payload key naming: the control-plane envelope is camelCase (`action`, `payload`, `targets`, …), but the *keys inside `payload`* are written **snake_case** in the action code (`app_id`, `steam_id`, `items_to_give`, …). Most lookups go through `PayloadReader`, which matches keys **case-insensitively**, so `appIds` also resolves to `app_id`. Exception: `send_trade_offer`'s `items_to_give` / `items_to_receive` (and their nested fields) are read with an exact, case-**sensitive** dictionary lookup. This catalog records the exact keys as written in code.
- Output key naming: output dictionary keys are literal (mostly snake_case; a few actions use camelCase keys — noted per action). Values that are embedded .NET model objects (e.g. `GameInfo`, `PriceOverview`) serialize with the standard camelCase JSON policy.
- Payload value types: after the SQLite/WebSocket round-trip, payload values arrive as `JsonElement`s; every parser accepts numbers, strings, arrays or a single bare value unless noted.
- Metadata: every action declares `RequiresLogin` and `TimeoutSeconds`; noted as `login: yes/no`, `timeout: Ns`. `login: yes` actions need the account's session to be logged in; dispatching them to an offline account fails the task with a login error.
- Execution safety: every action declares an `ActionSafety` class (noted as `safety:` in each header) — `ReadOnly` (never mutates anything), `Idempotent` (repeating converges to the same state), `GuardedWrite` (mutates, but a bounded retry is safe), `NonIdempotent` (repeating doubles the external side effect). The source of truth is the `IAction.Metadata.Safety` annotation next to each action; the control plane mirrors it in `ActionSemantics.cs` and bounds redispatch accordingly: `GuardedWrite`/`NonIdempotent` (and unclassified/`Unknown` actions, e.g. third-party plugins) cap dispatch attempts at 2 regardless of the configured ceiling, while `ReadOnly`/`Idempotent` actions use the configured ceiling unchanged. The three surfaces are kept in agreement by `scripts/verify-actions-safety.py`.

---

## Generic job invocation envelope

Create a job — `POST /v1/jobs` (admin auth header; `CreateJobRequest` in `src/Vapor.Protocol/Models.cs`):

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

| Field | Type | Required | Notes |
|---|---|---|---|
| `action` | string | yes | Exact action name (see catalog below); blank → 400. |
| `targets` | string[] | yes (≥1) | One task is fanned out **per target**. A target is normally an **account name**; for host actions it is the machine selector `"agent:{agentId}"`. |
| `region` | string? | no | Empty string when omitted; the scheduler only dispatches to agents in this region. |
| `payload` | object? | no | Free-form dict handed to the action; keys per action below. |
| `meta` | object? | no | String-valued metadata stored with the job. |
| `schedule` | object? | no | When present the job becomes a **recurring template** (status `scheduled`): either `intervalSeconds` or `cron` (5-field, UTC; cron wins). `missed`: `skip` (default) \| `runOnce`; `overlap`: `skip` (default) \| `allow`. |

Response: `202 Accepted`, `Location: /v1/jobs/{id}`, body `{ "job": { id, action, region, targets, meta, status, createdAt, updatedAt, schedule?, nextRunAt? } }`. Status enum: `queued | running | scheduled | finished | failed | canceled`.

Dispatch: `TaskSchedulerService` polls every 250 ms; account-targeted tasks go to the deterministic pick (lowest agent id) among the region's connected agents that advertise the action in their hello `capabilities`; `agent:{id}` targets go to that exact agent (capability-checked).

Read results — `GET /v1/jobs/{jobId}` returns `JobWithTasks`:

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

Per-task `status`: `queued | running | finished | failed | canceled`; `output` is the action's result dictionary documented below; `error` carries the action's error string on failure. Companion endpoints: `GET /v1/jobs` (list; `limit` clamped 1–500, default 50; `account` filter), `GET /v1/jobs/{jobId}/events` (SSE: `job.created`, `task.dispatched`, `task.*`), `POST /v1/jobs/{jobId}/cancel`.

Most REST wrappers under `/v1/accounts/{name}/...` are thin: they build the payload for one of these actions and dispatch a one-target job, returning `202 { job_id, status }` (or the task output inline when it finishes fast).

## Registry / namespacing note

- `ActionRegistry` (`src/Vapor.Steam.Core/IAction.cs`) is a single flat, case-insensitive dictionary. Core actions are registered at agent startup; plugin actions are registered into the *same* dictionary via `PluginManager.PluginLoaded` (`actionRegistry.Register(action)`) and removed on `PluginUnloading`.
- There is no namespace prefixing. Plugin action names (`market_watch_add`, `get_metrics`, …) live alongside core names (`get_inventory`, …). Collisions are not detected: a plugin action whose `Name` equals a core action's name **silently overwrites** it (last writer wins), and when the plugin unloads, the name is unregistered entirely — the shadowed core action does not come back. Convention: plugins prefix their names (`market_watch_*`, `get_metrics`) and none of the shipped plugins collide with core names.
- Agent host actions (`plugin_install` / `plugin_uninstall` / `plugin_list` / `plugin_update_check`) are *not* in the registry; they live in a separate dictionary checked before the registry in the agent's task executor, and are advertised in hello capabilities like any other action. They additionally require the task target to be exactly `agent:{thisAgentId}` (defense in depth — a misrouted delivery fails loudly instead of mutating the wrong machine's plugin directory).

---

# Steam.Core actions (`Vapor.Steam.Core` assembly, `src/Vapor.Steam.Core/Actions/`)


### `login` — `LoginAction.cs` (login: no, timeout: 60s, safety: Idempotent)
Logs the account's session in to Steam. No payload fields.
Output: `account`, `state`, `action`.

### `echo` — `EchoAction.cs` (login: no, timeout: 10s, safety: ReadOnly)
Echoes the payload back. Payload: anything (passed through).
Output: `echo` (the payload dict), `account`.

### `ping` — `PingAction.cs` (login: no, timeout: 10s, safety: ReadOnly)
Liveness probe. No payload fields.
Output: `pong` (always `true`), `account`, `state`, `timestamp` (unix seconds).

### `idle` — `IdleAction.cs` (login: yes, timeout: 300s, safety: Idempotent)
Simulates being online for a period (returns immediately; the session stays idle).
Payload: `duration` int, optional, default `60` (seconds).
Output: `action` (`"idle"`), `duration`, `state`.


### `get_trade_offers` — `GetTradeOffersAction.cs` (login: yes, timeout: 30s, safety: ReadOnly)
Lists incoming and outgoing trade offers (IEconService). Read side of the trade loop.
Payload: `active_only` bool, optional, default `true`.
Output: `active_only`, `sent_count`, `received_count`, `sent_offers[]` / `received_offers[]` `{ trade_offer_id, partner_steam_id, is_our_offer, state, items_to_give_count, items_to_receive_count, time_created, items_to_give[], items_to_receive[], message?, time_expires? }`, where each asset entry is `{ app_id, context_id, asset_id, class_id, instance_id, amount, is_currency }`.

### `send_trade_offer` — `TradeOfferActions.cs` (`SendTradeOfferAction`) (login: yes, timeout: 60s, safety: NonIdempotent)
Sends a trade offer to another Steam user.
Payload:
- `partner_steam_id` string, optional — SteamID64 of the partner (**either this or `trade_url` is required**).
- `trade_url` string, optional — token-bearing trade URL (lets Steam deliver to a non-friend); partner id and `token` are extracted from it.
- `token` string, optional — trade token when using `partner_steam_id`.
- `message` string, optional — offer message.
- `items_to_give` array, optional — entries `{ app_id, context_id, asset_id, amount }`; defaults per entry: `app_id` 730, `context_id` 2, `amount` 1; `asset_id` required (an entry with missing/zero `asset_id` is silently dropped). Keys here are matched **case-sensitively** (plain dictionary read).
- `items_to_receive` array, optional — same entry shape.
- `skip_verification` bool, optional (default `false`) — **safety gate**: bypasses pre-send ownership verification (item exists, tradable, off cooldown, sufficient quantity in the sender's inventory).
Output: `trade_offer_id`, `partner_steam_id`, `requires_mobile_confirmation`, `ownership_verified` (bool).
Safety: per-account **trade rate limiter** (`TradeRateLimiter` lease; refusal error "Trade rate limit exceeded for this account, try again later"); verification pagination capped at 50 pages.

### `accept_trade_offer` — `TradeOfferActions.cs` (`AcceptTradeOfferAction`) (login: yes, timeout: 30s, safety: GuardedWrite)
Accepts a received trade offer.
Payload: `trade_offer_id` string **required** (ulong); `partner_steam_id` string **required** (expected partner); `verify_state` bool, optional, default `true` — set `false` to **bypass** state-machine verification (offer Active, received-not-sent, not expired, from the expected partner).
Output: `trade_offer_id`, `requires_mobile_confirmation`, `state_verified`. Rate-limited like `send_trade_offer`.

### `decline_trade_offer` — `TradeOfferActions.cs` (`DeclineTradeOfferAction`) (login: yes, timeout: 30s, safety: GuardedWrite)
Declines a received offer.
Payload: `trade_offer_id` string **required**; `verify_state` bool optional default `true` (validates the offer is received + Active; `false` bypasses).
Output: `trade_offer_id`, `state_verified`. Rate-limited.

### `cancel_trade_offer` — `TradeOfferActions.cs` (`CancelTradeOfferAction`) (login: yes, timeout: 30s, safety: GuardedWrite)
Cancels an offer the account sent.
Payload: `trade_offer_id` string **required**; `verify_state` bool optional default `true` (validates the offer was sent by us and is still Active; `false` bypasses).
Output: `trade_offer_id`, `state_verified`. Rate-limited.


### `get_my_market_listings` — `GetMyMarketListingsAction.cs` (login: yes, timeout: 30s, safety: ReadOnly)
Lists the account's own market listings (login-gated mylistings page — the only source for own-listing ids and the fee split). One page per dispatch; page by issuing further dispatches until `start` reaches `total_count`.
Payload: `start` int optional default `0`; `count` int optional default `100`.
Output: `start`, `count`, `total_count`, `active_count`, `on_hold_count`, `to_be_confirmed_count`, `listings[]` `{ listing_id, app_id, context_id, asset_id, class_id, market_hash_name, market_name, game_name, price_cents, fee_cents, seller_proceeds_cents, currency_id, icon_url, time_created, cancel_requested }`.
(Distinct from `get_market_listings`, the public per-app market search.)

### `create_market_listing` — `CreateMarketListingAction.cs` (login: yes, timeout: 120s, safety: NonIdempotent)
Puts one inventory item up for sale — the ToS-gray-zone core of the market loop, triple-gated:
1. `send` defaults to `false` → **dry run** that only computes the fee-aware pricing plan (zero requests to Steam).
2. A real listing additionally requires this agent's explicit opt-in env `AGENT_MARKET_LISTINGS_ENABLED=true` — a direct dispatch with `send=true` is refused without it.
3. And the per-account switch (control-plane account spec `marketListingsEnabled=true`, enforced by `POST /v1/accounts/{name}/market/listings` before dispatch; default off).
Payload:
- `app_id` int **required** (positive).
- `context_id` string **required** (e.g. `"6"` for the community inventory).
- `asset_id` string **required** (from `get_inventory` / `get_my_market_listings` output).
- `amount` int optional default `1` (must be ≥ 1).
- `seller_proceeds_cents` int / `buyer_price_cents` int — **exactly one required** (≥ 1). A buyer price is walked down to the largest seller amount Steam's fee model allows; below the minimum (3-cent buyer price) it fails.
- `send` bool optional default `false` — `true` performs the real listing.
Output (dry run): `dry_run: true`, `would_list: true`, `asset` `{ app_id, context_id, asset_id, amount }`, `pricing` `{ seller_proceeds_cents, steam_fee_cents, publisher_fee_cents, buyer_price_cents }`.
Output (real): `dry_run: false`, `asset`, `pricing`, `success`, `requires_confirmation`, `needs_mobile_confirmation`, `needs_email_confirmation`, `message?`, `email_domain?`.
Safety: prices come from the caller only (no auto-repricing); Steam-side rate limits surface as the task error with the full reply kept in output; a mobile/email confirmation requirement is reported back — confirming it is `confirm_all_confirmations` (type `market`)'s job.

### `cancel_market_listings` — `CancelMarketListingsAction.cs` (login: yes, timeout: 600s, safety: GuardedWrite)
Bulk-cancels the account's own listings selected by filter; every matched listing is attempted individually (one failure does not abort the batch).
Payload:
- `app_id` int optional; `market_hash_name` string optional; `min_price_cents` / `max_price_cents` int optional (not negative, min ≤ max); `older_than_seconds` int optional (>0) — cancel only listings created before now minus this.
- `dry_run` bool optional, **default `true`** — a dry run only reports what *would* be canceled; a real run (`dry_run=false`) **with no filter at all is refused** (fat-finger guard).
- `delay_ms` int optional default `1000` — pacing between cancellations (market-page conservatism; not applied in dry runs).
Output: `dry_run`, `matched`, `scanned`, `succeeded` (null in dry runs), `failed` (null in dry runs), `listings[]` `{ listing_id, market_hash_name, price_cents, would_cancel | succeeded }`.
Safety: 20 pages × 500 listings defensive fetch cap.


### `get_game_info` — `DataActions.cs` (`GetGameInfoAction`) (login: no, timeout: 30s, safety: ReadOnly)
Full store details for one game.
Payload: `app_id` string **required** (positive); `cc` string optional default `"us"`; `cache_ttl_seconds`; `force_refresh`.
Output: `game` (GameInfo object — camelCase JSON: `appId`, `name`, `type`, `developer`, `publisher`, `releaseDate`, `isFree`, `requiresPurchase`, `price` {currency, final, initial, discountPercent, finalFormatted}, `metacriticScore`, `recommendationsTotal`, `genres`, `categories`, `headerImage`, `smallCapsuleImage`, `shortDescription`, `supportedLanguages`, `fetchedAt`), `cache_key`.

### `get_game_info_batch` — `DataActions.cs` (`GetGameInfoBatchAction`) (login: no, timeout: 240s, safety: ReadOnly)
Store details for a batch; **per-app errors do not abort the batch** (succeeds if ≥ 1 app resolves).
Payload: `app_ids` **required** — CSV string or array (numbers/strings), max **200** entries, zero/invalid entries fail the whole call; `cc` optional default `"us"`; `interval_ms` int optional default `500` (clamped 0–5000; pacing gap only between real store fetches, skipped on cache hits); `cache_ttl_seconds`; `force_refresh`.
Output: `total_count`, `fetched`, `failed`, `games[]` (GameInfo), `errors[]` `{ app_id, error }`, `cc`.

### `search_games` — `DataActions.cs` (`SearchGamesAction`) (login: no, timeout: 30s, safety: ReadOnly)
Searches the store catalog.
Payload: `term` string **required**; `limit` int optional default `20`; `cc` optional; cache opts.
Output: `term`, `total_count`, `results[]` (GameSearchResult: `appId`, `name`, `type`, `isFree`, `price`, `headerImage`, `fetchedAt`).

### `get_price` — `DataActions.cs` (`GetPriceAction`) (login: no, timeout: 30s, safety: ReadOnly)
Current price overview for one game.
Payload: `app_id` string **required**; `cc` optional default `"us"`; cache opts.
Output: `app_id`, `price` (PriceOverview: `currency`, `final`, `initial`, `discountPercent`, `finalFormatted`), `cache_key`.

### `get_market_listings` — `DataActions.cs` (`GetMarketListingsAction`) (login: no, timeout: 30s, safety: ReadOnly)
Public Community Market listings for a game.
Payload: `app_id` string **required**; `start` int optional default `0`; `count` int optional default `20` (clamped 1–100); cache opts.
Output: `app_id`, `total_count`, `start`, `page_size`, `has_more`, `listings[]` (MarketListing objects: `listingId`, `name`, `hashName`, `sellListings`, `appId`, `assetId`, `classId`, `instanceId`, `totalPrice`, `currencyId`, `fetchedAt`).

### `cache_invalidate` — `DataActions.cs` (`InvalidateCacheAction`) (login: no, timeout: 10s, safety: Idempotent)
Invalidates cached store data by key prefix or clears everything (lets jobs expire stale tiers without restarting the agent).
Payload: `prefix` string optional (e.g. `"price:730"`); `clear_all` bool optional (`true` wipes the whole cache). One of the two is required.
Output: `prefix` (null on clear-all), `cleared_all`, `removed` (entry count). Fails when the agent has no cache configured.


### `check_account_standing` — `CheckAccountStandingAction.cs` (login: yes, timeout: 60s, safety: ReadOnly)
Health probe for ban/standing state (GetPlayerBans + limited-account marker). Orchestration quarantines accounts from trade/market work based on `standing`.
Payload: `steam_id` string optional (SteamID64, ≥ 76561197960265728); defaults to the session's own.
Output (**note camelCase keys**): `account`, `steamId`, `standing` (`"banned"` | `"restricted"` | `"clean"` — any VAC/community/game/economy ban → banned; economy probation → restricted), `vacBanned`, `numberOfVacBans`, `numberOfGameBans`, `daysSinceLastBan`, `communityBanned`, `economyBan`, `limited`, `steamLevel`.

### `check_proxy` — `CheckProxyAction.cs` (login: no, timeout: 30s, safety: ReadOnly)
Connectivity self-check for the account's egress proxy: opens the same proxied HTTP stack the session uses and probes exit IP + Steam reachability.
Payload: `proxy` string optional — proxy URL; defaults to the session's configured proxy. When neither exists the action succeeds with "no proxy".
Output (no proxy): `proxyEnabled: false`, `account`.
Output: `proxyEnabled`, `proxy` (**always the masked form — credentials never leave the agent**), `scheme`, `account`, `exitIp`, `steamReachable`, `error`, `latencyMs` (when measured).
Note: exit IP via `api.ipify.org`, then `steamcommunity.com` reachability; success requires both.

---

# Game Access plugin actions (`Vapor.Plugins.GameAccess` official plugin, `src/Vapor.Plugins.GameAccess/`)

The fourteen game-access actions shipped inside the host until the plugin split; action names and payload schemas are unchanged, the agent Docker image bundles the plugin, and a host without it simply stops advertising these names in hello (dispatch fails with a capability mismatch / `action not found`, never a silent no-op). See the plugin README for the split contract.

## Farming / playtime

### `play_games` — `src/Vapor.Plugins.GameAccess/PlayGamesAction.cs` (login: yes, timeout: 30s, safety: Idempotent)
Starts or stops playing games on the session.
Payload:
- `games` string, optional — CSV of AppIDs; also accepts the `id/12345` per-item format (`"12345,67890"`, `"id/220,730"`).
- `action` string, optional — `play` (default) | `stop` | `idle`. `idle` and `stop` both stop all games. At least one of `games`/`action` is required.
Output: `action` (`"play"` | `"stop"`), `games` (set of AppIDs, play only), `account`.

### `get_card_drops` — `src/Vapor.Plugins.GameAccess/GetCardDropsAction.cs` (login: yes, timeout: 120s, safety: ReadOnly)
Lists games with remaining trading-card drops (community badges page); stale-while-revalidate cached (default TTL 10 min, stale 30 min).
Payload:
- `steam_id` string, optional — SteamID64; defaults to the session's own SteamID resolved from its cookies.
- `cache_ttl_seconds` int, optional — `0` disables caching entirely; `>0` overrides TTL (stale window derived).
- `force_refresh` bool, optional (default `false`) — bypass the cache and repopulate it.
Output: `steam_id`, `apps_with_drops`, `total_drops_remaining`, `drops[]` `{ app_id, name, drops_remaining }` (sorted by drops desc, then app id).

### `get_playtime` — `src/Vapor.Plugins.GameAccess/GetPlaytimeAction.cs` (login: yes, timeout: 120s, safety: ReadOnly)
Owned games with total playtime (profile games tab); cached (default TTL 30 min, stale 60 min).
Payload: `steam_id` (optional, as above), `games` string optional (CSV AppID filter, same parser as `play_games`), `cache_ttl_seconds`, `force_refresh` (same semantics as `get_card_drops`).
Output: `steam_id`, `games_count`, `total_hours`, `playtimes[]` `{ app_id, name, hours }`.

## Licenses & keys

### `add_license` — `src/Vapor.Plugins.GameAccess/AddLicenseAction.cs` (login: yes, timeout: 120s, safety: Idempotent)
Claims free Steam content on the account (addlicense flow). App IDs go through the client protocol (free-on-demand apps); sub IDs through the store checkout endpoint the website's "Add to account" button uses.
Payload:
- `app_ids` uint array, optional — client-protocol free license (needs a connected Steam client).
- `sub_ids` uint array, optional — store checkout (needs a web session). At least one of the two is required; entries must be `> 0`, deduplicated; a single bare value is accepted.
Output: `app_ids`, `sub_ids`, `apps_result` (Steam result string), `granted_app_ids`, `granted_package_ids` (when granted), `purchases[]` `{ id, success, detail }` (sub path).

### `redeem_key` — `src/Vapor.Plugins.GameAccess/RedeemKeyAction.cs` (login: yes, timeout: 60s, safety: GuardedWrite)
Redeems a Steam product key; retries transient failures up to 3 attempts (250 ms × attempt backoff).
Payload: `key` string, **required**.
Output: `action`, `key` (**masked** — middle segments `*`-ed), `result` (Steam result name), `resultCode` (int), `attempts`, `success`, `requestId`, `durationMs`, `grantedAppIds`, `grantedPackageIds`, `receiptDetails` (each present when non-empty/non-null).
Safety: `RateLimitExceeded` surfaces as "Too many key redemption attempts"; `AlreadyOwned` and `DuplicateRequest` count as success.

## Achievements

### `get_achievements` — `src/Vapor.Plugins.GameAccess/GetAchievementsAction.cs` (login: yes, timeout: 120s, safety: ReadOnly)
Lists one game's achievements with unlock state (community stats page). Read-only; `api_name` values are the write-side identifiers for `unlock_achievements`.
Payload: `app_id` string, **required** (positive); `steam_id` string optional (session default).
Output: `steam_id`, `app_id`, `unlocked_count`, `total_count`, `achievements[]` `{ api_name, display_name, description, unlocked, icon_url }`.

### `unlock_achievements` — `src/Vapor.Plugins.GameAccess/UnlockAchievementsAction.cs` (login: yes, timeout: 180s, safety: Idempotent)
Sets the named achievements to unlocked via the client stats protocol. **No "unlock everything" path exists** — names must be explicit.
Payload: `app_id` string **required** (positive); `names` string array **required, non-empty** (deduplicated case-insensitively; a single string is accepted).
Output: `app_id`, `unlock` (`true`), `requested_count`, `results[]` `{ name, success, detail }`, `succeeded_count`, `failed_count`, `verified`. One failure never stops the rest.

### `reset_achievements` — `src/Vapor.Plugins.GameAccess/UnlockAchievementsAction.cs` (`ResetAchievementsAction` class) (login: yes, timeout: 180s, safety: GuardedWrite)
Clears the named achievements. **Destructive; double-gated**: requires an explicit non-empty `names` list **and** `confirm: true`, enforced independently at the action layer *and* the control-plane API.
Payload: `app_id` (as above), `names` (as above), `confirm` bool — must be explicitly `true`.
Output: same shape as `unlock_achievements` with `unlock: false`.

## Inventory (plugin half)

### `get_inventory` — `src/Vapor.Plugins.GameAccess/GetInventoryAction.cs` (login: yes, timeout: 60s, safety: ReadOnly)
Reads a Steam inventory. Two modes: classic single app or an `app_ids` multi-app scan (max 5 apps; context rules: 753 → 6, else 2).
Payload:
- `steam_id` string, optional (session default).
- `app_ids` uint array, optional — multi-app scan; takes precedence over the classic pair; max 5 per call.
- `app_id` string, optional — default `730` (CS2).
- `context_id` string, optional — default `2`.
- `tradable_only` bool, optional (default `false`) — keep only items tradable right now.
- `marketable_only` bool, optional (default `false`) — keep only marketable items.
Output (classic): `steam_id`, `app_id`, `context_id`, `total_count`, `items[]` `{ asset_id, class_id, instance_id, app_id, amount, name, market_name, market_hash_name, type, tradable, marketable }`.
Output (multi-app): `steam_id`, `total_count`, `items[]` (same shape), `apps[]` `{ app_id, context_id, item_count }`. Pagination is capped defensively at 50 000 items.

### `get_game_inventory` — `src/Vapor.Plugins.GameAccess/GetGameInventoryAction.cs` (login: yes, timeout: 60s, safety: ReadOnly)
The game-economy view of one app's inventory: pages the same source as `get_inventory`, then folds items into per-(class, instance) stacks — counts, tradable/marketable splits — largest first. Read-only: no trade or market call is ever made.
Payload:
- `steam_id` string, optional (session default).
- `app_id` string, optional — default `730` (CS2); Dota 2 is `570`.
- `context_id` string, optional — default `2`.
- `tradable_only` / `marketable_only` bool, optional (default `false`) — filtered out before aggregation.
- `value` bool, optional (default `false`) — attach market valuation.
- `currency` string, optional — priceoverview currency id, passed through when valuing.
- `cc` string, optional — country code for the app-level price (default `us`).
Output: `steam_id`, `app_id`, `context_id` (string), `total_items`, `stack_count`, `stacks[]` `{ class_id, instance_id, name, market_name, market_hash_name, type, count, tradable_count, marketable_count, amount_total }`; with `value=true` also `valuation` `{ currency, priced_stacks, unpriced_stacks, truncated, items[] { market_hash_name, count, lowest_price, median_price, volume, error }, game_price }`. Prices come from the community market `priceoverview` endpoint per stack — cached under the `SteamCacheTtl.Price` tier, capped at the 50 largest marketable stacks (past-cap stacks are skipped, not failed) — and are returned verbatim as Steam formats them (currency symbol included, never parsed into numbers). `game_price` mirrors `get_price` (same endpoint, same `price:{appId}:{cc}` cache key, so the two actions share one warm cache) and is supplementary: a store failure lands in `game_price.error` without sinking the per-stack valuation.

### `get_item_details` — `src/Vapor.Plugins.GameAccess/GetItemDetailsAction.cs` (login: yes, timeout: 60s, safety: ReadOnly)
Per-item metadata and valuation for specific inventory items: give `asset_ids` (exact items) or `class_ids` (all items of a kind) and the action pages the inventory once and returns the matching entries, optionally with market prices. Read-only: no trade or market call is ever made.
Payload:
- `steam_id` string, optional (session default).
- `app_id` string, optional — default `730`; `context_id` string, optional — default `2`.
- `asset_ids` / `class_ids` uint64 list (a single scalar is accepted) — at least one id required; unparsable or non-positive entries are dropped silently, duplicates deduplicated.
- `value` bool, optional (default `false`) — attach market prices per item; `currency` string, optional.
Output: `steam_id`, `app_id`, `context_id` (string), `matched_count`, `items[]` `{ asset_id, class_id, instance_id, app_id, amount, name, market_name, market_hash_name, type, tradable, marketable }` (64-bit ids as strings); with `value=true` also `valuation` `{ currency, priced_count, failed_count, items[] { asset_id, market_hash_name, lowest_price, median_price, volume, error } }` — non-marketable entries carry `error: "not marketable"` and get no price lookup; prices as in `get_game_inventory`.

### `loot_inventory` — `src/Vapor.Plugins.GameAccess/LootAction.cs` (`LootInventoryAction`) (login: yes, timeout: 120s, safety: GuardedWrite)
Sends all of the account's currently-tradable items to a partner (ASF/Watt "loot" flow).
Payload:
- `partner_steam_id` string or `trade_url` string — one **required**.
- `message` string, optional.
- `app_ids` uint array, optional — **default `[753]`** (Steam community items — where trading cards land); max 5 apps; 50 inventory pages per app cap.
Output: `trade_offer_id`, `partner_steam_id`, `item_count`, `apps_scanned[]` `{ app_id, context_id, tradable_items }`, `requires_mobile_confirmation`.
Safety: only items tradable *now* are offered; rate-limiter lease before sending; fails when nothing tradable was found.

### `find_duplicates` — `src/Vapor.Plugins.GameAccess/FindDuplicatesAction.cs` (login: yes, timeout: 60s, safety: ReadOnly)
Scans inventories for duplicate items and reports the tradable copies beyond `keep` (TradeMatcher-style analysis; no offer is sent).
Payload: `app_ids` uint array optional (default `[753]`, max 5); `keep` int optional, default `1`, must be 1–100.
Output: `steam_id`, `keep`, `apps_scanned[]` `{ app_id, context_id, scanned_items }`, `duplicates[]` `{ app_id, class_id, instance_id, name, total, excess_count, excess_asset_ids }`, `excess_count`.

### `swap_duplicates` — `src/Vapor.Plugins.GameAccess/SwapDuplicatesAction.cs` (login: yes, timeout: 120s, safety: NonIdempotent)
Pairs the account's duplicate copies against the partner's duplicates of cards the account lacks (and vice versa); **dry run by default**, sends a symmetric 1:1 offer with `send=true`.
Payload:
- `partner_steam_id` string or `trade_url` string — one **required**; must differ from the account's own SteamID.
- `message` string, optional.
- `send` bool, optional (default `false`) — **the dry-run switch**; `false` reports matches only, `true` sends the offer.
- `app_ids` uint array optional (default `[753]`, max 5); `keep` int 1–100 default `1`; `max_swaps` int 1–100 default `25`.
Output: `partner_steam_id`, `keep`, `matches[]` `{ give: {app_id, context_id, asset_id, class_id, instance_id, name}, receive: {…} }`, `give_count`, `receive_count`, `dry_run`; when sent also `trade_offer_id`, `requires_mobile_confirmation`.
Safety: dry-run default; rate-limiter when sending.

## Points shop

### `get_points_shop_summary` — `src/Vapor.Plugins.GameAccess/GetPointsShopSummaryAction.cs` (login: yes, timeout: 60s, safety: ReadOnly)
Points-shop balance and reward definitions (discovery feed for `claim_points_shop_items`; free claimables are `point_cost == 0`).
Payload: `definition_ids` uint array optional (look up specific definitions); `free_only` bool optional default `false` (client-side filter; `items_total` still reports what Steam returned).
Output: `points`, `points_earned`, `points_spent`, `items[]` `{ defid, app_id, point_cost, active, type?, description?, free_until? }` (optional keys present when non-default), `items_total`.

### `claim_points_shop_items` — `src/Vapor.Plugins.GameAccess/ClaimPointsShopItemsAction.cs` (login: yes, timeout: 120s, safety: GuardedWrite)
Redeems points-shop reward definitions (the points-shop flavor of `add_license`; mirrors ASF "RP").
Payload:
- `definition_ids` uint array **required**.
- `force` bool optional default `false` — **safety gate**: without `force` the whole batch is validated first and **any paid definition (`point_cost != 0`) or unknown id rejects the batch before anything is redeemed**; with `force=true` paid items are redeemed too.
Output: `force`, `requested`, `succeeded`, `failed`, `results[]` `{ defid, success, result, community_item_id? }` (64-bit id kept as string), `points_after` (best-effort balance refresh).
# Plugin-contributed actions


### `generate_totp` (login: no, timeout: 15s, safety: ReadOnly)
Current Steam mobile authenticator code from a base64 shared secret (uses the synced Steam time offset when available).
Payload: `shared_secret` string **required** (alias `sharedSecret`); `time` long optional (unix seconds, for testing).
Output: `code`, `seconds_remaining`, `time`, `time_synced`.

### `generate_confirmation_hash` (login: no, timeout: 15s, safety: ReadOnly)
HMAC-SHA1 confirmation hash for the /mobileconf endpoints.
Payload: `identity_secret` string **required** (alias `identitySecret`); `tag` string optional default `"conf"` — must be one of `conf | details | allow | cancel`; `time` long optional.
Output: `hash`, `tag`, `time`, `time_synced`.

### `sync_steam_time` (login: no, timeout: 30s, safety: Idempotent)
Queries Steam's QueryTime endpoint and stores the local/server clock offset (used by all TOTP/hash generation). No payload fields.
Output: `offset_seconds`, `steam_time`, `synced_at`.

### `get_trade_confirmations` (login: yes, timeout: 30s, safety: ReadOnly)
Lists pending mobile trade/market confirmations for the session account.
Payload: `identity_secret` string **required** (alias `identitySecret`).
Output: `count`, `confirmations[]` `{ id, nonce, creator_id, headline, summary }` (64-bit ids as strings).

### `respond_trade_confirmation` (login: yes, timeout: 30s, safety: NonIdempotent)
Accepts (`allow`) or cancels one pending mobile confirmation.
Payload: `identity_secret` string **required**; `confirmation_id` **required** (positive integer); `nonce` **required** (positive integer); `operation` string optional default `"allow"` — `allow | cancel`.
Output: `confirmation_id`, `operation`.

### `save_shared_secret` (login: no, timeout: 10s, safety: GuardedWrite)
Stores the account's authenticator shared secret in the agent's **encrypted credential store** so 2FA challenges can be answered locally (also feeds the `AGENT_2FA_AUTO_SUBMIT` auto-responder).
Payload: `shared_secret` string **required**.
Output: `account`, `stored: true`.
Safety: the secret stays agent-side.

### `save_identity_secret` (login: no, timeout: 10s, safety: GuardedWrite)
Stores the identity secret for local trade-confirmation signing. The secret never leaves the agent — confirmation tasks are dispatched without it and the store is read at execution time.
Payload: `identity_secret` string **required**.
Output: `account`, `stored: true`.

### `confirm_trade_offer` (login: yes, timeout: 60s, safety: GuardedWrite)
Completes the mobile-confirmation step of a trade offer **using the stored identity secret** (never payload-supplied, never in task records). Lists pending confirmations, matches the one whose creator id equals the trade offer id (polls up to 6 × 250 ms — Steam lags a few seconds behind offer acceptance), then responds.
Payload: `trade_offer_id` **required** (positive integer); `operation` string optional default `"allow"` — `allow | cancel`.
Output: `trade_offer_id`, `confirmation_id`, `operation`, `confirmed` (true iff operation was `allow`).

### `confirm_all_confirmations` (login: yes, timeout: 120s, safety: NonIdempotent)
Responds to every pending mobile confirmation in one shot (Watt/ASF "accept all"); each item individually — one failure does not abort the batch.
Payload: `operation` string optional default `"allow"` — `allow | cancel`; `type` string optional — `all` (default) | `trade` | `market` filter.
Output: `operation`, `total`, `succeeded`, `failed`, `results[]` `{ confirmation_id, type, succeeded, error? }`.


### `market_watch_add` (login: no, timeout: 10s, safety: Idempotent)
Starts watching a game: `kind=price` alerts on threshold moves (default), `kind=free` alerts when the game turns free. Background poll interval/threshold defaults come from plugin config (`market.check_interval_seconds` 300, `market.threshold_percent` 10, `market.country` "us").
Payload: `app_id` string **required** (positive); `kind` string optional default `"price"` — `price | free`; `threshold_percent` number optional (default from plugin config, must be > 0, only meaningful for `price`); `cc` string optional (default from plugin config, lowercased).
Output: `app_id`, `kind`, `threshold_percent`, `cc`, `watched` (total watch count). Fails if the app is already watched.

### `market_watch_remove` (login: no, timeout: 10s, safety: Idempotent)
Stops watching a game's price.
Payload: `app_id` string **required**.
Output: `app_id`, `watched` (remaining count). Fails if not watched.

### `market_watch_list` (login: no, timeout: 10s, safety: ReadOnly)
Lists watched games with baselines and last observed prices. No payload fields.
Output: `watches[]` `{ app_id, kind, threshold_percent, cc, currency, baseline, last_price, last_known_free, last_checked_at, alerts }`, `count`, `interval_seconds`.


### `get_metrics` (login: no, timeout: 10s, safety: ReadOnly)
Returns the current metrics snapshot so the control plane can pull monitoring data through normal task execution (the plugin also serves it on its own HTTP endpoint). No payload fields.
Output: `format` (`"prometheus"`), `metrics` (Prometheus exposition text), `summary` (JSON summary string).

## Game Data plugin (`vapor.game-data`, `src/Vapor.Plugins.GameData/GameDataPlugin.cs`)

Four read-only actions over the official Steam Web API (`api.steampowered.com`, key-authed GETs). The API key lives **agent-side** in the plugin configuration (`webapi.key`) or the `VAPOR_GAME_DATA_WEBAPI_KEY` environment variable — the control plane never sees it — and a missing key fails every action with explicit guidance instead of faking data. Every action catches transport (`HttpRequestException`) and parse (`JsonException`) failures into failed results with the operation name. Projections are compact digests, not raw payloads. Per-title plugin boundaries: [game-plugins.md](game-plugins.md).

### `dota2_match_history` (login: no, timeout: 30s, safety: ReadOnly)
Fetches public Dota 2 match history (`IDOTA2Match_570/GetMatchHistory/v1/`).
Payload: `heroId` number optional; `gameMode` number optional; `matchesRequested` number optional (clamped to 1..100). Unparsable values are ignored rather than rejected.
Output: `status` (Web API result status, 0 when absent), `matchCount`, `totalResults`, `matches[]` `{ matchId, matchSeqNum, startTime, lobbyType, gameMode }` — `matchId`/`matchSeqNum` are int64 (real match ids exceed int32).

### `dota2_heroes` (login: no, timeout: 30s, safety: ReadOnly)
Fetches the Dota 2 hero catalog (`IEconDOTA2_570/GetHeroes/v1/`). No payload fields.
Output: `status`, `count`, `items[]` `{ id, name, localizedName, legs }`.

### `dota2_game_items` (login: no, timeout: 30s, safety: ReadOnly)
Fetches the Dota 2 item catalog (`IEconDOTA2_570/GetGameItems/v1/`). No payload fields.
Output: `status`, `count`, `items[]` `{ id, name, localizedName, cost }`.

### `econ_item_schema` (login: no, timeout: 60s, safety: ReadOnly)
Fetches a TF2/CS2 item-schema digest (`IEconItems_{appid}/GetSchema/v1/`).
Payload: `appid` number **required** — only `440` (TF2) and `730` (CS2) are accepted; anything else fails with the whitelist spelled out.
Output: `status`, `count` (schema items), `items[]` `{ defIndex, name }`, `appId`. The full schema is megabytes and stays at the Web API — only the digest is returned.

---

# Agent host actions (`Vapor.Agent` assembly, `src/Vapor.Agent/HostActions/`)

All five require the job target to be exactly `"agent:{agentId}"` (see `HostTaskTarget`); they run host-scoped — no bot session — and ride the regular task pipeline (retries/audit/jobs panel apply). `pluginId` spellings: both `pluginId` and `plugin_id` are accepted (alias lookup).

### `plugin_install` (login: no, timeout: 300s, safety: Idempotent)
Installs a plugin package (zip) by URL with a **mandatory SHA-256 checksum**, validates it against the manifest and hot-loads it.
Payload: `url` string **required** (package location); `sha256` string **required** (64-hex-digit digest; alias `sha_256`); `pluginId` string optional (alias `plugin_id`; derived from the manifest when omitted); `version` string optional.
Output: `pluginId`, `version`, `replaced` (bool — overwrote an existing install), `actions` (contributed action names), `plugins` (full loaded-plugin list so the control plane can mirror the agent's inventory). On failure the output still carries `url` + `plugins`.

### `plugin_uninstall` (login: no, timeout: 60s, safety: Idempotent)
Unloads the plugin by id and removes its directory (rename-aside then best-effort delete — CLR file locks on Windows must not block discovery). **Idempotent**: uninstalling a not-loaded plugin succeeds with `removed=false`.
Payload: `pluginId` string **required** (alias `plugin_id`).
Output: `pluginId`, `removed`, `plugins` (remaining list).

### `plugin_list` (login: no, timeout: 15s, safety: ReadOnly)
Reports the currently loaded plugins and the plugins root. No payload fields.
Output: `directory` (plugins root path), `count`, `plugins` (loaded-plugin list; each entry carries `id`, `name`, `version`, `apiVersion`, `trust`, `permissions`, `resources` (manifest's advisory runtime-resource declaration, empty when undeclared), `actions`).

### `plugin_update_check` (login: no, timeout: 15s, safety: ReadOnly)
Compares the catalog candidates staged from the plugin index against the plugins actually loaded on this agent and reports a verdict per candidate. Read-only by design — nothing is downloaded or replaced here; the operator decides what (and where) to install with `plugin_install`.
Payload: `candidates` array optional — `{ id, version }` objects staged from the index (non-object entries are skipped; entries without `id` are skipped); omitting it yields an empty report (the loaded-plugin mirror still rides along).
Output: `count`, `updates` (per candidate: `id`, `catalogVersion`, `installedVersion` (null when not installed), `status` — `updateAvailable` (catalog strictly newer, both versions parse as `System.Version`) / `upToDate` (equal or installed ahead) / `notComparable` (either side unparseable — never coerced) / `notInstalled`), `plugins` (full loaded-plugin list so the control plane can mirror the agent's inventory).

### `set_proxy` (login: no, timeout: 120s, safety: GuardedWrite)
Assigns (or clears) an account's egress proxy: validates the endpoint, persists it in **this agent's** credential store and, when a live session exists, rebuilds that session through the new exit (remove → restore → token re-login). A failed rebuild is not an error: the assignment persists and the next login picks it up (`sessionRestarted=false`).
Payload: `account` string **required**; `proxy` string optional — the endpoint (`http|https|socks5://[user:pass@]host:port`); **omitted/empty means clear** (the REST layer enforces explicitness so an accidental empty call cannot strip a pinned exit).
Output: `account`, `proxy` (**masked form only** — `socks5://user:<redacted>@host:port`; credentials never leave the agent), `cleared` (bool), `sessionRestarted` (bool).
Note: this action is host-scoped (`agent:{id}` target) but keyed by account — the ControlPlane refuses assignment for accounts without a pinned agent, because the endpoint would otherwise persist into whichever agent claims the task. Pair with `check_proxy` to verify the live exit. Rationale: Steam correlates logins by IP; an account's exit is pinned once and changed rarely (see `todo.md` §39).

---

## Action count summary

| Provider | Count |
|---|---|
| Steam.Core host actions (`src/Vapor.Steam.Core/Actions/`) | 20 |
| Game Access plugin (`vapor.game-access`) | 16 |
| Mobile Authenticator plugin (`vapor.mobile-authenticator`) | 9 |
| Market Watch plugin (`vapor.market-watch`) | 3 |
| Monitoring plugin (`vapor.monitoring`) | 1 |
| Game Data plugin (`vapor.game-data`) | 4 |
| Agent host (`Vapor.Agent/HostActions/`) | 5 |
| **Total** | **58** |
