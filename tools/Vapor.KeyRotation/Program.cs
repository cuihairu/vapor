using System.Text;
using Vapor.Steam.Core.Security;

namespace Vapor.KeyRotation;

/// <summary>
/// CLI tool that re-encrypts the Vapor credential store from an old encryption key to a new one.
///
/// Usage:
///   dotnet run --project tools/Vapor.KeyRotation -- --store ~/.vapor/credentials.json \
///     --old-key file:/run/secrets/vapor-old-key --new-key base64:<...> [--dry-run]
///
/// Key formats (for both --old-key and --new-key):
///   base64:<value>  raw key bytes encoded as base64 (recommended)
///   file:<path>     key file content (trimmed; base64 content is decoded when length fits)
///   env:<VAR>       environment variable holding the key (interpreted as plain text)
///   <plain text>    fallback: raw UTF-8 text (must be >= 32 bytes)
/// </summary>
public static class Program
{
	public static int Main(string[] args)
	{
		string? storePath = null;
		string? oldKeySpec = null;
		string? newKeySpec = null;
		bool dryRun = false;

		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--store":
					storePath = GetValue(args, ref i, "--store");
					break;
				case "--old-key":
					oldKeySpec = GetValue(args, ref i, "--old-key");
					break;
				case "--new-key":
					newKeySpec = GetValue(args, ref i, "--new-key");
					break;
				case "--dry-run":
					dryRun = true;
					break;
				case "--help" or "-h":
					PrintUsage();
					return 0;
				default:
					Console.Error.WriteLine($"Unknown argument: {args[i]}");
					PrintUsage();
					return 2;
			}
		}

		if (string.IsNullOrWhiteSpace(storePath) ||
			string.IsNullOrWhiteSpace(oldKeySpec) ||
			string.IsNullOrWhiteSpace(newKeySpec))
		{
			PrintUsage();
			return 2;
		}

		storePath = ExpandPath(storePath);

		byte[] oldKey;
		byte[] newKey;
		try
		{
			oldKey = ParseKeySpec(oldKeySpec, "--old-key");
			newKey = ParseKeySpec(newKeySpec, "--new-key");
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"ERROR: {ex.Message}");
			return 2;
		}

		if (oldKey.SequenceEqual(newKey))
		{
			Console.Error.WriteLine("ERROR: --new-key must differ from --old-key");
			return 2;
		}

		try
		{
			var result = CredentialStoreRotator.Rotate(storePath, oldKey, newKey, logger: null, dryRun: dryRun);

			string mode = result.DryRun
				? "dry-run (file unchanged)"
				: result.Success
					? "applied (backup written as *.bak.pre-rotate)"
					: "aborted (store unchanged; resolve failed accounts first)";

			Console.WriteLine($"Store:            {storePath}");
			Console.WriteLine($"Total accounts:   {result.TotalAccounts}");
			Console.WriteLine($"Rotated accounts: {result.RotatedAccounts}");
			Console.WriteLine($"Failed accounts:  {result.FailedAccounts.Count}");
			Console.WriteLine($"Mode:             {mode}");

			foreach (string account in result.FailedAccounts)
			{
				Console.Error.WriteLine($"FAILED: {account}");
			}

			return result.Success ? 0 : 1;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"ERROR: {ex.Message}");
			return 1;
		}
	}

	private static string GetValue(string[] args, ref int index, string option)
	{
		if (index + 1 >= args.Length)
		{
			Console.Error.WriteLine($"ERROR: {option} requires a value");
			Environment.Exit(2);
		}

		return args[++index];
	}

	private static byte[] ParseKeySpec(string spec, string optionName)
	{
		if (spec.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				return Convert.FromBase64String(spec["base64:".Length..].Trim());
			}
			catch (FormatException)
			{
				throw new ArgumentException($"{optionName}: invalid base64 value");
			}
		}

		if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
		{
			string path = ExpandPath(spec["file:".Length..]);
			if (!File.Exists(path))
			{
				throw new ArgumentException($"{optionName}: key file not found: {path}");
			}

			string content = File.ReadAllText(path).Trim();

			try
			{
				byte[] decoded = Convert.FromBase64String(content);
				if (decoded.Length >= 32)
				{
					return decoded;
				}
			}
			catch (FormatException)
			{
				// Fall through to raw text.
			}

			return Encoding.UTF8.GetBytes(content);
		}

		if (spec.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
		{
			string? value = Environment.GetEnvironmentVariable(spec["env:".Length..]);
			if (string.IsNullOrEmpty(value))
			{
				throw new ArgumentException($"{optionName}: environment variable is not set or empty");
			}

			return Encoding.UTF8.GetBytes(value.Trim());
		}

		return Encoding.UTF8.GetBytes(spec);
	}

	private static string ExpandPath(string path)
	{
		if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Path.Combine(home, path[2..]);
		}

		return Path.GetFullPath(path);
	}

	private static void PrintUsage()
	{
		Console.WriteLine("""
			Vapor key rotation tool

			Usage:
			  dotnet run --project tools/Vapor.KeyRotation -- [options]

			Options:
			  --store <path>     Path to the credentials.json store (required)
			  --old-key <spec>   Current encryption key (required)
			  --new-key <spec>   Replacement encryption key (required)
			  --dry-run          Validate only; do not modify the store
			  --help             Show this help

			Key spec formats:
			  base64:<value>   raw key bytes as base64 (recommended)
			  file:<path>      key read from file (base64 content decoded when valid)
			  env:<VAR>        environment variable holding plain-text key
			  <plain text>     raw UTF-8 text, must be >= 32 bytes
			""");
	}
}
