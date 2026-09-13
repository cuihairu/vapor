using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Tests for the plugin trust and permission model: manifest declaration parsing,
/// host trust gating and capability/permission enforcement.
/// </summary>
public sealed class PluginTrustTests : IDisposable
{
	private readonly string _root;

	public PluginTrustTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "vapor-plugin-trust-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
		{
			// Best-effort cleanup; the load context may still hold the assembly
			// (Windows raises UnauthorizedAccessException for directories with open files).
		}
	}

	// --- Manifest declaration parsing ---

	[Theory]
	[InlineData("official", PluginTrust.Official)]
	[InlineData("Official", PluginTrust.Official)]
	[InlineData(" COMMUNITY ", PluginTrust.Community)]
	[InlineData("unknown", PluginTrust.Unknown)]
	[InlineData(null, PluginTrust.Unknown)]
	public void Descriptor_TrustIsNormalized(string? declared, PluginTrust expected)
	{
		var manifest = PluginManifest.Parse(
			$$"""
			{
				"id": "p",
				"name": "P",
				"version": "1.0.0",
				"apiVersion": "1.0",
				"entryAssembly": "p.dll",
				"trust": {{(declared is null ? "null" : $"\"{declared}\"")}}
			}
			""");
		var descriptor = new PluginDescriptor(manifest, "/plugins/p", "/plugins/p/plugin.json", "/plugins/p/p.dll");

		Assert.Equal(expected, descriptor.Trust);
	}

	[Fact]
	public void Parse_InvalidTrust_Throws()
	{
		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(ParseManifest("\"trust\": \"verified\"")));

		Assert.Contains("trust", ex.Message);
		Assert.Contains("unknown, community, official", ex.Message);
	}

	[Fact]
	public void Parse_PermissionsAreNormalizedAndDeduplicated()
	{
		var manifest = PluginManifest.Parse(ParseManifest("\"permissions\": [\"Actions\", \"WEB\", \"actions\"]"));

		Assert.Equal(["actions", "web"], manifest.Permissions);
	}

	[Fact]
	public void Parse_UnknownPermission_Throws()
	{
		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(ParseManifest("\"permissions\": [\"actions\", \"filesystem\"]")));

		Assert.Contains("filesystem", ex.Message);
		Assert.Contains("actions, commands, web, events", ex.Message);
	}

	[Fact]
	public void Parse_MissingPermissions_RemainsNull()
	{
		var manifest = PluginManifest.Parse(ParseManifest(null));

		Assert.Null(manifest.Permissions);
		Assert.Null(manifest.Trust);
	}

	// --- Host trust gate ---

	[Fact]
	public async Task LoadAsync_TrustBelowMinimum_Throws()
	{
		PluginStaging.StageTestPlugin(_root, trust: "community");
		var manager = PluginStaging.CreateManager(Options());
		var descriptor = Assert.Single(manager.Discover(_root));

		var ex = await Assert.ThrowsAsync<PluginException>(
			() => manager.LoadAsync(descriptor));

		Assert.Contains("trust level 'Community' is below the host minimum 'Official'", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_UndeclaredTrust_RejectedByMinimumTrust()
	{
		PluginStaging.StageTestPlugin(_root);
		var manager = PluginStaging.CreateManager(Options());
		var descriptor = Assert.Single(manager.Discover(_root));

		var ex = await Assert.ThrowsAsync<PluginException>(
			() => manager.LoadAsync(descriptor));

		Assert.Contains("trust level 'Unknown' is below the host minimum 'Official'", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_TrustAtMinimum_Loads()
	{
		PluginStaging.StageTestPlugin(_root, trust: "official");
		var manager = PluginStaging.CreateManager(Options());

		var report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Failures);
	}

	[Fact]
	public async Task LoadAllAsync_UntrustedPlugin_FailureIsolated()
	{
		PluginStaging.StageTestPlugin(_root, pluginDirName: "untrusted");
		PluginStaging.StageTestPlugin(_root, pluginDirName: "trusted", trust: "official", pluginId: "vapor.trusted-plugin");
		var manager = PluginStaging.CreateManager(Options());

		var report = await manager.LoadAllAsync(_root);

		var plugin = Assert.Single(report.Loaded);
		// Note: LoadedPlugin.Info reflects the instance's self-declared identity (shared by
		// the staged test assembly), so assert on the manifest id instead.
		Assert.Equal("vapor.trusted-plugin", plugin.Descriptor.Manifest.Id);
		var failure = Assert.Single(report.Failures);
		Assert.Contains("trust level 'Unknown' is below the host minimum 'Official'", failure);
	}

	// --- Capability/permission enforcement ---

	[Fact]
	public async Task LoadAsync_AllPermissionsDeclared_AllCapabilitiesGranted()
	{
		PluginStaging.StageTestPlugin(_root);
		var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		var plugin = await manager.LoadAsync(descriptor);

		Assert.Equal(
			[PluginPermissions.Actions, PluginPermissions.Commands, PluginPermissions.Web],
			plugin.GrantedPermissions);
		Assert.Single(plugin.Actions);
		Assert.Single(plugin.Commands);
		Assert.Single(plugin.Routes);
	}

	[Fact]
	public async Task LoadAsync_UndeclaredCapabilities_StrippedWithMinimalTrust()
	{
		PluginStaging.StageTestPlugin(_root, permissions: []);
		var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		var plugin = await manager.LoadAsync(descriptor);

		Assert.Empty(plugin.GrantedPermissions);
		Assert.Empty(plugin.Actions);
		Assert.Empty(plugin.Commands);
		Assert.Empty(plugin.Routes);
	}

	[Fact]
	public async Task LoadAsync_PartialPermissions_OnlyDeclaredCapabilitiesGranted()
	{
		PluginStaging.StageTestPlugin(_root, permissions: [PluginPermissions.Actions]);
		var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		var plugin = await manager.LoadAsync(descriptor);

		Assert.Equal([PluginPermissions.Actions], plugin.GrantedPermissions);
		Assert.Single(plugin.Actions);
		Assert.Empty(plugin.Commands);
		Assert.Empty(plugin.Routes);
	}

	[Fact]
	public async Task LoadAsync_StrictPolicy_UndeclaredCapabilityFails()
	{
		PluginStaging.StageTestPlugin(_root, permissions: [PluginPermissions.Actions], trust: "official");
		var manager = PluginStaging.CreateManager(Options(strictPermissions: true));
		var descriptor = Assert.Single(manager.Discover(_root));

		var ex = await Assert.ThrowsAsync<PluginException>(
			() => manager.LoadAsync(descriptor));

		Assert.Contains("'commands' capabilities but does not declare the 'commands' permission", ex.Message);
	}

	[Fact]
	public async Task LoadAsync_StrictPolicy_AllDeclared_Loads()
	{
		PluginStaging.StageTestPlugin(_root);
		var manager = PluginStaging.CreateManager();
		var descriptor = Assert.Single(manager.Discover(_root));

		var plugin = await manager.LoadAsync(descriptor);

		Assert.Single(plugin.Actions);
		Assert.Single(plugin.Commands);
		Assert.Single(plugin.Routes);
	}

	/// <summary>Options with Official-only trust and optionally strict permission policy.</summary>
	private static PluginManagerOptions Options(bool strictPermissions = false) => new()
	{
		MinimumTrust = PluginTrust.Official,
		RequirePermissionsDeclared = strictPermissions
	};

	private static string ParseManifest(string? extraJson)
	{
		var extra = extraJson is null ? string.Empty : $",{extraJson}";
		return $$"""
			{
				"id": "p",
				"name": "P",
				"version": "1.0.0",
				"apiVersion": "1.0",
				"entryAssembly": "p.dll"{{extra}}
			}
			""";
	}
}
