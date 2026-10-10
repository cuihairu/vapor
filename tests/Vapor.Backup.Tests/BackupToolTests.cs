using Microsoft.Data.Sqlite;
using Vapor.Backup;
using Xunit;

namespace Vapor.Backup.Tests;

/// <summary>
/// The backup/restore/verify core: online-backup round-trips (including a
/// snapshot taken while an uncommitted write transaction is open), force/
/// refuse semantics on both ends, corrupt-backup refusal, and the integrity
/// check arms.
/// </summary>
public sealed class BackupToolTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vapor-backup-tool-{Guid.NewGuid():N}");
	private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));

	public BackupToolTests()
	{
		Directory.CreateDirectory(_dir);
	}

	public void Dispose()
	{
		// Windows: ADO.NET keeps a pooled file handle even after Dispose();
		// clearing the pool is what actually releases the files for deletion.
		SqliteConnection.ClearAllPools();
		Directory.Delete(_dir, recursive: true);
		_cts.Dispose();
	}

	private string Path_(params string[] parts) => System.IO.Path.Combine(parts.Prepend(_dir).ToArray());

	[Fact]
	public async Task Backup_RoundTripsRowsThroughRestore()
	{
		string db = Path_("source.db");
		await CreateSampleDbAsync(db);

		await BackupTool.BackupAsync(db, Path_("backup.db"), cancellationToken: _cts.Token);
		await BackupTool.RestoreAsync(Path_("backup.db"), Path_("restored.db"), cancellationToken: _cts.Token);

		Assert.Equal(await ReadNamesAsync(db), await ReadNamesAsync(Path_("restored.db")));
	}

	[Fact]
	public async Task Backup_SnapshotsConsistentlyWhileUncommittedWriteIsOpen()
	{
		// The documented production property: the copy is consistent even while
		// the control plane keeps writing. An open uncommitted transaction must
		// not leak half of itself into the backup.
		string db = Path_("live.db");
		await CreateSampleDbAsync(db);
		await using var writer = new SqliteConnection($"Data Source={db}");
		await writer.OpenAsync(_cts.Token);
		await using (var tx = writer.BeginTransaction())
		{
			await using var insert = writer.CreateCommand();
			insert.Transaction = tx;
			insert.CommandText = "INSERT INTO t (name) VALUES ('uncommitted');";
			await insert.ExecuteNonQueryAsync(_cts.Token);

			await BackupTool.BackupAsync(db, Path_("snapshot.db"), cancellationToken: _cts.Token);
			await tx.RollbackAsync(_cts.Token);
		}

		Assert.Equal(["alpha", "beta"], await ReadNamesAsync(Path_("snapshot.db")));
	}

	[Fact]
	public async Task Backup_CreatesMissingOutputDirectory()
	{
		string db = Path_("source.db");
		await CreateSampleDbAsync(db);

		await BackupTool.BackupAsync(db, Path_("nested", "deeper", "backup.db"), cancellationToken: _cts.Token);

		Assert.True(File.Exists(Path_("nested", "deeper", "backup.db")));
	}

	[Fact]
	public async Task Backup_ExistingOutputRefusedWithoutForce()
	{
		string db = Path_("source.db");
		await CreateSampleDbAsync(db);
		string output = Path_("backup.db");
		await File.WriteAllTextAsync(output, "stale", _cts.Token);

		await Assert.ThrowsAsync<IOException>(
			() => BackupTool.BackupAsync(db, output, cancellationToken: _cts.Token));
		Assert.Equal("stale", await File.ReadAllTextAsync(output, _cts.Token));
	}

	[Fact]
	public async Task Backup_ForceReplacesExistingOutput()
	{
		string db = Path_("source.db");
		await CreateSampleDbAsync(db);
		string output = Path_("backup.db");
		await File.WriteAllTextAsync(output, "stale", _cts.Token);

		await BackupTool.BackupAsync(db, output, force: true, cancellationToken: _cts.Token);

		Assert.Equal(new FileInfo(db).Length, new FileInfo(output).Length);
		Assert.Equal(["alpha", "beta"], await ReadNamesAsync(output));
	}

	[Fact]
	public async Task Backup_MissingSource_Throws()
	{
		await Assert.ThrowsAsync<FileNotFoundException>(
			() => BackupTool.BackupAsync(Path_("nope.db"), Path_("out.db"), cancellationToken: _cts.Token));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Backup_BlankSourcePath_Throws(string? source)
	{
		await Assert.ThrowsAnyAsync<ArgumentException>(
			() => BackupTool.BackupAsync(source!, Path_("out.db"), cancellationToken: _cts.Token));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Backup_BlankOutputPath_Throws(string? output)
	{
		string db = Path_("source.db");
		await CreateSampleDbAsync(db);
		await Assert.ThrowsAnyAsync<ArgumentException>(
			() => BackupTool.BackupAsync(db, output!, cancellationToken: _cts.Token));
	}

	[Fact]
	public async Task Restore_ExistingTargetRefusedWithoutForce()
	{
		string backup = Path_("backup.db");
		await CreateSampleDbAsync(backup);
		string target = Path_("target.db");
		await File.WriteAllTextAsync(target, "precious", _cts.Token);

		await Assert.ThrowsAsync<IOException>(
			() => BackupTool.RestoreAsync(backup, target, cancellationToken: _cts.Token));
		Assert.Equal("precious", await File.ReadAllTextAsync(target, _cts.Token));
	}

	[Fact]
	public async Task Restore_ForceReplacesExistingTarget()
	{
		string backup = Path_("backup.db");
		await CreateSampleDbAsync(backup);
		string target = Path_("target.db");
		await File.WriteAllTextAsync(target, "precious", _cts.Token);

		await BackupTool.RestoreAsync(backup, target, force: true, cancellationToken: _cts.Token);

		Assert.Equal(["alpha", "beta"], await ReadNamesAsync(target));
	}

	[Fact]
	public async Task Restore_CorruptBackupRefusedWithoutTouchingTarget()
	{
		string backup = Path_("garbage.db");
		await File.WriteAllBytesAsync(backup, [0x6e, 0x6f, 0x74, 0x2d, 0x73, 0x71, 0x6c, 0x69, 0x74, 0x65], _cts.Token);
		string target = Path_("target.db");

		await Assert.ThrowsAsync<InvalidDataException>(
			() => BackupTool.RestoreAsync(backup, target, cancellationToken: _cts.Token));
		Assert.False(File.Exists(target));
	}

	[Fact]
	public async Task Restore_MissingBackup_Throws()
	{
		await Assert.ThrowsAsync<FileNotFoundException>(
			() => BackupTool.RestoreAsync(Path_("nope.db"), Path_("target.db"), cancellationToken: _cts.Token));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Restore_BlankBackupPath_Throws(string? backup)
	{
		await Assert.ThrowsAnyAsync<ArgumentException>(
			() => BackupTool.RestoreAsync(backup!, Path_("target.db"), cancellationToken: _cts.Token));
	}

	[Fact]
	public async Task Verify_ReturnsOkForHealthyDatabase()
	{
		string db = Path_("source.db");
		await CreateSampleDbAsync(db);

		VerifyResult result = await BackupTool.VerifyAsync(db, _cts.Token);

		Assert.True(result.Ok);
		Assert.Empty(result.Errors);
	}

	[Fact]
	public async Task Verify_ReturnsErrorsForNonDatabaseFile()
	{
		string junk = Path_("junk.db");
		await File.WriteAllBytesAsync(junk, [0x6e, 0x6f, 0x74, 0x2d, 0x73, 0x71, 0x6c, 0x69, 0x74, 0x65], _cts.Token);

		VerifyResult result = await BackupTool.VerifyAsync(junk, _cts.Token);

		Assert.False(result.Ok);
		Assert.NotEmpty(result.Errors);
	}

	[Fact]
	public async Task Verify_ReportsDamageAsRowsWithoutThrowing()
	{
		// A damaged but recognizable database reports its problems through the
		// integrity_check result rows instead of throwing — both failure shapes
		// must end up as a failed verification.
		string db = Path_("damaged.db");
		await using (var connection = new SqliteConnection($"Data Source={db}"))
		{
			await connection.OpenAsync(_cts.Token);
			await using var command = connection.CreateCommand();
			command.CommandText = """
				CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT);
				INSERT INTO t (name)
				WITH RECURSIVE seq(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < 500)
				SELECT 'row-' || i FROM seq;
				""";
			await command.ExecuteNonQueryAsync(_cts.Token);
		}

		await using (var stream = new FileStream(db, FileMode.Open, FileAccess.ReadWrite))
		{
			stream.SetLength(stream.Length - 100);
		}

		// The pool may still hold a cached page image from the write connection;
		// drop it so the check reads the truncated file from disk.
		SqliteConnection.ClearAllPools();

		VerifyResult result = await BackupTool.VerifyAsync(db, _cts.Token);

		Assert.False(result.Ok);
		Assert.NotEmpty(result.Errors);
	}

	[Fact]
	public async Task Verify_MissingDatabase_Throws()
	{
		await Assert.ThrowsAsync<FileNotFoundException>(
			() => BackupTool.VerifyAsync(Path_("nope.db"), _cts.Token));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Verify_BlankPath_Throws(string? dbPath)
	{
		await Assert.ThrowsAnyAsync<ArgumentException>(
			() => BackupTool.VerifyAsync(dbPath!, _cts.Token));
	}

	private static async Task CreateSampleDbAsync(string path)
	{
		await using var connection = new SqliteConnection($"Data Source={path}");
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t (name) VALUES ('alpha'); INSERT INTO t (name) VALUES ('beta');";
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<List<string>> ReadNamesAsync(string path)
	{
		await using var connection = new SqliteConnection($"Data Source={path}");
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM t ORDER BY id;";
		await using var reader = await command.ExecuteReaderAsync();
		var names = new List<string>();
		while (await reader.ReadAsync())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}
}
