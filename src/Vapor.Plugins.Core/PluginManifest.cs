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

	/// <summary>
	/// Declared trust level ("unknown", "community" or "official"). Normalized to the
	/// lowercase canonical form during parsing; <see cref="PluginTrust.Unknown"/> when omitted.
	/// </summary>
	public string? Trust { get; init; }

	/// <summary>
	/// Declared permission names (<see cref="PluginPermissions"/>). Normalized to lowercase,
	/// deduplicated, order-preserving; null when the manifest omits the field.
	/// </summary>
	public IReadOnlyList<string>? Permissions { get; init; }

	/// <summary>Free-form configuration values handed to the plugin at initialization.</summary>
	public IReadOnlyDictionary<string, string>? Configuration { get; init; } =
		new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Optional per-key validation rules for <see cref="Configuration"/>. When present, the
	/// loader validates the configuration against it before <c>InitializeAsync</c> and a
	/// violation fails the load with a field-level error. Null when the manifest omits it.
	/// </summary>
	public IReadOnlyDictionary<string, PluginConfigRule>? ConfigurationSchema { get; init; }

	/// <summary>
	/// Optional plugin dependencies. When present, enablement builds a dependency graph:
	/// missing dependencies, circular dependencies and apiVersion mismatches fail discovery,
	/// and plugins load after their dependencies. Null when the manifest omits it.
	/// </summary>
	public IReadOnlyList<PluginDependency>? Dependencies { get; init; }

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

		manifest = manifest with
		{
			Trust = NormalizeTrust(manifest.Trust, source),
			Permissions = NormalizePermissions(manifest.Permissions, source),
			ConfigurationSchema = NormalizeConfigurationSchema(manifest.ConfigurationSchema, source),
			Dependencies = NormalizeDependencies(manifest.Dependencies, manifest.Id, source)
		};

		Validate(manifest, source);
		return manifest;
	}

	private static IReadOnlyDictionary<string, PluginConfigRule>? NormalizeConfigurationSchema(
		IReadOnlyDictionary<string, PluginConfigRule>? schema, string source)
	{
		if (schema is null || schema.Count == 0)
		{
			return null;
		}

		var normalized = new Dictionary<string, PluginConfigRule>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, rule) in schema)
		{
			if (string.IsNullOrWhiteSpace(key))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema keys must not be empty");
			}

			if (rule is null)
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'] must be an object");
			}

			var type = rule.Type?.Trim().ToLowerInvariant();
			if (string.IsNullOrEmpty(type))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'].type is required");
			}

			if (!PluginConfigurationSchema.AllowedTypes.Contains(type))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'].type '{rule.Type}' is not one of: {string.Join(", ", PluginConfigurationSchema.AllowedTypes)}");
			}

			if ((rule.Min.HasValue || rule.Max.HasValue) && type is not ("int" or "decimal"))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'] min/max are only valid for int/decimal rules");
			}

			if (rule.Min.HasValue && rule.Max.HasValue && rule.Min.Value > rule.Max.Value)
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'] min {rule.Min.Value} must not exceed max {rule.Max.Value}");
			}

			if (rule.Enum is not null && type != "string")
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'] enum is only valid for string rules");
			}

			if (rule.Enum is not null && rule.Enum.Any(string.IsNullOrWhiteSpace))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': configurationSchema['{key}'] enum values must not be empty");
			}

			normalized[key] = rule with
			{
				Type = type,
				Enum = rule.Enum is null ? null : rule.Enum.Select(e => e.Trim()).ToArray()
			};
		}

		return normalized;
	}

	private static IReadOnlyList<PluginDependency>? NormalizeDependencies(
		IReadOnlyList<PluginDependency>? dependencies, string? manifestId, string source)
	{
		if (dependencies is null || dependencies.Count == 0)
		{
			return null;
		}

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var normalized = new List<PluginDependency>(dependencies.Count);
		foreach (var dependency in dependencies)
		{
			if (dependency is null)
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': dependencies entries must be objects");
			}

			var pluginId = dependency.PluginId?.Trim();
			if (string.IsNullOrEmpty(pluginId))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': dependencies[].pluginId is required");
			}

			if (!seen.Add(pluginId))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': duplicate dependency '{pluginId}'");
			}

			if (string.Equals(pluginId, manifestId?.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': dependency '{pluginId}' must not be the plugin itself");
			}

			if (dependency.ApiVersion is not null && !PluginApi.TryParseVersion(dependency.ApiVersion, out _))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': dependency '{pluginId}' apiVersion '{dependency.ApiVersion}' is not a valid SemVer version");
			}

			normalized.Add(dependency with { PluginId = pluginId });
		}

		return normalized;
	}

	private static string? NormalizeTrust(string? trust, string source)
	{
		if (trust is null)
		{
			return null;
		}

		var normalized = trust.Trim().ToLowerInvariant();
		if (normalized.Length == 0)
		{
			return null;
		}

		if (normalized is not ("unknown" or "community" or "official"))
		{
			throw new PluginException(
				$"Invalid plugin manifest '{source}': 'trust' '{trust}' is not one of: unknown, community, official");
		}

		return normalized;
	}

	private static IReadOnlyList<string>? NormalizePermissions(IReadOnlyList<string>? permissions, string source)
	{
		if (permissions is null)
		{
			return null;
		}

		var normalized = new List<string>();
		foreach (var raw in permissions)
		{
			var value = raw.Trim().ToLowerInvariant();
			if (normalized.Contains(value))
			{
				continue;
			}

			if (!PluginPermissions.All.Contains(value))
			{
				throw new PluginException(
					$"Invalid plugin manifest '{source}': permission '{raw}' is not one of: {string.Join(", ", PluginPermissions.All)}");
			}

			normalized.Add(value);
		}

		return normalized;
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
