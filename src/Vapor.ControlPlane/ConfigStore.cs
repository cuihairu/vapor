using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed class ConfigStore {
	private readonly object _gate = new();
	private GlobalConfig _global;
	private readonly Dictionary<string, AccountConfig> _accounts = new(StringComparer.OrdinalIgnoreCase);

	public ConfigStore() {
		var now = DateTimeOffset.UtcNow;
		_global = new GlobalConfig(
			Version: new ConfigVersion(1, now, "system"),
			Settings: new Dictionary<string, object?>()
		);
	}

	public GlobalConfig GetGlobal() {
		lock (_gate) {
			return _global;
		}
	}

	public IReadOnlyList<AccountConfig> ListAccounts() {
		lock (_gate) {
			return _accounts.Values
				.OrderBy(account => account.AccountName, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
	}

	public GlobalConfig SetGlobal(IReadOnlyDictionary<string, object?>? settings, string? updatedBy) {
		lock (_gate) {
			var now = DateTimeOffset.UtcNow;
			int nextVersion = _global.Version.Version + 1;
			_global = new GlobalConfig(
				Version: new ConfigVersion(nextVersion, now, string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy),
				Settings: settings ?? new Dictionary<string, object?>()
			);
			return _global;
		}
	}

	public AccountConfig SetAccount(
		string accountName,
		bool enabled,
		string? region,
		IReadOnlyList<string>? labels,
		IReadOnlyDictionary<string, object?>? settings,
		string? updatedBy) {
		if (string.IsNullOrWhiteSpace(accountName)) {
			throw new ArgumentException("accountName is required", nameof(accountName));
		}

		lock (_gate) {
			var normalizedAccountName = accountName.Trim();
			var now = DateTimeOffset.UtcNow;
			_accounts.TryGetValue(normalizedAccountName, out var existing);
			int nextVersion = (existing?.Version?.Version ?? 0) + 1;

			var updated = new AccountConfig(
				AccountName: normalizedAccountName,
				Enabled: enabled,
				Region: string.IsNullOrWhiteSpace(region) ? null : region.Trim(),
				Labels: labels,
				Settings: settings,
				Version: new ConfigVersion(nextVersion, now, string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy)
			);

			_accounts[normalizedAccountName] = updated;
			return updated;
		}
	}
}
