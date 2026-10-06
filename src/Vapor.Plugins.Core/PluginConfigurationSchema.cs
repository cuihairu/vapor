using System.Globalization;

namespace Vapor.Plugins.Core;

/// <summary>
/// A single rule from a manifest's optional <c>configurationSchema</c> section: the value
/// type a configuration key must carry, whether the key is required, and optional bounds
/// or allowed values. Rules mirror the typed readers in
/// <see cref="PluginConfigurationExtensions"/> so anything the schema accepts is a value
/// those readers parse without falling back.
/// </summary>
public sealed record PluginConfigRule
{
	/// <summary>Expected value type: one of "string", "int", "bool" or "decimal".</summary>
	public string? Type { get; init; }

	/// <summary>When true, the key must be present with a non-blank value.</summary>
	public bool Required { get; init; }

	/// <summary>Inclusive lower bound for int/decimal rules; ignored otherwise.</summary>
	public decimal? Min { get; init; }

	/// <summary>Inclusive upper bound for int/decimal rules; ignored otherwise.</summary>
	public decimal? Max { get; init; }

	/// <summary>Allowed values for string rules (case-insensitive); ignored otherwise.</summary>
	public IReadOnlyList<string>? Enum { get; init; }
}

/// <summary>
/// Validates a plugin's configuration dictionary against the manifest's optional
/// <c>configurationSchema</c> before the plugin initializes. A missing schema is a no-op;
/// any violation throws a <see cref="PluginException"/> whose message names every offending
/// field, so one load attempt surfaces the full set of problems instead of the first.
/// </summary>
public static class PluginConfigurationSchema
{
	/// <summary>Rule types accepted in <see cref="PluginConfigRule.Type"/>.</summary>
	public static readonly IReadOnlyList<string> AllowedTypes = ["string", "int", "bool", "decimal"];

	/// <summary>
	/// Validates <paramref name="configuration"/> against <paramref name="schema"/>.
	/// Keys present in the configuration but absent from the schema are rejected: the
	/// schema is authoritative for the plugin version that shipped it, so a typo'd key
	/// fails loudly instead of silently doing nothing.
	/// </summary>
	public static void Validate(
		IReadOnlyDictionary<string, string> configuration,
		IReadOnlyDictionary<string, PluginConfigRule>? schema,
		string pluginId)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		if (schema is null)
		{
			return;
		}

		ArgumentNullException.ThrowIfNull(pluginId);

		var errors = new List<string>();
		foreach (var (key, rule) in schema)
		{
			configuration.TryGetValue(key, out string? value);
			if (string.IsNullOrWhiteSpace(value))
			{
				if (rule.Required)
				{
					errors.Add($"field '{key}' is required by configurationSchema");
				}

				continue;
			}

			switch (rule.Type?.Trim().ToLowerInvariant())
			{
				case "string":
					ValidateString(key, value, rule, errors);
					break;
				case "int":
					ValidateInt(key, value, rule, errors);
					break;
				case "bool":
					if (!PluginConfigurationExtensions.TryParseBool(value, out _))
					{
						errors.Add($"field '{key}' value '{value}' is not a valid bool");
					}

					break;
				case "decimal":
					ValidateDecimal(key, value, rule, errors);
					break;
				default:
					errors.Add($"field '{key}': unsupported rule type '{rule.Type}'");
					break;
			}
		}

		foreach (var key in configuration.Keys)
		{
			if (!schema.ContainsKey(key))
			{
				errors.Add($"field '{key}' is unknown (not declared in configurationSchema)");
			}
		}

		if (errors.Count > 0)
		{
			throw new PluginException(
				$"Plugin '{pluginId}' configuration failed schema validation: {string.Join("; ", errors)}");
		}
	}

	private static void ValidateString(string key, string value, PluginConfigRule rule, List<string> errors)
	{
		if (rule.Enum is null)
		{
			return;
		}

		bool matched = false;
		foreach (var allowed in rule.Enum)
		{
			if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase))
			{
				matched = true;
				break;
			}
		}

		if (!matched)
		{
			errors.Add($"field '{key}' value '{value}' is not one of: {string.Join(", ", rule.Enum)}");
		}
	}

	private static void ValidateInt(string key, string value, PluginConfigRule rule, List<string> errors)
	{
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
		{
			errors.Add($"field '{key}' value '{value}' is not a valid int");
			return;
		}

		if (rule.Min.HasValue && parsed < rule.Min.Value)
		{
			errors.Add($"field '{key}' value '{value}' is below min {rule.Min.Value}");
		}

		if (rule.Max.HasValue && parsed > rule.Max.Value)
		{
			errors.Add($"field '{key}' value '{value}' is above max {rule.Max.Value}");
		}
	}

	private static void ValidateDecimal(string key, string value, PluginConfigRule rule, List<string> errors)
	{
		if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
		{
			errors.Add($"field '{key}' value '{value}' is not a valid decimal");
			return;
		}

		if (rule.Min.HasValue && parsed < rule.Min.Value)
		{
			errors.Add($"field '{key}' value '{value}' is below min {rule.Min.Value}");
		}

		if (rule.Max.HasValue && parsed > rule.Max.Value)
		{
			errors.Add($"field '{key}' value '{value}' is above max {rule.Max.Value}");
		}
	}
}
