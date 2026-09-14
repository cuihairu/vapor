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
		bool? marketListingsEnabled = null)
	{
		var spec = Build(accountName, enabled, desiredState, idleApps, region, agentId, note, updatedBy, out var normalizedAccountName);

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
		out string normalizedAccountName)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			throw new ArgumentException("account name is required", nameof(accountName));
		}

		normalizedAccountName = accountName.Trim();

		var normalizedApps = NormalizeIdleApps(idleApps);

		return new AccountSpec(
			AccountName: normalizedAccountName,
			Enabled: enabled,
			DesiredState: desiredState,
			IdleApps: normalizedApps,
			Region: NormalizeOptional(region),
			AgentId: NormalizeOptional(agentId),
			Note: NormalizeOptional(note),
			Version: new ConfigVersion(1, DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy)
		);
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
