using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginManifestTests
{
	[Fact]
	public void Parse_ValidManifest_Succeeds()
	{
		var json = """
			{
				"id": "vapor.sample",
				"name": "Sample Plugin",
				"version": "1.2.3",
				"apiVersion": "1.0",
				"description": "a sample",
				"entryAssembly": "Sample.dll",
				"entryType": "Sample.Plugin",
				"configuration": { "key": "value" }
			}
			""";

		var manifest = PluginManifest.Parse(json);

		Assert.Equal("vapor.sample", manifest.Id);
		Assert.Equal("Sample Plugin", manifest.Name);
		Assert.Equal("1.2.3", manifest.Version);
		Assert.Equal("1.0", manifest.ApiVersion);
		Assert.Equal("Sample.dll", manifest.EntryAssembly);
		Assert.Equal("Sample.Plugin", manifest.EntryType);
		Assert.Equal("value", manifest.Configuration!["key"]);
	}

	[Fact]
	public void Parse_MissingId_Throws()
	{
		var json = """
			{ "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
	}

	[Fact]
	public void Parse_InvalidPluginVersion_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "nope", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("version", ex.Message);
	}

	[Fact]
	public void Parse_InvalidApiVersion_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "nope", "entryAssembly": "x.dll" }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("apiVersion", ex.Message);
	}

	[Fact]
	public void Parse_MissingEntryAssembly_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0" }
			""";

		Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
	}

	[Fact]
	public void Parse_InvalidJson_Throws()
	{
		Assert.Throws<PluginException>(() => PluginManifest.Parse("{ not json"));
	}

	[Fact]
	public void Parse_NullDocument_Throws()
	{
		// The literal JSON document "null" deserializes to a null manifest, which the
		// parser must reject instead of returning an empty plugin.
		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse("null"));
		Assert.Contains("empty document", ex.Message);
	}

	[Fact]
	public void Parse_BlankId_Throws()
	{
		// A present-but-blank id passes deserialization (the property exists) and must be
		// caught by validation, not by the JSON layer.
		var json = """
			{ "id": "  ", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("'id' is required", ex.Message);
	}

	[Fact]
	public void Parse_BlankName_Throws()
	{
		var json = """
			{ "id": "x", "name": "  ", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("'name' is required", ex.Message);
	}

	[Fact]
	public void Parse_BlankEntryAssembly_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "  " }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("'entryAssembly' is required", ex.Message);
	}

	[Fact]
	public void Parse_BlankTrust_NormalizesToNull()
	{
		// A whitespace-only trust declaration means "not declared", not "invalid".
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll", "trust": "   " }
			""";

		var manifest = PluginManifest.Parse(json);

		Assert.Null(manifest.Trust);
	}

	[Fact]
	public void Parse_OptionalFields_Default()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		var manifest = PluginManifest.Parse(json);

		Assert.Null(manifest.Description);
		Assert.Null(manifest.EntryType);
		Assert.Empty(manifest.Configuration!);
	}

	[Fact]
	public void Load_MissingFile_ThrowsPluginException()
	{
		Assert.Throws<PluginException>(() =>
			PluginManifest.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "plugin.json")));
	}

	[Fact]
	public void Parse_ConfigurationSchema_RoundTrips()
	{
		var json = """
			{
				"id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
				"configurationSchema": {
					"interval": { "type": "int", "required": true, "min": 1, "max": 60 },
					"ratio": { "type": "decimal", "min": 0.5 },
					"mode": { "type": "string", "enum": ["fast", "slow"] },
					"flag": { "type": "bool" }
				}
			}
			""";

		var manifest = PluginManifest.Parse(json);

		Assert.NotNull(manifest.ConfigurationSchema);
		Assert.Equal(4, manifest.ConfigurationSchema.Count);
		var interval = manifest.ConfigurationSchema["interval"];
		Assert.Equal("int", interval.Type);
		Assert.True(interval.Required);
		Assert.Equal(1m, interval.Min);
		Assert.Equal(60m, interval.Max);
		Assert.Null(manifest.ConfigurationSchema["ratio"].Max);
		Assert.Equal(["fast", "slow"], manifest.ConfigurationSchema["mode"].Enum);
		Assert.False(manifest.ConfigurationSchema["flag"].Required);
	}

	[Fact]
	public void Parse_ConfigurationSchema_Omitted_IsNull()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		Assert.Null(PluginManifest.Parse(json).ConfigurationSchema);
	}

	[Fact]
	public void Parse_ConfigurationSchema_EmptyObject_IsNull()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll", "configurationSchema": {} }
			""";

		Assert.Null(PluginManifest.Parse(json).ConfigurationSchema);
	}

	[Fact]
	public void Parse_ConfigurationSchema_TypeNormalizedToLowercase()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "type": " INT " } } }
			""";

		Assert.Equal("int", PluginManifest.Parse(json).ConfigurationSchema!["k"].Type);
	}

	[Fact]
	public void Parse_ConfigurationSchema_MissingType_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "min": 1 } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("configurationSchema['k'].type is required", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_UnknownType_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "type": "float" } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("configurationSchema['k'].type 'float'", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_NullRule_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": null } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("configurationSchema['k'] must be an object", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_MinMaxOnString_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "type": "string", "min": 1 } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("min/max are only valid for int/decimal rules", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_EnumOnInt_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "type": "int", "enum": ["1"] } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("enum is only valid for string rules", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_MinExceedsMax_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "type": "int", "min": 10, "max": 5 } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("min 10 must not exceed max 5", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_EmptyEnumValue_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { "k": { "type": "string", "enum": ["a", " "] } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("enum values must not be empty", ex.Message);
	}

	[Fact]
	public void Parse_ConfigurationSchema_EmptyKey_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "configurationSchema": { " ": { "type": "int" } } }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("configurationSchema keys must not be empty", ex.Message);
	}

	[Fact]
	public void Parse_Dependencies_RoundTrips()
	{
		var json = """
			{
				"id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
				"dependencies": [
					{ "pluginId": "vapor.base" },
					{ "pluginId": "vapor.other", "apiVersion": "1.2" }
				]
			}
			""";

		var manifest = PluginManifest.Parse(json);

		Assert.NotNull(manifest.Dependencies);
		Assert.Equal(2, manifest.Dependencies.Count);
		Assert.Equal("vapor.base", manifest.Dependencies[0].PluginId);
		Assert.Null(manifest.Dependencies[0].ApiVersion);
		Assert.Equal("vapor.other", manifest.Dependencies[1].PluginId);
		Assert.Equal("1.2", manifest.Dependencies[1].ApiVersion);
	}

	[Fact]
	public void Parse_Dependencies_Omitted_IsNull()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll" }
			""";

		Assert.Null(PluginManifest.Parse(json).Dependencies);
	}

	[Fact]
	public void Parse_Dependencies_EmptyList_IsNull()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll", "dependencies": [] }
			""";

		Assert.Null(PluginManifest.Parse(json).Dependencies);
	}

	[Fact]
	public void Parse_Dependencies_NullEntry_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [null] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("dependencies entries must be objects", ex.Message);
	}

	[Fact]
	public void Parse_Dependencies_MissingPluginId_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "apiVersion": "1.0" }] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("dependencies[].pluginId is required", ex.Message);
	}

	[Fact]
	public void Parse_Dependencies_BlankPluginId_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "pluginId": "   " }] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("dependencies[].pluginId is required", ex.Message);
	}

	[Fact]
	public void Parse_Dependencies_PluginIdTrimmed()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "pluginId": "  vapor.base  " }] }
			""";

		Assert.Equal("vapor.base", PluginManifest.Parse(json).Dependencies![0].PluginId);
	}

	[Fact]
	public void Parse_Dependencies_Duplicate_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "pluginId": "vapor.base" }, { "pluginId": "VAPOR.BASE" }] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("duplicate dependency 'VAPOR.BASE'", ex.Message);
	}

	[Fact]
	public void Parse_Dependencies_SelfDependency_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "pluginId": "X" }] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("must not be the plugin itself", ex.Message);
	}

	[Fact]
	public void Parse_Dependencies_InvalidApiVersion_Throws()
	{
		var json = """
			{ "id": "x", "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "pluginId": "vapor.base", "apiVersion": "not-a-version" }] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("apiVersion 'not-a-version' is not a valid SemVer version", ex.Message);
	}

	[Fact]
	public void Parse_NullIdWithDependencies_StillReportsIdRequired()
	{
		// "id": null is present (required satisfied) but empty; dependency normalization
		// runs before the required-field validation, so the self-dependency check must
		// tolerate a null manifest id and let Validate report the real problem.
		var json = """
			{ "id": null, "name": "x", "version": "1.0.0", "apiVersion": "1.0", "entryAssembly": "x.dll",
			  "dependencies": [{ "pluginId": "vapor.base" }] }
			""";

		var ex = Assert.Throws<PluginException>(() => PluginManifest.Parse(json));
		Assert.Contains("'id' is required", ex.Message);
	}
}
