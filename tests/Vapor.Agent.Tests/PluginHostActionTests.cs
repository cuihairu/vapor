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
			Directory.Delete(Directory.GetParent(staging)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(staging)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(staging)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Assert.DoesNotContain(Directory.GetDirectories(root), d => d.Contains(".old-", StringComparison.Ordinal));
		}
		finally
		{
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
		}
	}

	[Fact]
	public async Task InstallAsync_RejectsDeclaredLengthOverTheSizeCap()
	{
		string root = PluginTestPackages.NewPluginsRoot("size-cap");
		try
		{
			var installer = new PluginPackageInstaller(
				root, () => new HttpClient(new FakePackageHandler(new byte[] { 1, 2, 3 }, declaredLength: PluginPackageInstaller.MaxPackageBytes + 1)),
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
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
			Directory.Delete(Directory.GetParent(root)!.FullName, recursive: true);
		}
	}
}
