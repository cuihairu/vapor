# Valve game plugins: status and boundaries

This page answers one question precisely: **which Valve-title-specific plugins
exist, which do not, and why**. The short version:

- The plugin **mechanism** (discovery, manifest, trust/permissions, isolated
  loading, hot install) is complete and ASF-aligned — see
  [Plugin development](plugins.md). Nothing in this page blocks on it.
- The official plugin **catalog** is deliberately small. Only one plugin is
  tied to a specific Valve title today: `vapor.caseopening` (CS:GO/CS2, dry-run
  only). There is **no** dedicated CS2 or Dota 2 plugin beyond that, and some
  of that gap is structural, not backlog.

## The game-agnostic base already covers most of it

Almost everything an operator does with a Valve title is game-independent and
already ships in the agent's core action catalog
([Actions catalog](actions.md)):

| Surface | Core coverage (works for any Valve title) |
|---------|-------------------------------------------|
| Playtime / idle / boost | `idle`, `play_games`, `get_playtime`; `boost` desired state with multi-app farming |
| Trading cards | `get_card_drops` + the farm desired state (two-phase engine) — applies to titles that have card sets (e.g. Dota 2 does; CS:GO/CS2 never had them) |
| Licenses & keys | `add_license`, `redeem_key` |
| Achievements | `get_achievements`, `unlock_achievements`, `reset_achievements` |
| Inventory & trading | `get_inventory`, `find_duplicates`, `swap_duplicates`, trade offers, `loot_inventory` (+ §39 storage-role collect orchestration) |
| Community market | `get_my_market_listings`, `create_market_listing`, `cancel_market_listings`, `get_price` |
| Store data | `get_game_info` (batch), `search_games`, market listings — tiered cache |
| Points shop | `get_points_shop_summary`, `claim_points_shop_items` |

A dedicated per-game plugin is justified **only** when a title has mechanics
this catalog cannot express. Everything above runs today for CS2, Dota 2, TF2
and any other title with no per-game code at all.

## Why some gaps cannot be closed by a plugin

Valve exposes **no public API for game-coordinator transactions** — opening a
case, crafting, battle-pass/candy-works interactions, matchmaking. Those are
game-client traffic. Automating them via scripted web sessions or bot fleets
is exactly the "Automation" the
[Steam Subscriber Agreement §4.C](https://store.steampowered.com/subscriber_agreement/)
prohibits (the same analysis the case-opening plugin documents in its
[ToS boundary](plugins.md#tos-boundary)). These are **non-goals**, not pending
work:

- real (non-simulated) case opening for CS:GO/CS2
- match making / in-match farming automation for any title
- battle pass, event or crafting automation for Dota 2 / TF2

The honest per-game plugin surface is therefore three shapes:

1. **Dry-run simulators** over Valve's published data (the `vapor.caseopening`
   pattern: local RNG over published odds, recorded results, zero Steam calls).
2. **Read-only analytics** over Valve's actual public Steam Web API surfaces
   (long-standing key-gated interfaces such as `IEconItems_*` item schemas and
   `IDOTA2Match_570` / `IEconDOTA2_570` match/hero data). Availability and
   coverage are Valve's to change; a plugin must degrade gracefully.
3. **Orchestration glue** over the generic catalog above (game-specific
   presets, drop-window schedulers), which needs no new Steam access at all.

## Current catalog vs. candidates

| Valve title | Dedicated official plugin | Status | Notes |
|-------------|---------------------------|--------|-------|
| CS:GO / CS2 | `vapor.caseopening` | ✅ shipped (dry-run) | Valve's published odds, simulation only; real backend is a stated non-goal |
| CS:GO / CS2 | drop/playtime farming | ✅ via generic catalog | `play_games`/`idle`/`boost`; no plugin needed |
| CS:GO / CS2 | inventory/trade/market tooling | ✅ via generic catalog | inventory, duplicates, trade offers, §39 storage collect, market actions |
| CS:GO / CS2 | item schema / drop telemetry (read-only, `IEconItems_730`) | ❌ not implemented | plausible future candidate; read-only only |
| Dota 2 | dedicated plugin | ❌ not implemented | card farming and playtime already work through the generic catalog; the missing piece would be read-only match/econ analytics (`IDOTA2Match_570` / `IEconDOTA2_570`) |
| TF2 | dedicated plugin | ❌ not implemented | same shape: read-only schema/inventory analytics candidate (`IEconItems_440`) |
| any title | market watch / price alerts | ✅ `vapor.market-watch` | economy-wide, not title-specific |

## What a future per-game plugin looks like

The mechanism side is ready — nothing needs to change to add one:

- Package as a normal plugin ([Quick start](plugins.md#quick-start)); declare
  `trust: official` and honest `permissions`; official-trust plugins must pin
  the host's plugin API version exactly (ASF `HasSameVersion` alignment).
- Follow the `vapor.caseopening` reference shape: actions through
  `IActionPlugin` (callable from jobs and orchestration), read-only dashboards
  through `IWebApiPlugin`, configuration through the manifest `configuration`
  map with env overrides, and — where a real integration might one day exist —
  a single honest backend seam like `ICaseOpeningBackend`, defaulting to the
  simulation/read-only implementation and **rejecting** anything else at
  initialization instead of silently faking it.
- Keep the boundary stated in the manifest description and docs: if a
  capability would require game-coordinator or scripted-client traffic, it is
  a non-goal — the seam documents the refusal rather than reserving a stub
  for it.
