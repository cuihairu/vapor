using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed partial class SqliteJobStore : IJobStore, IDisposable
{
	/// <summary>
	/// Login/session credential aliases consumed by <see cref="Vapor.Agent.AgentTaskExecutor"/>.
	/// Agent-local provisioning actions (for example proxy and mobile-authenticator secrets)
	/// intentionally use dedicated action payloads and are not covered by this set.
	/// </summary>
	private static readonly IReadOnlySet<string> SecretPayloadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"password", "pass", "accessToken", "access_token",
		"refreshToken", "refresh_token", "authCode", "auth_code",
		"twoFactorCode", "two_factor_code"
	};

	private readonly SqliteConnection _connection;
	private readonly string _dbPath;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteJobStore(string dbPath)
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

		_dbPath = dbPath;
		_connection = new SqliteConnection($"Data Source={dbPath}");
		_connection.Open();

		try
		{
			Migrate();
		}
		catch
		{
			// A failed migration (e.g. a corrupt file) must not leak the open handle:
			// on Windows the lingering lock would block the operator from repairing
			// or replacing the database before the next start. Dispose alone returns
			// the connection to the ADO.NET pool, which keeps the file handle open —
			// evict the pool so the file is usable again immediately.
			_connection.Dispose();
			SqliteConnection.ClearAllPools();
			throw;
		}
	}

	public void Dispose()
	{
		_connection.Dispose();
		_mutex.Dispose();
	}

}
