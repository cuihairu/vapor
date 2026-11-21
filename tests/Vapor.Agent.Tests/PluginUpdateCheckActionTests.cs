using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Agent;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

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
	public async Task ExecuteAsync_FlagsPublisherDriftOnlyWhenBothSidesDeclareOne()
	{
		string root = PluginTestPackages.NewPluginsRoot("update-check-publisher");
		try
		{
			var installer = new PluginPackageInstaller(root, null, NullLogger.Instance);
			await using var manager = PluginTestPackages.CreateManager();
			(string url, string sha) = PluginPackageInstallerTests.StagePackage(
				root, PluginPackageInstallerTests.ManifestJson(null, "1.0", null, "vapor"));
			PluginInstallResult installedResult = await installer.InstallAsync(url, sha, null, null, manager, CancellationToken.None);
			Assert.True(installedResult.Success, installedResult.Error);

			var action = new PluginUpdateCheckAction(manager);
			// The installed id repeats across candidates on purpose: a verdict is
			// computed per candidate, and the drift comparison is candidate-scoped.
			var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(new
			{
				candidates = new object[]
				{
					new { id = "vapor.test-plugin", version = "2.0.0", publisher = "vapor" },
					new { id = "vapor.test-plugin", version = "2.0.0", publisher = "someone-else" },
					new { id = "vapor.test-plugin", version = "2.0.0" },
					new { id = "vapor.test-plugin", version = "2.0.0", publisher = 5 }
				}
			}))!;

			ActionResult result = await action.ExecuteAsync(payload, CancellationToken.None);

			Assert.True(result.Success);
			var updates = Assert.IsType<List<object>>(result.Output!["updates"]);
			Assert.Equal(4, updates.Count);
			var same = Assert.IsType<Dictionary<string, object?>>(updates[0]);
			Assert.Equal("updateAvailable", same["status"]);
			Assert.False(same.ContainsKey("publisherChanged")); // identical declared publisher stays silent
			var drifted = Assert.IsType<Dictionary<string, object?>>(updates[1]);
			Assert.Equal("updateAvailable", drifted["status"]);
			Assert.Equal(true, drifted["publisherChanged"]); // declared publisher would change on update
			var undeclared = Assert.IsType<Dictionary<string, object?>>(updates[2]);
			Assert.False(undeclared.ContainsKey("publisherChanged")); // candidate without a claim stays silent
			var nonString = Assert.IsType<Dictionary<string, object?>>(updates[3]);
			Assert.False(nonString.ContainsKey("publisherChanged")); // a non-string publisher is not a claim

			// The declared publisher mirrors into the plugins output like every other
			// manifest field, so the ControlPlane inventory can carry it.
			var plugins = Assert.IsType<List<object>>(result.Output["plugins"]);
			var loaded = Assert.IsType<Dictionary<string, object?>>(plugins.Single());
			Assert.Equal("vapor", loaded["publisher"]);

			// In-process candidate shape: the publisher key reads the same way.
			var inProcess = new Dictionary<string, object?>
			{
				["candidates"] = new List<object>
				{
					new Dictionary<string, object?> { ["id"] = "vapor.test-plugin", ["version"] = "2.0.0", ["publisher"] = "someone-else" },
					new Dictionary<string, object?> { ["id"] = "vapor.test-plugin4", ["version"] = "2.0.0" },
					new Dictionary<string, object?> { ["id"] = "vapor.test-plugin", ["version"] = "2.0.0", ["publisher"] = 42 }
				}
			};
			ActionResult second = await action.ExecuteAsync(inProcess, CancellationToken.None);
			Assert.True(second.Success);
			var secondUpdates = Assert.IsType<List<object>>(second.Output!["updates"]);
			var driftedInProcess = Assert.IsType<Dictionary<string, object?>>(secondUpdates[0]);
			Assert.Equal(true, driftedInProcess["publisherChanged"]);
			var notInstalled = Assert.IsType<Dictionary<string, object?>>(secondUpdates[2]);
			Assert.Equal("notInstalled", notInstalled["status"]);
			Assert.False(notInstalled.ContainsKey("publisherChanged"));
			var nonStringInProcess = Assert.IsType<Dictionary<string, object?>>(secondUpdates[1]);
			Assert.Equal("updateAvailable", nonStringInProcess["status"]);
			Assert.False(nonStringInProcess.ContainsKey("publisherChanged"));
		}
		finally
		{
			PluginTestPackages.DeleteBestEffort(Directory.GetParent(root)!.FullName);
		}
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
