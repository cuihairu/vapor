using System.Text.Json;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Exercises every value-shape branch of <see cref="PayloadReader"/>, with the
/// emphasis on post-JSON-round-trip shapes (JsonElement values) that actions
/// receive when payloads come back from SQLite task records.
/// </summary>
public sealed class PayloadReaderTests
{
	private static Dictionary<string, object?> FromJson(string json) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

	// --- TryGetValue / GetString shapes ---

	[Fact]
	public void TryGetValue_MatchesKeyCaseInsensitively()
	{
		var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["API_KEY"] = "v" };

		Assert.True(PayloadReader.TryGetValue(payload, "api_key", out object? value));
		Assert.Equal("v", value);
	}

	[Fact]
	public void GetString_NullValue_ReturnsNull()
	{
		var payload = new Dictionary<string, object?> { ["k"] = null };

		Assert.Null(PayloadReader.GetString(payload, "k"));
	}

	[Fact]
	public void GetString_JsonElementString_ReturnsText()
	{
		var payload = FromJson("""{"k":"hello"}""");

		Assert.Equal("hello", PayloadReader.GetString(payload, "k"));
	}

	[Fact]
	public void GetString_JsonElementObject_ReturnsRawJson()
	{
		var payload = FromJson("""{"k":{"a":1}}""");

		Assert.Equal("""{"a":1}""", PayloadReader.GetString(payload, "k"));
	}

	[Fact]
	public void GetString_NonStringValue_FallsBackToToString()
	{
		var payload = new Dictionary<string, object?> { ["k"] = 42 };

		Assert.Equal("42", PayloadReader.GetString(payload, "k"));
	}

	// --- GetInt32 shapes ---

	[Fact]
	public void GetInt32_LongWithinRange_Converts()
	{
		var payload = new Dictionary<string, object?> { ["k"] = 42L };

		Assert.Equal(42, PayloadReader.GetInt32(payload, "k"));
	}

	[Fact]
	public void GetInt32_LongOutOfRange_ReturnsNull()
	{
		var payload = new Dictionary<string, object?> { ["k"] = long.MaxValue };

		Assert.Null(PayloadReader.GetInt32(payload, "k"));
	}

	[Fact]
	public void GetInt32_WholeDouble_Converts()
	{
		var payload = new Dictionary<string, object?> { ["k"] = 7.0 };

		Assert.Equal(7, PayloadReader.GetInt32(payload, "k"));
	}

	[Fact]
	public void GetInt32_FractionalDouble_ReturnsNull()
	{
		var payload = new Dictionary<string, object?> { ["k"] = 7.5 };

		Assert.Null(PayloadReader.GetInt32(payload, "k"));
	}

	[Fact]
	public void GetInt32_JsonElementNumber_Converts()
	{
		var payload = FromJson("""{"k":123}""");

		Assert.Equal(123, PayloadReader.GetInt32(payload, "k"));
	}

	[Fact]
	public void GetInt32_JsonElementNumericString_Parses()
	{
		var payload = FromJson("""{"k":"123"}""");

		Assert.Equal(123, PayloadReader.GetInt32(payload, "k"));
	}

	[Fact]
	public void GetInt32_NonNumeric_ReturnsNull()
	{
		var payload = new Dictionary<string, object?> { ["k"] = "abc" };

		Assert.Null(PayloadReader.GetInt32(payload, "k"));
	}

	// --- GetBool shapes ---

	[Fact]
	public void GetBool_JsonElementTrueFalse_Converts()
	{
		Assert.True(PayloadReader.GetBool(FromJson("""{"k":true}"""), "k"));
		Assert.False(PayloadReader.GetBool(FromJson("""{"k":false}"""), "k"));
	}

	[Fact]
	public void GetBool_String_Parses()
	{
		var payload = new Dictionary<string, object?> { ["k"] = "true" };

		Assert.True(PayloadReader.GetBool(payload, "k"));
	}

	[Fact]
	public void GetBool_JsonElementBooleanString_Parses()
	{
		Assert.False(PayloadReader.GetBool(FromJson("""{"k":"false"}"""), "k"));
	}

	[Fact]
	public void GetBool_NonBoolean_ReturnsNull()
	{
		var payload = new Dictionary<string, object?> { ["k"] = 1 };

		Assert.Null(PayloadReader.GetBool(payload, "k"));
	}

	[Fact]
	public void GetBool_MissingKey_ReturnsNull()
	{
		Assert.Null(PayloadReader.GetBool(new Dictionary<string, object?>(), "missing"));
	}
}
