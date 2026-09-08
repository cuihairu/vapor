using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vapor.Plugins.Core;

/// <summary>
/// Deserialized form of a plugin's plugin.json manifest.
/// </summary>
public sealed record PluginManifest
{
	public const string ManifestFileName = "plugin.json";

	public required string Id { get; init; }

	public required string Name { get; init; }

	/// <summary>SemVer version of the plugin itself (e.g. "1.2.0").</summary>
	public required string Version { get; init; }

	/// <summary>SemVer version of the plugin API the plugin was built against.</summary>
	public required string ApiVersion { get; init; }

	public string? Description { get; init; }

	/// <summary>File name of the assembly containing the plugin, relative to the plugin directory.</summary>
	public required string EntryAssembly { get; init; }

	/// <summary>
	/// Optional full type name of the <see cref="IPlugin"/> implementation. When omitted,
	/// all public IPlugin implementations in the entry assembly are instantiated (exactly
	/// one is required).
	/// </summary>
	public string? EntryType { get; init; }

	/// <summary>Free-form configuration values handed to the plugin at initialization.</summary>
	public IReadOnlyDictionary<string, string>? Configuration { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	/// <summary>Loads and validates the manifest from a plugin.json file.</summary>
	public static PluginManifest Load(string manifestPath)
	{
		string json;
		try
		{
			json = File.ReadAllText(manifestPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new PluginException($"Failed to read plugin manifest '{manifestPath}': {ex.Message}", ex);
		}

		return Parse(json, manifestPath);
	}

	/// <summary>Parses and validates manifest JSON.</summary>
	public static PluginManifest Parse(string json, string? sourceName = null)
	{
		var source = sourceName ?? ManifestFileName;

		PluginManifest? manifest;
		try
		{
			manifest = JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);
		}
		catch (JsonException ex)
		{
			throw new PluginException($"Invalid plugin manifest '{source}': {ex.Message}", ex);
		}

		if (manifest is null)
		{
			throw new PluginException($"Invalid plugin manifest '{source}': empty document");
		}

		// An explicit "configuration": null should behave the same as omitting the key.
		if (manifest.Configuration is null)
		{
			manifest = manifest with { Configuration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
		}

		Validate(manifest, source);
		return manifest;
	}

	private static void Validate(PluginManifest manifest, string source)
	{
		if (string.IsNullOrWhiteSpace(manifest.Id))
		{
			throw new PluginException($"Invalid plugin manifest '{source}': 'id' is required");
		}

		if (string.IsNullOrWhiteSpace(manifest.Name))
		{
			throw new PluginException($"Invalid plugin manifest '{source}': 'name' is required");
		}

		if (!PluginApi.TryParseVersion(manifest.Version, out _))
		{
			throw new PluginException(
				$"Invalid plugin manifest '{source}': 'version' '{manifest.Version}' is not a valid SemVer version");
		}

		if (!PluginApi.TryParseVersion(manifest.ApiVersion, out _))
		{
			throw new PluginException(
				$"Invalid plugin manifest '{source}': 'apiVersion' '{manifest.ApiVersion}' is not a valid SemVer version");
		}

		if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
		{
			throw new PluginException($"Invalid plugin manifest '{source}': 'entryAssembly' is required");
		}
	}
}
