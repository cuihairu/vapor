using System.Text.Json;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class PluginInventoryTests
{
	[Fact]
	public void Entry_RecordMembers_BehaveValueLike()
	{
		var entry = new PluginInventoryEntry(
			"vapor.echo", "Echo", "1.0.0", "1.0", "official",
			new[] { "actions" }, new[] { "plugin_echo" }, new[] { "network" }, "vapor");

		PluginInventoryEntry copy = entry with { };
		Assert.True(entry.Equals(copy));
		Assert.Equal(entry.GetHashCode(), copy.GetHashCode());
		Assert.NotEqual(entry, entry with { Version = "2.0.0" });
		Assert.NotEqual(entry, entry with { Publisher = null });

		(string id, string name, string version, string apiVersion, string? trust, IReadOnlyList<string> permissions, IReadOnlyList<string> actions, IReadOnlyList<string> resources, string? publisher) = entry;
		Assert.Equal("vapor.echo", id);
		Assert.Equal("Echo", name);
		Assert.Equal("1.0.0", version);
		Assert.Equal("1.0", apiVersion);
		Assert.Equal("official", trust);
		Assert.Equal(new[] { "actions" }, permissions);
		Assert.Equal(new[] { "plugin_echo" }, actions);
		Assert.Equal(new[] { "network" }, resources);
		Assert.Equal("vapor", publisher);

		Assert.Contains("vapor.echo", entry.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Update_EntryWithoutIdentityKeys_FallsBackToEmptyStrings()
	{
		var inventory = new PluginInventory();

		inventory.Update("agent-a", DateTimeOffset.UnixEpoch, RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new object[] { new Dictionary<string, object?>() }
		}));

		IReadOnlyDictionary<string, AgentPlugins> snapshot = inventory.Snapshot();
		AgentPlugins agent = Assert.Single(snapshot.Values);
		PluginInventoryEntry entry = Assert.Single(agent.Plugins);
		Assert.Equal(string.Empty, entry.Id);
		Assert.Equal(string.Empty, entry.Name);
		Assert.Equal(string.Empty, entry.Version);
		Assert.Equal(string.Empty, entry.ApiVersion);
		Assert.Null(entry.Trust);
	}

	[Fact]
	public void Update_RebuildsMirrorFromRoundTrippedOutput()
	{
		var inventory = new PluginInventory();
		// Agent outputs travel over the WS as JSON: mirror the exact round-trip shape.
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object>
			{
				new Dictionary<string, object?>
				{
					["id"] = "vapor.b",
					["name"] = "B",
					["version"] = "2.0.0",
					["publisher"] = "vapor",
					["apiVersion"] = "1.0",
					["trust"] = "official",
					["permissions"] = new List<string> { "actions" },
					["resources"] = new List<object?> { "network", 42, "fileSystem" },
					["actions"] = new List<string> { "b_one" }
				},
				new Dictionary<string, object?>
				{
					["id"] = "vapor.a",
					["name"] = "A",
					["version"] = "1.0.0",
					["apiVersion"] = "1.0",
					["trust"] = "unknown",
					["permissions"] = Array.Empty<string>(),
					["actions"] = Array.Empty<string>()
				}
			}
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		var snapshot = inventory.Snapshot();
		Assert.Single(snapshot);
		AgentPlugins agent = snapshot["agent-1"];
		Assert.Equal(2, agent.Plugins.Count);
		Assert.Equal("vapor.a", agent.Plugins[0].Id);
		Assert.Equal("vapor.b", agent.Plugins[1].Id);
		Assert.Equal("official", agent.Plugins[1].Trust);
		Assert.Equal(new[] { "b_one" }, agent.Plugins[1].Actions);
		Assert.Equal(new[] { "network", "fileSystem" }, agent.Plugins[1].Resources);
		Assert.Equal("vapor", agent.Plugins[1].Publisher);
		// vapor.a declares no resources: the mirror records an empty list, and a
		// non-array value is tolerated the same way (third-party output).
		Assert.Empty(agent.Plugins[0].Resources);
		Assert.Null(agent.Plugins[0].Publisher);
	}

	[Fact]
	public void Update_ToleratesNonArrayResourcesValue()
	{
		var inventory = new PluginInventory();
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object>
			{
				new Dictionary<string, object?>
				{
					["id"] = "vapor.r",
					["name"] = "R",
					["version"] = "1.0.0",
					["apiVersion"] = "1.0",
					["resources"] = "bogus"
				}
			}
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		Assert.Empty(inventory.Snapshot()["agent-1"].Plugins.Single().Resources);
	}

	[Fact]
	public void Update_IgnoresOutputWithoutUsablePluginsArray()
	{
		var inventory = new PluginInventory();

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(new Dictionary<string, object?> { ["pluginId"] = "vapor.x" }));
		Assert.Empty(inventory.Snapshot());

		// A plugins key holding a non-array value is ignored just the same.
		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(new Dictionary<string, object?> { ["plugins"] = "bogus" }));
		Assert.Empty(inventory.Snapshot());

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, null);
		Assert.Empty(inventory.Snapshot());
	}

	[Fact]
	public void Update_SkipsNonObjectArrayEntries()
	{
		// Agents are third-party code: a malformed "plugins" entry (a scalar or an
		// array element instead of an object) must be skipped, not crash the mirror.
		var inventory = new PluginInventory();
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object?>
			{
				"vapor.bogus",
				new Dictionary<string, object?>
				{
					["id"] = "vapor.a",
					["name"] = "A",
					["version"] = "1.0.0",
					["apiVersion"] = "1.0",
					["trust"] = "official",
					["permissions"] = Array.Empty<string>(),
					["actions"] = Array.Empty<string>()
				},
				new List<object?> { "vapor.nested" },
				42
			}
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		AgentPlugins agent = inventory.Snapshot()["agent-1"];
		Assert.Equal("vapor.a", Assert.Single(agent.Plugins).Id);
	}

	[Fact]
	public void Update_ReplacesWholeListPerAgent()
	{
		var inventory = new PluginInventory();
		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(PluginsOutput("vapor.a", "vapor.b")));
		inventory.Update("agent-1", DateTimeOffset.UtcNow, RoundTrip(PluginsOutput("vapor.a")));

		AgentPlugins agent = inventory.Snapshot()["agent-1"];
		Assert.Single(agent.Plugins);
		Assert.Equal("vapor.a", agent.Plugins[0].Id);
	}

	[Fact]
	public void Remove_DropsAgentEntry()
	{
		var inventory = new PluginInventory();
		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, RoundTrip(PluginsOutput("vapor.a")));

		inventory.Remove("agent-1");

		Assert.Empty(inventory.Snapshot());
	}

	[Fact]
	public void Update_CapturesUpdateVerdictsFromRoundTrippedOutput()
	{
		var inventory = new PluginInventory();
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object>(),
			["updates"] = new List<object>
			{
				new Dictionary<string, object?>
				{
					["id"] = "vapor.b", ["catalogVersion"] = "2.0.0",
					["installedVersion"] = "1.0.0", ["status"] = "updateAvailable",
					["publisherChanged"] = true
				},
				"junk",
				new Dictionary<string, object?> { ["catalogVersion"] = "3.0.0", ["status"] = "notInstalled" },
				// No fields at all: the ?? fallbacks yield empty id/catalog/status and the entry drops out.
				new Dictionary<string, object?>(),
				new Dictionary<string, object?>
				{
					["id"] = "vapor.a", ["catalogVersion"] = "v2",
					["installedVersion"] = null,
					["status"] = "notComparable",
					["publisherChanged"] = "bogus"
				}
			}
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		AgentPlugins agent = inventory.Snapshot()["agent-1"];
		Assert.NotNull(agent.Updates);
		Assert.Equal(2, agent.Updates!.Count);
		Assert.Equal("vapor.a", agent.Updates[0].Id);
		Assert.Equal("v2", agent.Updates[0].CatalogVersion);
		Assert.Null(agent.Updates[0].InstalledVersion);
		Assert.Equal("notComparable", agent.Updates[0].Status);
		// Only the literal JSON true reads as a drift flag; anything else is unparsed.
		Assert.Null(agent.Updates[0].PublisherChanged);
		Assert.Equal("vapor.b", agent.Updates[1].Id);
		Assert.Equal("1.0.0", agent.Updates[1].InstalledVersion);
		Assert.Equal("updateAvailable", agent.Updates[1].Status);
		Assert.True(agent.Updates[1].PublisherChanged);
	}

	[Fact]
	public void Update_IgnoresNonArrayUpdates()
	{
		var inventory = new PluginInventory();
		var output = RoundTrip(new Dictionary<string, object?>
		{
			["plugins"] = new List<object>(),
			["updates"] = 42
		});

		inventory.Update("agent-1", DateTimeOffset.UnixEpoch, output);

		AgentPlugins agent = inventory.Snapshot()["agent-1"];
		Assert.Null(agent.Updates);
	}

	private static Dictionary<string, object?> PluginsOutput(params string[] ids) => new()
	{
		["plugins"] = ids.Select(id => (object)new Dictionary<string, object?>
		{
			["id"] = id,
			["name"] = id,
			["version"] = "1.0.0",
			["apiVersion"] = "1.0",
			["trust"] = null,
			["permissions"] = Array.Empty<string>(),
			["actions"] = Array.Empty<string>()
		}).ToList()
	};

	private static IReadOnlyDictionary<string, object?> RoundTrip(IReadOnlyDictionary<string, object?> output) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(output))!;

	/// <summary>Visible to other test classes seeding a PluginInventory mirror.</summary>
	internal static IReadOnlyDictionary<string, object?> RoundTripForSeed(IReadOnlyDictionary<string, object?> output) =>
		RoundTrip(output);
}
