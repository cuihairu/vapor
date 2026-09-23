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
		"secret",
		"proxy"
	};

	public static string Redact(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value ?? string.Empty;
		}

		// Inline proxy credentials can ride anywhere (a note, a URL, a nested
		// JSON string), so the URI credential form is scrubbed before any
		// structured redaction looks at keys.
		return TryRedactJson(ScrubProxyCredentials(value), out var redactedJson)
			? redactedJson
			: RedactKeyValueText(ScrubProxyCredentials(value));
	}

	/// <summary>
	/// Strips control characters (line breaks among them) so caller-controlled
	/// values such as account names or job ids cannot forge log lines or break
	/// out of a structured-log boundary. Legitimate identifiers never contain
	/// control characters, so well-formed input passes through unchanged.
	/// </summary>
	public static string SanitizeLogValue(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		var builder = new StringBuilder(value.Length);
		foreach (var c in value)
		{
			if (!char.IsControl(c))
			{
				builder.Append(c);
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// Redacts a structured value when its key is sensitive (password, token, code, key, ...).
	/// Returns the value unchanged otherwise. Useful for structured-log state where the
	/// key is known separately from the value.
	/// </summary>
	public static string? RedactValue(string? key, string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value;
		}

		return key != null && IsSensitiveKey(key)
			? RedactedValue
			: Redact(value);
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
				if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
				{
					writer.WriteStringValue(ScrubProxyCredentials(text));
				}
				else
				{
					element.WriteTo(writer);
				}

				break;
		}
	}

	/// <summary>
	/// Masks the user:password segment of an inline proxy URI (http/https/socks5),
	/// wherever the URI appears. Credentials-free URIs never carry the '@' form
	/// and pass through untouched; over-masking the user name is acceptable —
	/// only the endpoint host and port need to stay legible for diagnostics.
	/// </summary>
	private static string ScrubProxyCredentials(string value) =>
		ProxyCredentialPattern().Replace(value, match =>
		{
			var scheme = match.Groups["scheme"].Value;
			return scheme + "://<redacted>@";
		});

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
				// No Success check: the suffix group sits on the main pattern path
				// (optional zero-width), so it always participates; and a group that
				// did not match has Value == "" per .NET, making the guard a tautology.
				var suffix = match.Groups["suffix"].Value;
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

	[GeneratedRegex("(?<scheme>https?|socks5)://[^/@\\s]+@", RegexOptions.CultureInvariant)]
	private static partial Regex ProxyCredentialPattern();
}
