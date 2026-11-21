using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

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
			var mirror = Assert.IsType<Dictionary<string, object?>>(plugins.Single());
			Assert.Empty((System.Collections.IEnumerable)mirror["resources"]!);
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
				expectedApiVersion = "2.0",
				expectedPublisher = "vapor"
			});
			var divergent = JsonSerializer.Deserialize<Dictionary<string, object?>>(divergentJson)!;
			ActionResult failed = await action.ExecuteAsync(divergent, CancellationToken.None);
			Assert.False(failed.Success);
			Assert.Contains("does not match the catalog entry", failed.Error);
			Assert.Contains("trust 'official' != catalog 'community'", failed.Error);
			Assert.Contains("publisher '<none>' != catalog 'vapor'", failed.Error);

			(string matchUrl, string matchSha) = PluginPackageInstallerTests.StagePackage(
				root, PluginPackageInstallerTests.ManifestJson("official", "1.0", new[] { "actions" }, "vapor"));
			string matchingJson = JsonSerializer.Serialize(new
			{
				url = matchUrl,
				sha256 = matchSha,
				pluginId = PluginTestPackages.PluginId,
				expectedTrust = "official",
				expectedPermissions = new[] { "actions" },
				expectedApiVersion = "1.0",
				expectedPublisher = "vapor"
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

	[Fact]
	public async Task ExecuteAsync_CarriesSignatureInstructionIntoTheInstaller()
	{
		string root = PluginTestPackages.NewPluginsRoot("install-signature");
		try
		{
			// No pinned keys on this installer, so a declared signature cannot
			// verify — which proves the action handed both halves over.
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			var action = new PluginInstallAction(installer, manager, NullLogger.Instance);
			using var signer = RSA.Create(2048);
			(string url, string sha) = PluginPackageInstallerTests.StagePackage(
				root, PluginPackageInstallerTests.ManifestJson("official", "1.0", new[] { "actions" }));

			var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(new
			{
				url,
				sha256 = sha,
				pluginId = PluginTestPackages.PluginId,
				expectedTrust = "official",
				expectedApiVersion = "1.0",
				expectedPublisher = "vapor",
				expectedSigningPublicKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),
				expectedSigningSignature = PluginSignatureVerifier.Sign(sha, signer)
			}))!;

			ActionResult failed = await action.ExecuteAsync(payload, CancellationToken.None);

			Assert.False(failed.Success);
			Assert.Contains("signature verification failed", failed.Error);
			Assert.Contains("pins no trusted publisher keys", failed.Error);
			// The mirror still comes back on the failure path like every other failure.
			Assert.NotNull(failed.Output!["plugins"]);
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
	}
}
