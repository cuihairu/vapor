using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// SQLite persistence for the operator-declared state: account specs
/// (<c>account_specs</c>), global settings (<c>global_config</c>, at most one
/// row) and per-account settings (<c>account_configs</c>). It is the write-through
/// sink for <see cref="AccountStore"/> and <see cref="ConfigStore"/>, which stay
/// the read path (the reconciler lists specs on every pass — reads must not hit
/// a database), so that a control-plane restart no longer erases the declared
/// farm: desired state and settings survive like jobs, audit and crawl rows do.
/// </summary>
/// <remarks>
/// Rows are opaque payloads: nothing queries inside a spec, the whole store is
/// always read back as a complete list, so only the (case-insensitive) name is
/// a column. The PK uses <c>COLLATE NOCASE</c> to mirror the stores'
/// <see cref="StringComparer.OrdinalIgnoreCase"/> keys: declaring "Foo" then
/// "foo" must end with one row, exactly like one dictionary entry.
/// Sync by design — the owning stores mutate under a plain lock, so the
/// write-through must be synchronous to stay inside the single-writer
/// critical section (local, tiny writes; nothing to await).
/// </remarks>
public sealed class SqliteConfigStore : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly object _gate = new();

	public SqliteConfigStore(string dbPath)
	{
		if (string.IsNullOrWhiteSpace(dbPath))
		{
			throw new ArgumentException("DB path is required", nameof(dbPath));
		}

		if (!string.Equals(dbPath, ":memory:", StringComparison.Ordinal))
		{
			string? dir = Path.GetDirectoryName(dbPath);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
		}

		_connection = new SqliteConnection($"Data Source={dbPath}");
		_connection.Open();
		Migrate();
	}

	public void Dispose() => _connection.Dispose();

	// --- account specs ---

	public IReadOnlyList<AccountSpec> LoadAccountSpecs()
	{
		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT payload FROM account_specs ORDER BY name;";
			using var reader = cmd.ExecuteReader();
			var specs = new List<AccountSpec>();
			while (reader.Read())
			{
				specs.Add(Deserialize<AccountSpec>(reader.GetString(0)));
			}

			return specs;
		}
	}

	public void SaveAccountSpec(AccountSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);

		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO account_specs (name, payload) VALUES ($name, $payload)
				ON CONFLICT(name) DO UPDATE SET payload=$payload;
				""";
			cmd.Parameters.AddWithValue("$name", spec.AccountName);
			cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(spec, JsonDefaults.Options));
			cmd.ExecuteNonQuery();
		}
	}

	public void DeleteAccountSpec(string accountName)
	{
		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM account_specs WHERE name = $name;";
			cmd.Parameters.AddWithValue("$name", accountName);
			cmd.ExecuteNonQuery();
		}
	}

	// --- global config ---

	public GlobalConfig? LoadGlobalConfig()
	{
		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT payload FROM global_config WHERE id = 1;";
			using var reader = cmd.ExecuteReader();
			return reader.Read() ? Deserialize<GlobalConfig>(reader.GetString(0)) : null;
		}
	}

	public void SaveGlobalConfig(GlobalConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO global_config (id, payload) VALUES (1, $payload)
				ON CONFLICT(id) DO UPDATE SET payload=$payload;
				""";
			cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(config, JsonDefaults.Options));
			cmd.ExecuteNonQuery();
		}
	}

	// --- per-account config ---

	public IReadOnlyList<AccountConfig> LoadAccountConfigs()
	{
		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT payload FROM account_configs ORDER BY name;";
			using var reader = cmd.ExecuteReader();
			var configs = new List<AccountConfig>();
			while (reader.Read())
			{
				configs.Add(Deserialize<AccountConfig>(reader.GetString(0)));
			}

			return configs;
		}
	}

	public void SaveAccountConfig(AccountConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO account_configs (name, payload) VALUES ($name, $payload)
				ON CONFLICT(name) DO UPDATE SET payload=$payload;
				""";
			cmd.Parameters.AddWithValue("$name", config.AccountName);
			cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(config, JsonDefaults.Options));
			cmd.ExecuteNonQuery();
		}
	}

	private void Migrate()
	{
		lock (_gate)
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				CREATE TABLE IF NOT EXISTS account_specs (
					name TEXT PRIMARY KEY COLLATE NOCASE,
					payload TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS global_config (
					id INTEGER PRIMARY KEY,
					payload TEXT NOT NULL
				);
				CREATE TABLE IF NOT EXISTS account_configs (
					name TEXT PRIMARY KEY COLLATE NOCASE,
					payload TEXT NOT NULL
				);
				""";
			cmd.ExecuteNonQuery();
		}
	}

	/// <summary>
	/// Fail closed: a row that will not deserialize must abort the load (and
	/// with it the host start) rather than boot with a silently-shrunken
	/// desired state whose first write would overwrite the surviving rows.
	/// </summary>
	private static T Deserialize<T>(string payload)
	{
		return JsonSerializer.Deserialize<T>(payload, JsonDefaults.Options)
			?? throw new InvalidDataException($"stored config row is not a valid {typeof(T).Name}");
	}
}
