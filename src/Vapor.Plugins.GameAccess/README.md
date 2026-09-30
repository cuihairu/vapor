# Vapor.Plugins.GameAccess

Official Vapor plugin carrying the **game-access action surface** — the
fourteen Steam-domain actions that used to be wired into the agent host
directly, now split out as an ASF-style independent plugin assembly, plus
the game-economy pair `get_game_inventory` / `get_item_details`
(inventory aggregation + per-item market valuation):

| Surface | Actions |
|---|---|
| Farming / playtime | `play_games`, `get_card_drops`, `get_playtime` |
| Licenses & keys | `add_license`, `redeem_key` |
| Achievements | `get_achievements`, `unlock_achievements`, `reset_achievements` |
| Inventory & economy | `get_inventory`, `get_game_inventory`, `get_item_details`, `find_duplicates`, `swap_duplicates`, `loot_inventory` |
| Points shop | `get_points_shop_summary`, `claim_points_shop_items` |

The trading / market / session-engine / diagnostics actions **stayed in the
host** (`Vapor.Steam.Core`): trade offers, market listings, store data,
`login`/`ping`/`echo`/`idle`, proxy and standing checks. `loot_inventory` and
`swap_duplicates` build on the trade pipeline but own no trading logic of
their own — they only share the trade rate limiter (resolved from host
services) and the `SendTradeOfferAction.NoopLease` sentinel, which is why
that static was made public rather than duplicated.

## Compatibility contract

- **Action names and payload schemas are byte-for-byte unchanged.** Already
  deployed jobs, recurring schedules, control-plane code paths and dashboards
  keep dispatching by the same names — the plugin registers the same
  `IAction.Name` strings the host used to.
- **The agent Docker image bundles this plugin** into
  `/app/plugins/vapor.game-access` (same mechanism as Monitoring), so the
  out-of-the-box hello capability set is identical to before the split.
- **A host without the plugin** stops advertising the sixteen names in its
  hello capabilities. The control-plane scheduler only routes actions a
  connected agent declared, so jobs for these actions fail at dispatch with
  "no agent declares the action" — and even a force-routed task fails
  agent-side with the regular `action not found: {name}` session error. No
  silent no-ops.
- Source-tree runs (no Docker): the plugin is not auto-staged; point
  `VAPOR_PLUGINS_DIR` at a directory containing the built
  `Vapor.Plugins.GameAccess` output (or copy it into `./plugins/game-access/`)
  to restore the full action set.

## Host dependencies

Loggers come from the host logger factory; `IVaporCache` (card-drop/playtime
caching) and `TradeRateLimiter` (loot/swap throttling) are resolved from the
host service provider exactly as the host wiring did. A host exposing no rate
limiter logs a warning and the two throttled actions run unthrottled (their
constructors accept a null limiter by design); the plugin still loads.

## Tests

`tests/Vapor.Plugins.GameAccess.Tests/` holds the migrated action suites plus
`PluginHostLoadTests`, which loads the compiled plugin through a real
`PluginManager` (discovery → isolated ALC → permission gating → action
registration) and asserts the sixteen names — the same
host-load proof the MarketWatch plugin established.
