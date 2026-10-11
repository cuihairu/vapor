using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.CaseOpening;

/// <summary>
/// Dry-run case-opening simulator (the second official plugin). Rolls Valve's
/// published CS:GO/CS2 case odds, records every result to a JSONL archive plus
/// an in-memory window, and exposes catalog/open/results/stats through the
/// <c>case_open</c> action and four host-agnostic web routes. Beyond the dry
/// run it carries the local bookkeeping half: <c>case_inventory</c> classifies
/// a live CS2 backpack into cases/keys/other, and <c>case_consume</c> (gated,
/// default off) records what the operator physically consumed into a ledger.
/// Only the simulation backend exists — see <see cref="ICaseOpeningBackend"/>
/// and docs/plugins.md ("ToS boundary").
/// </summary>
public sealed class CaseOpeningPlugin : IPlugin, IActionPlugin, IWebApiPlugin
{
	/// <summary>The only accepted backend value ("dry-run" simulation).</summary>
	public const string DefaultBackend = "simulation";

	/// <summary>Configuration key for the backend selection.</summary>
	public const string BackendConfigKey = "backend";

	/// <summary>Configuration key for the optional catalog file path.</summary>
	public const string CasesPathConfigKey = "cases.path";

	/// <summary>Configuration key for the optional results archive path.</summary>
	public const string ResultsPathConfigKey = "results.path";

	/// <summary>
	/// Configuration key for the consumption-ledger gate. <c>false</c> by
	/// default and hard-required to stay that way: enabling it only allows
	/// recording ledger entries — it never grants any ability to open cases.
	/// </summary>
	public const string ConsumeEnabledConfigKey = "consume.enabled";

	/// <summary>Environment override for <see cref="ConsumeEnabledConfigKey"/>.</summary>
	public const string ConsumeEnabledEnvironmentVariable = "VAPOR_CASEOPENING_CONSUME_ENABLED";

	/// <summary>Configuration key for the optional consumption-ledger file path.</summary>
	public const string LedgerPathConfigKey = "ledger.path";

	/// <summary>Upper bound on opens per request (action or route).</summary>
	public const int MaxOpensPerRequest = 100;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	private readonly ICaseOpeningBackend? _backendOverride;
	private readonly Func<SteamWebHandler, ISteamTradeClient>? _tradeClientFactory;

	private ILogger<CaseOpeningPlugin>? _logger;
	private CaseCatalog _catalog = CaseCatalog.Default;
	private ICaseOpeningBackend _backend = new SimulationBackend();
	private CaseOpeningStore _store = new();
	private bool _consumeEnabled;
	private CaseLedger _ledger = new();

	/// <summary>Creates the plugin with default wiring.</summary>
	public CaseOpeningPlugin()
	{
	}

	/// <summary>Test hook: injects a backend and/or a trade client factory instead of the default wiring.</summary>
	internal CaseOpeningPlugin(
		ICaseOpeningBackend? backendOverride = null,
		Func<SteamWebHandler, ISteamTradeClient>? tradeClientFactory = null)
	{
		_backendOverride = backendOverride;
		_tradeClientFactory = tradeClientFactory;
	}

	public PluginInfo Info { get; } = new(
		Id: "vapor.caseopening",
		Name: "Vapor Case Opening",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Dry-run CS:GO/CS2 case-opening simulator over Valve's published odds; records every result. No Steam side effects.");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		cancellationToken.ThrowIfCancellationRequested();

		_logger = context.Host.LoggerFactory.CreateLogger<CaseOpeningPlugin>();

		string backendName = context.Configuration.GetString(BackendConfigKey, DefaultBackend, "VAPOR_CASEOPENING_BACKEND")
			.Trim().ToLowerInvariant();
		if (!string.Equals(backendName, DefaultBackend, StringComparison.Ordinal))
		{
			throw new PluginException(
				$"Case opening backend '{backendName}' is not supported: only '{DefaultBackend}' (dry-run) exists. " +
				"Real case opening is a CS2 game-client transaction and is deliberately not implemented — see docs/plugins.md (ToS boundary).");
		}

		string casesPath = context.Configuration.GetString(CasesPathConfigKey, string.Empty, "VAPOR_CASEOPENING_CASES_PATH").Trim();
		try
		{
			_catalog = casesPath.Length > 0 ? CaseCatalog.LoadFile(casesPath) : CaseCatalog.Default;
		}
		catch (FormatException ex)
		{
			throw new PluginException(ex.Message, ex);
		}

		string resultsPath = context.Configuration.GetString(ResultsPathConfigKey, string.Empty, "VAPOR_CASEOPENING_RESULTS_PATH").Trim();
		_store = resultsPath.Length > 0 ? new CaseOpeningStore(resultsPath) : new CaseOpeningStore();

		// The consume gate defaults to off and reads like every other knob —
		// but it is the only one whose "on" state must never imply a Steam side
		// effect: it merely unlocks ledger bookkeeping (see docs/plugins.md).
		_consumeEnabled = context.Configuration.GetBool(
			ConsumeEnabledConfigKey, fallback: false, ConsumeEnabledEnvironmentVariable);

		string ledgerPath = context.Configuration.GetString(LedgerPathConfigKey, string.Empty, "VAPOR_CASEOPENING_LEDGER_PATH").Trim();
		_ledger = ledgerPath.Length > 0 ? new CaseLedger(ledgerPath) : new CaseLedger();

		_backend = _backendOverride ?? new SimulationBackend();

		_logger.LogInformation(
			"Case opening started: backend {Backend}, {CaseCount} case(s), results archive {Results}, consume {Consume}, ledger {Ledger}",
			_backend.ModeName, _catalog.Cases.Count, resultsPath.Length > 0 ? resultsPath : "disabled",
			_consumeEnabled ? "enabled" : "disabled", ledgerPath.Length > 0 ? ledgerPath : "disabled");

		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		// Nothing to release: the engine and store are stateless w.r.t. the
		// load context, and every record was appended at open time.
		return Task.CompletedTask;
	}

	public IEnumerable<IAction> GetActions()
	{
		yield return new CaseOpenAction(this);
		yield return new CaseInventoryAction(this);
		yield return new CaseConsumeAction(this);
	}

	public IEnumerable<PluginWebRoute> GetRoutes()
	{
		yield return new PluginWebRoute("GET", "/cases", HandleListCasesAsync);
		yield return new PluginWebRoute("POST", "/open", HandleOpenAsync);
		yield return new PluginWebRoute("GET", "/results", HandleResultsAsync);
		yield return new PluginWebRoute("GET", "/stats", HandleStatsAsync);
		yield return new PluginWebRoute("GET", "/inventory", HandleInventoryAsync);
		yield return new PluginWebRoute("POST", "/consume", HandleConsumeAsync);
	}

	internal CaseCatalog Catalog => _catalog;

	internal CaseOpeningStore Store => _store;

	internal ICaseOpeningBackend Backend => _backend;

	/// <summary>The consumption ledger (totals feed case_inventory reconciliation).</summary>
	internal CaseLedger Ledger => _ledger;

	/// <summary>Whether consumption bookkeeping is unlocked (never a Steam capability).</summary>
	internal bool ConsumeEnabled => _consumeEnabled;

	/// <summary>Builds a trade client for a web session (injectable for tests).</summary>
	internal Func<SteamWebHandler, ISteamTradeClient> TradeClientFactory =>
		_tradeClientFactory ?? (webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance));

	/// <summary>
	/// Opens a case, records the results and returns a batch summary; null when
	/// the case id is unknown.
	/// </summary>
	internal async Task<CaseOpenBatch?> OpenAsync(string caseId, int count, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

		CaseDefinition? definition = _catalog.Find(caseId);
		if (definition is null)
		{
			return null;
		}

		int clamped = Math.Clamp(count, 1, MaxOpensPerRequest);
		IReadOnlyList<OpenResult> results = await _backend.OpenManyAsync(definition, clamped, cancellationToken).ConfigureAwait(false);
		foreach (OpenResult result in results)
		{
			_store.Record(result);
		}

		return new CaseOpenBatch(_backend.ModeName, definition.Id, results);
	}

	/// <summary>
	/// Records one consumption into the ledger and returns it; null when the
	/// case id is unknown. Counts clamp to 1..MaxOpensPerRequest like
	/// OpenAsync, and keys default to the case count (one key per case).
	/// Callers must check <see cref="ConsumeEnabled"/> first — this method is
	/// pure bookkeeping and does not re-check the gate.
	/// </summary>
	internal ConsumeEntry? Consume(string caseId, int count, int? keys, IReadOnlyList<string>? awards)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

		CaseDefinition? definition = _catalog.Find(caseId);
		if (definition is null)
		{
			return null;
		}

		int clamped = Math.Clamp(count, 1, MaxOpensPerRequest);
		int keyCount = Math.Clamp(keys ?? clamped, 1, MaxOpensPerRequest);
		ConsumeEntry entry = new(definition.Id, clamped, keyCount, awards);
		_ledger.Record(entry);
		return entry;
	}

	private Task<PluginWebResponse> HandleListCasesAsync(PluginWebRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var payload = _catalog.Cases.Select(definition => new
		{
			definition.Id,
			definition.Name,
			items = definition.Items
				.GroupBy(item => item.Rarity)
				.OrderBy(group => group.Key)
				.Select(group => new
				{
					rarity = CaseDefinition.RarityName(group.Key),
					count = group.Count(),
					entries = group.Select(item => new { item.Id, item.Name, item.MinFloat, item.MaxFloat, stattrak = item.StatTrakAllowed })
				})
		});

		return Task.FromResult(PluginWebResponse.Json(JsonSerializer.Serialize(payload, JsonOptions)));
	}

	private async Task<PluginWebResponse> HandleOpenAsync(PluginWebRequest request, CancellationToken cancellationToken)
	{
		JsonDocument? parsed = ParseBodyOrNull(request.Body, out string? parseError);
		if (parsed is null)
		{
			// ParseBodyOrNull contract: a null result always carries the error text.
			return PluginWebResponse.Error(400, parseError!);
		}

		using JsonDocument document = parsed;

		if (!document.RootElement.TryGetProperty("caseId", out JsonElement caseIdElement) || caseIdElement.ValueKind != JsonValueKind.String)
		{
			return PluginWebResponse.Error(400, "body needs a string 'caseId'");
		}

		string caseId = caseIdElement.GetString()!; // String kind never yields null.
		int count = 1;
		if (document.RootElement.TryGetProperty("count", out JsonElement countElement)
			&& (countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetInt32(out count)))
		{
			return PluginWebResponse.Error(400, "'count' must be an integer");
		}

		CaseOpenBatch? batch = await OpenAsync(caseId, count, cancellationToken).ConfigureAwait(false);
		if (batch is null)
		{
			return PluginWebResponse.Error(404, $"unknown case '{caseId}'");
		}

		return PluginWebResponse.Json(JsonSerializer.Serialize(ToBatchPayload(batch), JsonOptions));
	}

	private Task<PluginWebResponse> HandleResultsAsync(PluginWebRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		int limit = 50;
		if (request.Query.TryGetValue("limit", out string? limitText)
			&& int.TryParse(limitText, out int parsedLimit))
		{
			limit = Math.Clamp(parsedLimit, 1, CaseOpeningStore.DefaultCapacity);
		}

		IReadOnlyList<OpenResultDto> results = Store.RecentResults()
			.TakeLast(limit)
			.Select(CaseOpeningStore.ToDto)
			.ToArray();

		return Task.FromResult(PluginWebResponse.Json(JsonSerializer.Serialize(new { results }, JsonOptions)));
	}

	private Task<PluginWebResponse> HandleStatsAsync(PluginWebRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		CaseOpeningStats stats = Store.ComputeStats();
		return Task.FromResult(PluginWebResponse.Json(JsonSerializer.Serialize(ToStatsPayload(stats), JsonOptions)));
	}

	/// <summary>
	/// Ledger read surface (the web layer has no bot session, so the live
	/// inventory half of reconciliation lives in the case_inventory action).
	/// </summary>
	private Task<PluginWebResponse> HandleInventoryAsync(PluginWebRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		int limit = 50;
		if (request.Query.TryGetValue("limit", out string? limitText)
			&& int.TryParse(limitText, out int parsedLimit))
		{
			limit = Math.Clamp(parsedLimit, 1, CaseLedger.DefaultCapacity);
		}

		CaseLedgerSummary summary = _ledger.Summarize();
		IReadOnlyList<ConsumeEntryDto> recent = _ledger.RecentEntries()
			.TakeLast(limit)
			.Select(CaseLedger.ToDto)
			.ToArray();

		return Task.FromResult(PluginWebResponse.Json(JsonSerializer.Serialize(new
		{
			mode = "readonly",
			entries = summary.TotalEntries,
			cases = summary.TotalCases,
			keys = summary.TotalKeys,
			perCase = summary.Cases.Select(total => new { total.CaseId, total.Cases, total.Keys }),
			recent
		}, JsonOptions)));
	}

	private Task<PluginWebResponse> HandleConsumeAsync(PluginWebRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		JsonDocument? parsed = ParseBodyOrNull(request.Body, out string? parseError);
		if (parsed is null)
		{
			// ParseBodyOrNull contract: a null result always carries the error text.
			return Task.FromResult(PluginWebResponse.Error(400, parseError!));
		}

		using JsonDocument document = parsed;

		if (!_consumeEnabled)
		{
			return Task.FromResult(PluginWebResponse.Error(403,
				"case consume is disabled: set configuration 'consume.enabled' (or environment " +
				ConsumeEnabledEnvironmentVariable + ") to true; the route only records ledger entries — it never opens anything itself"));
		}

		if (!document.RootElement.TryGetProperty("caseId", out JsonElement caseIdElement) || caseIdElement.ValueKind != JsonValueKind.String)
		{
			return Task.FromResult(PluginWebResponse.Error(400, "body needs a string 'caseId'"));
		}

		string caseId = caseIdElement.GetString()!; // String kind never yields null.
		int count = 1;
		if (document.RootElement.TryGetProperty("count", out JsonElement countElement)
			&& (countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetInt32(out count)))
		{
			return Task.FromResult(PluginWebResponse.Error(400, "'count' must be an integer"));
		}

		int? keys = null;
		if (document.RootElement.TryGetProperty("keys", out JsonElement keysElement))
		{
			if (keysElement.ValueKind == JsonValueKind.Null)
			{
				keys = null;
			}
			else if (keysElement.ValueKind == JsonValueKind.Number && keysElement.TryGetInt32(out int parsedKeys))
			{
				keys = parsedKeys;
			}
			else
			{
				return Task.FromResult(PluginWebResponse.Error(400, "'keys' must be an integer"));
			}
		}

		var awards = new List<string>();
		if (document.RootElement.TryGetProperty("awards", out JsonElement awardsElement))
		{
			if (awardsElement.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement award in awardsElement.EnumerateArray())
				{
					if (award.ValueKind == JsonValueKind.String)
					{
						awards.Add(award.GetString()!); // String kind never yields null.
					}
				}
			}
			else if (awardsElement.ValueKind != JsonValueKind.Null)
			{
				return Task.FromResult(PluginWebResponse.Error(400, "'awards' must be an array of strings"));
			}
		}

		ConsumeEntry? entry = Consume(caseId, count, keys, awards);
		if (entry is null)
		{
			return Task.FromResult(PluginWebResponse.Error(404, $"unknown case '{caseId}'"));
		}

		CaseLedgerSummary totals = _ledger.Summarize();
		return Task.FromResult(PluginWebResponse.Json(JsonSerializer.Serialize(new
		{
			mode = "ledger",
			entry = CaseLedger.ToDto(entry),
			totals = new { entries = totals.TotalEntries, cases = totals.TotalCases, keys = totals.TotalKeys }
		}, JsonOptions)));
	}

	private static JsonDocument? ParseBodyOrNull(string? body, out string? error)
	{
		error = null;
		if (string.IsNullOrWhiteSpace(body))
		{
			error = "empty request body";
			return null;
		}

		try
		{
			return JsonDocument.Parse(body);
		}
		catch (JsonException ex)
		{
			error = $"invalid JSON body: {ex.Message}";
			return null;
		}
	}

	/// <summary>Renders a batch for JSON output (mode, case, results, tier counts).</summary>
	internal static object ToBatchPayload(CaseOpenBatch batch)
	{
		ArgumentNullException.ThrowIfNull(batch);

		return new
		{
			mode = batch.Mode,
			caseId = batch.CaseId,
			opened = batch.Results.Count,
			rarityCounts = Enum.GetValues<CaseRarity>()
				.Select(tier => new
				{
					rarity = CaseDefinition.RarityName(tier),
					count = batch.Results.Count(result => result.Rarity == tier)
				}),
			results = batch.Results.Select(CaseOpeningStore.ToDto)
		};
	}

	/// <summary>Renders store stats for JSON output.</summary>
	internal static object ToStatsPayload(CaseOpeningStats stats)
	{
		ArgumentNullException.ThrowIfNull(stats);

		return new
		{
			totalOpens = stats.TotalOpens,
			cases = stats.Cases.Select(caseStat => new
			{
				caseId = caseStat.CaseId,
				opened = caseStat.Opens,
				rarities = caseStat.Rarities
			})
		};
	}

	/// <summary>One executed batch of opens (shared by the action and the POST route).</summary>
	internal sealed record CaseOpenBatch(string Mode, string CaseId, IReadOnlyList<OpenResult> Results);
}

/// <summary>
/// The <c>case_open</c> action: opens <c>count</c> (default 1, clamped to
/// 1..100) of the named case in dry-run mode and records the results.
/// </summary>
public sealed class CaseOpenAction : IAction
{
	private readonly CaseOpeningPlugin _plugin;

	/// <summary>Creates the action bound to its owning plugin.</summary>
	public CaseOpenAction(CaseOpeningPlugin plugin)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
	}

	public string Name => "case_open";

	public ActionMetadata Metadata { get; } = new(
		Name: "case_open",
		Description: "Opens a weapon case in dry-run simulation (Valve's published odds) and records the results. No Steam side effects.",
		RequiresLogin: false,
		TimeoutSeconds: 60);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		if (!TryReadCaseId(payload, out string? caseId))
		{
			return new ActionResult(false, "payload needs a string 'caseId'");
		}

		int count = 1;
		TryReadInt32(payload, "count", ref count);

		CaseOpeningPlugin.CaseOpenBatch? batch = await _plugin.OpenAsync(caseId, count, cancellationToken).ConfigureAwait(false);
		if (batch is null)
		{
			return new ActionResult(false, $"unknown case '{caseId}'");
		}

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["payload"] = CaseOpeningPlugin.ToBatchPayload(batch)
		});
	}

	private static bool TryReadCaseId(
		IReadOnlyDictionary<string, object?> payload,
		[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? caseId)
	{
		if (!payload.TryGetValue("caseId", out object? value))
		{
			caseId = null;
			return false;
		}

		caseId = value switch
		{
			string text => text,
			JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
			_ => null
		};

		return !string.IsNullOrWhiteSpace(caseId);
	}

	/// <summary>Reads an integer payload value (mirrors PayloadReader's type handling).</summary>
	private static bool TryReadInt32(IReadOnlyDictionary<string, object?> payload, string key, ref int parsed)
	{
		if (!payload.TryGetValue(key, out object? value))
		{
			return false;
		}

		int candidate;
		switch (value)
		{
			case int i:
				candidate = i;
				break;
			case long l when l is < int.MaxValue and > int.MinValue:
				candidate = (int)l;
				break;
			case decimal d when d is < int.MaxValue and > int.MinValue:
				candidate = (int)d;
				break;
			case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out int extracted):
				candidate = extracted;
				break;
			case string s:
				return int.TryParse(s, out parsed);
			default:
				return false;
		}

		parsed = candidate;
		return true;
	}
}
