using Microsoft.Data.Sqlite;
using Vapor.Backup;
using Xunit;

namespace Vapor.Backup.Tests;

/// <summary>
/// The CLI shell: exit-code contract (0 success / 1 operation failure / 2
/// usage error) driven by calling <see cref="Program.Main"/> in-process with
/// captured stdout/stderr, plus the happy paths end to end.
/// </summary>
public sealed class BackupCliTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vapor-backup-cli-{Guid.NewGuid():N}");

	public BackupCliTests()
	{
		Directory.CreateDirectory(_dir);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(_dir, recursive: true);
	}

	private string P(string name) => Path.Combine(_dir, name);

	[Fact]
	public async Task Backup_HappyPath_ReturnsZero()
	{
		await CreateSampleDbAsync(P("db.db"));

		(int exit, string stdout, string stderr) = await RunAsync("backup", P("db.db"), P("out.db"));

		Assert.Equal(0, exit);
		Assert.True(File.Exists(P("out.db")));
		Assert.Contains("Backed up", stdout);
		Assert.Empty(stderr);
	}

	[Fact]
	public async Task Backup_WithForce_ReturnsZero()
	{
		await CreateSampleDbAsync(P("db.db"));
		await File.WriteAllTextAsync(P("out.db"), "stale");

		(int exit, string stdout, string _) = await RunAsync("backup", P("db.db"), P("out.db"), "--force");

		Assert.Equal(0, exit);
		Assert.Contains("Backed up", stdout);
	}

	[Fact]
	public async Task Backup_ExistingOutputWithoutForce_ReturnsOne()
	{
		await CreateSampleDbAsync(P("db.db"));
		await File.WriteAllTextAsync(P("out.db"), "stale");

		(int exit, string _, string stderr) = await RunAsync("backup", P("db.db"), P("out.db"));

		Assert.Equal(1, exit);
		Assert.Contains("already exists", stderr);
		Assert.Equal("stale", await File.ReadAllTextAsync(P("out.db")));
	}

	[Fact]
	public async Task Backup_MissingSource_ReturnsOne()
	{
		(int exit, string _, string stderr) = await RunAsync("backup", P("nope.db"), P("out.db"));

		Assert.Equal(1, exit);
		Assert.Contains("not found", stderr);
	}

	[Fact]
	public async Task Backup_WrongArity_ReturnsTwo()
	{
		(int exit, string _, string stderr) = await RunAsync("backup", P("db.db"));

		Assert.Equal(2, exit);
		Assert.Contains("backup requires", stderr);
	}

	[Fact]
	public async Task Restore_HappyPath_ReturnsZero()
	{
		await CreateSampleDbAsync(P("backup.db"));

		(int exit, string stdout, string _) = await RunAsync("restore", P("backup.db"), P("db.db"));

		Assert.Equal(0, exit);
		Assert.Contains("Restored", stdout);
		Assert.True(File.Exists(P("db.db")));
	}

	[Fact]
	public async Task Restore_ExistingTargetWithoutForce_ReturnsOne()
	{
		await CreateSampleDbAsync(P("backup.db"));
		await File.WriteAllTextAsync(P("db.db"), "precious");

		(int exit, string _, string stderr) = await RunAsync("restore", P("backup.db"), P("db.db"));

		Assert.Equal(1, exit);
		Assert.Contains("already exists", stderr);
		Assert.Equal("precious", await File.ReadAllTextAsync(P("db.db")));
	}

	[Fact]
	public async Task Restore_WithForce_ReplacesTarget_ReturnsZero()
	{
		await CreateSampleDbAsync(P("backup.db"));
		await File.WriteAllTextAsync(P("db.db"), "precious");

		(int exit, string _, string _) = await RunAsync("restore", P("backup.db"), P("db.db"), "--force");

		Assert.Equal(0, exit);
	}

	[Fact]
	public async Task Restore_CorruptBackup_ReturnsOneWithoutCreatingTarget()
	{
		await File.WriteAllTextAsync(P("garbage.db"), "not-sqlite");

		(int exit, string _, string stderr) = await RunAsync("restore", P("garbage.db"), P("db.db"));

		Assert.Equal(1, exit);
		Assert.Contains("integrity", stderr);
		Assert.False(File.Exists(P("db.db")));
	}

	[Fact]
	public async Task Restore_MissingBackup_ReturnsOne()
	{
		(int exit, string _, string stderr) = await RunAsync("restore", P("nope.db"), P("db.db"));

		Assert.Equal(1, exit);
		Assert.Contains("not found", stderr);
	}

	[Fact]
	public async Task Restore_WrongArity_ReturnsTwo()
	{
		(int exit, string _, string stderr) = await RunAsync("restore", P("backup.db"));

		Assert.Equal(2, exit);
		Assert.Contains("restore requires", stderr);
	}

	[Fact]
	public async Task Verify_HealthyDatabase_ReturnsZero()
	{
		await CreateSampleDbAsync(P("db.db"));

		(int exit, string stdout, string stderr) = await RunAsync("verify", P("db.db"));

		Assert.Equal(0, exit);
		Assert.Contains("integrity check: ok", stdout);
		Assert.Empty(stderr);
	}

	[Fact]
	public async Task Verify_NonDatabaseFile_ReturnsOne()
	{
		await File.WriteAllTextAsync(P("junk.db"), "not-sqlite");

		(int exit, string _, string stderr) = await RunAsync("verify", P("junk.db"));

		Assert.Equal(1, exit);
		Assert.NotEmpty(stderr);
	}

	[Fact]
	public async Task Verify_MissingDatabase_ReturnsOne()
	{
		(int exit, string _, string stderr) = await RunAsync("verify", P("nope.db"));

		Assert.Equal(1, exit);
		Assert.Contains("not found", stderr);
	}

	[Fact]
	public async Task Verify_WrongArity_ReturnsTwo()
	{
		(int exit, string _, string stderr) = await RunAsync("verify");

		Assert.Equal(2, exit);
		Assert.Contains("verify requires", stderr);
	}

	[Theory]
	[InlineData("--help")]
	[InlineData("-h")]
	public async Task HelpFlag_ReturnsZeroWithUsage(string flag)
	{
		(int exit, string stdout, string _) = await RunAsync(flag);

		Assert.Equal(0, exit);
		Assert.Contains("Vapor backup tool", stdout);
	}

	[Fact]
	public async Task NoArguments_ReturnsTwoWithUsage()
	{
		(int exit, string stdout, string _) = await RunAsync();

		Assert.Equal(2, exit);
		Assert.Contains("Vapor backup tool", stdout);
	}

	[Fact]
	public async Task UnknownCommand_ReturnsTwo()
	{
		(int exit, string _, string stderr) = await RunAsync("vacuum", P("db.db"));

		Assert.Equal(2, exit);
		Assert.Contains("Unknown command: vacuum", stderr);
	}

	[Fact]
	public async Task UnknownOption_ReturnsTwo()
	{
		(int exit, string _, string stderr) = await RunAsync("backup", P("db.db"), P("out.db"), "--ssh");

		Assert.Equal(2, exit);
		Assert.Contains("Unknown option: --ssh", stderr);
	}

	private static async Task<(int Exit, string StdOut, string StdErr)> RunAsync(params string[] args)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalErr = Console.Error;
		var outWriter = new StringWriter();
		var errWriter = new StringWriter();
		Console.SetOut(outWriter);
		Console.SetError(errWriter);
		int exit;
		try
		{
			exit = await Program.Main(args);
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalErr);
		}

		return (exit, outWriter.ToString(), errWriter.ToString());
	}

	private static async Task CreateSampleDbAsync(string path)
	{
		await using var connection = new SqliteConnection($"Data Source={path}");
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t (name) VALUES ('alpha');";
		await command.ExecuteNonQueryAsync();
	}
}
