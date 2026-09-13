using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Security;

namespace Vapor.Agent;

/// <summary>
/// Implements the `import-mafile` CLI entry point: imports SteamDesktopAuthenticator /
/// steamguard-cli .maFile exports into this agent's encrypted credential store and exits.
/// The import runs entirely agent-side on purpose — the secrets inside a maFile are the
/// same shared/identity secrets that must never travel through the control plane or
/// appear in task records.
/// </summary>
public static class MaFileImportCli
{
	public const string Usage = "usage: Vapor.Agent import-mafile <file-or-directory...> [--password <mafile password>]";

	public static async Task<int> RunAsync(
		string[] args,
		ICredentialStore credentialStore,
		ILogger logger,
		CancellationToken cancellationToken = default)
	{
		string? password = null;
		var paths = new List<string>();

		for (int i = 0; i < args.Length; i++)
		{
			if (args[i] is "--password" or "-p")
			{
				if (i + 1 >= args.Length)
				{
					logger.LogError("{Usage}", Usage);
					return 2;
				}

				password = args[++i];
			}
			else
			{
				paths.Add(args[i]);
			}
		}

		if (paths.Count == 0)
		{
			logger.LogError("{Usage}", Usage);
			return 2;
		}

		List<string> files = ExpandPaths(paths);
		if (files.Count == 0)
		{
			logger.LogError("no .maFile files found in the given paths");
			return 2;
		}

		int imported = 0;
		int failed = 0;

		foreach (string file in files)
		{
			try
			{
				string json = await File.ReadAllTextAsync(file, cancellationToken);
				MaFileInfo info = MaFileParser.Parse(json, password);

				if (!string.IsNullOrWhiteSpace(info.SharedSecret))
				{
					await credentialStore.SaveSharedSecretAsync(info.AccountName, info.SharedSecret, cancellationToken);
				}

				if (!string.IsNullOrWhiteSpace(info.IdentitySecret))
				{
					await credentialStore.SaveIdentitySecretAsync(info.AccountName, info.IdentitySecret, cancellationToken);
				}

				logger.LogInformation(
					"Imported {File}: account={AccountName} steamid={SteamId} sharedSecret={HasShared} identitySecret={HasIdentity} (the device id is derived per-account at use time; any session tokens in the file were ignored)",
					file,
					info.AccountName,
					string.IsNullOrEmpty(info.SteamId) ? "-" : info.SteamId,
					!string.IsNullOrWhiteSpace(info.SharedSecret),
					!string.IsNullOrWhiteSpace(info.IdentitySecret));
				imported++;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				logger.LogError("Failed to import {File}: {Message}", file, ex.Message);
				failed++;
			}
		}

		logger.LogInformation("maFile import finished: {Imported} imported, {Failed} failed", imported, failed);
		return failed == 0 ? 0 : 1;
	}

	// Each path is either a maFile or a directory to scan for *.maFile (SDA exports a
	// directory of them, one per account).
	private static List<string> ExpandPaths(IEnumerable<string> paths)
	{
		var files = new List<string>();

		foreach (string path in paths)
		{
			if (Directory.Exists(path))
			{
				files.AddRange(Directory.EnumerateFiles(path)
					.Where(f => f.EndsWith(".maFile", StringComparison.OrdinalIgnoreCase))
					.OrderBy(f => f, StringComparer.Ordinal));
			}
			else if (File.Exists(path))
			{
				files.Add(path);
			}
			else
			{
				// Keep unknown paths in the list so the per-file error message names them.
				files.Add(path);
			}
		}

		return files;
	}
}
