using Microsoft.Data.Sqlite;

namespace Vapor.Backup;

/// <summary>Outcome of a <c>PRAGMA integrity_check</c>: ok, or the reported problems.</summary>
public sealed record VerifyResult(bool Ok, IReadOnlyList<string> Errors);

/// <summary>
/// The backup/restore/verify trio over the Vapor SQLite databases. Backup uses
/// SQLite's online backup API — a consistent snapshot while the control plane
/// keeps running (production.md §"Data, backup and upgrades"). Restore checks
/// the backup's integrity before touching the target and never overwrites an
/// existing database without <c>force</c>. Verify runs <c>PRAGMA
/// integrity_check</c> and reports every row that is not "ok".
/// </summary>
public static class BackupTool
{
	/// <summary>
	/// Copies a database to <paramref name="outputPath"/> through the online
	/// backup API: consistent even while other connections are writing.
	/// </summary>
	public static async Task BackupAsync(string dbPath, string outputPath, bool force = false, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		if (!File.Exists(dbPath))
		{
			throw new FileNotFoundException($"Source database not found: {dbPath}");
		}

		if (File.Exists(outputPath) && !force)
		{
			throw new IOException($"Output file already exists: {outputPath} (use --force to overwrite)");
		}

		if (force)
		{
			// Fresh file: a force-rewrite over a larger previous backup must not
			// leave stale trailing pages behind.
			File.Delete(outputPath);
		}

		string? outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (!string.IsNullOrEmpty(outputDir))
		{
			Directory.CreateDirectory(outputDir);
		}

		await using var source = new SqliteConnection($"Data Source={dbPath}");
		await source.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var destination = new SqliteConnection($"Data Source={outputPath}");
		await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
		source.BackupDatabase(destination);
	}

	/// <summary>
	/// Copies a backup file back over <paramref name="dbPath"/>. The backup is
	/// integrity-checked first — restoring a corrupt file over the last good
	/// database is the worst possible outcome — and an existing target is only
	/// replaced with <c>force</c>.
	/// </summary>
	public static async Task RestoreAsync(string backupPath, string dbPath, bool force = false, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
		if (!File.Exists(backupPath))
		{
			throw new FileNotFoundException($"Backup file not found: {backupPath}");
		}

		if (File.Exists(dbPath) && !force)
		{
			throw new IOException($"Target database already exists: {dbPath} (use --force to overwrite)");
		}

		VerifyResult verify = await VerifyAsync(backupPath, cancellationToken).ConfigureAwait(false);
		if (!verify.Ok)
		{
			throw new InvalidDataException($"Backup failed the integrity check, refusing to restore: {verify.Errors[0]}");
		}

		string? targetDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
		if (!string.IsNullOrEmpty(targetDir))
		{
			Directory.CreateDirectory(targetDir);
		}

		File.Copy(backupPath, dbPath, overwrite: force);
	}

	/// <summary>Runs <c>PRAGMA integrity_check</c>; throws if the file is missing.</summary>
	public static async Task<VerifyResult> VerifyAsync(string dbPath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
		if (!File.Exists(dbPath))
		{
			throw new FileNotFoundException($"Database not found: {dbPath}");
		}

		await using var connection = new SqliteConnection($"Data Source={dbPath}");
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA integrity_check;";

		var errors = new List<string>();
		try
		{
			await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				string row = reader.GetString(0);
				if (!string.Equals(row, "ok", StringComparison.Ordinal))
				{
					errors.Add(row);
				}
			}
		}
		catch (SqliteException ex)
		{
			// A file that is not a database fails the check itself instead of
			// returning error rows; surface that as a failed verification.
			return new VerifyResult(false, [ex.Message]);
		}

		return new VerifyResult(errors.Count == 0, errors);
	}
}
