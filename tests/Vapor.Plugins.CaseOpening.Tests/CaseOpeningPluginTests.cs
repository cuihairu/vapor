using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Plugins.CaseOpening.Tests;

/// <summary>
/// Plugin surface: real host load through PluginManager (discovery, trust,
/// permission grants, contributions), configuration handling, the case_open
/// action, the four web routes and the backend seam.
/// </summary>
public sealed class CaseOpeningPluginTests : IDisposable
{
	private readonly string _root;

	public CaseOpeningPluginTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "vapor-caseopening-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "case-opening"));

		string binDirectory = AppContext.BaseDirectory;
		File.Copy(
			Path.Combine(binDirectory, "Vapor.Plugins.CaseOpening.dll"),
			Path.Combine(_root, "case-opening", "Vapor.Plugins.CaseOpening.dll"));
		File.Copy(
			Path.Combine(binDirectory, "plugin.json"),
			Path.Combine(_root, "case-opening", "plugin.json"));
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
		{
			// Best-effort cleanup; the load context may still hold the assembly.
		}
	}

	[Fact]
	public async Task HostLoadsPlugin_WithDeclaredPermissionsAndContributions()
	{
		await using var manager = new PluginManager(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance);

		PluginLoadReport report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Failures);
		LoadedPlugin plugin = Assert.Single(report.Loaded);

		Assert.Equal("vapor.caseopening", plugin.Descriptor.Manifest.Id);
		Assert.Equal(PluginTrust.Official, plugin.Descriptor.Trust);
		Assert.Equal([PluginPermissions.Actions, PluginPermissions.Web], plugin.GrantedPermissions.Order().ToArray());

		Assert.Equal(["case_open"], plugin.Actions.Select(static action => action.Name).Order().ToArray());
		Assert.Equal(4, plugin.Routes.Count);

		await plugin.Instance.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task InitializeAsync_RejectsUnknownBackends_Loudly()
	{
		var plugin = new CaseOpeningPlugin();

		var error = await Assert.ThrowsAsync<PluginException>(() =>
			plugin.InitializeAsync(TestContext(("backend", "steam-web")), CancellationToken.None));

		Assert.Contains("only 'simulation' (dry-run) exists", error.Message);
		Assert.Contains("docs/plugins.md", error.Message);
	}

	[Fact]
	public async Task InitializeAsync_LoadsCatalogAndArchive_FromConfiguration()
	{
		string catalogPath = Path.Combine(_root, "custom-catalog.json");
		string archivePath = Path.Combine(_root, "archive", "results.jsonl");
		await File.WriteAllTextAsync(catalogPath, MinimalCatalog("custom.case"));

		var plugin = new CaseOpeningPlugin();
		await plugin.InitializeAsync(
			TestContext(
				("cases.path", catalogPath),
				("results.path", archivePath)),
			CancellationToken.None);

		Assert.NotNull(plugin.Catalog.Find("custom.case"));
		Assert.Null(plugin.Catalog.Find("vapor.cases.starter")); // replaces the default
		Assert.Equal("dry-run", plugin.Backend.ModeName);

		CaseOpeningPlugin.CaseOpenBatch? batch = await plugin.OpenAsync("custom.case", 3, CancellationToken.None);
		Assert.NotNull(batch);
		Assert.Equal(3, plugin.Store.Count);
		Assert.True(File.Exists(archivePath));
	}

	[Fact]
	public async Task InitializeAsync_FailsOnUnreadableCatalogPath()
	{
		var plugin = new CaseOpeningPlugin();

		var error = await Assert.ThrowsAsync<PluginException>(() =>
			plugin.InitializeAsync(TestContext(("cases.path", Path.Combine(_root, "missing.json"))), CancellationToken.None));

		Assert.Contains("could not be read", error.Message);
	}

	[Fact]
	public async Task InitializeAsync_UsesInjectedBackend_WhenOverridden()
	{
		var backend = new RecordingBackend();
		var plugin = new CaseOpeningPlugin(backend);

		await plugin.InitializeAsync(TestContext(), CancellationToken.None);

		Assert.Same(backend, plugin.Backend);
	}

	[Fact]
	public async Task OpenAsync_RejectsBlankCaseIds()
	{
		var plugin = InitializedPlugin();

		await Assert.ThrowsAsync<ArgumentException>(() => plugin.OpenAsync(" ", 1, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => plugin.OpenAsync("", 1, CancellationToken.None));
	}

	[Fact]
	public async Task OpenAsync_ReturnsNull_ForUnknownCases_AndClampsCount()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();

		Assert.Null(await plugin.OpenAsync("nope.case", 1, CancellationToken.None));

		CaseOpeningPlugin.CaseOpenBatch batch = (await plugin.OpenAsync("vapor.cases.starter", 100_000, CancellationToken.None))!;
		Assert.Equal(CaseOpeningPlugin.MaxOpensPerRequest, batch.Results.Count);
	}

	[Fact]
	public async Task CaseOpenAction_HappyPath_ReportsModeCountsAndRecords()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		var action = new CaseOpenAction(plugin);

		ActionResult result = await action.ExecuteAsync(
			null!,
			new Dictionary<string, object?> { ["caseId"] = "vapor.cases.golden", ["count"] = 5 },
			CancellationToken.None);

		Assert.True(result.Success);
		string payload = JsonSerializer.Serialize(result.Output!["payload"]);
		Assert.Contains("\"mode\":\"dry-run\"", payload);
		Assert.Contains("\"opened\":5", payload);
		Assert.Contains("\"caseId\":\"vapor.cases.golden\"", payload);
		Assert.Equal(5, plugin.Store.Count);
	}

	[Fact]
	public async Task CaseOpenAction_MissingOrUnknownCaseId_Fails()
	{
		var action = new CaseOpenAction(InitializedPlugin());

		ActionResult missing = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CancellationToken.None);
		Assert.False(missing.Success);
		Assert.Contains("'caseId'", missing.Error);

		ActionResult wrongType = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = 42 }, CancellationToken.None);
		Assert.False(wrongType.Success);
		Assert.Contains("'caseId'", wrongType.Error);

		ActionResult blank = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = "  " }, CancellationToken.None);
		Assert.False(blank.Success);

		ActionResult unknown = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = "ghost.case" }, CancellationToken.None);
		Assert.False(unknown.Success);
		Assert.Contains("unknown case 'ghost.case'", unknown.Error);
	}

	[Fact]
	public async Task CaseOpenAction_CoercesCountPayloadShapes()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		var action = new CaseOpenAction(plugin);
		using JsonDocument number = JsonDocument.Parse("2");

		Dictionary<string, object?>[] payloads =
		[
			new Dictionary<string, object?> { ["caseId"] = "vapor.cases.starter", ["count"] = 3L },
			new Dictionary<string, object?> { ["caseId"] = "vapor.cases.starter", ["count"] = 4m },
			new Dictionary<string, object?> { ["caseId"] = "vapor.cases.starter", ["count"] = number.RootElement.Clone() },
			new Dictionary<string, object?> { ["caseId"] = "vapor.cases.starter", ["count"] = "5" },
			new Dictionary<string, object?> { ["caseId"] = BoxedJsonString("vapor.cases.starter") },
			new Dictionary<string, object?> { ["caseId"] = "vapor.cases.starter", ["count"] = 1.5 },
		];

		foreach (Dictionary<string, object?> payload in payloads)
		{
			ActionResult result = await action.ExecuteAsync(null!, payload, CancellationToken.None);
			Assert.True(result.Success, JsonSerializer.Serialize(payload));
		}

		// 3L + 4m + 2(json) + "5" + default (string caseId) + default (double
		// 1.5 is unparsable and keeps 1) = 16 recorded opens.
		Assert.Equal(16, plugin.Store.Count);
	}

	[Fact]
	public async Task GetCasesRoute_ListsCatalogWithTiers()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/cases");

		PluginWebResponse response = await route.Handler(Req("/cases"), CancellationToken.None);

		Assert.Equal(200, response.StatusCode);
		Assert.Contains("vapor.cases.starter", response.Body);
		Assert.Contains("vapor.cases.golden", response.Body);
		Assert.Contains("\"rarity\":\"milspec\"", response.Body);
	}

	[Theory]
	[InlineData(null, 400)]
	[InlineData("""{"caseId":"vapor.cases.starter","count":2}""", 200)]
	[InlineData("""{"caseId":"vapor.cases.starter","count":"many"}""", 400)]
	[InlineData("""{"count":2}""", 400)]
	[InlineData("""{"caseId":"ghost.case"}""", 404)]
	[InlineData("not json", 400)]
	[InlineData("", 400)]
	public async Task OpenRoute_ValidatesBody_AndOpensInDryRun(string? body, int expectedStatus)
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/open");

		PluginWebResponse response = await route.Handler(
			Req("/open", body: body),
			CancellationToken.None);

		Assert.Equal(expectedStatus, response.StatusCode);
		if (expectedStatus == 200)
		{
			Assert.Contains("\"mode\":\"dry-run\"", response.Body);
			Assert.Contains("\"opened\":", response.Body);
		}
	}

	[Fact]
	public async Task ResultsRoute_ServesRecentWindow_WithLimitClamping()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		foreach (int seed in Enumerable.Range(1, 4))
		{
			plugin.Store.Record(new OpenResult(
				"c", "i", "N", CaseRarity.MilSpec, false, 0.2, WearTier.FieldTested, seed));
		}

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/results");

		// Default limit (50) keeps all four seeds.
		PluginWebResponse all = await route.Handler(Req("/results"), CancellationToken.None);
		Assert.All(Enumerable.Range(1, 4), seed => Assert.Contains($"\"paintSeed\":{seed}", all.Body));

		PluginWebResponse limited = await route.Handler(Req("/results", query: ("limit", "2")), CancellationToken.None);
		Assert.Contains("\"paintSeed\":3", limited.Body);
		Assert.DoesNotContain("\"paintSeed\":2", limited.Body);

		PluginWebResponse clampedLow = await route.Handler(Req("/results", query: ("limit", "0")), CancellationToken.None);
		Assert.Contains("\"paintSeed\":4", clampedLow.Body);
		Assert.DoesNotContain("\"paintSeed\":3", clampedLow.Body);

		PluginWebResponse clampedHigh = await route.Handler(Req("/results", query: ("limit", "999999")), CancellationToken.None);
		Assert.Contains("\"paintSeed\":1", clampedHigh.Body);

		PluginWebResponse unparsable = await route.Handler(Req("/results", query: ("limit", "abc")), CancellationToken.None);
		Assert.Contains("\"paintSeed\":1", unparsable.Body);
	}

	[Fact]
	public async Task StatsRoute_ServesAggregates()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		plugin.Store.Record(new OpenResult(
			"c", "i", "N", CaseRarity.RareSpecial, true, 0.5, WearTier.BattleScarred, 1));

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/stats");
		PluginWebResponse response = await route.Handler(Req("/stats"), CancellationToken.None);

		Assert.Equal(200, response.StatusCode);
		Assert.Contains("\"totalOpens\":1", response.Body);
		Assert.Contains("\"caseId\":\"c\"", response.Body);
		Assert.Contains("\"rarity\":\"rare_special\"", response.Body);
	}

	[Fact]
	public async Task Routes_HonorCancellation()
	{
		CaseOpeningPlugin plugin = InitializedPlugin();
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		PluginWebRoute[] routes = [.. plugin.GetRoutes()];
		foreach (PluginWebRoute route in routes)
		{
			PluginWebRequest request = route.Path == "/open"
				? Req("/open", body: """{"caseId":"vapor.cases.starter"}""")
				: Req(route.Path);
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route.Handler(request, cancelled.Token));
		}
	}

	[Fact]
	public void PayloadRenderers_RejectNullInput()
	{
		Assert.Throws<ArgumentNullException>(() => CaseOpeningPlugin.ToBatchPayload(null!));
		Assert.Throws<ArgumentNullException>(() => CaseOpeningPlugin.ToStatsPayload(null!));
	}

	[Fact]
	public void ActionConstructor_RejectsNullPlugin()
	{
		Assert.Throws<ArgumentNullException>(() => new CaseOpenAction(null!));
	}

	[Fact]
	public async Task SimulationBackend_GuardsInputs_AndLoops()
	{
		var backend = new SimulationBackend();
		CaseDefinition definition = CaseCatalog.Default.Cases[0];

		Assert.Throws<ArgumentNullException>(() => new SimulationBackend(null!));
		OpenResult seeded = await new SimulationBackend(20260927).OpenAsync(definition, CancellationToken.None);
		Assert.Equal(definition.Id, seeded.CaseId);

		await Assert.ThrowsAsync<ArgumentNullException>(() => backend.OpenAsync(null!, CancellationToken.None));

		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => backend.OpenAsync(definition, cancelled.Token));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => backend.OpenManyAsync(definition, 3, cancelled.Token));

		await Assert.ThrowsAsync<ArgumentNullException>(() => backend.OpenManyAsync(null!, 1, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.OpenManyAsync(definition, 0, CancellationToken.None));

		IReadOnlyList<OpenResult> results = await backend.OpenManyAsync(definition, 3, CancellationToken.None);
		Assert.Equal(3, results.Count);
		Assert.All(results, result => Assert.Equal(definition.Id, result.CaseId));
	}

	private static PluginWebRequest Req(string path, (string Key, string Value)? query = null, string? body = null) =>
		new(
			Path: path,
			Query: query is null
				? new Dictionary<string, string>()
				: new Dictionary<string, string> { [query.Value.Key] = query.Value.Value },
			Headers: new Dictionary<string, string>(),
			Body: body);

	private static string MinimalCatalog(string caseId) => $$"""
		{ "cases": [ { "id": "{{caseId}}", "name": "Custom Case", "items": [
		  { "id": "x1", "name": "Pistol | X", "rarity": "milspec" },
		  { "id": "x2", "name": "SMG | X", "rarity": "restricted" },
		  { "id": "x3", "name": "Rifle | X", "rarity": "classified" },
		  { "id": "x4", "name": "Rifle | Y", "rarity": "covert" },
		  { "id": "x5", "name": "Knife | X", "rarity": "rare_special" } ] } ] }
		""";

	private CaseOpeningPlugin InitializedPlugin()
	{
		var plugin = new CaseOpeningPlugin();
		plugin.InitializeAsync(TestContext(), CancellationToken.None).GetAwaiter().GetResult();
		return plugin;
	}

	private static IPluginContext TestContext(params (string Key, string Value)[] configuration) => new TestPluginContext(
		configuration.ToDictionary(entry => entry.Key, entry => entry.Value));

	private sealed class TestPluginContext(IReadOnlyDictionary<string, string> configuration) : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			"vapor.caseopening", "Vapor Case Opening (test)", new Version(1, 0, 0), PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } =
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub());
	}

	/// <summary>Boxes a JSON string element, as a job payload would carry it.</summary>
	private static object BoxedJsonString(string value)
	{
		using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
		return document.RootElement.Clone();
	}

	/// <summary>Backend stub that never rolls (only proves override wiring).</summary>
	private sealed class RecordingBackend : ICaseOpeningBackend
	{
		public string ModeName => "recording";

		public Task<OpenResult> OpenAsync(CaseDefinition caseDefinition, CancellationToken cancellationToken) =>
			Task.FromResult(new OpenResult(caseDefinition.Id, "x", "N", CaseRarity.MilSpec, false, 0.2, WearTier.FieldTested, 0));

		public Task<IReadOnlyList<OpenResult>> OpenManyAsync(
			CaseDefinition caseDefinition,
			int count,
			CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<OpenResult>>(
				Enumerable.Range(0, count).Select(_ => new OpenResult(
					caseDefinition.Id, "x", "N", CaseRarity.MilSpec, false, 0.2, WearTier.FieldTested, 0)).ToArray());
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
