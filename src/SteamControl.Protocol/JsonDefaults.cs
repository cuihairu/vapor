using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamControl.Protocol;

public static class JsonDefaults {
	public static readonly JsonSerializerOptions Options = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};
}

