using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Plugins.Core;
using Xunit;

namespace Vapor.Agent.Tests;

/// <summary>Builds an installable in-memory package around the real TestPlugin assembly.</summary>
internal static class PluginTestPackages
{
	public const string PluginId = "vapor.test-plugin";
	public const string PluginVersion = "1.0.0";

	public static string ManifestJson =>
		JsonSerializer.Serialize(new Dictionary<string, object?>
		{
			["id"] = PluginId,
			["name"] = "Vapor Test Plugin",
			["version"] = PluginVersion,
			["apiVersion"] = "1.0",
			["entryAssembly"] = "Vapor.Plugins.TestPlugin.dll",
			["entryType"] = "Vapor.Plugins.TestPlugin.TestPlugin",
			["permissions"] = PluginPermissions.All
		});

	public static byte[] Build(string? manifestJson = null)
	{
		using var buffer = new MemoryStream();
		using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			var manifest = zip.CreateEntry("plugin.json");
			using (var writer = new StreamWriter(manifest.Open()))
			{
				writer.Write(manifestJson ?? ManifestJson);
			}

			var assemblyEntry = zip.CreateEntry("Vapor.Plugins.TestPlugin.dll");
			using (var stream = assemblyEntry.Open())
			{
				stream.Write(File.ReadAllBytes(AssemblyPath));
			}
		}

		return buffer.ToArray();
	}

	/// <summary>Writes the package next to the plugins root and returns its file:// url + sha256 hex.</summary>
	public static (string Url, string Sha256) StageAsFile(string directory, string? manifestJson = null)
	{
		string packagePath = Path.Combine(directory, $"pkg-{Guid.NewGuid():N}.zip");
		File.WriteAllBytes(packagePath, Build(manifestJson));
		return (new Uri(packagePath).AbsoluteUri, Sha256Hex(File.ReadAllBytes(packagePath)));
	}

	public static string Sha256Hex(byte[] content) =>
		Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

	public static string AssemblyPath =>
		Path.Combine(AppContext.BaseDirectory, "Vapor.Plugins.TestPlugin.dll");

	public static PluginManager CreateManager() =>
		new(new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()), NullLoggerFactory.Instance);

	public static string NewPluginsRoot(string kind)
	{
		string parent = Path.Combine(Path.GetTempPath(), $"vapor-{kind}-tests", Guid.NewGuid().ToString("N"));
		string dir = Path.Combine(parent, "plugins");
		Directory.CreateDirectory(dir);
		return dir;
	}

	/// <summary>
	/// Cleanup that tolerates the collectible-ALC file lock on Windows: a plugin
	/// DLL stays mapped until GC releases it, so deleting the plugins root can
	/// throw right after a test passes. Leftovers live under %TEMP% either way.
	/// </summary>
	internal static void DeleteBestEffort(string directory)
	{
		try
		{
			Directory.Delete(directory, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
