using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Store of global and per-account settings: the dictionaries are the read
/// path; when a <see cref="SqliteConfigStore"/> is supplied the store
/// additionally loads at startup and write-throughs every mutation (DB before
/// memory, inside <c>_gate</c>), so settings survive a control-plane restart
/// like jobs, audit and crawl rows do.
/// </summary>
public sealed class ConfigStore
{
	private readonly object _gate = new();
	private GlobalConfig _global;
	private readonly Dictionary<string, AccountConfig> _accounts = new(StringComparer.OrdinalIgnoreCase);
	private readonly SqliteConfigStore? _persistence;

	public ConfigStore(SqliteConfigStore? persistence = null)
	{
		_persistence = persistence;
		_global = persistence?.LoadGlobalConfig() ?? DefaultGlobal();
		if (persistence is not null)
		{
			foreach (AccountConfig config in persistence.LoadAccountConfigs())
			{
				_accounts[config.AccountName] = config;
			}
		}
	}

	private static GlobalConfig DefaultGlobal()
	{
		var now = DateTimeOffset.UtcNow;
		return new GlobalConfig(
			Version: new ConfigVersion(1, now, "system"),
			Settings: new Dictionary<string, object?>()
		);
	}

	public GlobalConfig GetGlobal()
	{
		lock (_gate)
		{
			return _global;
		}
	}

	public IReadOnlyList<AccountConfig> ListAccounts()
	{
		lock (_gate)
		{
			return _accounts.Values
				.OrderBy(account => account.AccountName, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
	}

	public GlobalConfig SetGlobal(IReadOnlyDictionary<string, object?>? settings, string? updatedBy)
	{
		lock (_gate)
		{
			var now = DateTimeOffset.UtcNow;
			int nextVersion = _global.Version.Version + 1;
			var updated = new GlobalConfig(
				Version: new ConfigVersion(nextVersion, now, string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy),
				Settings: settings ?? new Dictionary<string, object?>()
			);
			_persistence?.SaveGlobalConfig(updated);
			_global = updated;
			return updated;
		}
	}

	public AccountConfig SetAccount(
		string accountName,
		bool enabled,
		string? region,
		IReadOnlyList<string>? labels,
		IReadOnlyDictionary<string, object?>? settings,
		string? updatedBy)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			throw new ArgumentException("accountName is required", nameof(accountName));
		}

		lock (_gate)
		{
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

			_persistence?.SaveAccountConfig(updated);
			_accounts[normalizedAccountName] = updated;
			return updated;
		}
	}
}
