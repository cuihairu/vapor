using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Plugins.GameData.Tests;

/// <summary>
/// Plugin surface: real host load through PluginManager (manifest, trust,
/// actions-only permissions), key handling (config/env, lazy failure), the
/// four read-only actions over a scripted transport, and the projection
/// helpers' tolerance of degenerate Web API envelopes.
/// </summary>
public sealed class GameDataPluginTests : IDisposable
{
	private const string MatchHistoryBody = """
		{"result":{"status":1,"num_results":2,"total_results":100,"matches":[
		  {"match_id":7045869111,"match_seq_num":555111000,"start_time":1758900000,"lobby_type":7,"game_mode":22},
		  {"match_id":7045869112,"match_seq_num":555111001,"start_time":1758900600,"lobby_type":0,"game_mode":1}]}}
		""";

	private const string HeroesBody = """
		{"result":{"heroes":[
		  {"id":14,"name":"npc_dota_hero_pudge","localized_name":"Pudge","legs":2},
		  {"id":1,"name":"npc_dota_hero_antimage","localized_name":"Anti-Mage","legs":2}],
		  "status":1,"count":2}}
		""";

	private const string ItemsBody = """
		{"result":{"items":[
		  {"id":1,"name":"item_blink","localized_name":"Blink Dagger","cost":2250},
		  {"id":44,"name":"item_ward_observer","localized_name":"Observer Ward","cost":0}],
		  "status":1}}
		""";

	private const string SchemaBody = """
		{"result":{"status":1,"items":[
		  {"defindex":5021,"name":"Mann Co. Supply Crate Key"},
		  {"defindex":205,"name":"Scattergun"}]}}
		""";

	private readonly string _root;

	public GameDataPluginTests()
	{
		_root = Path.Combine(Path.GetTempPath(), "vapor-gamedata-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "game-data"));

		string binDirectory = AppContext.BaseDirectory;
		File.Copy(
			Path.Combine(binDirectory, "Vapor.Plugins.GameData.dll"),
			Path.Combine(_root, "game-data", "Vapor.Plugins.GameData.dll"));
		File.Copy(
			Path.Combine(binDirectory, "plugin.json"),
			Path.Combine(_root, "game-data", "plugin.json"));
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
	public async Task HostLoadsPlugin_WithActionsOnlyPermissions()
	{
		await using var manager = new PluginManager(
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub()),
			NullLoggerFactory.Instance);

		PluginLoadReport report = await manager.LoadAllAsync(_root);

		Assert.Empty(report.Failures);
		LoadedPlugin plugin = Assert.Single(report.Loaded);

		Assert.Equal("vapor.game-data", plugin.Descriptor.Manifest.Id);
		Assert.Equal(PluginTrust.Official, plugin.Descriptor.Trust);
		Assert.Equal([PluginPermissions.Actions], plugin.GrantedPermissions.Order().ToArray());
		Assert.Equal(
			["dota2_game_items", "dota2_heroes", "dota2_match_history", "econ_item_schema"],
			plugin.Actions.Select(static action => action.Name).Order().ToArray());
		Assert.Empty(plugin.Routes);

		await plugin.Instance.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public void Info_IdentifiesTheReadOnlyGameDataPlugin()
	{
		var plugin = new GameDataPlugin();

		Assert.Equal("vapor.game-data", plugin.Info.Id);
		Assert.Equal("Vapor Game Data", plugin.Info.Name);
		Assert.Equal(new Version(1, 0, 0), plugin.Info.Version);
		Assert.Equal(PluginApi.Current, plugin.Info.ApiVersion);
	}

	[Fact]
	public void GetActions_ExposesFourDistinctReadOnlyActions()
	{
		var plugin = new GameDataPlugin();

		IAction[] actions = plugin.GetActions().ToArray();

		Assert.Equal(4, actions.Length);
		Assert.All(actions, action =>
		{
			Assert.Equal(action.Name, action.Metadata.Name);
			Assert.False(action.Metadata.RequiresLogin);
		});
	}

	[Fact]
	public async Task InitializeAsync_GuardsNullContextAndCancelledToken()
	{
		var plugin = new GameDataPlugin();

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			plugin.InitializeAsync(null!, CancellationToken.None));

		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			plugin.InitializeAsync(TestContext(), cancelled.Token));
	}

	[Fact]
	public async Task ShutdownAsync_GuardsCancelledToken()
	{
		var plugin = new GameDataPlugin();
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			plugin.ShutdownAsync(cancelled.Token));
	}

	[Fact]
	public async Task InitializeAsync_BuildsClientFromConfiguredKey_AndShutdownDisposesIt()
	{
		var plugin = new GameDataPlugin();
		await plugin.InitializeAsync(TestContext(("webapi.key", " secret-key ")), CancellationToken.None);

		Assert.NotNull(plugin.Client);

		await plugin.ShutdownAsync(CancellationToken.None);
		Assert.Null(plugin.Client);
	}

	[Fact]
	public async Task InitializeAsync_EnvironmentVariableOverridesConfiguration()
	{
		var plugin = new GameDataPlugin();
		try
		{
			Environment.SetEnvironmentVariable(GameDataPlugin.ApiKeyEnvVar, "env-key");
			await plugin.InitializeAsync(TestContext(), CancellationToken.None);

			Assert.NotNull(plugin.Client);
		}
		finally
		{
			Environment.SetEnvironmentVariable(GameDataPlugin.ApiKeyEnvVar, null);
		}

		await plugin.ShutdownAsync(CancellationToken.None);
	}

	[Fact]
	public async Task InitializeAsync_KeepsInjectedClient_AndShutdownLeavesItUndisposed()
	{
		SteamWebApiClientTests.FakeSteamHandler handler = new();
		handler.Queue(System.Net.HttpStatusCode.OK, "{}");
		using var injected = new SteamWebApiClient("test-key", handler);
		var plugin = new GameDataPlugin(injected);

		await plugin.InitializeAsync(TestContext(), CancellationToken.None);

		Assert.Same(injected, plugin.Client);

		await plugin.ShutdownAsync(CancellationToken.None);
		Assert.Null(plugin.Client);

		// The injected client must still be usable after the plugin released it.
		using System.Text.Json.JsonDocument document = await injected.GetJsonAsync(
			"IEconDOTA2_570/GetHeroes/v1/", [], CancellationToken.None);
		Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
	}

	[Fact]
	public async Task AllActions_FailWithGuidance_WhenNoKeyIsConfigured()
	{
		var plugin = new GameDataPlugin();
		await plugin.InitializeAsync(TestContext(), CancellationToken.None);
		Assert.Null(plugin.Client);

		foreach (IAction action in plugin.GetActions())
		{
			ActionResult result = await ExecuteAsync(action, PayloadFor(action));
			Assert.False(result.Success, action.Name);
			Assert.Equal(GameDataPlugin.MissingKeyError, result.Error);
		}

		Assert.Contains("'webapi.key'", GameDataPlugin.MissingKeyError);
		Assert.Contains(GameDataPlugin.ApiKeyEnvVar, GameDataPlugin.MissingKeyError);
	}

	[Fact]
	public async Task Dota2MatchHistory_FetchesAndProjectsCompactMatches()
	{
		(GameDataPlugin plugin, SteamWebApiClientTests.FakeSteamHandler handler) = PluginWithScript(
			(System.Net.HttpStatusCode.OK, MatchHistoryBody));
		try
		{
			ActionResult result = await ExecuteAsync(
				new Dota2MatchHistoryAction(plugin),
				new Dictionary<string, object?> { ["heroId"] = 14, ["gameMode"] = 22, ["matchesRequested"] = 5 });

			Assert.True(result.Success, result.Error);
			Assert.Equal(
				"https://api.steampowered.com/IDOTA2Match_570/GetMatchHistory/v1/?key=test-key&hero_id=14&game_mode=22&matches_requested=5",
				handler.Requests[0].AbsoluteUri);

			Assert.Equal(1, result.Output!["status"]);
			Assert.Equal(2, result.Output["matchCount"]);
			Assert.Equal(100, result.Output["totalResults"]);

			var matches = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["matches"]);
			Assert.Equal(2, matches.Count);
			Assert.Equal(7045869111L, matches[0]["matchId"]);
			Assert.Equal(555111000L, matches[0]["matchSeqNum"]);
			Assert.Equal(1758900000, matches[0]["startTime"]);
			Assert.Equal(7, matches[0]["lobbyType"]);
			Assert.Equal(22, matches[0]["gameMode"]);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Dota2MatchHistory_ClampsRequestedCount_IgnoresUnparsableFilters()
	{
		(GameDataPlugin plugin, SteamWebApiClientTests.FakeSteamHandler handler) = PluginWithScript(
			(System.Net.HttpStatusCode.OK, MatchHistoryBody),
			(System.Net.HttpStatusCode.OK, MatchHistoryBody));
		try
		{
			ActionResult clamped = await ExecuteAsync(
				new Dota2MatchHistoryAction(plugin),
				new Dictionary<string, object?> { ["matchesRequested"] = 500 });

			Assert.True(clamped.Success, clamped.Error);
			Assert.Contains("matches_requested=100", handler.Requests[0].AbsoluteUri);

			ActionResult unparsable = await ExecuteAsync(
				new Dota2MatchHistoryAction(plugin),
				new Dictionary<string, object?>
				{
					["heroId"] = "many",
					["gameMode"] = true
				});

			Assert.True(unparsable.Success, unparsable.Error);
			Assert.EndsWith("?key=test-key", handler.Requests[1].AbsoluteUri);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Dota2Heroes_FetchesAndProjectsCatalog()
	{
		(GameDataPlugin plugin, SteamWebApiClientTests.FakeSteamHandler handler) = PluginWithScript(
			(System.Net.HttpStatusCode.OK, HeroesBody));
		try
		{
			ActionResult result = await ExecuteAsync(new Dota2HeroesAction(plugin), NoPayload);

			Assert.True(result.Success, result.Error);
			Assert.Equal(
				"https://api.steampowered.com/IEconDOTA2_570/GetHeroes/v1/?key=test-key",
				handler.Requests[0].AbsoluteUri);

			Assert.Equal(1, result.Output!["status"]);
			Assert.Equal(2, result.Output["count"]);

			var heroes = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
			Assert.Equal("Pudge", heroes[0]["localizedName"]);
			Assert.Equal("npc_dota_hero_pudge", heroes[0]["name"]);
			Assert.Equal(14, heroes[0]["id"]);
			Assert.Equal(2, heroes[0]["legs"]);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Dota2GameItems_FetchesAndProjectsCatalog()
	{
		(GameDataPlugin plugin, SteamWebApiClientTests.FakeSteamHandler handler) = PluginWithScript(
			(System.Net.HttpStatusCode.OK, ItemsBody));
		try
		{
			ActionResult result = await ExecuteAsync(new Dota2GameItemsAction(plugin), NoPayload);

			Assert.True(result.Success, result.Error);
			Assert.Equal(
				"https://api.steampowered.com/IEconDOTA2_570/GetGameItems/v1/?key=test-key",
				handler.Requests[0].AbsoluteUri);
			Assert.Equal(1, result.Output!["status"]);
			Assert.Equal(2, result.Output["count"]);

			var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
			Assert.Equal("Blink Dagger", items[0]["localizedName"]);
			Assert.Equal("item_blink", items[0]["name"]);
			Assert.Equal(1, items[0]["id"]);
			Assert.Equal(2250, items[0]["cost"]);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task EconItemSchema_RequiresAppid_AndRejectsUnsupportedGames()
	{
		(GameDataPlugin plugin, _) = PluginWithScript((System.Net.HttpStatusCode.OK, SchemaBody));
		try
		{
			ActionResult missing = await ExecuteAsync(new EconItemSchemaAction(plugin), NoPayload);
			Assert.False(missing.Success);
			Assert.Contains("'appid'", missing.Error);

			ActionResult unsupported = await ExecuteAsync(
				new EconItemSchemaAction(plugin),
				new Dictionary<string, object?> { ["appid"] = 570 });
			Assert.False(unsupported.Success);
			Assert.Contains("only 440 (TF2) and 730 (CS2)", unsupported.Error);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Theory]
	[InlineData(440, "IEconItems_440/GetSchema/v1/")]
	[InlineData(730, "IEconItems_730/GetSchema/v1/")]
	public async Task EconItemSchema_FetchesCompactDigest_ForSupportedAppids(int appid, string expectedPath)
	{
		(GameDataPlugin plugin, SteamWebApiClientTests.FakeSteamHandler handler) = PluginWithScript(
			(System.Net.HttpStatusCode.OK, SchemaBody));
		try
		{
			ActionResult result = await ExecuteAsync(
				new EconItemSchemaAction(plugin),
				new Dictionary<string, object?> { ["appid"] = appid });

			Assert.True(result.Success, result.Error);
			Assert.Equal($"https://api.steampowered.com/{expectedPath}?key=test-key", handler.Requests[0].AbsoluteUri);
			Assert.Equal(appid, result.Output!["appId"]);
			Assert.Equal(1, result.Output["status"]);
			Assert.Equal(2, result.Output["count"]);

			var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
			Assert.Equal(5021, items[0]["defIndex"]);
			Assert.Equal("Mann Co. Supply Crate Key", items[0]["name"]);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Actions_TranslateHttpFailuresIntoFailedResults()
	{
		(GameDataPlugin plugin, _) = PluginWithScript((System.Net.HttpStatusCode.InternalServerError, "boom"));
		try
		{
			ActionResult result = await ExecuteAsync(new Dota2HeroesAction(plugin), NoPayload);

			Assert.False(result.Success);
			Assert.Contains("Dota 2 heroes failed", result.Error);
			Assert.Contains("500 InternalServerError", result.Error);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Actions_TranslateMalformedJsonIntoFailedResults()
	{
		(GameDataPlugin plugin, _) = PluginWithScript((System.Net.HttpStatusCode.OK, "{\"result\": truncated"));
		try
		{
			ActionResult result = await ExecuteAsync(new Dota2GameItemsAction(plugin), NoPayload);

			Assert.False(result.Success);
			Assert.Contains("malformed JSON", result.Error);
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task Actions_PropagateCancellation()
	{
		(GameDataPlugin plugin, _) = PluginWithScript((System.Net.HttpStatusCode.OK, HeroesBody));
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();
		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				new Dota2HeroesAction(plugin).ExecuteAsync(null!, NoPayload, cancelled.Token));
		}
		finally
		{
			await plugin.ShutdownAsync(CancellationToken.None);
		}
	}

	[Fact]
	public void Projections_TolerateMissingAndWrongTypedProperties()
	{
		using JsonDocument document = JsonDocument.Parse("""
			{"number": 7, "fraction": 1.5, "text": "x", "numericText": "42", "object": {"n": 1}}
			""");
		JsonElement element = document.RootElement;

		Assert.Equal(7, GameDataPlugin.IntOrDefault(element, "number"));
		Assert.Equal(0, GameDataPlugin.IntOrDefault(element, "missing"));
		Assert.Equal(0, GameDataPlugin.IntOrDefault(element, "text"));
		Assert.Equal(0, GameDataPlugin.IntOrDefault(element, "object"));
		Assert.Equal(0, GameDataPlugin.IntOrDefault(element, "fraction"));

		Assert.Equal(7L, GameDataPlugin.LongOrDefault(element, "number"));
		Assert.Equal(0L, GameDataPlugin.LongOrDefault(element, "missing"));
		Assert.Equal(0L, GameDataPlugin.LongOrDefault(element, "text"));
		Assert.Equal(0L, GameDataPlugin.LongOrDefault(element, "object"));
		Assert.Equal(0L, GameDataPlugin.LongOrDefault(element, "fraction"));

		Assert.Equal("x", GameDataPlugin.TextOrDefault(element, "text"));
		Assert.Equal(string.Empty, GameDataPlugin.TextOrDefault(element, "missing"));
		Assert.Equal(string.Empty, GameDataPlugin.TextOrDefault(element, "number"));
		Assert.Equal(string.Empty, GameDataPlugin.TextOrDefault(element, "object"));
	}

	[Fact]
	public void ItemsProjection_ToleratesDegenerateEnvelopes()
	{
		Dictionary<string, object?> nonObjectRoot = GameDataPlugin.ProjectItemsResult(
			Parse("[1,2]"), "heroes", element => new Dictionary<string, object?>());
		Assert.Equal(0, nonObjectRoot["status"]);
		Assert.Equal(0, nonObjectRoot["count"]);

		Dictionary<string, object?> missingArray = GameDataPlugin.ProjectItemsResult(
			Parse("""{"result":{"status":31}}"""), "heroes", element => new Dictionary<string, object?>());
		Assert.Equal(31, missingArray["status"]);
		Assert.Equal(0, missingArray["count"]);

		Dictionary<string, object?> nonArrayProperty = GameDataPlugin.ProjectItemsResult(
			Parse("""{"result":{"status":1,"heroes":{"id":1}}}"""), "heroes", element => new Dictionary<string, object?>());
		Assert.Equal(1, nonArrayProperty["status"]);
		Assert.Equal(0, nonArrayProperty["count"]);
	}

	[Fact]
	public void MatchHistoryProjection_ToleratesDegenerateEnvelopes()
	{
		Dictionary<string, object?> nonObjectRoot = Dota2MatchHistoryAction.Project(Parse("\"nope\""));
		Assert.Equal(0, nonObjectRoot["status"]);
		Assert.Equal(0, nonObjectRoot["matchCount"]);
		Assert.Equal(0, nonObjectRoot["totalResults"]);
		Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(nonObjectRoot["matches"]));

		Dictionary<string, object?> missingMatches = Dota2MatchHistoryAction.Project(
			Parse("""{"result":{"status":1,"num_results":0,"total_results":0}}"""));
		Assert.Equal(1, missingMatches["status"]);
		Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(missingMatches["matches"]));

		Dictionary<string, object?> nonArrayMatches = Dota2MatchHistoryAction.Project(
			Parse("""{"result":{"status":1,"matches":{"match_id":1}}}"""));
		Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(nonArrayMatches["matches"]));
	}

	[Fact]
	public void ActionConstructors_RejectNullPlugin()
	{
		Assert.Throws<ArgumentNullException>(() => new Dota2MatchHistoryAction(null!));
		Assert.Throws<ArgumentNullException>(() => new Dota2HeroesAction(null!));
		Assert.Throws<ArgumentNullException>(() => new Dota2GameItemsAction(null!));
		Assert.Throws<ArgumentNullException>(() => new EconItemSchemaAction(null!));
	}

	private static readonly Dictionary<string, object?> NoPayload = new();

	private static JsonElement Parse(string json)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	private static Task<ActionResult> ExecuteAsync(IAction action, IReadOnlyDictionary<string, object?> payload) =>
		action.ExecuteAsync(null!, payload, CancellationToken.None);

	private static IReadOnlyDictionary<string, object?> PayloadFor(IAction action) => action.Name switch
	{
		"econ_item_schema" => new Dictionary<string, object?> { ["appid"] = 730 },
		_ => NoPayload
	};

	private (GameDataPlugin Plugin, SteamWebApiClientTests.FakeSteamHandler Handler) PluginWithScript(
		params (System.Net.HttpStatusCode Status, string Body)[] responses)
	{
		SteamWebApiClientTests.FakeSteamHandler handler = new();
		foreach ((System.Net.HttpStatusCode status, string body) in responses)
		{
			handler.Queue(status, body);
		}

		var client = new SteamWebApiClient("test-key", handler);
		var plugin = new GameDataPlugin(client);
		plugin.InitializeAsync(TestContext(), CancellationToken.None).GetAwaiter().GetResult();
		return (plugin, handler);
	}

	private static IPluginContext TestContext(params (string Key, string Value)[] configuration) => new TestPluginContext(
		configuration.ToDictionary(entry => entry.Key, entry => entry.Value));

	private sealed class TestPluginContext(IReadOnlyDictionary<string, string> configuration) : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			"vapor.game-data", "Vapor Game Data (test)", new Version(1, 0, 0), PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } =
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub());
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
