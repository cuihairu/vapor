using System.Globalization;

namespace Vapor.Plugins.Core;

/// <summary>
/// Typed readers over a plugin's configuration dictionary (as declared in the manifest's
/// <c>configuration</c> section). Every reader accepts an optional environment variable
/// that overrides the configured value, so operators can adjust plugin behaviour without
/// editing plugin.json: precedence is environment > configuration > fallback.
/// </summary>
public static class PluginConfigurationExtensions
{
	/// <summary>Reads a string value; empty or whitespace values fall back.</summary>
	public static string GetString(
		this IReadOnlyDictionary<string, string> configuration,
		string key,
		string fallback,
		string? environmentVariable = null)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentException.ThrowIfNullOrEmpty(key);

		string? fromEnv = environmentVariable is null ? null : Environment.GetEnvironmentVariable(environmentVariable);
		if (!string.IsNullOrWhiteSpace(fromEnv))
		{
			return fromEnv;
		}

		return configuration.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
	}

	/// <summary>Reads an integer value; unparsable or out-of-range values fall back.</summary>
	public static int GetInt32(
		this IReadOnlyDictionary<string, string> configuration,
		string key,
		int fallback,
		string? environmentVariable = null,
		int? min = null,
		int? max = null)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentException.ThrowIfNullOrEmpty(key);

		string? fromEnv = environmentVariable is null ? null : Environment.GetEnvironmentVariable(environmentVariable);
		if (!string.IsNullOrWhiteSpace(fromEnv) && TryParseIntInRange(fromEnv, min, max, out int envParsed))
		{
			return envParsed;
		}

		return configuration.TryGetValue(key, out string? value) && TryParseIntInRange(value, min, max, out int parsed)
			? parsed
			: fallback;
	}

	/// <summary>Reads a boolean value; accepts true/false/1/0/yes/no (case-insensitive).</summary>
	public static bool GetBool(
		this IReadOnlyDictionary<string, string> configuration,
		string key,
		bool fallback,
		string? environmentVariable = null)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentException.ThrowIfNullOrEmpty(key);

		string? fromEnv = environmentVariable is null ? null : Environment.GetEnvironmentVariable(environmentVariable);
		if (!string.IsNullOrWhiteSpace(fromEnv) && TryParseBool(fromEnv, out bool envParsed))
		{
			return envParsed;
		}

		return configuration.TryGetValue(key, out string? value) && TryParseBool(value, out bool parsed)
			? parsed
			: fallback;
	}

	/// <summary>Reads a decimal value; unparsable or out-of-range values fall back.</summary>
	public static decimal GetDecimal(
		this IReadOnlyDictionary<string, string> configuration,
		string key,
		decimal fallback,
		string? environmentVariable = null,
		decimal? min = null,
		decimal? max = null)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentException.ThrowIfNullOrEmpty(key);

		string? fromEnv = environmentVariable is null ? null : Environment.GetEnvironmentVariable(environmentVariable);
		if (!string.IsNullOrWhiteSpace(fromEnv) && TryParseDecimalInRange(fromEnv, min, max, out decimal envParsed))
		{
			return envParsed;
		}

		return configuration.TryGetValue(key, out string? value) && TryParseDecimalInRange(value, min, max, out decimal parsed)
			? parsed
			: fallback;
	}

	private static bool TryParseIntInRange(string? value, int? min, int? max, out int parsed)
	{
		parsed = 0;
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
			&& (!min.HasValue || parsed >= min.Value)
			&& (!max.HasValue || parsed <= max.Value);
	}

	private static bool TryParseDecimalInRange(string? value, decimal? min, decimal? max, out decimal parsed)
	{
		parsed = 0;
		return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed)
			&& (!min.HasValue || parsed >= min.Value)
			&& (!max.HasValue || parsed <= max.Value);
	}

	private static bool TryParseBool(string? value, out bool parsed)
	{
		switch (value?.Trim().ToLowerInvariant())
		{
			case "true":
			case "1":
			case "yes":
				parsed = true;
				return true;
			case "false":
			case "0":
			case "no":
				parsed = false;
				return true;
			default:
				parsed = false;
				return false;
		}
	}
}
