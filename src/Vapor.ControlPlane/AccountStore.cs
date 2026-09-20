using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// In-memory store of declared account specifications (desired farm state).
/// Follows the same lifecycle as <see cref="ConfigStore"/>: CRUD via the API,
/// orchestrator reads it on every reconcile pass.
/// </summary>
public sealed class AccountStore
{
	private readonly object _gate = new();
	private readonly Dictionary<string, AccountSpec> _accounts = new(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<AccountSpec> List()
	{
		lock (_gate)
		{
			return _accounts.Values
				.OrderBy(account => account.AccountName, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
	}

	public AccountSpec? Get(string accountName)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return null;
		}

		lock (_gate)
		{
			return _accounts.TryGetValue(accountName.Trim(), out var spec) ? spec : null;
		}
	}

	public AccountSpec Upsert(
		string accountName,
		bool enabled,
		AccountDesiredState desiredState,
		IReadOnlyList<string>? idleApps,
		string? region,
		string? agentId,
		string? note,
		string? updatedBy = null,
		bool? marketListingsEnabled = null,
		IReadOnlyList<BoostTarget>? boostTargets = null,
		TradePolicy? tradePolicy = null,
		FarmPolicy? farmPolicy = null)
	{
		var spec = Build(accountName, enabled, desiredState, idleApps, region, agentId, note, updatedBy, boostTargets, tradePolicy, farmPolicy, out var normalizedAccountName);

		lock (_gate)
		{
			if (_accounts.TryGetValue(normalizedAccountName, out var existing))
			{
				// The market-listings opt-in is carried per update: null keeps the
				// current value so a PUT that omits the flag never silently flips it.
				spec = spec with
				{
					MarketListingsEnabled = marketListingsEnabled ?? existing.MarketListingsEnabled,
					Version = new ConfigVersion((existing.Version?.Version ?? 0) + 1, spec.Version!.UpdatedAt, spec.Version.UpdatedBy)
				};
			}
			else if (marketListingsEnabled is true)
			{
				spec = spec with { MarketListingsEnabled = true };
			}

			_accounts[normalizedAccountName] = spec;
			return spec;
		}
	}

	public AccountSpec? SetEnabled(string accountName, bool enabled)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return null;
		}

		lock (_gate)
		{
			if (!_accounts.TryGetValue(accountName.Trim(), out var existing))
			{
				return null;
			}

			var updated = existing with
			{
				Enabled = enabled,
				Version = new ConfigVersion((existing.Version?.Version ?? 0) + 1, DateTimeOffset.UtcNow)
			};
			_accounts[existing.AccountName] = updated;
			return updated;
		}
	}

	public AccountSpec? Remove(string accountName)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return null;
		}

		lock (_gate)
		{
			if (!_accounts.TryGetValue(accountName.Trim(), out var existing))
			{
				return null;
			}

			_accounts.Remove(existing.AccountName);
			return existing;
		}
	}

	private static AccountSpec Build(
		string accountName,
		bool enabled,
		AccountDesiredState desiredState,
		IReadOnlyList<string>? idleApps,
		string? region,
		string? agentId,
		string? note,
		string? updatedBy,
		IReadOnlyList<BoostTarget>? boostTargets,
		TradePolicy? tradePolicy,
		FarmPolicy? farmPolicy,
		out string normalizedAccountName)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			throw new ArgumentException("account name is required", nameof(accountName));
		}

		normalizedAccountName = accountName.Trim();

		var normalizedApps = NormalizeIdleApps(idleApps);
		var normalizedTargets = NormalizeBoostTargets(boostTargets);
		if (desiredState == AccountDesiredState.Boost && normalizedTargets is null)
		{
			throw new ArgumentException(
				$"the {nameof(AccountDesiredState.Boost)} state requires at least one boost target",
				nameof(boostTargets));
		}

		return new AccountSpec(
			AccountName: normalizedAccountName,
			Enabled: enabled,
			DesiredState: desiredState,
			IdleApps: normalizedApps,
			Region: NormalizeOptional(region),
			AgentId: NormalizeOptional(agentId),
			Note: NormalizeOptional(note),
			Version: new ConfigVersion(1, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy),
			BoostTargets: normalizedTargets,
			TradePolicy: NormalizeTradePolicy(tradePolicy),
			FarmPolicy: NormalizeFarmPolicy(farmPolicy)
		);
	}

	/// <summary>
	/// Validates and normalizes the farm policy: the per-game budget must be a
	/// finite positive number (a NaN/∞ goal would silently never trip, a zero
	/// or negative one is a contradictory "no farming" declaration), the
	/// priority list keeps its declaration order (list position IS the queue
	/// priority — the first entry heads the queue) with zero ids dropped and
	/// duplicates collapsed. A policy with nothing active normalizes to null;
	/// an omitted policy clears any previous one (full replace semantics,
	/// same direction as the rest of the spec).
	/// </summary>
	// internal for tests (property-based invariants), see Vapor.ControlPlane.Tests.
	internal static FarmPolicy? NormalizeFarmPolicy(FarmPolicy? farmPolicy)
	{
		if (farmPolicy is null)
		{
			return null;
		}

		if (farmPolicy.PerGameHourBudget is { } budget && (!double.IsFinite(budget) || budget <= 0))
		{
			throw new ArgumentException(
				$"farm per-game hour budget must be a finite positive number, got {budget}",
				nameof(farmPolicy));
		}

		uint[]? priorityApps = null;
		if (farmPolicy.PriorityApps is { Count: > 0 })
		{
			var seen = new HashSet<uint>();
			priorityApps = farmPolicy.PriorityApps
				.Where(id => id != 0u && seen.Add(id))
				.ToArray();
			if (priorityApps.Length == 0)
			{
				priorityApps = null;
			}
		}

		bool orderActive = farmPolicy.PriorityOrder != FarmPriorityOrder.CardsDescending;
		return farmPolicy.PerGameHourBudget is not null || orderActive || priorityApps is not null
			? new FarmPolicy(farmPolicy.PerGameHourBudget, farmPolicy.PriorityOrder, priorityApps)
			: null;
	}

	/// <summary>
	/// Validates and normalizes the auto-accept policy: partner ids must be
	/// positive (zero is not a usable SteamId and is dropped, mirroring boost
	/// targets), the whitelist is deduplicated and sorted so the spec is
	/// independent of input order, and enabling the flag with an empty
	/// whitelist is rejected at declaration time — the empty whitelist means
	/// the policy is off, and that interlock must never depend on the
	/// reconciler remembering to check it. A policy with nothing active
	/// normalizes to null; an omitted policy clears any previous one (full
	/// replace semantics, same direction as the rest of the spec: an update
	/// that forgets the policy lands on the safe side — no auto-accept).
	/// </summary>
	// internal for tests (property-based invariants), see Vapor.ControlPlane.Tests.
	internal static TradePolicy? NormalizeTradePolicy(TradePolicy? tradePolicy)
	{
		if (tradePolicy is null)
		{
			return null;
		}

		ulong[]? whitelist = null;
		if (tradePolicy.PartnerWhitelist is { Count: > 0 })
		{
			whitelist = tradePolicy.PartnerWhitelist
				.Where(id => id != 0ul)
				.Distinct()
				.OrderBy(id => id)
				.ToArray();
			if (whitelist.Length == 0)
			{
				// Every entry was unusable: an empty whitelist means the policy is off.
				whitelist = null;
			}
		}

		if (tradePolicy.AutoAcceptGifts && whitelist is null)
		{
			throw new ArgumentException(
				"auto-accept requires a non-empty partner whitelist (an empty whitelist means the policy is off)",
				nameof(tradePolicy));
		}

		return tradePolicy.AutoAcceptGifts || whitelist is not null
			? new TradePolicy(tradePolicy.AutoAcceptGifts, whitelist)
			: null;
	}

	/// <summary>
	/// Trims nothing (typed payload), drops empty entries, and validates that
	/// every target is usable: a positive numeric app id and a finite positive
	/// hour goal. The same app declared twice with different goals is a
	/// contradictory configuration and is rejected; exact duplicates
	/// collapse. Null when nothing remains.
	/// </summary>
	private static IReadOnlyList<BoostTarget>? NormalizeBoostTargets(IReadOnlyList<BoostTarget>? boostTargets)
	{
		if (boostTargets is not { Count: > 0 })
		{
			return null;
		}

		var byApp = new Dictionary<uint, BoostTarget>();
		foreach (BoostTarget target in boostTargets)
		{
			if (target.AppId == 0)
			{
				continue;
			}

			if (!double.IsFinite(target.TargetHours) || target.TargetHours <= 0)
			{
				throw new ArgumentException(
					$"boost target hours must be a finite positive number, got {target.TargetHours} for app {target.AppId}",
					nameof(boostTargets));
			}

			if (byApp.TryGetValue(target.AppId, out BoostTarget? existing))
			{
				if (existing.TargetHours != target.TargetHours)
				{
					throw new ArgumentException(
						$"app {target.AppId} declared twice with different boost targets ({existing.TargetHours} vs {target.TargetHours})",
						nameof(boostTargets));
				}

				continue;
			}

			byApp[target.AppId] = target;
		}

		return byApp.Count > 0 ? [.. byApp.Values.OrderBy(t => t.AppId)] : null;
	}

	/// <summary>Trims, drops empties and duplicates, and validates that every app id is numeric; null when nothing remains.</summary>
	private static IReadOnlyList<string>? NormalizeIdleApps(IReadOnlyList<string>? idleApps)
	{
		if (idleApps is not { Count: > 0 })
		{
			return null;
		}

		var apps = new List<string>(idleApps.Count);
		foreach (string raw in idleApps)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}

			string app = raw.Trim();
			if (!uint.TryParse(app, out _))
			{
				throw new ArgumentException($"idle app ids must be numeric, got '{app}'", nameof(idleApps));
			}

			if (!apps.Contains(app, StringComparer.Ordinal))
			{
				apps.Add(app);
			}
		}

		return apps.Count > 0 ? apps : null;
	}

	private static string? NormalizeOptional(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}
}
