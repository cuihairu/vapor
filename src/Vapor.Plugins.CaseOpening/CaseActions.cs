using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.CaseOpening;

/// <summary>
/// "case_inventory": reads the CS2 backpack (app 730 / context 2) through the
/// shared paginator and classifies it into case containers, keys and other
/// items, so an operator can see how many cases and keys a session holds
/// before opening anything. The steam id defaults to the session's own
/// (resolved from its web cookies). The output also carries the local ledger
/// totals, which is what turns a live read into a reconciliation: cases minus
/// recorded consumptions is what should still be in the bag.
/// </summary>
public sealed class CaseInventoryAction : IAction
{
	private readonly CaseOpeningPlugin _plugin;
	private readonly ILogger<CaseInventoryAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public CaseInventoryAction(CaseOpeningPlugin plugin)
		: this(plugin, NullLogger<CaseInventoryAction>.Instance)
	{
	}

	// Constructor for testing with a custom trade client factory.
	internal CaseInventoryAction(
		CaseOpeningPlugin plugin,
		ILogger<CaseInventoryAction> logger,
		Func<SteamWebHandler, ISteamTradeClient>? tradeClientFactory = null)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
		_logger = logger;
		_tradeClientFactory = tradeClientFactory ?? plugin.TradeClientFactory;
	}

	public string Name => "case_inventory";

	public ActionMetadata Metadata => new(
		Name,
		"Classify a Steam CS2 backpack into case containers, keys and other items (app 730/context 2), with the local consumption ledger totals alongside. The steam_id defaults to the session's own.",
		RequiresLogin: true,
		TimeoutSeconds: 60
	)
	{ Safety = ActionSafety.ReadOnly };

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		var webHandler = session.SteamWebHandler;
		ulong steamId;
		string? steamIdParam = PayloadReader.GetString(payload, "steam_id");
		if (!string.IsNullOrEmpty(steamIdParam))
		{
			if (!ulong.TryParse(steamIdParam, out steamId))
			{
				return new ActionResult(false, "invalid steam_id parameter", null);
			}
		}
		else
		{
			// Default to the session's own SteamID; without a web session there is
			// nothing to resolve it from.
			if (webHandler is null)
			{
				return new ActionResult(false, "steam_id parameter is required when no web session is available", null);
			}

			ulong? resolved = webHandler.TryResolveOwnSteamId();
			if (resolved is null)
			{
				return new ActionResult(false, "steam_id parameter is required when the session cookies do not carry a SteamID (is the session logged on?)", null);
			}

			steamId = resolved.Value;
		}

		if (webHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			(List<InventoryItem> items, string? error) = await SteamInventoryPaginator.FetchAsync(
				tradeClient, steamId, appId: 730, contextId: 2, _logger, cancellationToken).ConfigureAwait(false);
			if (error is not null)
			{
				return new ActionResult(false, error, null);
			}

			CaseInventorySummary summary = CaseInventoryClassifier.Classify(items, _plugin.Catalog);
			CaseLedgerSummary ledger = _plugin.Ledger.Summarize();

			_logger.LogInformation(
				"Classified inventory for SteamID {SteamId}: {CaseKinds} case kind(s), {Keys} key(s)",
				steamId, summary.Cases.Count, summary.KeyCount);

			var output = new Dictionary<string, object?>
			{
				["mode"] = "readonly",
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["app_id"] = 730u,
				["context_id"] = 2ul,
				["total_count"] = summary.TotalCount,
				["cases"] = summary.Cases.Select(group => new Dictionary<string, object?>
				{
					["name"] = group.Name,
					["case_id"] = group.CaseId,
					["count"] = group.Count
				}).ToList(),
				["key_count"] = summary.KeyCount,
				["other_count"] = summary.OtherCount,
				["ledger"] = new Dictionary<string, object?>
				{
					["entries"] = ledger.TotalEntries,
					["cases"] = ledger.TotalCases,
					["keys"] = ledger.TotalKeys
				}
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to classify inventory for {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}

/// <summary>
/// "case_consume": local consumption bookkeeping — records how many cases and
/// keys the operator consumed (plus the awards reported received) into the
/// plugin's ledger. Gated by <c>consume.enabled</c> (default off). It
/// deliberately never touches the inventory: the physical opening is a CS2
/// client transaction the operator performs by hand, this only keeps the
/// ledger that pairs with a later <c>case_inventory</c> read.
/// </summary>
public sealed class CaseConsumeAction : IAction
{
	private readonly CaseOpeningPlugin _plugin;

	public CaseConsumeAction(CaseOpeningPlugin plugin)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
	}

	public string Name => "case_consume";

	public ActionMetadata Metadata => new(
		Name,
		"Records a local consumption entry (cases + keys + awards) into the case-opening ledger. Gated by consume.enabled (default off); never touches the inventory itself.",
		RequiresLogin: false,
		TimeoutSeconds: 30
	)
	{ Safety = ActionSafety.NonIdempotent };

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);
		cancellationToken.ThrowIfCancellationRequested();

		if (!_plugin.ConsumeEnabled)
		{
			return Task.FromResult(new ActionResult(false,
				"case consume is disabled: set configuration 'consume.enabled' (or environment " +
				CaseOpeningPlugin.ConsumeEnabledEnvironmentVariable + ") to true; the ledger only records what the operator consumed — it never opens anything itself",
				null));
		}

		if (!TryReadCaseId(payload, out string? caseId))
		{
			return Task.FromResult(new ActionResult(false, "payload needs a string 'caseId'", null));
		}

		int count = 1;
		TryReadInt32(payload, "count", ref count);

		int? keys = null;
		if (payload.TryGetValue("keys", out object? keysValue) && TryReadValueAsInt32(keysValue, out int parsedKeys))
		{
			keys = parsedKeys;
		}

		IReadOnlyList<string>? awards = TryReadAwards(payload, "awards");

		ConsumeEntry? entry = _plugin.Consume(caseId, count, keys, awards);
		if (entry is null)
		{
			return Task.FromResult(new ActionResult(false, $"unknown case '{caseId}'", null));
		}

		CaseLedgerSummary totals = _plugin.Ledger.Summarize();
		return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
		{
			["mode"] = "ledger",
			["caseId"] = entry.CaseId,
			["cases"] = entry.Cases,
			["keys"] = entry.Keys,
			["awards"] = entry.Awards,
			["ledger"] = new Dictionary<string, object?>
			{
				["entries"] = totals.TotalEntries,
				["cases"] = totals.TotalCases,
				["keys"] = totals.TotalKeys
			}
		}));
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

		if (!TryReadValueAsInt32(value, out int candidate))
		{
			return false;
		}

		parsed = candidate;
		return true;
	}

	private static bool TryReadValueAsInt32(object? value, out int parsed)
	{
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
				parsed = 0;
				return false;
		}

		parsed = candidate;
		return true;
	}

	/// <summary>
	/// Reads an optional awards list: a JSON string array, a CLR string
	/// enumerable, or a single string; null when absent or not a string shape.
	/// </summary>
	private static IReadOnlyList<string>? TryReadAwards(IReadOnlyDictionary<string, object?> payload, string key)
	{
		if (!payload.TryGetValue(key, out object? value) || value is null)
		{
			return null;
		}

		if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
		{
			var awards = new List<string>();
			foreach (JsonElement element in array.EnumerateArray())
			{
				if (element.ValueKind == JsonValueKind.String)
				{
					awards.Add(element.GetString()!); // String kind never yields null.
				}
			}

			return awards;
		}

		if (value is System.Collections.IEnumerable enumerable and not string)
		{
			var awards = new List<string>();
			foreach (object? item in enumerable)
			{
				if (item is string text)
				{
					awards.Add(text);
				}
			}

			return awards;
		}

		return value is string single ? [single] : null;
	}
}
