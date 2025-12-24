using System.Globalization;
using System.Text.Json;

namespace SteamControl.Steam.Core;

public static class PayloadReader
{
	public static bool TryGetValue(IReadOnlyDictionary<string, object?> payload, string key, out object? value)
	{
		if (payload.TryGetValue(key, out value))
		{
			return true;
		}

		foreach (var kvp in payload)
		{
			if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				value = kvp.Value;
				return true;
			}
		}

		value = null;
		return false;
	}

	public static string? GetString(IReadOnlyDictionary<string, object?> payload, string key)
	{
		if (!TryGetValue(payload, key, out var value) || value == null)
		{
			return null;
		}

		return value switch
		{
			string s => s,
			JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
			JsonElement je => je.ToString(),
			_ => value.ToString()
		};
	}

	public static int? GetInt32(IReadOnlyDictionary<string, object?> payload, string key)
	{
		if (!TryGetValue(payload, key, out var value) || value == null)
		{
			return null;
		}

		return value switch
		{
			int i => i,
			long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
			double d when d is >= int.MinValue and <= int.MaxValue => (int)d,
			JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var i) => i,
			JsonElement { ValueKind: JsonValueKind.String } je when int.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
			string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
			_ => null
		};
	}
}

