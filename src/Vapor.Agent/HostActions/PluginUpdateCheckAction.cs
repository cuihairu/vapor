using System.Text.Json;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Plugins.Core;

namespace Vapor.Agent;

/// <summary>
/// Host action "plugin_update_check": compares the catalog candidates carried in
/// the payload ({id, version} pairs staged from the plugin index) against the
/// plugins actually loaded on this agent and reports a verdict per candidate.
/// Read-only by design — nothing is downloaded or replaced here; the operator
/// decides what (and where) to install with plugin_install.
/// </summary>
public sealed class PluginUpdateCheckAction : IHostAction
{
	private readonly PluginManager? _manager;

	public PluginUpdateCheckAction(PluginManager? manager)
	{
		_manager = manager;
	}

	public string Name => "plugin_update_check";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Compares catalog candidates against the loaded plugins and reports which have updates",
		RequiresLogin: false,
		TimeoutSeconds: 15
	)
	{ Safety = ActionSafety.ReadOnly };

	public Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		if (_manager is null)
		{
			return Task.FromResult(new ActionResult(false, "plugin host is not initialized on this agent", null));
		}

		// First loaded instance wins: the installer replaces by plugin id, so a
		// duplicated id across ALCs is a pre-existing anomaly, not a version race.
		var installed = _manager.LoadedPlugins
			.GroupBy(p => p.Descriptor.Manifest.Id, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First().Descriptor.Manifest.Version, StringComparer.OrdinalIgnoreCase);

		var updates = ReadCandidates(payload)
			.OrderBy(c => c.Id, StringComparer.Ordinal)
			.Select(c =>
			{
				string? current = installed.TryGetValue(c.Id, out var version) ? version : null;
				return (object)new Dictionary<string, object?>
				{
					["id"] = c.Id,
					["catalogVersion"] = c.Version,
					["installedVersion"] = current,
					["status"] = Classify(current, c.Version)
				};
			})
			.ToList();

		// "plugins" rides along like every plugin_* action so the ControlPlane
		// mirror stays wholesale-consistent on any plugin task outcome.
		return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
		{
			["count"] = updates.Count,
			["updates"] = updates,
			["plugins"] = PluginOutput.ListLoaded(_manager)
		}));
	}

	/// <summary>
	/// Version verdict: both sides parse as System.Version → updateAvailable when
	/// the catalog is strictly newer, else upToDate; either side unparseable →
	/// notComparable (the index may carry non-numeric versions); nothing installed
	/// → notInstalled. The agent never guesses across formats — a 1.0-vs-"v2"
	/// mismatch is reported, not coerced.
	/// </summary>
	public static string Classify(string? installedVersion, string? catalogVersion)
	{
		if (string.IsNullOrWhiteSpace(installedVersion))
		{
			return "notInstalled";
		}

		bool installedParsed = Version.TryParse(installedVersion, out Version? installed);
		bool catalogParsed = Version.TryParse(catalogVersion, out Version? catalog);
		if (!installedParsed || !catalogParsed)
		{
			return "notComparable";
		}

		return catalog > installed ? "updateAvailable" : "upToDate";
	}

	private static IReadOnlyList<(string Id, string Version)> ReadCandidates(IReadOnlyDictionary<string, object?> payload)
	{
		if (!payload.TryGetValue("candidates", out var raw))
		{
			return [];
		}

		if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
		{
			return array.EnumerateArray()
				.Where(item => item.ValueKind == JsonValueKind.Object)
				.Select(item => (
					Id: item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
					Version: item.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null))
				.Where(c => !string.IsNullOrWhiteSpace(c.Id))
				.Select(c => (c.Id!, c.Version ?? string.Empty))
				.ToList();
		}

		if (raw is IEnumerable<object> items)
		{
			return items.OfType<IReadOnlyDictionary<string, object?>>()
				.Select(item => (
					Id: item.TryGetValue("id", out var id) ? id as string : null,
					Version: item.TryGetValue("version", out var version) ? version as string : null))
				.Where(c => !string.IsNullOrWhiteSpace(c.Id))
				.Select(c => (c.Id!, c.Version ?? string.Empty))
				.ToList();
		}

		return [];
	}
}
