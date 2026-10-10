namespace Vapor.Backup;

/// <summary>
/// CLI: backup/restore/verify over the Vapor SQLite databases.
/// Exit codes: 0 = success, 1 = operation failure, 2 = usage error.
///
/// Usage:
///   dotnet run --project tools/Vapor.Backup -- backup &lt;db&gt; &lt;output&gt; [--force]
///   dotnet run --project tools/Vapor.Backup -- restore &lt;backup&gt; &lt;db&gt; [--force]
///   dotnet run --project tools/Vapor.Backup -- verify &lt;db&gt;
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args.Length == 0)
		{
			PrintUsage();
			return 2;
		}

		string command = args[0];
		if (command is "--help" or "-h")
		{
			PrintUsage();
			return 0;
		}

		bool force = false;
		var positional = new List<string>();
		for (int i = 1; i < args.Length; i++)
		{
			if (args[i] == "--force")
			{
				force = true;
			}
			else if (args[i].StartsWith('-'))
			{
				Console.Error.WriteLine($"Unknown option: {args[i]}");
				PrintUsage();
				return 2;
			}
			else
			{
				positional.Add(args[i]);
			}
		}

		try
		{
			switch (command)
			{
				case "backup":
					if (positional.Count != 2)
					{
						Console.Error.WriteLine("backup requires <db> <output>");
						PrintUsage();
						return 2;
					}

					await BackupTool.BackupAsync(positional[0], positional[1], force).ConfigureAwait(false);
					Console.WriteLine($"Backed up {positional[0]} -> {positional[1]}");
					return 0;
				case "restore":
					if (positional.Count != 2)
					{
						Console.Error.WriteLine("restore requires <backup> <db>");
						PrintUsage();
						return 2;
					}

					await BackupTool.RestoreAsync(positional[0], positional[1], force).ConfigureAwait(false);
					Console.WriteLine($"Restored {positional[0]} -> {positional[1]}");
					return 0;
				case "verify":
					if (positional.Count != 1)
					{
						Console.Error.WriteLine("verify requires <db>");
						PrintUsage();
						return 2;
					}

					VerifyResult result = await BackupTool.VerifyAsync(positional[0]).ConfigureAwait(false);
					if (result.Ok)
					{
						Console.WriteLine("integrity check: ok");
						return 0;
					}

					foreach (string error in result.Errors)
					{
						Console.Error.WriteLine(error);
					}

					return 1;
				default:
					Console.Error.WriteLine($"Unknown command: {command}");
					PrintUsage();
					return 2;
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"ERROR: {ex.Message}");
			return 1;
		}
	}

	private static void PrintUsage()
	{
		Console.WriteLine("""
			Vapor backup tool

			Usage:
			  dotnet run --project tools/Vapor.Backup -- backup <db> <output> [--force]
			  dotnet run --project tools/Vapor.Backup -- restore <backup> <db> [--force]
			  dotnet run --project tools/Vapor.Backup -- verify <db>
			  dotnet run --project tools/Vapor.Backup -- --help

			Commands:
			  backup   Consistent online copy of a SQLite database (safe while the control plane runs)
			  restore  Integrity-check a backup, then copy it over a database path
			  verify   Run PRAGMA integrity_check

			Exit codes: 0 success, 1 operation failure, 2 usage error.
			""");
	}
}
