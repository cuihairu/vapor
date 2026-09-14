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
}
