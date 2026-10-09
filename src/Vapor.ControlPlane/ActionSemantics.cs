using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Control-plane mirror of the per-action execution-safety classification. The
/// single source of truth is the agent-side <c>IAction.Metadata.Safety</c>
/// annotations; this table exists so the scheduler can bound redispatch without
/// round-tripping to the fleet, and
/// <c>scripts/verify-actions-safety.py</c> asserts the two stay in agreement
/// (in-tree action code ⊆ this table ⊆ actions.md catalog). A name absent from
/// the table (or a future plugin action) is treated as
/// <see cref="ActionSafety.Unknown"/> — conservatively bounded, never spinning at
/// the configured ceiling.
/// </summary>
public static class ActionSemantics
{
	/// <summary>Total dispatch attempts allowed for unsafe classes (1 initial + 1 retry).</summary>
	public const int ConservativeMaxDispatchAttempts = 2;

	private static readonly IReadOnlyDictionary<string, ActionSafety> SafetyByAction =
		new Dictionary<string, ActionSafety>(StringComparer.OrdinalIgnoreCase)
		{
			// Steam.Core host actions (20).
			["login"] = ActionSafety.Idempotent,
			["echo"] = ActionSafety.ReadOnly,
			["ping"] = ActionSafety.ReadOnly,
			["idle"] = ActionSafety.Idempotent,
			["get_trade_offers"] = ActionSafety.ReadOnly,
			["send_trade_offer"] = ActionSafety.NonIdempotent,
			["accept_trade_offer"] = ActionSafety.GuardedWrite,
			["decline_trade_offer"] = ActionSafety.GuardedWrite,
			["cancel_trade_offer"] = ActionSafety.GuardedWrite,
			["get_my_market_listings"] = ActionSafety.ReadOnly,
			["create_market_listing"] = ActionSafety.NonIdempotent,
			["cancel_market_listings"] = ActionSafety.GuardedWrite,
			["get_game_info"] = ActionSafety.ReadOnly,
			["get_game_info_batch"] = ActionSafety.ReadOnly,
			["search_games"] = ActionSafety.ReadOnly,
			["get_price"] = ActionSafety.ReadOnly,
			["get_market_listings"] = ActionSafety.ReadOnly,
			["cache_invalidate"] = ActionSafety.Idempotent,
			["check_account_standing"] = ActionSafety.ReadOnly,
			["check_proxy"] = ActionSafety.ReadOnly,

			// GameAccess plugin (16).
			["play_games"] = ActionSafety.Idempotent,
			["get_card_drops"] = ActionSafety.ReadOnly,
			["get_playtime"] = ActionSafety.ReadOnly,
			["add_license"] = ActionSafety.Idempotent,
			["redeem_key"] = ActionSafety.GuardedWrite,
			["get_achievements"] = ActionSafety.ReadOnly,
			["unlock_achievements"] = ActionSafety.Idempotent,
			["reset_achievements"] = ActionSafety.GuardedWrite,
			["get_inventory"] = ActionSafety.ReadOnly,
			["get_game_inventory"] = ActionSafety.ReadOnly,
			["get_item_details"] = ActionSafety.ReadOnly,
			["loot_inventory"] = ActionSafety.GuardedWrite,
			["find_duplicates"] = ActionSafety.ReadOnly,
			["swap_duplicates"] = ActionSafety.NonIdempotent,
			["get_points_shop_summary"] = ActionSafety.ReadOnly,
			["claim_points_shop_items"] = ActionSafety.GuardedWrite,

			// MobileAuthenticator plugin (9).
			["generate_totp"] = ActionSafety.ReadOnly,
			["generate_confirmation_hash"] = ActionSafety.ReadOnly,
			["sync_steam_time"] = ActionSafety.Idempotent,
			["get_trade_confirmations"] = ActionSafety.ReadOnly,
			["respond_trade_confirmation"] = ActionSafety.NonIdempotent,
			["save_shared_secret"] = ActionSafety.GuardedWrite,
			["save_identity_secret"] = ActionSafety.GuardedWrite,
			["confirm_trade_offer"] = ActionSafety.GuardedWrite,
			["confirm_all_confirmations"] = ActionSafety.NonIdempotent,

			// MarketWatch plugin (3).
			["market_watch_add"] = ActionSafety.Idempotent,
			["market_watch_remove"] = ActionSafety.Idempotent,
			["market_watch_list"] = ActionSafety.ReadOnly,

			// Monitoring plugin (1).
			["get_metrics"] = ActionSafety.ReadOnly,

			// GameData plugin (4).
			["dota2_match_history"] = ActionSafety.ReadOnly,
			["dota2_heroes"] = ActionSafety.ReadOnly,
			["dota2_game_items"] = ActionSafety.ReadOnly,
			["econ_item_schema"] = ActionSafety.ReadOnly,

			// Agent host actions (5).
			["plugin_install"] = ActionSafety.Idempotent,
			["plugin_uninstall"] = ActionSafety.Idempotent,
			["plugin_list"] = ActionSafety.ReadOnly,
			["plugin_update_check"] = ActionSafety.ReadOnly,
			["script_exec"] = ActionSafety.NonIdempotent,
			["set_proxy"] = ActionSafety.GuardedWrite,
		};

	public static IReadOnlyCollection<string> ActionNames => SafetyByAction.Keys.ToArray();

	public static ActionSafety SafetyOf(string actionName)
		=> SafetyByAction.TryGetValue(actionName, out ActionSafety safety) ? safety : ActionSafety.Unknown;

	/// <summary>
	/// Effective dispatch budget for an action: unsafe or unclassified actions cap
	/// at <see cref="ConservativeMaxDispatchAttempts"/> so a redelivery that could
	/// double an external side effect never spins at the configured ceiling; safe
	/// classes use the configured ceiling unchanged. A ceiling of 0 keeps today's
	/// "unlimited" semantics.
	/// </summary>
	public static int MaxDispatchAttempts(string actionName, int configuredCeiling)
		=> SafetyOf(actionName) switch
		{
			ActionSafety.GuardedWrite => CeilingFor(configuredCeiling),
			ActionSafety.NonIdempotent => CeilingFor(configuredCeiling),
			ActionSafety.Unknown => CeilingFor(configuredCeiling),
			_ => configuredCeiling,
		};

	private static int CeilingFor(int configuredCeiling)
		=> configuredCeiling > 0 ? Math.Min(configuredCeiling, ConservativeMaxDispatchAttempts) : 0;
}
