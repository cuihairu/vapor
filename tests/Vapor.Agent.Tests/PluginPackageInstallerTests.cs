using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Xunit;

namespace Vapor.Agent.Tests;

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

	internal static string ManifestJson(string? trust, string apiVersion, string[]? permissions, string? publisher = null)
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
		if (publisher is { } publisherValue)
		{
			fields["publisher"] = publisherValue;
		}

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
	public async Task InstallAsync_PublisherDivergenceFromCatalogFailsAndNamesBothSides()
	{
		string root = PluginTestPackages.NewPluginsRoot("publisher-divergence");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();

			// Manifest without a publisher claim vs a catalog that declares one.
			(string bareUrl, string bareSha) = StagePackage(root, ManifestJson(null, "1.0", null));
			PluginInstallResult undeclared = await installer.InstallAsync(
				bareUrl, bareSha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedPublisher: "vapor");
			Assert.False(undeclared.Success);
			Assert.Contains("does not match the catalog entry", undeclared.Error);
			Assert.Contains("publisher '<none>' != catalog 'vapor'", undeclared.Error);

			// Manifest declares a different publisher than the catalog.
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", null, "someone-else"));
			PluginInstallResult divergent = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedPublisher: "vapor");
			Assert.False(divergent.Success);
			Assert.Contains("publisher 'someone-else' != catalog 'vapor'", divergent.Error);
			Assert.Empty(manager.LoadedPlugins);

			// The comparison itself is case-insensitive: matching claims install.
			(string matchUrl, string matchSha) = StagePackage(root, ManifestJson(null, "1.0", null, "vapor"));
			PluginInstallResult matching = await installer.InstallAsync(
				matchUrl, matchSha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedPublisher: "Vapor");
			Assert.True(matching.Success, matching.Error);
			Assert.Single(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_DeclaredSignatureFailsClosedWithoutPinnedKeys()
	{
		string root = PluginTestPackages.NewPluginsRoot("signature-nopins");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			using var signer = RSA.Create(2048);
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", null));
			var signature = PluginSignatureVerifier.Sign(sha, signer);

			// A signature declaration with no pinned keys is an origin claim the
			// agent cannot make: fail closed instead of installing unsigned.
			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedSigningPublicKey: Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),
				expectedSigningSignature: signature);

			Assert.False(result.Success);
			Assert.Contains("signature verification failed", result.Error);
			Assert.Contains("pins no trusted publisher keys", result.Error);
			Assert.Empty(manager.LoadedPlugins);
			// The signature gate sits before the extraction step: nothing unpacked.
			Assert.Empty(Directory.GetDirectories(root));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_DeclaredSignatureFromUnpinnedKeyFails()
	{
		string root = PluginTestPackages.NewPluginsRoot("signature-unpinned");
		try
		{
			using var pinned = RSA.Create(2048);
			using var stranger = RSA.Create(2048);
			var installer = new PluginPackageInstaller(
				root, null, NullLogger.Instance,
				[Convert.ToBase64String(pinned.ExportSubjectPublicKeyInfo())]);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", null));

			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedSigningPublicKey: Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo()),
				expectedSigningSignature: PluginSignatureVerifier.Sign(sha, stranger));

			Assert.False(result.Success);
			Assert.Contains("not among this agent's pinned", result.Error);
			Assert.Empty(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_ValidDeclaredSignatureInstalls()
	{
		string root = PluginTestPackages.NewPluginsRoot("signature-valid");
		try
		{
			using var signer = RSA.Create(2048);
			var pin = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance, [pin]);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", null));

			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedSigningPublicKey: pin,
				expectedSigningSignature: PluginSignatureVerifier.Sign(sha, signer));

			Assert.True(result.Success, result.Error);
			Assert.Single(manager.LoadedPlugins);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}

	[Fact]
	public async Task InstallAsync_PartialSigningInstructionIsSkipped()
	{
		string root = PluginTestPackages.NewPluginsRoot("signature-partial");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = StagePackage(root, ManifestJson(null, "1.0", null));

			// Half a trust instruction is not a trust instruction: only a complete
			// key+signature pair switches the gate on.
			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedSigningPublicKey: "definitely not a key");

			Assert.True(result.Success, result.Error);
			Assert.Single(manager.LoadedPlugins);
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
			(string url, string sha) = StagePackage(root, ManifestJson("official", "1.0", new[] { "actions" }, "someone-else"));

			PluginInstallResult result = await installer.InstallAsync(
				url, sha, PluginTestPackages.PluginId, null, manager, CancellationToken.None,
				expectedTrust: "community",
				expectedPermissions: new[] { "web" },
				expectedApiVersion: "2.0",
				expectedPublisher: "vapor");

			Assert.False(result.Success);
			Assert.NotNull(result.Error);
			Assert.Contains("does not match the catalog entry", result.Error);
			Assert.Contains("trust 'official' != catalog 'community'", result.Error);
			Assert.Contains("apiVersion '1.0' != catalog '2.0'", result.Error);
			Assert.Contains("permissions [actions] != catalog [web]", result.Error);
			Assert.Contains("publisher 'someone-else' != catalog 'vapor'", result.Error);
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
