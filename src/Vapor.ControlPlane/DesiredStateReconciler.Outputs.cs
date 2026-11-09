using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>Pure readers of agent task outputs: trade-policy evaluation, farm-queue, boost and playtime extraction.</summary>
public sealed partial class DesiredStateReconciler
{
	/// <summary>
	/// The accept-queue projection of a policy evaluation (the decisions that
	/// said "accept"); see <see cref="EvaluateTradeOffers"/> for the full rules.
	/// </summary>
	internal static List<PendingGiftOffer> ExtractPendingGiftOffers(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
		=> EvaluateTradeOffers(output, spec).Accepted;

	/// <summary>
	/// Evaluates a get_trade_offers task output against the account's policy,
	/// producing a decision for every received offer — not just the accepted
	/// ones — so the audit trail shows why each skip happened. An offer
	/// qualifies only if it is incoming (not ours), from a whitelisted partner,
	/// and gives nothing back. All three are red lines: a partner outside the
	/// whitelist is skipped even for an empty offer, and any offer with
	/// items_to_give is skipped even for a whitelisted partner. Accepts both
	/// in-memory dictionaries and JsonElement objects (SQLite round-trip); a
	/// missing/invalid output or an absent whitelist yields empty results, never
	/// a query failure. Entries with no usable offer id are not auditable and
	/// produce no decision.
	/// </summary>
	internal static (List<PendingGiftOffer> Accepted, List<TradeOfferDecision> Decisions) EvaluateTradeOffers(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
	{
		var accepted = new List<PendingGiftOffer>();
		var decisions = new List<TradeOfferDecision>();
		if (output is null
			|| !output.TryGetValue("received_offers", out object? raw)
			|| raw is null
			|| spec.TradePolicy?.PartnerWhitelist is not { Count: > 0 } whitelist)
		{
			return (accepted, decisions);
		}

		var whitelistAccounts = whitelist
			.Select(id => id >= SteamId64Base ? (ulong)(id - SteamId64Base) : 0ul)
			.Where(id => id != 0ul)
			.ToHashSet();

		void Collect(object? entry)
		{
			(ulong OfferId, ulong PartnerAccount, int GiveCount, bool IsOurs)? parsed = TryReadOfferEntry(entry);
			if (parsed is not { } offer || offer.OfferId == 0)
			{
				return;
			}

			ulong partner64 = SteamId64Base + offer.PartnerAccount;
			if (offer.PartnerAccount == 0)
			{
				decisions.Add(new TradeOfferDecision(offer.OfferId, partner64, "skip", "unreadable"));
				return;
			}

			// Never act on our own offers (a counter-offer can surface here),
			// and gifts-only: anything the partner asks in return disqualifies
			// the offer, regardless of the whitelist. A give count that cannot
			// be read is treated as non-zero (safe side).
			string? skipReason = offer.IsOurs ? "our_offer"
				: offer.GiveCount == int.MaxValue ? "give_count_unreadable"
				: offer.GiveCount != 0 ? "has_give_items"
				: !whitelistAccounts.Contains(offer.PartnerAccount) ? "partner_not_whitelisted"
				: null;
			if (skipReason is not null)
			{
				decisions.Add(new TradeOfferDecision(offer.OfferId, partner64, "skip", skipReason));
				return;
			}

			decisions.Add(new TradeOfferDecision(offer.OfferId, partner64, "accept", null));
			accepted.Add(new PendingGiftOffer(offer.OfferId, partner64));
		}

		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } jsonArray)
		{
			foreach (System.Text.Json.JsonElement entry in jsonArray.EnumerateArray())
			{
				Collect(entry);
			}
		}
		else if (raw is System.Collections.IEnumerable entries and not string)
		{
			foreach (object? entry in entries)
			{
				Collect(entry);
			}
		}

		return (accepted, decisions);
	}

	/// <summary>Reads one received-offer entry (dictionary or JsonElement) as (offerId, partnerAccount, giveCount, isOurs).</summary>
	private static (ulong OfferId, ulong PartnerAccount, int GiveCount, bool IsOurs)? TryReadOfferEntry(object? entry)
	{
		if (entry is System.Text.Json.JsonElement json)
		{
			if (json.ValueKind != System.Text.Json.JsonValueKind.Object)
			{
				return null;
			}

			ulong offerId = json.TryGetProperty("trade_offer_id", out System.Text.Json.JsonElement idElem)
				&& ulong.TryParse(idElem.GetString(), out ulong parsedId) ? parsedId : 0;
			ulong partnerAccount = json.TryGetProperty("partner_steam_id", out System.Text.Json.JsonElement partnerElem)
				&& ulong.TryParse(partnerElem.GetString(), out ulong parsedPartner) ? parsedPartner : 0;
			int giveCount = json.TryGetProperty("items_to_give_count", out System.Text.Json.JsonElement giveElem)
				&& giveElem.TryGetInt32(out int parsedGive) ? parsedGive : int.MaxValue;
			bool isOurs = json.TryGetProperty("is_our_offer", out System.Text.Json.JsonElement oursElem)
				&& oursElem.ValueKind == System.Text.Json.JsonValueKind.True;
			return (offerId, partnerAccount, giveCount, isOurs);
		}

		if (entry is IReadOnlyDictionary<string, object?> dict)
		{
			ulong offerId = dict.TryGetValue("trade_offer_id", out object? idValue)
				&& ulong.TryParse(idValue?.ToString(), out ulong parsedId) ? parsedId : 0;
			ulong partnerAccount = dict.TryGetValue("partner_steam_id", out object? partnerValue)
				&& ulong.TryParse(partnerValue?.ToString(), out ulong parsedPartner) ? parsedPartner : 0;
			int giveCount = dict.TryGetValue("items_to_give_count", out object? giveValue)
				&& int.TryParse(giveValue?.ToString(), out int parsedGive) ? parsedGive : int.MaxValue;
			bool isOurs = dict.TryGetValue("is_our_offer", out object? oursValue) && oursValue is true;
			return (offerId, partnerAccount, giveCount, isOurs);
		}

		return null;
	}

	/// <summary>Reads a boolean output flag; a missing or unreadable flag is false (never chains a confirmation).</summary>
	private static bool OutputFlagIsTrue(IReadOnlyDictionary<string, object?>? output, string key)
	{
		if (output is null || !output.TryGetValue(key, out object? value) || value is null)
		{
			return false;
		}

		if (value is System.Text.Json.JsonElement json)
		{
			return json.ValueKind == System.Text.Json.JsonValueKind.True;
		}

		// The `|| value is true` arm was redundant: a boxed bool ToString()s to
		// exactly "True"/"False", which the ordinal-ignore-case equality above
		// already classifies. No other type stringifies to either token.
		return string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
	}


	/// <summary>
	/// Rebuilds the farm queue from a get_card_drops task output. Entries are
	/// ordered by the account's <see cref="FarmPolicy"/> (default: report order,
	/// drops-remaining descending as produced by the action), with the policy's
	/// priority apps — in their declaration order — heading the queue; the
	/// account's IdleApps list is the farm exclusion list. Accepts both
	/// in-memory dictionaries and JsonElement objects (SQLite round-trip).
	/// A missing/invalid output yields an empty queue — treated as "nothing to
	/// farm", never as a query failure.
	/// </summary>
	internal static List<uint> ExtractFarmQueue(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
	{
		var excluded = new HashSet<string>(StringComparer.Ordinal);
		foreach (string app in spec.IdleApps ?? [])
		{
			excluded.Add(app.Trim());
		}

		var drops = new List<(uint AppId, int Remaining)>();
		if (output is null || !output.TryGetValue("drops", out object? raw) || raw is null)
		{
			return [];
		}

		void Collect(object? entry)
		{
			if (!TryReadDropEntry(entry, out uint appId, out int remaining))
			{
				return;
			}

			if (remaining > 0 && appId != 0 && !excluded.Contains(appId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
			{
				drops.Add((appId, remaining));
			}
		}

		// After the SQLite JSON round-trip the array (and its entries) are JsonElements;
		// freshly dispatched outputs carry in-memory dictionaries.
		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } jsonArray)
		{
			foreach (System.Text.Json.JsonElement entry in jsonArray.EnumerateArray())
			{
				Collect(entry);
			}
		}
		else if (raw is System.Collections.IEnumerable entries and not string)
		{
			foreach (object? entry in entries)
			{
				Collect(entry);
			}
		}

		IEnumerable<(uint AppId, int Remaining)> ordered = spec.FarmPolicy?.PriorityOrder switch
		{
			// CardsDescending keeps the report's native order (already remaining-descending).
			FarmPriorityOrder.CardsAscending => drops.OrderBy(t => t.Remaining),
			FarmPriorityOrder.AppIdAscending => drops.OrderBy(t => t.AppId),
			_ => drops
		};

		var queue = ordered.Select(t => t.AppId).ToList();
		if (spec.FarmPolicy?.PriorityApps is { Count: > 0 } priorities)
		{
			// Declaration order IS the queue priority: pull each prioritized app
			// (when present) to the head, first declared first.
			var head = new List<uint>();
			foreach (uint app in priorities)
			{
				if (queue.Remove(app))
				{
					head.Add(app);
				}
			}

			queue.InsertRange(0, head);
		}

		return queue;
	}

	/// <summary>
	/// Computes the unmet boost targets from a get_playtime task output: target
	/// apps whose reported total hours are below their goal — or that are
	/// missing from the report entirely (conservative: an absent app keeps
	/// idling until it shows up with enough hours; mistaking missing data for
	/// "reached the target" would stop boosting early). The account's IdleApps
	/// list acts as an exclusion list, mirroring farming. Returns null when the
	/// output is unusable (missing/malformed report) — the caller keeps its
	/// previous state, because an empty unmet set would otherwise read as "all
	/// targets met". Accepts both in-memory dictionaries and JsonElement
	/// objects (SQLite round-trip).
	/// </summary>
	internal static List<uint>? ExtractBoostUnmet(IReadOnlyDictionary<string, object?>? output, AccountSpec spec)
	{
		if (output is null || !output.TryGetValue("playtimes", out object? raw) || raw is null)
		{
			return null;
		}

		var targets = spec.BoostTargets;
		if (targets is not { Count: > 0 })
		{
			return [];
		}

		var reported = new Dictionary<uint, double>();
		bool valid = true;
		void Collect(object? entry)
		{
			if (!valid || !TryReadPlaytimeEntry(entry, out uint appId, out double hours))
			{
				valid = false;
				return;
			}

			reported[appId] = hours;
		}

		// After the SQLite JSON round-trip the array (and its entries) are
		// JsonElements; freshly dispatched outputs carry in-memory dictionaries.
		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } jsonArray)
		{
			foreach (System.Text.Json.JsonElement entry in jsonArray.EnumerateArray())
			{
				Collect(entry);
			}
		}
		else if (raw is System.Collections.IEnumerable entries and not string)
		{
			foreach (object? entry in entries)
			{
				Collect(entry);
			}
		}
		else
		{
			valid = false;
		}

		if (!valid)
		{
			return null;
		}

		var excluded = new HashSet<string>(StringComparer.Ordinal);
		foreach (string app in spec.IdleApps ?? [])
		{
			excluded.Add(app.Trim());
		}

		// Targets are stored app-id ascending, so the unmet list is too.
		var unmet = new List<uint>();
		foreach (BoostTarget target in targets)
		{
			if (excluded.Contains(target.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
			{
				continue;
			}

			if (!reported.TryGetValue(target.AppId, out double hours) || hours < target.TargetHours)
			{
				unmet.Add(target.AppId);
			}
		}

		return unmet;
	}

	private static bool TryReadPlaytimeEntry(object? entry, out uint appId, out double hours)
	{
		appId = 0;
		hours = 0;

		if (entry is Dictionary<string, object?> dict)
		{
			return TryGetNumber(dict, "app_id", out long id)
				&& NormalizeAppId(id, out appId)
				&& TryGetDouble(dict, "hours", out hours)
				&& hours >= 0;
		}

		if (entry is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element)
		{
			return TryGetNumberFromElement(element, "app_id", out long jsonId)
				&& NormalizeAppId(jsonId, out appId)
				&& TryGetDoubleFromElement(element, "hours", out hours)
				&& hours >= 0;
		}

		return false;
	}

	private static bool NormalizeAppId(long id, out uint appId)
	{
		appId = id is > 0 and <= uint.MaxValue ? (uint)id : 0;
		return appId != 0;
	}

	// internal for tests (property-based invariants), see Vapor.ControlPlane.Tests.
	internal static bool TryGetDouble(Dictionary<string, object?> dict, string key, out double value)
	{
		value = 0;
		if (!dict.TryGetValue(key, out object? raw) || raw is null)
		{
			return false;
		}

		switch (raw)
		{
			// Non-finite values are rejected: a NaN/∞ boost goal would silently
			// never satisfy (NaN comparisons are false) but would still flow into
			// deviation records; conservative refusal keeps the payload honest.
			case double d when double.IsFinite(d):
				value = d;
				return true;
			case long l:
				value = l;
				return true;
			case int i:
				value = i;
				return true;
			case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element:
				return element.TryGetDouble(out value);
			case string s:
				return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
					&& double.IsFinite(value);
			default:
				return false;
		}
	}

	private static bool TryGetDoubleFromElement(System.Text.Json.JsonElement element, string name, out double value)
	{
		value = 0;
		return element.TryGetProperty(name, out System.Text.Json.JsonElement property)
			&& property.ValueKind == System.Text.Json.JsonValueKind.Number
			&& property.TryGetDouble(out value);
	}

	private static bool TryReadDropEntry(object? entry, out uint appId, out int remaining)
	{
		appId = 0;
		remaining = 0;

		if (entry is Dictionary<string, object?> dict)
		{
			return TryGetNumber(dict, "app_id", out long id)
				&& TryGetNumber(dict, "drops_remaining", out long drops)
				&& Normalize(id, drops, out appId, out remaining);
		}

		if (entry is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Object } element)
		{
			return TryGetNumberFromElement(element, "app_id", out long jsonId)
				&& TryGetNumberFromElement(element, "drops_remaining", out long jsonDrops)
				&& Normalize(jsonId, jsonDrops, out appId, out remaining);
		}

		return false;
	}

	private static bool Normalize(long id, long drops, out uint appId, out int remaining)
	{
		appId = 0;
		remaining = 0;
		if (id is > 0 and <= uint.MaxValue && drops is > 0 and <= int.MaxValue)
		{
			appId = (uint)id;
			remaining = (int)drops;
			return true;
		}

		return false;
	}

	private static bool TryGetNumberFromElement(System.Text.Json.JsonElement element, string name, out long value)
	{
		value = 0;
		return element.TryGetProperty(name, out System.Text.Json.JsonElement property)
			&& property.ValueKind == System.Text.Json.JsonValueKind.Number
			&& property.TryGetInt64(out value);
	}

	/// <summary>
	/// Clears the farm policy's decision bookkeeping (completion/skip marks,
	/// the per-game clock and the efficiency counters). Used when the account
	/// leaves the farm state and when a fresh report starts a new round after a
	/// full drain — a spec update deliberately keeps the marks and counters
	/// (facts survive policy edits) and only restarts the budget clock.
	/// </summary>
	private static void ResetFarmPolicyBookkeeping(AccountRuntime runtime)
	{
		runtime.FarmCompletedApps = null;
		runtime.FarmSkippedApps = null;
		runtime.FarmAppStartedAt = null;
		runtime.FarmStatsStartedAt = null;
		runtime.FarmCardsRemaining = null;
		runtime.FarmCardsCollected = 0;
		runtime.FarmCompletedNotified = false;
	}

	/// <summary>Reads the report's total remaining drop count; a missing or unreadable total simply skips the counters.</summary>
	private static bool TryReadTotalDropsRemaining(IReadOnlyDictionary<string, object?>? output, out int totalRemaining)
	{
		totalRemaining = 0;
		if (output is null || !output.TryGetValue("total_drops_remaining", out object? raw) || raw is null)
		{
			return false;
		}

		if (raw is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element)
		{
			return element.TryGetInt32(out totalRemaining) && totalRemaining >= 0;
		}

		if (TryGetNumber(new Dictionary<string, object?> { ["v"] = raw }, "v", out long value)
			&& value is >= 0 and <= int.MaxValue)
		{
			totalRemaining = (int)value;
			return true;
		}

		return false;
	}

	private static bool TryGetNumber(Dictionary<string, object?> dict, string key, out long value)
	{
		value = 0;
		if (!dict.TryGetValue(key, out object? raw) || raw is null)
		{
			return false;
		}

		switch (raw)
		{
			case long l:
				value = l;
				return true;
			case int i:
				value = i;
				return true;
			case double d:
				value = (long)d;
				return true;
			case System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element:
				return element.TryGetInt64(out value);
			case string s:
				return long.TryParse(s, out value);
			default:
				return false;
		}
	}

}
