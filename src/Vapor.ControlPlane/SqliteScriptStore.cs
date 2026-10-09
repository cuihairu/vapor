using Microsoft.Data.Sqlite;

namespace Vapor.ControlPlane;

/// <summary>
/// A stored operator script: the control plane's script repository. Content is
/// opaque text (shell, python, …) — execution semantics live elsewhere; the
/// repository only owns identity, metadata and bytes.
/// </summary>
public sealed record ScriptRecord(
	string Id,
	string Name,
	string Description,
	string Language,
	string Content,
	long CreatedAtMs,
	long UpdatedAtMs);

/// <summary>SQLite-backed script repository (data/scripts.db).</summary>
public sealed class SqliteScriptStore : IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _mutex = new(1, 1);

	public SqliteScriptStore(string dbPath)
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

	public void Dispose()
	{
		_connection.Dispose();
		_mutex.Dispose();
	}

	public async Task UpsertAsync(ScriptRecord script, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(script);

		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO scripts (id, name, description, language, content, created_at_ms, updated_at_ms)
				VALUES ($id, $name, $description, $language, $content, $created, $updated)
				ON CONFLICT(id) DO UPDATE SET
					name=$name, description=$description, language=$language, content=$content, updated_at_ms=$updated;
				""";
			cmd.Parameters.AddWithValue("$id", script.Id);
			cmd.Parameters.AddWithValue("$name", script.Name);
			cmd.Parameters.AddWithValue("$description", script.Description);
			cmd.Parameters.AddWithValue("$language", script.Language);
			cmd.Parameters.AddWithValue("$content", script.Content);
			cmd.Parameters.AddWithValue("$created", script.CreatedAtMs);
			cmd.Parameters.AddWithValue("$updated", script.UpdatedAtMs);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_mutex.Release();
		}
	}

	public async Task<ScriptRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT id, name, description, language, content, created_at_ms, updated_at_ms FROM scripts WHERE id = $id";
			cmd.Parameters.AddWithValue("$id", id);
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadScript(reader) : null;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>All scripts ordered by name (ordinal), then id for stable ties.</summary>
	public async Task<List<ScriptRecord>> ListAsync(CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT id, name, description, language, content, created_at_ms, updated_at_ms FROM scripts ORDER BY name, id";
			using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			var scripts = new List<ScriptRecord>();
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				scripts.Add(ReadScript(reader));
			}

			return scripts;
		}
		finally
		{
			_mutex.Release();
		}
	}

	/// <summary>Removes the script; returns false when the id is unknown.</summary>
	public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
	{
		await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM scripts WHERE id = $id";
			cmd.Parameters.AddWithValue("$id", id);
			return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
		}
		finally
		{
			_mutex.Release();
		}
	}

	private static ScriptRecord ReadScript(SqliteDataReader reader)
	{
		// Single reader helper keeps the column mapping in exactly one place.
		return new ScriptRecord(
			Id: reader.GetString(reader.GetOrdinal("id")),
			Name: reader.GetString(reader.GetOrdinal("name")),
			Description: reader.GetString(reader.GetOrdinal("description")),
			Language: reader.GetString(reader.GetOrdinal("language")),
			Content: reader.GetString(reader.GetOrdinal("content")),
			CreatedAtMs: reader.GetInt64(reader.GetOrdinal("created_at_ms")),
			UpdatedAtMs: reader.GetInt64(reader.GetOrdinal("updated_at_ms")));
	}

	private void Migrate()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = """
			PRAGMA journal_mode = WAL;
			PRAGMA synchronous = NORMAL;

			CREATE TABLE IF NOT EXISTS scripts (
				id TEXT PRIMARY KEY,
				name TEXT NOT NULL,
				description TEXT NOT NULL,
				language TEXT NOT NULL,
				content TEXT NOT NULL,
				created_at_ms INTEGER NOT NULL,
				updated_at_ms INTEGER NOT NULL
			);
			""";
		cmd.ExecuteNonQuery();
	}
}
