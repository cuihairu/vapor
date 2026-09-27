using Xunit;

namespace Vapor.Plugins.CaseOpening.Tests;

/// <summary>
/// Catalog behavior: the default catalog's shape, strict JSON parsing and every
/// validation failure mode (a typo must reshape the odds, never silently).
/// </summary>
public sealed class CaseCatalogTests
{
	private const string ValidCatalog = """
		{
		  "cases": [
		    {
		      "id": "acme.case1",
		      "name": "Acme Case",
		      "items": [
		        { "id": "a1", "name": "Pistol | Sand", "rarity": "milspec" },
		        { "id": "a2", "name": "SMG | Dust", "rarity": "Restricted", "minFloat": 0.0, "maxFloat": 0.5, "stattrak": false },
		        { "id": "a3", "name": "Rifle | Ash", "rarity": "classified", "minFloat": 0.1, "maxFloat": 0.7 },
		        { "id": "a4", "name": "Rifle | Ember", "rarity": "covert" },
		        { "id": "a5", "name": "Knife | Blaze", "rarity": "rare_special", "minFloat": 0.0, "maxFloat": 1.0 }
		      ]
		    }
		  ]
		}
		""";

	[Fact]
	public void DefaultCatalog_ShipsTwoIllustrativeCases_WithEveryTier()
	{
		Assert.Equal(2, CaseCatalog.Default.Cases.Count);
		Assert.All(CaseCatalog.Default.Cases, definition =>
		{
			Assert.Equal(5, definition.ItemsByRarity.Count);
			Assert.All(definition.ItemsByRarity.Values, tier => Assert.NotEmpty(tier));
		});
	}

	[Fact]
	public void Find_IsCaseInsensitive_AndUnknownReturnsNull()
	{
		Assert.NotNull(CaseCatalog.Default.Find("VAPOR.CASES.STARTER"));
		Assert.Equal("vapor.cases.starter", CaseCatalog.Default.Find(" vapor.cases.starter ")!.Id);
		Assert.Null(CaseCatalog.Default.Find("nope.case"));
	}

	[Fact]
	public void Find_RejectsBlankIds()
	{
		Assert.Throws<ArgumentException>(() => CaseCatalog.Default.Find(" "));
	}

	[Fact]
	public void ParseJson_AcceptsFullSchema_WithDefaults()
	{
		CaseCatalog catalog = CaseCatalog.ParseJson(ValidCatalog);
		CaseDefinition definition = Assert.Single(catalog.Cases);

		Assert.Equal("acme.case1", definition.Id);
		Assert.Equal(5, definition.Items.Count);

		CaseItem milspec = definition.Items[0];
		Assert.Equal(0.06, milspec.MinFloat);
		Assert.Equal(0.80, milspec.MaxFloat);
		Assert.True(milspec.StatTrakAllowed);

		CaseItem restricted = definition.Items[1];
		Assert.Equal(0.0, restricted.MinFloat);
		Assert.Equal(0.5, restricted.MaxFloat);
		Assert.False(restricted.StatTrakAllowed);
	}

	[Theory]
	[InlineData("""{ "cases": [] }""", "non-empty 'cases'")]
	[InlineData("null", "non-empty 'cases'")]
	[InlineData("""{ }""", "non-empty 'cases'")]
	[InlineData("""{ "cases": [ { "name": "No Id", "items": [] } ] }""", "non-empty 'id'")]
	[InlineData("""{ "cases": [ { "id": "c", "items": [] } ] }""", "non-empty 'name'")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [] } ] }""", "has no items")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "name": "No Id", "rarity": "milspec" } ] } ] }""", "without an 'id'")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "id": "i", "rarity": "milspec" } ] } ] }""", "has no 'name'")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "id": "i", "name": "N", "rarity": "legendary" } ] } ] }""", "unknown rarity")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "id": "i", "name": "N", "rarity": "milspec", "minFloat": 0.7, "maxFloat": 0.2 } ] } ] }""", "invalid float range")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "id": "i", "name": "N", "rarity": "milspec", "minFloat": -0.1 } ] } ] }""", "invalid float range")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "id": "i", "name": "N", "rarity": "milspec", "maxFloat": 1.5 } ] } ] }""", "invalid float range")]
	[InlineData("""{ "cases": [ { "id": "c", "name": "C", "items": [ { "id": "i", "name": "N", "rarity": "milspec", "minFloat": 0.2, "maxFloat": 0.2 } ] } ] }""", "invalid float range")]
	public void ParseJson_RejectsInvalidDocuments(string json, string expectedFragment)
	{
		var error = Assert.Throws<FormatException>(() => CaseCatalog.ParseJson(json));
		Assert.Contains(expectedFragment, error.Message);
	}

	[Fact]
	public void ParseJson_RejectsMissingTier()
	{
		const string json = """
			{ "cases": [ { "id": "c", "name": "C", "items": [
			  { "id": "i", "name": "N", "rarity": "milspec" } ] } ] }
			""";

		var error = Assert.Throws<FormatException>(() => CaseCatalog.ParseJson(json));
		Assert.Contains("missing its restricted tier", error.Message);
	}

	[Fact]
	public void ParseJson_RejectsDuplicateCaseIds_AndDuplicateItemIds()
	{
		const string duplicateCases = """
			{ "cases": [
			  { "id": "c", "name": "C", "items": [
			    { "id": "i1", "name": "N", "rarity": "milspec" },
			    { "id": "i2", "name": "N", "rarity": "restricted" },
			    { "id": "i3", "name": "N", "rarity": "classified" },
			    { "id": "i4", "name": "N", "rarity": "covert" },
			    { "id": "i5", "name": "N", "rarity": "rare_special" } ] },
			  { "id": "C", "name": "C2", "items": [
			    { "id": "j1", "name": "N", "rarity": "milspec" },
			    { "id": "j2", "name": "N", "rarity": "restricted" },
			    { "id": "j3", "name": "N", "rarity": "classified" },
			    { "id": "j4", "name": "N", "rarity": "covert" },
			    { "id": "j5", "name": "N", "rarity": "rare_special" } ] } ] }
			""";
		Assert.Contains("duplicate case id", Assert.Throws<FormatException>(() => CaseCatalog.ParseJson(duplicateCases)).Message);

		const string duplicateItems = """
			{ "cases": [ { "id": "c", "name": "C", "items": [
			    { "id": "i1", "name": "N", "rarity": "milspec" },
			    { "id": "I1", "name": "N", "rarity": "restricted" },
			    { "id": "i3", "name": "N", "rarity": "classified" },
			    { "id": "i4", "name": "N", "rarity": "covert" },
			    { "id": "i5", "name": "N", "rarity": "rare_special" } ] } ] }
			""";
		Assert.Contains("duplicate item id", Assert.Throws<FormatException>(() => CaseCatalog.ParseJson(duplicateItems)).Message);
	}

	[Fact]
	public void ParseJson_RejectsUnknownMembers()
	{
		const string json = """{ "cases": [ { "id": "c", "name": "C", "rairty": "typo", "items": [ { "id": "i", "name": "N", "rarity": "milspec" } ] } ] }""";

		Assert.Throws<FormatException>(() => CaseCatalog.ParseJson(json));
	}

	[Fact]
	public void ParseJson_RejectsMalformedJson()
	{
		var error = Assert.Throws<FormatException>(() => CaseCatalog.ParseJson("{ not json"));

		Assert.StartsWith("Invalid case catalog:", error.Message);
	}

	[Fact]
	public void ParseJson_RejectsEmptyInput()
	{
		Assert.Throws<ArgumentException>(() => CaseCatalog.ParseJson(string.Empty));
	}

	[Fact]
	public void LoadFile_ReadsAndParses_AndReportsUnreadablePaths()
	{
		string path = Path.Combine(Path.GetTempPath(), $"vapor-cases-{Guid.NewGuid():N}.json");
		try
		{
			File.WriteAllText(path, ValidCatalog);
			CaseDefinition definition = Assert.Single(CaseCatalog.LoadFile(path).Cases);
			Assert.Equal("acme.case1", definition.Id);

			var missing = Assert.Throws<FormatException>(() => CaseCatalog.LoadFile(path + ".missing"));
			Assert.Contains("could not be read", missing.Message);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Theory]
	[InlineData("milspec", CaseRarity.MilSpec)]
	[InlineData("MIL-SPEC", CaseRarity.MilSpec)]
	[InlineData("blue", CaseRarity.MilSpec)]
	[InlineData("Restricted", CaseRarity.Restricted)]
	[InlineData("purple", CaseRarity.Restricted)]
	[InlineData("classified", CaseRarity.Classified)]
	[InlineData("pink", CaseRarity.Classified)]
	[InlineData("Covert", CaseRarity.Covert)]
	[InlineData("red", CaseRarity.Covert)]
	[InlineData("rare_special", CaseRarity.RareSpecial)]
	[InlineData("rarespecial", CaseRarity.RareSpecial)]
	[InlineData("Rare-Special", CaseRarity.RareSpecial)]
	[InlineData("gold", CaseRarity.RareSpecial)]
	[InlineData("Knife", CaseRarity.RareSpecial)]
	public void TryParseRarity_AcceptsDocumentedAliases(string text, CaseRarity expected)
	{
		Assert.True(CaseDefinition.TryParseRarity(text, out CaseRarity rarity));
		Assert.Equal(expected, rarity);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("legendary")]
	[InlineData(" ")]
	public void TryParseRarity_RejectsUnknownNames(string? text)
	{
		Assert.False(CaseDefinition.TryParseRarity(text, out CaseRarity rarity));
		Assert.Equal(default, rarity);
	}

	[Fact]
	public void RarityName_RendersCanonicalJsonNames()
	{
		Assert.Equal("milspec", CaseDefinition.RarityName(CaseRarity.MilSpec));
		Assert.Equal("restricted", CaseDefinition.RarityName(CaseRarity.Restricted));
		Assert.Equal("classified", CaseDefinition.RarityName(CaseRarity.Classified));
		Assert.Equal("covert", CaseDefinition.RarityName(CaseRarity.Covert));
		Assert.Equal("rare_special", CaseDefinition.RarityName(CaseRarity.RareSpecial));
	}
}
