using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vapor.Steam.Core.Utilities;

public static partial class SensitiveDataRedactor
{
	private const string RedactedValue = "<redacted>";

	private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"password",
		"pass",
		"accesstoken",
		"refreshtoken",
		"token",
		"apikey",
		"api_key",
		"authorization",
		"authcode",
		"twofactorcode",
		"code",
		"key",
		"secret"
	};

	public static string Redact(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value ?? string.Empty;
		}

		return TryRedactJson(value, out var redactedJson)
			? redactedJson
			: RedactKeyValueText(value);
	}

	private static bool TryRedactJson(string value, out string redacted)
	{
		try
		{
			using var document = JsonDocument.Parse(value);
			using var stream = new MemoryStream();
			using var writer = new Utf8JsonWriter(stream);
			WriteRedactedElement(writer, document.RootElement, propertyName: null);
			writer.Flush();
			redacted = Encoding.UTF8.GetString(stream.ToArray());
			return true;
		}
		catch (JsonException)
		{
			redacted = string.Empty;
			return false;
		}
	}

	private static void WriteRedactedElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
	{
		if (propertyName != null && IsSensitiveKey(propertyName))
		{
			writer.WriteStringValue(RedactedValue);
			return;
		}

		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				writer.WriteStartObject();
				foreach (var property in element.EnumerateObject())
				{
					writer.WritePropertyName(property.Name);
					WriteRedactedElement(writer, property.Value, property.Name);
				}
				writer.WriteEndObject();
				break;
			case JsonValueKind.Array:
				writer.WriteStartArray();
				foreach (var item in element.EnumerateArray())
				{
					WriteRedactedElement(writer, item, propertyName);
				}
				writer.WriteEndArray();
				break;
			default:
				element.WriteTo(writer);
				break;
		}
	}

	private static string RedactKeyValueText(string value)
	{
		return SensitiveValuePattern().Replace(
			value,
			match =>
			{
				var key = match.Groups["key"].Value;
				if (!IsSensitiveKey(key))
				{
					return match.Value;
				}

				var prefix = match.Groups["prefix"].Value;
				var suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value : string.Empty;
				return prefix + RedactedValue + suffix;
			});
	}

	private static bool IsSensitiveKey(string propertyName)
	{
		var normalized = NormalizeKey(propertyName);
		return SensitiveKeys.Contains(normalized);
	}

	private static string NormalizeKey(string propertyName)
	{
		var builder = new StringBuilder(propertyName.Length);
		foreach (var c in propertyName)
		{
			if (char.IsLetterOrDigit(c))
			{
				builder.Append(char.ToLowerInvariant(c));
			}
		}

		return builder.ToString();
	}

	[GeneratedRegex("(?<prefix>(?:^|[?&\\s,{])(?:\"?)(?<key>[A-Za-z0-9_\\-]+)(?:\"?)\\s*[:=]\\s*(?:\"?))(?<value>[^\",\\s}&]+)(?<suffix>\"?)", RegexOptions.CultureInvariant)]
	private static partial Regex SensitiveValuePattern();
}
