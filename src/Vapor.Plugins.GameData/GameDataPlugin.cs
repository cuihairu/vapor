using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Plugins.GameData;

/// <summary>
/// Read-only Valve game data from the official Steam Web API (the third game
/// plugin, closing the candidates registered in docs/game-plugins.md). Dota 2
/// match history, heroes and game items plus a TF2/CS2 item-schema summary —
/// every call is a key-authed GET against <c>api.steampowered.com</c>. There is
/// deliberately no write path and no game-client automation: real matches,
/// trading and battle-pass progress are a stated non-goal (SSA §4.C boundary,
/// see docs/game-plugins.md). The API key lives agent-side in the plugin
/// configuration (or environment) and never reaches the control plane; a
/// missing key fails actions with explicit guidance rather than faking data.
/// </summary>
public sealed class GameDataPlugin : IPlugin, IActionPlugin
{
	/// <summary>Configuration key for the Steam Web API key.</summary>
	public const string ApiKeyConfigKey = "webapi.key";

	/// <summary>Environment override for the Steam Web API key.</summary>
	public const string ApiKeyEnvVar = "VAPOR_GAME_DATA_WEBAPI_KEY";

	/// <summary>Upper bound on matches per history request (the Web API accepts more, but results stay compact).</summary>
	public const int MaxMatchesRequested = 100;

	/// <summary>Item-schema appids the schema action accepts (TF2, CS2).</summary>
	internal static readonly IReadOnlyList<int> SchemaAppIds = [440, 730];

	private readonly SteamWebApiClient? _clientOverride;

	private ILogger<GameDataPlugin>? _logger;
	private SteamWebApiClient? _client;

	/// <summary>Creates the plugin with default wiring.</summary>
	public GameDataPlugin()
	{
	}

	/// <summary>Test hook: injects a client instead of building one from the key.</summary>
	internal GameDataPlugin(SteamWebApiClient clientOverride)
	{
		_clientOverride = clientOverride;
	}

	public PluginInfo Info { get; } = new(
		Id: "vapor.game-data",
		Name: "Vapor Game Data",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Read-only Valve game data from the official Steam Web API (Dota 2 matches/heroes/items, TF2/CS2 schema summary). Key stays agent-side.");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		cancellationToken.ThrowIfCancellationRequested();

		_logger = context.Host.LoggerFactory.CreateLogger<GameDataPlugin>();

		string apiKey = context.Configuration.GetString(ApiKeyConfigKey, string.Empty, ApiKeyEnvVar).Trim();
		_client = _clientOverride ?? (apiKey.Length > 0 ? new SteamWebApiClient(apiKey) : null);

		_logger.LogInformation(
			"Game data started: Steam Web API key {KeyState}, actions are read-only",
			_client is null ? "not configured (actions will fail with guidance)" : "configured");

		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// The override client belongs to the test that injected it; only a
		// self-built client is disposed here so the load context can unload.
		if (_clientOverride is null)
		{
			_client?.Dispose();
		}

		_client = null;
		return Task.CompletedTask;
	}

	public IEnumerable<IAction> GetActions()
	{
		yield return new Dota2MatchHistoryAction(this);
		yield return new Dota2HeroesAction(this);
		yield return new Dota2GameItemsAction(this);
		yield return new EconItemSchemaAction(this);
	}

	internal SteamWebApiClient? Client => _client;

	/// <summary>The explicit failure every action reports when no key is configured.</summary>
	internal static string MissingKeyError =>
		"Steam Web API key is not configured: set 'webapi.key' in the plugin configuration or the VAPOR_GAME_DATA_WEBAPI_KEY environment variable.";

	/// <summary>
	/// Shared execution path for every action: the missing-key gate, the GET
	/// through the Web API client, and the two failure shapes that become
	/// failed results instead of thrown exceptions.
	/// </summary>
	internal async Task<ActionResult> FetchAsync(
		string operation,
		string path,
		IReadOnlyList<(string Name, string Value)> query,
		Func<JsonElement, Dictionary<string, object?>> project,
		CancellationToken cancellationToken)
	{
		SteamWebApiClient? client = _client;
		if (client is null)
		{
			return new ActionResult(false, MissingKeyError);
		}

		try
		{
			using JsonDocument document = await client.GetJsonAsync(path, query, cancellationToken).ConfigureAwait(false);
			return new ActionResult(true, null, project(document.RootElement));
		}
		catch (HttpRequestException ex)
		{
			return new ActionResult(false, $"{operation} failed: {ex.Message}");
		}
		catch (JsonException ex)
		{
			return new ActionResult(false, $"{operation} returned malformed JSON: {ex.Message}");
		}
	}

	/// <summary>Reads an integer property, tolerating missing or non-number values.</summary>
	internal static int IntOrDefault(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt32(out int parsed)
				? parsed
				: 0;
	}

	/// <summary>
	/// Reads a 64-bit integer property (Dota 2 match ids exceed int32),
	/// tolerating missing or non-number values.
	/// </summary>
	internal static long LongOrDefault(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt64(out long parsed)
				? parsed
				: 0L;
	}

	/// <summary>Reads a string property, tolerating missing or non-string values.</summary>
	internal static string TextOrDefault(JsonElement element, string property)
	{
		return element.ValueKind == JsonValueKind.Object
			&& element.TryGetProperty(property, out JsonElement value)
			&& value.ValueKind == JsonValueKind.String
				? value.GetString()! // String kind never yields null.
				: string.Empty;
	}

	/// <summary>
	/// Shared projection for the Web API <c>{"result": {..., "&lt;array&gt;": [...]}}</c>
	/// envelope: extracts the item array under the given property and reports
	/// status plus count. A missing or non-object <c>result</c> yields status 0
	/// and an empty list instead of throwing.
	/// </summary>
	internal static Dictionary<string, object?> ProjectItemsResult(
		JsonElement root,
		string arrayProperty,
		Func<JsonElement, Dictionary<string, object?>> projectItem)
	{
		JsonElement result = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out JsonElement found)
			? found
			: default;

		var items = new List<Dictionary<string, object?>>();
		if (result.ValueKind == JsonValueKind.Object
			&& result.TryGetProperty(arrayProperty, out JsonElement arrayElement)
			&& arrayElement.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in arrayElement.EnumerateArray())
			{
				items.Add(projectItem(item));
			}
		}

		return new Dictionary<string, object?>
		{
			["status"] = GameDataPlugin.IntOrDefault(result, "status"),
			["count"] = items.Count,
			["items"] = items
		};
	}
}

/// <summary>
/// The <c>dota2_match_history</c> action: fetches public Dota 2 match history
/// (IDOTA2Match_570/GetMatchHistory) with optional hero/game-mode filters and a
/// clamped result count, projected to a compact match list.
/// </summary>
public sealed class Dota2MatchHistoryAction : IAction
{
	private const string Operation = "Dota 2 match history";
	private const string Path = "IDOTA2Match_570/GetMatchHistory/v1/";

	private readonly GameDataPlugin _plugin;

	/// <summary>Creates the action bound to its owning plugin.</summary>
	public Dota2MatchHistoryAction(GameDataPlugin plugin)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
	}

	public string Name => "dota2_match_history";

	public ActionMetadata Metadata { get; } = new(
		Name: "dota2_match_history",
		Description: "Fetches public Dota 2 match history from the Steam Web API (optional heroId/gameMode filters, up to 100 matches). Read-only.",
		RequiresLogin: false,
		TimeoutSeconds: 30);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		var query = new List<(string Name, string Value)>();
		if (PayloadReader.GetInt32(payload, "heroId") is int heroId)
		{
			query.Add(("hero_id", heroId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		if (PayloadReader.GetInt32(payload, "gameMode") is int gameMode)
		{
			query.Add(("game_mode", gameMode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		if (PayloadReader.GetInt32(payload, "matchesRequested") is int requested)
		{
			query.Add(("matches_requested", Math.Clamp(requested, 1, GameDataPlugin.MaxMatchesRequested)
				.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		return await _plugin.FetchAsync(Operation, Path, query, Project, cancellationToken).ConfigureAwait(false);
	}

	internal static Dictionary<string, object?> Project(JsonElement root)
	{
		JsonElement result = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out JsonElement found)
			? found
			: default;

		var matches = new List<Dictionary<string, object?>>();
		if (result.ValueKind == JsonValueKind.Object
			&& result.TryGetProperty("matches", out JsonElement matchesElement)
			&& matchesElement.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement match in matchesElement.EnumerateArray())
			{
				matches.Add(new Dictionary<string, object?>
				{
					["matchId"] = GameDataPlugin.LongOrDefault(match, "match_id"),
					["matchSeqNum"] = GameDataPlugin.LongOrDefault(match, "match_seq_num"),
					["startTime"] = GameDataPlugin.IntOrDefault(match, "start_time"),
					["lobbyType"] = GameDataPlugin.IntOrDefault(match, "lobby_type"),
					["gameMode"] = GameDataPlugin.IntOrDefault(match, "game_mode")
				});
			}
		}

		return new Dictionary<string, object?>
		{
			["status"] = GameDataPlugin.IntOrDefault(result, "status"),
			["matchCount"] = GameDataPlugin.IntOrDefault(result, "num_results"),
			["totalResults"] = GameDataPlugin.IntOrDefault(result, "total_results"),
			["matches"] = matches
		};
	}
}

/// <summary>
/// The <c>dota2_heroes</c> action: fetches the Dota 2 hero catalog
/// (IEconDOTA2_570/GetHeroes) projected to id/name/localizedName/legs.
/// </summary>
public sealed class Dota2HeroesAction : IAction
{
	private const string Operation = "Dota 2 heroes";
	private const string Path = "IEconDOTA2_570/GetHeroes/v1/";

	private readonly GameDataPlugin _plugin;

	/// <summary>Creates the action bound to its owning plugin.</summary>
	public Dota2HeroesAction(GameDataPlugin plugin)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
	}

	public string Name => "dota2_heroes";

	public ActionMetadata Metadata { get; } = new(
		Name: "dota2_heroes",
		Description: "Fetches the Dota 2 hero catalog from the Steam Web API. Read-only.",
		RequiresLogin: false,
		TimeoutSeconds: 30);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		return _plugin.FetchAsync(Operation, Path, [], Project, cancellationToken);
	}

	internal static Dictionary<string, object?> Project(JsonElement root)
	{
		return GameDataPlugin.ProjectItemsResult(root, "heroes", hero => new Dictionary<string, object?>
		{
			["id"] = GameDataPlugin.IntOrDefault(hero, "id"),
			["name"] = GameDataPlugin.TextOrDefault(hero, "name"),
			["localizedName"] = GameDataPlugin.TextOrDefault(hero, "localized_name"),
			["legs"] = GameDataPlugin.IntOrDefault(hero, "legs")
		});
	}
}

/// <summary>
/// The <c>dota2_game_items</c> action: fetches the Dota 2 item catalog
/// (IEconDOTA2_570/GetGameItems) projected to id/name/localizedName/cost.
/// </summary>
public sealed class Dota2GameItemsAction : IAction
{
	private const string Operation = "Dota 2 game items";
	private const string Path = "IEconDOTA2_570/GetGameItems/v1/";

	private readonly GameDataPlugin _plugin;

	/// <summary>Creates the action bound to its owning plugin.</summary>
	public Dota2GameItemsAction(GameDataPlugin plugin)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
	}

	public string Name => "dota2_game_items";

	public ActionMetadata Metadata { get; } = new(
		Name: "dota2_game_items",
		Description: "Fetches the Dota 2 item catalog from the Steam Web API. Read-only.",
		RequiresLogin: false,
		TimeoutSeconds: 30);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		return _plugin.FetchAsync(Operation, Path, [], Project, cancellationToken);
	}

	internal static Dictionary<string, object?> Project(JsonElement root)
	{
		return GameDataPlugin.ProjectItemsResult(root, "items", item => new Dictionary<string, object?>
		{
			["id"] = GameDataPlugin.IntOrDefault(item, "id"),
			["name"] = GameDataPlugin.TextOrDefault(item, "name"),
			["localizedName"] = GameDataPlugin.TextOrDefault(item, "localized_name"),
			["cost"] = GameDataPlugin.IntOrDefault(item, "cost")
		});
	}
}

/// <summary>
/// The <c>econ_item_schema</c> action: fetches a TF2 (440) or CS2 (730) item
/// schema summary (IEconItems_{appid}/GetSchema). Only a status/count digest is
/// returned — the full schema is megabytes and stays at the Web API.
/// </summary>
public sealed class EconItemSchemaAction : IAction
{
	private const string Operation = "Item schema";

	private readonly GameDataPlugin _plugin;

	/// <summary>Creates the action bound to its owning plugin.</summary>
	public EconItemSchemaAction(GameDataPlugin plugin)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
	}

	public string Name => "econ_item_schema";

	public ActionMetadata Metadata { get; } = new(
		Name: "econ_item_schema",
		Description: "Fetches a TF2 (440) or CS2 (730) item-schema summary (status and item count) from the Steam Web API. Read-only.",
		RequiresLogin: false,
		TimeoutSeconds: 60);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		int? appid = PayloadReader.GetInt32(payload, "appid");
		if (appid is null)
		{
			return new ActionResult(false, "payload needs an integer 'appid' (440 = TF2, 730 = CS2)");
		}

		if (!GameDataPlugin.SchemaAppIds.Contains(appid.Value))
		{
			return new ActionResult(
				false,
				$"appid {appid.Value} is not supported: only 440 (TF2) and 730 (CS2) have an IEconItems schema");
		}

		return await _plugin
			.FetchAsync(
				Operation,
				$"IEconItems_{appid.Value}/GetSchema/v1/",
				[],
				root => Project(root, appid.Value),
				cancellationToken)
			.ConfigureAwait(false);
	}

	internal static Dictionary<string, object?> Project(JsonElement root, int appid)
	{
		Dictionary<string, object?> projected = GameDataPlugin.ProjectItemsResult(root, "items", item => new Dictionary<string, object?>
		{
			["defIndex"] = GameDataPlugin.IntOrDefault(item, "defindex"),
			["name"] = GameDataPlugin.TextOrDefault(item, "name")
		});

		projected["appId"] = appid;
		return projected;
	}
}
