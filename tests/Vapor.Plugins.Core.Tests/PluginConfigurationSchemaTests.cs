using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginConfigurationSchemaTests
{
	private static Dictionary<string, string> Config(params (string Key, string Value)[] entries) =>
		new(entries.ToDictionary(e => e.Key, e => e.Value), StringComparer.OrdinalIgnoreCase);

	private static Dictionary<string, PluginConfigRule> Schema(params (string Key, PluginConfigRule Rule)[] entries) =>
		new(entries.ToDictionary(e => e.Key, e => e.Rule), StringComparer.OrdinalIgnoreCase);

	[Fact]
	public void Validate_NullSchema_NoOp()
	{
		var config = Config(("anything", "not-validated"));

		PluginConfigurationSchema.Validate(config, null, pluginId: null!);
	}

	[Fact]
	public void Validate_ValidConfiguration_Passes()
	{
		var config = Config(
			("s", "hello"),
			("i", "42"),
			("b", "yes"),
			("d", "0.5"),
			("e", "LARGE"),
			("min_only", "3"),
			("max_only", "7"));
		var schema = Schema(
			("s", new PluginConfigRule { Type = "string" }),
			("i", new PluginConfigRule { Type = "int", Min = 0, Max = 100 }),
			("b", new PluginConfigRule { Type = "bool" }),
			("d", new PluginConfigRule { Type = "decimal", Min = 0m, Max = 1m }),
			("e", new PluginConfigRule { Type = "string", Enum = ["small", "large"] }),
			("min_only", new PluginConfigRule { Type = "int", Min = 3 }),
			("max_only", new PluginConfigRule { Type = "int", Max = 7 }));

		PluginConfigurationSchema.Validate(config, schema, "vapor.sample");
	}

	[Fact]
	public void Validate_RequiredKeyMissing_Throws()
	{
		var schema = Schema(("must", new PluginConfigRule { Type = "string", Required = true }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(), schema, "vapor.sample"));
		Assert.Contains("field 'must' is required by configurationSchema", ex.Message);
	}

	[Fact]
	public void Validate_RequiredKeyWhitespace_Throws()
	{
		var schema = Schema(("must", new PluginConfigRule { Type = "string", Required = true }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("must", "   ")), schema, "vapor.sample"));
		Assert.Contains("field 'must' is required", ex.Message);
	}

	[Fact]
	public void Validate_OptionalKeyWhitespace_Passes()
	{
		var schema = Schema(("maybe", new PluginConfigRule { Type = "int", Min = 1 }));

		// Whitespace counts as absent: mirrors the readers' blank-means-fallback semantics.
		PluginConfigurationSchema.Validate(Config(("maybe", "")), schema, "vapor.sample");
	}

	[Fact]
	public void Validate_UnknownKey_Throws()
	{
		var schema = Schema(("known", new PluginConfigRule { Type = "string" }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("known", "v"), ("typo", "x")), schema, "vapor.sample"));
		Assert.Contains("field 'typo' is unknown (not declared in configurationSchema)", ex.Message);
	}

	[Theory]
	[InlineData("abc")]
	[InlineData("1.5")]
	public void Validate_IntNotParsable_Throws(string value)
	{
		var schema = Schema(("i", new PluginConfigRule { Type = "int" }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("i", value)), schema, "vapor.sample"));
		Assert.Contains($"field 'i' value '{value}' is not a valid int", ex.Message);
	}

	[Fact]
	public void Validate_IntBelowMin_Throws()
	{
		var schema = Schema(("i", new PluginConfigRule { Type = "int", Min = 10 }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("i", "5")), schema, "vapor.sample"));
		Assert.Contains("field 'i' value '5' is below min 10", ex.Message);
	}

	[Fact]
	public void Validate_IntAboveMax_Throws()
	{
		var schema = Schema(("i", new PluginConfigRule { Type = "int", Max = 100 }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("i", "200")), schema, "vapor.sample"));
		Assert.Contains("field 'i' value '200' is above max 100", ex.Message);
	}

	[Theory]
	[InlineData("abc")]
	[InlineData("1.2.3")]
	public void Validate_DecimalNotParsable_Throws(string value)
	{
		var schema = Schema(("d", new PluginConfigRule { Type = "decimal" }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("d", value)), schema, "vapor.sample"));
		Assert.Contains($"field 'd' value '{value}' is not a valid decimal", ex.Message);
	}

	[Fact]
	public void Validate_DecimalBelowMin_Throws()
	{
		var schema = Schema(("d", new PluginConfigRule { Type = "decimal", Min = 0.01m }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("d", "0.001")), schema, "vapor.sample"));
		Assert.Contains("field 'd' value '0.001' is below min 0.01", ex.Message);
	}

	[Fact]
	public void Validate_DecimalAboveMax_Throws()
	{
		var schema = Schema(("d", new PluginConfigRule { Type = "decimal", Max = 10m }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("d", "10.01")), schema, "vapor.sample"));
		Assert.Contains("field 'd' value '10.01' is above max 10", ex.Message);
	}

	[Fact]
	public void Validate_BoolNotParsable_Throws()
	{
		var schema = Schema(("b", new PluginConfigRule { Type = "bool" }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("b", "maybe")), schema, "vapor.sample"));
		Assert.Contains("field 'b' value 'maybe' is not a valid bool", ex.Message);
	}

	[Fact]
	public void Validate_StringEnumMismatch_Throws()
	{
		var schema = Schema(("e", new PluginConfigRule { Type = "string", Enum = ["a", "b"] }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("e", "c")), schema, "vapor.sample"));
		Assert.Contains("field 'e' value 'c' is not one of: a, b", ex.Message);
	}

	[Fact]
	public void Validate_UnsupportedRuleType_Throws()
	{
		var schema = Schema(("x", new PluginConfigRule { Type = "float" }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("x", "1")), schema, "vapor.sample"));
		Assert.Contains("field 'x': unsupported rule type 'float'", ex.Message);
	}

	[Fact]
	public void Validate_NullRuleType_Throws()
	{
		// Manifest parsing normalizes type away, but a hand-built schema (the Validate
		// contract accepts any rule) must not NRE on a null Type.
		var schema = Schema(("x", new PluginConfigRule { Type = null }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(Config(("x", "1")), schema, "vapor.sample"));
		Assert.Contains("field 'x': unsupported rule type ''", ex.Message);
	}

	[Fact]
	public void Validate_MultipleErrors_AggregatedIntoOneMessage()
	{
		var config = Config(("i", "abc"), ("b", "nope"));
		var schema = Schema(
			("i", new PluginConfigRule { Type = "int" }),
			("b", new PluginConfigRule { Type = "bool" }),
			("missing", new PluginConfigRule { Type = "string", Required = true }));

		var ex = Assert.Throws<PluginException>(
			() => PluginConfigurationSchema.Validate(config, schema, "vapor.multi"));
		Assert.Contains("vapor.multi", ex.Message);
		Assert.Contains("field 'i' value 'abc' is not a valid int", ex.Message);
		Assert.Contains("field 'b' value 'nope' is not a valid bool", ex.Message);
		Assert.Contains("field 'missing' is required by configurationSchema", ex.Message);
	}

	[Fact]
	public void Validate_NullConfiguration_Throws()
	{
		Assert.Throws<ArgumentNullException>(
			() => PluginConfigurationSchema.Validate(null!, Schema(("k", new PluginConfigRule { Type = "string" })), "vapor.sample"));
	}

	[Fact]
	public void Validate_NullPluginId_Throws()
	{
		Assert.Throws<ArgumentNullException>(
			() => PluginConfigurationSchema.Validate(Config(), Schema(("k", new PluginConfigRule { Type = "string" })), null!));
	}
}
