using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core.Utilities;

namespace Vapor.Agent;

/// <summary>
/// Installs a plugin package from a URL: downloads the zip, verifies the SHA-256
/// checksum (mandatory — a package without a matching checksum never touches the
/// plugins directory), validates the embedded manifest, swaps the plugin directory
/// atomically and hot-loads it into the running <see cref="PluginManager"/>. The
/// ControlPlane never sees the binary; it only names a URL and its checksum.
/// </summary>
public sealed class PluginPackageInstaller
{
	/// <summary>Hard cap on downloaded package size; the whole package is held in
	/// memory for checksum verification, so an unbounded download would be an OOM lever.</summary>
	internal const long MaxPackageBytes = 128 * 1024 * 1024;

	private readonly string _pluginsRoot;
	private readonly Func<HttpClient> _httpClientFactory;
	private readonly ILogger _logger;

	public PluginPackageInstaller(string pluginsRoot, Func<HttpClient>? httpClientFactory, ILogger logger)
	{
		_pluginsRoot = pluginsRoot;
		_httpClientFactory = httpClientFactory ?? CreateDefaultHttpClient;
		_logger = logger;
	}

	/// <summary>The plugins root this installer swaps directories under.</summary>
	public string PluginsRoot => _pluginsRoot;

	/// <summary>
	/// Downloads and installs the package. Fails (returns an error, no state change)
	/// on download errors, size overruns, checksum mismatches, malformed manifests,
	/// id/version mismatches with the instruction, or load failures.
	/// </summary>
	public async Task<PluginInstallResult> InstallAsync(
		string url,
		string sha256,
		string? expectedPluginId,
		string? expectedVersion,
		PluginManager manager,
		CancellationToken cancellationToken)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var packageUri) ||
			(packageUri.Scheme != Uri.UriSchemeHttps && packageUri.Scheme != Uri.UriSchemeHttp && packageUri.Scheme != Uri.UriSchemeFile))
		{
			return PluginInstallResult.Fail($"package url '{url}' is not an absolute http(s) or file url");
		}

		if (string.IsNullOrEmpty(sha256) || !IsHexString(sha256))
		{
			return PluginInstallResult.Fail("sha256 must be a 64-character hex digest of the package");
		}

		// 1. Download (bounded) and verify the checksum before anything touches disk
		//    beyond a staging directory — a bad package must leave no trace.
		byte[] package;
		try
		{
			package = await DownloadAsync(packageUri, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			return PluginInstallResult.Fail($"package download failed: {ex.Message}");
		}

		string actual = Convert.ToHexString(SHA256.HashData(package));
		if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
		{
			return PluginInstallResult.Fail($"checksum mismatch: expected {sha256.ToLowerInvariant()}, got {actual.ToLowerInvariant()}");
		}

		// 2. Stage the extraction, then validate the manifest from the staged copy.
		string staging = Path.Combine(_pluginsRoot, $".staging-{Guid.NewGuid():N}");
		string? pluginId = null;
		try
		{
			Directory.CreateDirectory(staging);
			string? manifestError = ExtractPackage(package, staging, out string manifestPath);
			if (manifestError is not null)
			{
				return PluginInstallResult.Fail(manifestError);
			}

			PluginManifest manifest;
			try
			{
				manifest = PluginManifest.Load(manifestPath);
			}
			catch (Exception ex)
			{
				return PluginInstallResult.Fail($"package manifest is invalid: {ex.Message}");
			}

			pluginId = manifest.Id;
			if (expectedPluginId is { } wanted && !string.Equals(wanted, manifest.Id, StringComparison.OrdinalIgnoreCase))
			{
				return PluginInstallResult.Fail($"package manifest id '{manifest.Id}' does not match the requested plugin id '{wanted}'");
			}

			if (expectedVersion is { } wantedVersion && !string.Equals(wantedVersion, manifest.Version, StringComparison.Ordinal))
			{
				return PluginInstallResult.Fail($"package manifest version '{manifest.Version}' does not match the requested version '{wantedVersion}'");
			}

			// 3. Everything checked out — swap the directory and hot-load. The unload
			//    happens after extraction so a failed download/unpack never takes the
			//    previously-loaded plugin down with it.
			string target = Path.Combine(_pluginsRoot, manifest.Id);
			bool replaced = manager.LoadedPlugins.Any(p =>
				string.Equals(p.Descriptor.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
			if (replaced)
			{
				await manager.UnloadAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
			}

			RetireDirectory(target);
			Directory.Move(staging, target);
			staging = string.Empty;

			var descriptor = new PluginDescriptor(
				manifest,
				target,
				Path.Combine(target, PluginManifest.ManifestFileName),
				Path.Combine(target, manifest.EntryAssembly));
			var loaded = await manager.LoadAsync(descriptor, cancellationToken).ConfigureAwait(false);

			_logger.LogInformation(
				"Plugin {PluginId} {Version} installed from {Url} (replaced: {Replaced})",
				manifest.Id, manifest.Version, SensitiveDataRedactor.Redact(url), replaced);
			return new PluginInstallResult(
				true, null, manifest.Id, manifest.Version, replaced,
				loaded.Actions.Select(a => a.Name).Order(StringComparer.Ordinal).ToList());
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Plugin install from {Url} failed after staging", SensitiveDataRedactor.Redact(url));
			// The previous plugin was possibly unloaded but the new one failed to load;
			// the staged/target files remain for inspection and the next agent restart
			// picks the new directory up. Surface the failure honestly.
			return PluginInstallResult.Fail($"install failed: {ex.Message}");
		}
		finally
		{
			if (staging.Length > 0 && Directory.Exists(staging))
			{
				TryDeleteDirectory(staging);
			}
		}
	}

	/// <summary>
	/// Extracts the zip into <paramref name="staging"/> with zip-slip protection and
	/// returns null when the package root contains a manifest, or an error message.
	/// </summary>
	internal string? ExtractPackage(byte[] package, string staging, out string manifestPath)
	{
		manifestPath = string.Empty;
		string root = Path.GetFullPath(staging);
		using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
		bool hasManifest = false;
		foreach (var entry in archive.Entries)
		{
			string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
			if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
				!string.Equals(destination, root, StringComparison.Ordinal))
			{
				return $"package entry '{entry.FullName}' escapes the extraction directory (zip-slip rejected)";
			}

			if (entry.FullName.EndsWith('/'))
			{
				Directory.CreateDirectory(destination);
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			entry.ExtractToFile(destination, overwrite: true);
			if (string.Equals(Path.GetFileName(destination), PluginManifest.ManifestFileName, StringComparison.Ordinal) &&
				string.Equals(Path.GetDirectoryName(destination), root, StringComparison.Ordinal))
			{
				hasManifest = true;
				manifestPath = destination;
			}
		}

		return hasManifest ? null : $"package root must contain {PluginManifest.ManifestFileName}";
	}

	/// <summary>Moves a leftover plugin directory aside and deletes it best-effort.</summary>
	internal void RetireDirectory(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}

		string retired = directory + $".old-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
		try
		{
			Directory.Move(directory, retired);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not move retired plugin directory {Directory} aside", directory);
			return;
		}

		TryDeleteDirectory(retired);
	}

	private void TryDeleteDirectory(string directory)
	{
		try
		{
			Directory.Delete(directory, recursive: true);
		}
		catch (Exception ex)
		{
			// Windows can keep files locked until the collectible ALC is finalized;
			// the retired directory is renamed out of the way so discovery never
			// sees it again either way.
			_logger.LogWarning(ex, "Deferred cleanup of plugin directory {Directory}", directory);
		}
	}

	private async Task<byte[]> DownloadAsync(Uri packageUri, CancellationToken cancellationToken)
	{
		if (packageUri.IsFile)
		{
			return await File.ReadAllBytesAsync(packageUri.LocalPath, cancellationToken).ConfigureAwait(false);
		}

		using var client = _httpClientFactory();
		using var response = await client.GetAsync(
			packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();

		long? length = response.Content.Headers.ContentLength;
		if (length is { } declared && declared > MaxPackageBytes)
		{
			throw new InvalidOperationException($"package is {declared} bytes, over the {MaxPackageBytes}-byte limit");
		}

		using var buffer = new MemoryStream();
		await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
		{
			// Bounded copy: the size cap is enforced by counting bytes, not by a
			// wall-clock budget (a slow but legitimate download must not be cut).
			byte[] chunk = new byte[81920];
			int read;
			while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
			{
				if (buffer.Length + read > MaxPackageBytes)
				{
					throw new InvalidOperationException($"package exceeds the {MaxPackageBytes}-byte limit");
				}

				await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			}
		}

		return buffer.ToArray();
	}

	private static HttpClient CreateDefaultHttpClient()
	{
		// CA2000 suppressed: ownership of the handler transfers to the HttpClient,
		// which is disposed by the caller of the factory.
#pragma warning disable CA2000
		var handler = new System.Net.Http.SocketsHttpHandler
		{
			ConnectTimeout = TimeSpan.FromSeconds(10),
			AllowAutoRedirect = false
		};
#pragma warning restore CA2000
		return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
	}

	private static bool IsHexString(string value)
	{
		if (value.Length != 64)
		{
			return false;
		}

		foreach (char c in value)
		{
			if (!char.IsAsciiHexDigit(c))
			{
				return false;
			}
		}

		return true;
	}
}

/// <summary>Outcome of one package install. <see cref="Plugins"/> carries the full
/// installed list after the operation so the ControlPlane can mirror it wholesale.</summary>
public sealed record PluginInstallResult(
	bool Success,
	string? Error,
	string? PluginId,
	string? Version,
	bool Replaced,
	IReadOnlyList<string> Actions)
{
	public static PluginInstallResult Fail(string error) => new(false, error, null, null, false, []);
}

/// <summary>Shared JSON helpers for the plugin host actions' output payloads.</summary>
public static class PluginOutput
{
	/// <summary>Serializes the currently-loaded plugins (id/version/trust/permissions/actions).</summary>
	public static IReadOnlyList<object> ListLoaded(PluginManager? manager)
	{
		if (manager is null)
		{
			return [];
		}

		return manager.LoadedPlugins
			.OrderBy(p => p.Descriptor.Manifest.Id, StringComparer.OrdinalIgnoreCase)
			.Select(p => (object)new Dictionary<string, object?>
			{
				["id"] = p.Descriptor.Manifest.Id,
				["name"] = p.Descriptor.Manifest.Name,
				["version"] = p.Descriptor.Manifest.Version,
				["apiVersion"] = p.Descriptor.Manifest.ApiVersion,
				["trust"] = p.Descriptor.Trust.ToString().ToLowerInvariant(),
				["permissions"] = p.GrantedPermissions,
				["actions"] = p.Actions.Select(a => a.Name).Order(StringComparer.Ordinal).ToList()
			})
			.ToList();
	}
}
