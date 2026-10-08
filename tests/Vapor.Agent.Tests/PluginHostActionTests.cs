using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Plugins.Core;
using Vapor.Protocol;
using Vapor.Steam.Core;
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
	public static (string Url, string Sha256) StageAsFile(string directory)
	{
		string packagePath = Path.Combine(directory, $"pkg-{Guid.NewGuid():N}.zip");
		File.WriteAllBytes(packagePath, Build());
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

public sealed class HostActionExecutorTests
{
	[Fact]
	public async Task ExecuteAsync_RefusesTaskTargetedAtAnotherAgent()
	{
		var action = new PluginListAction("/tmp/plugins", null);
		var task = CreateTask("agent:other-agent");

		(bool success, string? error, _) = await HostActionExecutor.ExecuteAsync(
			action, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Contains("agent:other-agent", error);
		Assert.Contains("this-agent", error);
	}

	[Fact]
	public async Task ExecuteAsync_RunsHostActionWithoutSession()
	{
		var action = new PluginListAction("/tmp/plugins", null);
		var task = CreateTask("agent:this-agent");

		(bool success, string? error, IReadOnlyDictionary<string, object?>? output) = await HostActionExecutor.ExecuteAsync(
			action, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		Assert.Null(error);
		Assert.NotNull(output);
		Assert.Equal(0, output!["count"]);
	}

	[Fact]
	public async Task ExecuteAsync_WrapsUnexpectedFailureAsFailedResult()
	{
		var throwing = new ThrowingHostAction();
		var task = CreateTask("agent:this-agent");

		(bool success, string? error, _) = await HostActionExecutor.ExecuteAsync(
			throwing, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Equal("exploded", error);
	}

	[Fact]
	public async Task ExecuteAsync_PropagatesCancellationInsteadOfWrappingIt()
	{
		var canceling = new CancelingHostAction();
		var task = CreateTask("agent:this-agent");

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostActionExecutor.ExecuteAsync(
			canceling, task, "this-agent", NullLogger.Instance, new CancellationToken(canceled: true)));
	}

	[Fact]
	public async Task ExecuteAsync_EnforcesDeclaredTimeout_WithStructuredResult()
	{
		var hanging = new HangingHostAction();
		var task = CreateTask("agent:this-agent");

		(bool success, string? error, IReadOnlyDictionary<string, object?>? output) = await HostActionExecutor.ExecuteAsync(
			hanging, task, "this-agent", NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Equal("action timeout", error);
		Assert.Null(output);
	}

	[Fact]
	public async Task ExecuteAsync_NoDeclaredTimeout_NeverCancelsOnItsOwn()
	{
		var unbounded = new WaitingHostAction();
		var task = CreateTask("agent:this-agent");

		using var callerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		(bool success, string? error, _) = await HostActionExecutor.ExecuteAsync(
			unbounded, task, "this-agent", NullLogger.Instance, callerCts.Token);

		Assert.True(success);
		Assert.Null(error);
		// The executor must not have raced its own timeout: only the caller's
		// 10 s budget could have cancelled a task that declares no timeout.
		Assert.False(unbounded.WasCancelled);
	}

	private static JobTask CreateTask(string target)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		return new JobTask("task-1", "job-1", target, "plugin_list", "local", null, JobTaskStatus.Running, 0, now, now);
	}

	private sealed class ThrowingHostAction : IHostAction
	{
		public string Name => "boom";
		public ActionMetadata Metadata => new(Name, "throws");
		public Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("exploded");
	}

	private sealed class CancelingHostAction : IHostAction
	{
		public string Name => "canceling";
		public ActionMetadata Metadata => new(Name, "cancels");
		public Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken) =>
			throw new OperationCanceledException(cancellationToken);
	}

	private sealed class HangingHostAction : IHostAction
	{
		public string Name => "hanging";
		public ActionMetadata Metadata => new(Name, "never completes on its own", RequiresLogin: false, TimeoutSeconds: 1);

		public async Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return new ActionResult(true, null, null);
		}
	}

	private sealed class WaitingHostAction : IHostAction
	{
		public bool WasCancelled { get; private set; }

		public string Name => "waiting";
		public ActionMetadata Metadata => new(Name, "declares no timeout");

		public async Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, object?> payload, CancellationToken cancellationToken)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			WasCancelled = cancellationToken.IsCancellationRequested;
			return new ActionResult(true, null, null);
		}
	}
}

public sealed class PluginPackageInstallerTests
{
	[Fact]
	public void ExtractPackage_UnpacksZipAndFindsRootManifest()
	{
		string staging = PluginTestPackages.NewPluginsRoot("extract");
		try
		{
			var installer = new PluginPackageInstaller(staging, null, NullLogger.Instance);
			using var buffer = new MemoryStream();
			using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
			{
				var manifest = zip.CreateEntry("plugin.json");
				using (var writer = new StreamWriter(manifest.Open()))
				{
					writer.Write(PluginTestPackages.ManifestJson);
				}

				var dependency = zip.CreateEntry("sub/deps/extra.dll");
				using (var writer = new StreamWriter(dependency.Open()))
				{
					writer.Write("not really a dll");
				}
			}

			string? error = installer.ExtractPackage(buffer.ToArray(), staging, out string manifestPath);

			Assert.Null(error);
			Assert.True(File.Exists(manifestPath));
			Assert.True(File.Exists(Path.Combine(staging, "sub", "deps", "extra.dll")));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(staging)!.FullName);
		}
	}

	[Fact]
	public void ExtractPackage_RejectsZipSlipEntries()
	{
		string staging = PluginTestPackages.NewPluginsRoot("zipslip");
		try
		{
			var installer = new PluginPackageInstaller(staging, null, NullLogger.Instance);
			using var buffer = new MemoryStream();
			using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
			{
				var entry = zip.CreateEntry("../evil.dll");
				using var writer = new StreamWriter(entry.Open());
				writer.Write("malicious");
			}

			string? error = installer.ExtractPackage(buffer.ToArray(), staging, out _);

			Assert.NotNull(error);
			Assert.Contains("zip-slip", error);
			Assert.False(File.Exists(Path.Combine(staging, "..", "evil.dll")));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(staging)!.FullName);
		}
	}

	[Fact]
	public void ExtractPackage_RequiresManifestAtPackageRoot()
	{
		string staging = PluginTestPackages.NewPluginsRoot("manifest-root");
		try
		{
			var installer = new PluginPackageInstaller(staging, null, NullLogger.Instance);
			using var buffer = new MemoryStream();
			using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
			{
				var entry = zip.CreateEntry("nested/plugin.json");
				using (var writer = new StreamWriter(entry.Open()))
				{
					writer.Write(PluginTestPackages.ManifestJson);
				}
			}

			string? error = installer.ExtractPackage(buffer.ToArray(), staging, out _);

			Assert.NotNull(error);
			Assert.Contains("plugin.json", error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(staging)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_ValidatesUrlAndChecksumBeforeTouchingDisk()
	{
		string root = PluginTestPackages.NewPluginsRoot("validate");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			string missingZip = new Uri(Path.Combine(root, $"missing-{Guid.NewGuid():N}.zip")).AbsoluteUri;

			PluginInstallResult badUrl = await installer.InstallAsync(
				"not-a-url", PluginTestPackages.Sha256Hex(new byte[] { 1 }), null, null, manager, CancellationToken.None);
			Assert.False(badUrl.Success);
			Assert.Contains("not an absolute", badUrl.Error);

			PluginInstallResult badSha = await installer.InstallAsync(
				missingZip, "nothex", null, null, manager, CancellationToken.None);
			Assert.False(badSha.Success);
			Assert.Contains("sha256", badSha.Error);

			PluginInstallResult mismatch = await installer.InstallAsync(
				missingZip, PluginTestPackages.Sha256Hex(new byte[] { 2 }), null, null, manager, CancellationToken.None);
			// The url exists in neither case; checksum runs only after a successful download,
			// so this asserts the download-failure arm on a well-formed digest.
			Assert.False(mismatch.Success);
			Assert.Contains("download failed", mismatch.Error);

			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_ChecksumMismatchFailsWithoutTrace()
	{
		string root = PluginTestPackages.NewPluginsRoot("checksum");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			string wrongSha = PluginTestPackages.Sha256Hex(new byte[] { 9, 9, 9 });
			Assert.NotEqual(sha, wrongSha);

			PluginInstallResult result = await installer.InstallAsync(
				url, wrongSha, null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("checksum mismatch", result.Error);
			Assert.Empty(manager.LoadedPlugins);
			// Nothing but the package file itself was written under the root.
			Assert.Empty(Directory.GetDirectories(root));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_RejectsManifestIdOrVersionMismatch()
	{
		string root = PluginTestPackages.NewPluginsRoot("mismatch");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);

			PluginInstallResult wrongId = await installer.InstallAsync(
				url, sha, "vapor.other", null, manager, CancellationToken.None);
			Assert.False(wrongId.Success);
			Assert.Contains("does not match the requested plugin id", wrongId.Error);

			PluginInstallResult wrongVersion = await installer.InstallAsync(
				url, sha, null, "9.9.9", manager, CancellationToken.None);
			Assert.False(wrongVersion.Success);
			Assert.Contains("does not match the requested version", wrongVersion.Error);

			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	/// <summary>Stages a package with a custom manifest next to the plugins root.</summary>
	internal static (string Url, string Sha256) StagePackage(string root, string manifestJson)
	{
		string packagePath = Path.Combine(root, $"pkg-{Guid.NewGuid():N}.zip");
		File.WriteAllBytes(packagePath, PluginTestPackages.Build(manifestJson));
		return (new Uri(packagePath).AbsoluteUri, PluginTestPackages.Sha256Hex(File.ReadAllBytes(packagePath)));
	}

	internal static string ManifestJson(string? trust, string apiVersion, string[]? permissions)
	{
		var fields = new Dictionary<string, object?>
		{
			["id"] = PluginTestPackages.PluginId,
			["name"] = "Vapor Test Plugin",
			["version"] = PluginTestPackages.PluginVersion,
			["apiVersion"] = apiVersion,
			["entryAssembly"] = "Vapor.Plugins.TestPlugin.dll",
			["entryType"] = "Vapor.Plugins.TestPlugin.TestPlugin"
		};
		if (trust is { } trustValue)
		{
			fields["trust"] = trustValue;
		}

		if (permissions is { } permissionList)
		{
			fields["permissions"] = permissionList;
		}

		return JsonSerializer.Serialize(fields);
	}

	[Fact]
	public async Task InstallAsync_TrustDivergenceFromCatalogFailsAndNamesBothSides()
	{
		string root = PluginTestPackages.NewPluginsRoot("trust-divergence");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			// Manifest without a trust claim vs a catalog that declares one.
			(string bareUrl, string bareSha) = StagePackage(root, ManifestJson(null, "1.0", null));
			PluginInstallResult undeclared = await installer.InstallAsync(
				bareUrl, bareSha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedTrust: "official");
			Assert.False(undeclared.Success);
			Assert.Contains("does not match the catalog entry", undeclared.Error);
			Assert.Contains("trust '<none>' != catalog 'official'", undeclared.Error);

			// Manifest declares a different trust than the catalog.
			(string url, string sha) = StagePackage(root, ManifestJson("official", "1.0", null));
			PluginInstallResult divergent = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedTrust: "community");
			Assert.False(divergent.Success);
			Assert.Contains("trust 'official' != catalog 'community'", divergent.Error);
			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_ApiVersionDivergenceFromCatalogFails()
	{
		string root = PluginTestPackages.NewPluginsRoot("apiversion-divergence");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", null));

			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedApiVersion: "2.0");

			Assert.False(result.Success);
			Assert.Contains("apiVersion '1.0' != catalog '2.0'", result.Error);
			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_PermissionSetDivergenceFromCatalogFailsAndNamesBothSides()
	{
		string root = PluginTestPackages.NewPluginsRoot("permissions-divergence");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			// Manifest grants a different set than the catalog declares.
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", new[] { "actions", "web" }));
			PluginInstallResult divergent = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedPermissions: new[] { "actions" });
			Assert.False(divergent.Success);
			Assert.Contains("permissions [actions, web] != catalog [actions]", divergent.Error);

			// Manifest declares no permissions at all vs a catalog that declares some.
			(string bareUrl, string bareSha) = StagePackage(root, ManifestJson(null, "1.0", null));
			PluginInstallResult undeclared = await installer.InstallAsync(
				bareUrl, bareSha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedPermissions: new[] { "actions" });
			Assert.False(undeclared.Success);
			Assert.Contains("permissions [] != catalog [actions]", undeclared.Error);
			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_MatchingCatalogClaimsInstallAndHotLoad()
	{
		string root = PluginTestPackages.NewPluginsRoot("catalog-match");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = StagePackage(root, ManifestJson("community", "1.0", new[] { "actions", "web" }));

			// Comparison is case-insensitive on trust/permissions and order-insensitive
			// on the permission set; apiVersion compares exactly.
			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedTrust: "Community",
				expectedPermissions: new[] { "Web", "Actions" },
				expectedApiVersion: "1.0");

			Assert.True(result.Success, result.Error);
			Assert.Single(manager.LoadedPlugins);
			Assert.Contains("plugin_echo", result.Actions);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_AllCatalogDivergencesAggregateIntoOneError()
	{
		string root = PluginTestPackages.NewPluginsRoot("catalog-aggregate");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = StagePackage(root, ManifestJson("official", "1.0", new[] { "actions" }));

			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedTrust: "community",
				expectedPermissions: new[] { "web" },
				expectedApiVersion: "2.0");

			Assert.False(result.Success);
			Assert.NotNull(result.Error);
			Assert.Contains("does not match the catalog entry", result.Error);
			Assert.Contains("trust 'official' != catalog 'community'", result.Error);
			Assert.Contains("apiVersion '1.0' != catalog '2.0'", result.Error);
			Assert.Contains("permissions [actions] != catalog [web]", result.Error);
			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_InstallsHotLoadsAndThenReplaces()
	{
		string root = PluginTestPackages.NewPluginsRoot("install");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);

			PluginInstallResult first = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, PluginTestPackages.PluginVersion, manager, CancellationToken.None);
			Assert.True(first.Success, first.Error);
			Assert.False(first.Replaced);
			Assert.Equal(PluginTestPackages.PluginId, first.PluginId);
			Assert.Contains("plugin_echo", first.Actions);
			Assert.Single(manager.LoadedPlugins);
			Assert.True(Directory.Exists(Path.Combine(root, PluginTestPackages.PluginId)));

			PluginInstallResult second = await installer.InstallAsync(
				url, sha, null, null, manager, CancellationToken.None);
			Assert.True(second.Success, second.Error);
			Assert.True(second.Replaced);
			Assert.Single(manager.LoadedPlugins);
			if (!OperatingSystem.IsWindows())
			{
				// Windows keeps the retired DLL mapped until GC, so the installer's
				// best-effort delete can legitimately leave an ".old-" directory behind.
				Assert.DoesNotContain(Directory.GetDirectories(root), d => d.Contains(".old-", StringComparison.Ordinal));
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_DownloadFailureFailsCleanly()
	{
		string root = PluginTestPackages.NewPluginsRoot("download-fail");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			PluginInstallResult result = await installer.InstallAsync(
				"file:///nonexistent/path/package.zip",
				PluginTestPackages.Sha256Hex(new byte[] { 1 }),
				null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("download failed", result.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public void PluginsRoot_ReportsTheConfiguredRoot()
	{
		string root = PluginTestPackages.NewPluginsRoot("root-prop");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			Assert.Equal(root, installer.PluginsRoot);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_CanceledDownloadRethrowsInsteadOfFailing()
	{
		string root = PluginTestPackages.NewPluginsRoot("cancel");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
				"file:///nonexistent/path/package.zip",
				PluginTestPackages.Sha256Hex(new byte[] { 1 }),
				null, null, manager, new CancellationToken(canceled: true)));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_RejectsFullLengthNonHexDigest()
	{
		string root = PluginTestPackages.NewPluginsRoot("nonhex");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			PluginInstallResult result = await installer.InstallAsync(
				"file:///nonexistent/path/package.zip", new string('z', 64), null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("sha256", result.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_FailsWhenPackageRootHasNoManifest()
	{
		string root = PluginTestPackages.NewPluginsRoot("no-root-manifest");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			byte[] package;
			using (var buffer = new MemoryStream())
			{
				using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
				{
					var entry = zip.CreateEntry("nested/plugin.json");
					using var writer = new StreamWriter(entry.Open());
					writer.Write(PluginTestPackages.ManifestJson);
				}

				package = buffer.ToArray();
			}

			PluginInstallResult result = await installer.InstallAsync(
				StageBuffer(root, package), PluginTestPackages.Sha256Hex(package), null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("plugin.json", result.Error);
			// The failed install left no plugin directory behind.
			Assert.Empty(Directory.GetDirectories(root));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_FailsOnInvalidManifestJson()
	{
		string root = PluginTestPackages.NewPluginsRoot("bad-manifest");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			byte[] package;
			using (var buffer = new MemoryStream())
			{
				using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
				{
					var entry = zip.CreateEntry("plugin.json");
					using var writer = new StreamWriter(entry.Open());
					writer.Write("{ not json at all");
				}

				package = buffer.ToArray();
			}

			PluginInstallResult result = await installer.InstallAsync(
				StageBuffer(root, package), PluginTestPackages.Sha256Hex(package), null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("manifest is invalid", result.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_FailsWhenEntryAssemblyIsMissingFromPackage()
	{
		string root = PluginTestPackages.NewPluginsRoot("missing-assembly");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			byte[] package;
			using (var buffer = new MemoryStream())
			{
				using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
				{
					var entry = zip.CreateEntry("plugin.json");
					using var writer = new StreamWriter(entry.Open());
					writer.Write(PluginTestPackages.ManifestJson);
					// No Vapor.Plugins.TestPlugin.dll in the package.
				}

				package = buffer.ToArray();
			}

			PluginInstallResult result = await installer.InstallAsync(
				StageBuffer(root, package), PluginTestPackages.Sha256Hex(package), null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("install failed", result.Error);
			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_CreatesExplicitDirectoryEntriesFromZip()
	{
		string root = PluginTestPackages.NewPluginsRoot("dir-entries");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			byte[] package;
			using (var buffer = new MemoryStream())
			{
				using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
				{
					zip.CreateEntry("deps/"); // explicit directory entry
					var manifest = zip.CreateEntry("plugin.json");
					using (var writer = new StreamWriter(manifest.Open()))
					{
						writer.Write(PluginTestPackages.ManifestJson);
					}

					var assemblyEntry = zip.CreateEntry("Vapor.Plugins.TestPlugin.dll");
					using (var assemblyStream = assemblyEntry.Open())
					{
						assemblyStream.Write(File.ReadAllBytes(PluginTestPackages.AssemblyPath));
					}

					var depEntry = zip.CreateEntry("deps/readme.txt");
					using (var writer = new StreamWriter(depEntry.Open()))
					{
						writer.Write("dep payload");
					}
				}

				package = buffer.ToArray();
			}

			PluginInstallResult result = await installer.InstallAsync(
				StageBuffer(root, package), PluginTestPackages.Sha256Hex(package), null, null, manager, CancellationToken.None);

			Assert.True(result.Success, result.Error);
			Assert.True(Directory.Exists(Path.Combine(root, PluginTestPackages.PluginId, "deps")));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_DownloadsAndInstallsOverHttp()
	{
		string root = PluginTestPackages.NewPluginsRoot("http");
		try
		{
			var installer = new PluginPackageInstaller(
				root, () => new HttpClient(new FakePackageHandler(PluginTestPackages.Build())), NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			byte[] package = PluginTestPackages.Build();
			PluginInstallResult result = await installer.InstallAsync(
				"http://plugins.example/test-plugin.zip", PluginTestPackages.Sha256Hex(package),
				null, null, manager, CancellationToken.None);

			Assert.True(result.Success, result.Error);
			Assert.Single(manager.LoadedPlugins);
			Assert.True(Directory.Exists(Path.Combine(root, PluginTestPackages.PluginId)));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_RejectsDeclaredLengthOverTheSizeCap()
	{
		string root = PluginTestPackages.NewPluginsRoot("size-cap");
		try
		{
			var installer = new PluginPackageInstaller(
				root, () => new HttpClient(new FakePackageHandler(new byte[] { 1, 2, 3 }, declaredLength: PluginPackageInstaller.DefaultMaxPackageBytes + 1)),
				NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			PluginInstallResult result = await installer.InstallAsync(
				"http://plugins.example/huge.zip", PluginTestPackages.Sha256Hex(new byte[] { 1, 2, 3 }),
				null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("over the", result.Error);
			Assert.Contains("limit", result.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_DefaultHttpClientFactoryFailsCleanlyOnUnreachableHost()
	{
		string root = PluginTestPackages.NewPluginsRoot("default-factory");
		try
		{
			// No factory injected: exercises the real default client construction.
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			PluginInstallResult result = await installer.InstallAsync(
				"http://127.0.0.1:9/package.zip", PluginTestPackages.Sha256Hex(new byte[] { 1 }),
				null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("download failed", result.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_StreamingBodyOverTheShrunkCapFails()
	{
		string root = PluginTestPackages.NewPluginsRoot("stream-cap");
		try
		{
			// The declared length matches the shrunken cap, so only the streamed
			// byte count can catch the oversized package.
			var installer = new PluginPackageInstaller(
				root, () => new HttpClient(new FakePackageHandler(new byte[] { 1, 2, 3, 4 }, declaredLength: 2)), NullLogger.Instance)
			{
				MaxPackageBytes = 2
			};
			await using var manager = PluginTestPackages.CreateManager();

			PluginInstallResult result = await installer.InstallAsync(
				"http://plugins.example/small.zip", PluginTestPackages.Sha256Hex(new byte[] { 1, 2, 3, 4 }),
				null, null, manager, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("exceeds", result.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public void RetireDirectory_MoveFailureIsLeftBehind()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // The read-only-root trick uses Unix file modes.
		}

		string root = PluginTestPackages.NewPluginsRoot("retire-move");
		try
		{
			string target = Path.Combine(root, "vapor.stuck");
			Directory.CreateDirectory(target);
			File.WriteAllText(Path.Combine(target, "plugin.json"), "{}");

			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			// A read-only plugins root makes the rename-aside fail: Move needs a
			// writable parent, not a writable directory.
			File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				installer.RetireDirectory(target);

				Assert.True(Directory.Exists(target), "failed move must leave the plugin directory in place");
				Assert.Empty(Directory.EnumerateFileSystemEntries(root, "*.old-*"));
			}
			finally
			{
				File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public void RetireDirectory_DeleteFailureIsDeferred()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // The deferred-deletion trick uses Unix file modes.
		}

		string root = PluginTestPackages.NewPluginsRoot("retire-delete");
		try
		{
			string target = Path.Combine(root, "vapor.locked");
			Directory.CreateDirectory(target);
			File.WriteAllText(Path.Combine(target, "locked.dll"), "payload");

			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			// A read-only plugin directory renames fine (rename only needs a
			// writable parent) but cannot be deleted recursively: unlinking its
			// files needs the directory's write bit.
			File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				installer.RetireDirectory(target);

				Assert.False(Directory.Exists(target), "the directory itself must still move aside");
				string[] leftovers = Directory.GetDirectories(root, "*.old-*");
				Assert.Single(leftovers);
				Assert.True(File.Exists(Path.Combine(leftovers[0], "locked.dll")), "deletion of the files is deferred");
			}
			finally
			{
				// Restore so the best-effort cleanup below can actually run.
				foreach (string leftover in Directory.GetDirectories(root, "*.old-*"))
				{
					File.SetUnixFileMode(leftover, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	/// <summary>Writes a raw package next to the plugins root and returns its file:// url.</summary>
	private static string StageBuffer(string directory, byte[] package)
	{
		string packagePath = Path.Combine(directory, $"pkg-{Guid.NewGuid():N}.zip");
		File.WriteAllBytes(packagePath, package);
		return new Uri(packagePath).AbsoluteUri;
	}

	private sealed class FakePackageHandler(byte[] body, long? declaredLength = null) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(body)
			};
			if (declaredLength is { } declared)
			{
				response.Content.Headers.ContentLength = declared;
			}

			return Task.FromResult(response);
		}
	}
}

public sealed class PluginInstallActionTests
{
	[Fact]
	public void Metadata_DescribesInstallWithoutLogin()
	{
		string root = PluginTestPackages.NewPluginsRoot("install-meta");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			var action = new PluginInstallAction(installer, null, NullLogger.Instance);

			Assert.Equal("plugin_install", action.Name);
			Assert.Equal("plugin_install", action.Metadata.Name);
			Assert.False(action.Metadata.RequiresLogin);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_RequiresUrlAndChecksum()
	{
		string root = PluginTestPackages.NewPluginsRoot("install-require");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginInstallAction(installer, manager, NullLogger.Instance);

			ActionResult noUrl = await action.ExecuteAsync(
				new Dictionary<string, object?> { ["sha256"] = new string('a', 64) }, CancellationToken.None);
			Assert.False(noUrl.Success);
			Assert.Contains("'url'", noUrl.Error);

			ActionResult noSha = await action.ExecuteAsync(
				new Dictionary<string, object?> { ["url"] = "file:///pkg.zip" }, CancellationToken.None);
			Assert.False(noSha.Success);
			Assert.Contains("'sha256'", noSha.Error);

			// sha_256 alias is accepted.
			ActionResult aliasSha = await action.ExecuteAsync(new Dictionary<string, object?>
			{
				["url"] = "file:///nonexistent/pkg.zip",
				["sha_256"] = new string('a', 64)
			}, CancellationToken.None);
			Assert.False(aliasSha.Success);
			Assert.Contains("download failed", aliasSha.Error);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_FailsWhenPluginHostIsNotInitialized()
	{
		var installer = new PluginPackageInstaller("/tmp/plugins", null, NullLogger.Instance);
		var action = new PluginInstallAction(installer, null, NullLogger.Instance);

		ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>
		{
			["url"] = "file:///pkg.zip",
			["sha256"] = new string('a', 64)
		}, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not initialized", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_InstallsPackageAndReportsFullInventory()
	{
		string root = PluginTestPackages.NewPluginsRoot("install-action");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginInstallAction(installer, manager, NullLogger.Instance);
			(string url, string sha) = PluginTestPackages.StageAsFile(root);

			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>
			{
				["url"] = url,
				["sha256"] = sha,
				["pluginId"] = PluginTestPackages.PluginId,
				["version"] = PluginTestPackages.PluginVersion
			}, CancellationToken.None);

			Assert.True(result.Success, result.Error);
			Assert.Equal(PluginTestPackages.PluginId, result.Output!["pluginId"]);
			Assert.Equal(false, result.Output["replaced"]);
			Assert.NotNull(result.Output["actions"]);
			var plugins = Assert.IsType<List<object>>(result.Output["plugins"]);
			Assert.Single(plugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_FailedInstallStillReportsInventory()
	{
		string root = PluginTestPackages.NewPluginsRoot("install-action-fail");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginInstallAction(installer, manager, NullLogger.Instance);
			(string url, string sha) = PluginTestPackages.StageAsFile(root);

			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>
			{
				["url"] = url,
				["sha256"] = PluginTestPackages.Sha256Hex(new byte[] { 7 })
			}, CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("checksum mismatch", result.Error);
			// The mirror list rides along even on failure, so the ControlPlane
			// inventory stays truthful.
			Assert.NotNull(result.Output!["plugins"]);
			Assert.Equal(url, result.Output["url"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
	[Fact]
	public async Task ExecuteAsync_CarriesCatalogExpectationsIntoTheInstaller()
	{
		string root = PluginTestPackages.NewPluginsRoot("install-expectations");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginInstallAction(installer, manager, NullLogger.Instance);
			(string url, string sha) = PluginPackageInstallerTests.StagePackage(
				root, PluginPackageInstallerTests.ManifestJson("official", "1.0", new[] { "actions" }));

			// JSON round-trip shape: the expectations arrive as JsonElements, exactly as
			// they do when a task payload comes back from a SQLite task record.
			string divergentJson = JsonSerializer.Serialize(new
			{
				url,
				sha256 = sha,
				pluginId = PluginTestPackages.PluginId,
				expectedTrust = "community",
				expectedPermissions = new[] { "web" },
				expectedApiVersion = "2.0"
			});
			var divergent = JsonSerializer.Deserialize<Dictionary<string, object?>>(divergentJson)!;
			ActionResult failed = await action.ExecuteAsync(divergent, CancellationToken.None);
			Assert.False(failed.Success);
			Assert.Contains("does not match the catalog entry", failed.Error);
			Assert.Contains("trust 'official' != catalog 'community'", failed.Error);

			(string matchUrl, string matchSha) = PluginPackageInstallerTests.StagePackage(
				root, PluginPackageInstallerTests.ManifestJson("official", "1.0", new[] { "actions" }));
			string matchingJson = JsonSerializer.Serialize(new
			{
				url = matchUrl,
				sha256 = matchSha,
				pluginId = PluginTestPackages.PluginId,
				expectedTrust = "official",
				expectedPermissions = new[] { "actions" },
				expectedApiVersion = "1.0"
			});
			var matching = JsonSerializer.Deserialize<Dictionary<string, object?>>(matchingJson)!;
			ActionResult installed = await action.ExecuteAsync(matching, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);
			Assert.Single(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}

public sealed class PluginUninstallActionTests
{
	[Fact]
	public void Metadata_DescribesUninstall()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		Assert.Equal("plugin_uninstall", action.Name);
		Assert.Equal("plugin_uninstall", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresPluginId()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("pluginId", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_FailsWhenPluginHostIsNotInitialized()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["pluginId"] = "vapor.absent" }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not initialized", result.Error);
	}

	[Fact]
	public void RetirePluginDirectory_IgnoresMissingDirectory()
	{
		var action = new PluginUninstallAction("/tmp/plugins", null, NullLogger.Instance);

		// Must not throw for a plugin that was never on disk.
		action.RetirePluginDirectory("vapor.never-installed");
	}

	[Fact]
	public async Task ExecuteAsync_UninstallSurvivesReadOnlyPluginsRoot()
	{
		if (!OperatingSystem.IsLinux())
		{
			return; // The read-only-root trick uses Unix file modes.
		}

		string root = PluginTestPackages.NewPluginsRoot("uninstall-readonly");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			string pluginDir = Path.Combine(root, PluginTestPackages.PluginId);
			// Make the plugins root read-only: the directory rename-aside fails and
			// the uninstall must still succeed (the unload happened; deletion of the
			// orphaned directory is deferred to the next restart/discovery pass).
			File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				var action = new PluginUninstallAction(root, manager, NullLogger.Instance);
				ActionResult result = await action.ExecuteAsync(
					new Dictionary<string, object?> { ["pluginId"] = PluginTestPackages.PluginId }, CancellationToken.None);

				Assert.True(result.Success, result.Error);
				Assert.Equal(true, result.Output!["removed"]);
				Assert.Empty(manager.LoadedPlugins);
				Assert.True(Directory.Exists(pluginDir), "read-only root keeps the directory in place");
			}
			finally
			{
				File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_UninstallingUnknownPluginIsIdempotentSuccess()
	{
		string root = PluginTestPackages.NewPluginsRoot("uninstall-idempotent");
		try
		{
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginUninstallAction(root, manager, NullLogger.Instance);

			ActionResult result = await action.ExecuteAsync(
				new Dictionary<string, object?> { ["pluginId"] = "vapor.absent" }, CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(false, result.Output!["removed"]);
			Assert.NotNull(result.Output["plugins"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_UnloadsAndRetiresPluginDirectory()
	{
		string root = PluginTestPackages.NewPluginsRoot("uninstall");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			var action = new PluginUninstallAction(root, manager, NullLogger.Instance);
			ActionResult result = await action.ExecuteAsync(
				new Dictionary<string, object?> { ["pluginId"] = PluginTestPackages.PluginId }, CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(true, result.Output!["removed"]);
			Assert.Empty(manager.LoadedPlugins);
			Assert.False(Directory.Exists(Path.Combine(root, PluginTestPackages.PluginId)));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_DeleteFailureKeepsTheRetiredDirectory()
	{
		if (OperatingSystem.IsWindows())
		{
			return; // The deferred-deletion trick uses Unix file modes.
		}

		string root = PluginTestPackages.NewPluginsRoot("uninstall-defer");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			string pluginDir = Path.Combine(root, PluginTestPackages.PluginId);
			// A read-only plugin directory renames aside but cannot be deleted
			// recursively; the unload must still succeed and the leftover must
			// stay out of discovery's sight.
			File.SetUnixFileMode(pluginDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
			try
			{
				var action = new PluginUninstallAction(root, manager, NullLogger.Instance);
				ActionResult result = await action.ExecuteAsync(
					new Dictionary<string, object?> { ["pluginId"] = PluginTestPackages.PluginId }, CancellationToken.None);

				Assert.True(result.Success, result.Error);
				Assert.Equal(true, result.Output!["removed"]);
				Assert.Empty(manager.LoadedPlugins);
				Assert.False(Directory.Exists(pluginDir), "the directory itself must move aside");
				Assert.Single(Directory.GetDirectories(root, "*.old-*"));
			}
			finally
			{
				foreach (string leftover in Directory.GetDirectories(root, "*.old-*"))
				{
					File.SetUnixFileMode(leftover, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}

public sealed class PluginListActionTests
{
	[Fact]
	public void Metadata_DescribesList()
	{
		var action = new PluginListAction("/tmp/plugins", null);

		Assert.Equal("plugin_list", action.Name);
		Assert.Equal("plugin_list", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_ReportsEmptyInventoryAndDirectory()
	{
		string root = PluginTestPackages.NewPluginsRoot("list-empty");
		try
		{
			var action = new PluginListAction(root, null);

			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(root, result.Output!["directory"]);
			Assert.Equal(0, result.Output["count"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_ListsLoadedPluginsWithMetadata()
	{
		string root = PluginTestPackages.NewPluginsRoot("list-loaded");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installed = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installed.Success, installed.Error);

			var action = new PluginListAction(root, manager);
			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(1, result.Output!["count"]);
			var plugins = Assert.IsType<List<object>>(result.Output["plugins"]);
			var entry = Assert.IsType<Dictionary<string, object?>>(plugins.Single());
			Assert.Equal(PluginTestPackages.PluginId, entry["id"]);
			Assert.Equal(PluginTestPackages.PluginVersion, entry["version"]);
			Assert.Equal("unknown", entry["trust"]);
			Assert.NotNull(entry["actions"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}

public sealed class PluginUpdateCheckActionTests
{
	[Theory]
	[InlineData(null, "2.0.0", "notInstalled")]
	[InlineData("", "2.0.0", "notInstalled")]
	[InlineData("1.0.0", "2.1.0", "updateAvailable")]
	[InlineData("2.0.0", "2.0.0", "upToDate")]
	[InlineData("3.0.0", "2.0.0", "upToDate")]
	[InlineData("beta", "2.0.0", "notComparable")]
	[InlineData("1.0.0", "v2", "notComparable")]
	[InlineData("beta", "v2", "notComparable")]
	public void Classify_VersionsMapToTheFourVerdicts(string? installed, string catalog, string expected)
	{
		Assert.Equal(expected, PluginUpdateCheckAction.Classify(installed, catalog));
	}

	[Fact]
	public void Metadata_DescribesReadOnlyCheck()
	{
		var action = new PluginUpdateCheckAction(null);

		Assert.Equal("plugin_update_check", action.Name);
		Assert.Equal("plugin_update_check", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(15, action.Metadata.TimeoutSeconds);
		Assert.Equal(ActionSafety.ReadOnly, action.Metadata.Safety);
	}

	[Fact]
	public async Task ExecuteAsync_FailsWhenPluginHostIsNotInitialized()
	{
		var action = new PluginUpdateCheckAction(null);

		ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not initialized", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_ClassifiesRoundTrippedCandidatesAgainstLoadedPlugins()
	{
		string root = PluginTestPackages.NewPluginsRoot("update-check-wire");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installedResult = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installedResult.Success, installedResult.Error);

			var action = new PluginUpdateCheckAction(manager);
			// JSON round-trip shape: candidates arrive as JsonElements, exactly as they
			// do when a task payload comes back from a SQLite task record. A non-object
			// element must be skipped without failing the whole check.
			var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(new
			{
				candidates = new object[]
				{
					new { id = "vapor.test-plugin", version = "2.0.0" },
					"junk",
					new { version = "1.0.0" },
					new { id = 42 },
					new { id = "vapor.nokey" },
					new { id = "vapor.x", version = 5 },
					new { id = "vapor.absent", version = "1.5.0" }
				}
			}))!;

			ActionResult result = await action.ExecuteAsync(payload, CancellationToken.None);

			Assert.True(result.Success);
			// Non-object entries and candidates without a usable id are skipped; a
			// candidate whose version key is missing or non-string grades as empty.
			Assert.Equal(4, result.Output!["count"]);
			var updates = Assert.IsType<List<object>>(result.Output["updates"]);
			var absent = Assert.IsType<Dictionary<string, object?>>(updates[0]);
			Assert.Equal("vapor.absent", absent["id"]);
			Assert.Equal("1.5.0", absent["catalogVersion"]);
			Assert.Null(absent["installedVersion"]);
			Assert.Equal("notInstalled", absent["status"]);
			var missingKey = Assert.IsType<Dictionary<string, object?>>(updates[1]);
			Assert.Equal("vapor.nokey", missingKey["id"]);
			Assert.Equal(string.Empty, missingKey["catalogVersion"]);
			Assert.Equal("notInstalled", missingKey["status"]);
			var loaded = Assert.IsType<Dictionary<string, object?>>(updates[2]);
			Assert.Equal("vapor.test-plugin", loaded["id"]);
			Assert.Equal(PluginTestPackages.PluginVersion, loaded["installedVersion"]);
			Assert.Equal("updateAvailable", loaded["status"]);
			var wrongKind = Assert.IsType<Dictionary<string, object?>>(updates[3]);
			Assert.Equal("vapor.x", wrongKind["id"]);
			Assert.Equal(string.Empty, wrongKind["catalogVersion"]);
			Assert.Equal("notInstalled", wrongKind["status"]);
			var plugins = Assert.IsType<List<object>>(result.Output["plugins"]);
			Assert.Single(plugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task ExecuteAsync_ReadsInProcessCandidatesAndSkipsMalformedEntries()
	{
		await using var manager = PluginTestPackages.CreateManager();
		var action = new PluginUpdateCheckAction(manager);
		var payload = new Dictionary<string, object?>
		{
			["candidates"] = new List<object>
			{
				new Dictionary<string, object?> { ["id"] = "vapor.a", ["version"] = "1.0.0" },
				"junk",
				new Dictionary<string, object?> { ["version"] = "1.0.0" },
				new Dictionary<string, object?> { ["id"] = "vapor.b", ["version"] = "2.0.0" },
				new Dictionary<string, object?> { ["id"] = "vapor.nover" }
			}
		};

		ActionResult result = await action.ExecuteAsync(payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(3, result.Output!["count"]);
		var updates = Assert.IsType<List<object>>(result.Output["updates"]);
		Assert.All(updates, entry => Assert.Equal("notInstalled", Assert.IsType<Dictionary<string, object?>>(entry)["status"]));
	}

	[Fact]
	public async Task ExecuteAsync_UnrecognizedCandidateShapeYieldsEmptyUpdates()
	{
		await using var manager = PluginTestPackages.CreateManager();
		var action = new PluginUpdateCheckAction(manager);

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["candidates"] = 42 }, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(0, result.Output!["count"]);
		Assert.Empty(Assert.IsType<List<object>>(result.Output["updates"]));
	}

	[Fact]
	public async Task ExecuteAsync_MissingCandidatesStillMirrorsLoadedPlugins()
	{
		string root = PluginTestPackages.NewPluginsRoot("update-check-empty");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginTestPackages.StageAsFile(root);
			PluginInstallResult installedResult = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installedResult.Success, installedResult.Error);

			var action = new PluginUpdateCheckAction(manager);
			ActionResult result = await action.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal(0, result.Output!["count"]);
			Assert.Empty(Assert.IsType<List<object>>(result.Output["updates"]));
			Assert.Single(Assert.IsType<List<object>>(result.Output["plugins"]));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}
