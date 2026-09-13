using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "find_duplicates": scans this account's inventories for duplicate items (trading
/// cards mostly) and reports the tradable copies beyond <c>keep</c> as excess — the
/// input side of a 1:1 card swap (SteamTradeMatcher style). Analysis only; pairing and
/// the actual offer live in <see cref="SwapDuplicatesAction"/>.
/// </summary>
public sealed class FindDuplicatesAction : IAction
{
	// Same defensive caps as the loot flow.
	internal const int MaxApps = 5;
	internal const int MaxPagesPerApp = 50;
	internal const int DefaultKeep = 1;

	private readonly ILogger<FindDuplicatesAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public FindDuplicatesAction(ILogger<FindDuplicatesAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal FindDuplicatesAction(
		ILogger<FindDuplicatesAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "find_duplicates";

	public ActionMetadata Metadata => new(
		Name,
		"Find duplicate items (cards) in this account's inventories, beyond keep copies",
		RequiresLogin: true,
		TimeoutSeconds: 60);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		List<uint> appIds = ParseAppIds(payload);
		if (appIds.Count > MaxApps)
		{
			return new ActionResult(false, $"app_ids is limited to {MaxApps} apps per call", null);
		}

		int keep = DefaultKeep;
		if (PayloadReader.TryGetValue(payload, "keep", out var keepRaw))
		{
			if (!TryParsePositiveInt(keepRaw, out keep) || keep < 1 || keep > 100)
			{
				return new ActionResult(false, "keep must be an integer between 1 and 100", null);
			}
		}

		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			ulong? ownSteamId = tradeClient.GetOwnSteamId();
			if (ownSteamId == null)
			{
				return new ActionResult(false, "Unable to resolve own SteamID from the web session", null);
			}

			var items = new List<InventoryItem>();
			var scanned = new List<Dictionary<string, object?>>();

			foreach (uint appId in appIds)
			{
				ulong contextId = CardSwapMatcher.ContextIdFor(appId);
				int pages = 0;
				int scannedForApp = 0;
				ulong? startAssetId = null;
				bool hasMore;

				do
				{
					var response = await tradeClient.GetInventoryAsync(ownSteamId.Value, appId, contextId, startAssetId, cancellationToken).ConfigureAwait(false);
					if (!response.Success)
					{
						return new ActionResult(false, $"Failed to load inventory for app {appId}: {response.Error}", null);
					}

					items.AddRange(response.Items);
					scannedForApp += response.Items.Count;
					hasMore = response.HasMore;
					startAssetId = response.LastAssetId;
				}
				while (hasMore && startAssetId.HasValue && ++pages < MaxPagesPerApp);

				scanned.Add(new Dictionary<string, object?>
				{
					["app_id"] = appId,
					["context_id"] = contextId.ToString(CultureInfo.InvariantCulture),
					["scanned_items"] = scannedForApp
				});
			}

			var groups = CardSwapMatcher.FindDuplicates(items, keep);
			var duplicates = groups
				.Select(g => new Dictionary<string, object?>
				{
					["app_id"] = g.AppId,
					["class_id"] = g.ClassId,
					["instance_id"] = g.InstanceId,
					["name"] = g.Name,
					["total"] = g.TotalTradable,
					["excess_count"] = g.ExcessItems.Count,
					["excess_asset_ids"] = g.ExcessItems.Select(static i => i.AssetId).ToArray()
				})
				.ToArray();

			_logger.LogInformation(
				"Found {DuplicateGroups} duplicate groups ({ExcessCount} excess items) for {AccountName}",
				duplicates.Length, duplicates.Sum(static d => (int)d["excess_count"]!), session.AccountName);

			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["steam_id"] = ownSteamId.Value.ToString(CultureInfo.InvariantCulture),
				["keep"] = keep,
				["apps_scanned"] = scanned,
				["duplicates"] = duplicates,
				["excess_count"] = duplicates.Sum(static d => (int)d["excess_count"]!)
			});
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to scan duplicates for {AccountName}", session.AccountName);
			return new ActionResult(false, ex.Message, null);
		}
	}

	// app_ids arrives as a JSON array (a JsonElement after the job store round-trip);
	// also accept .NET collections and a single bare value for direct dispatches.
	private static List<uint> ParseAppIds(IReadOnlyDictionary<string, object?> payload)
	{
		var result = new List<uint>();

		if (!PayloadReader.TryGetValue(payload, "app_ids", out var raw) || raw is null)
		{
			result.Add(753); // Default: Steam community items (trading cards etc.)
			return result;
		}

		if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
		{
			foreach (var element in array.EnumerateArray())
			{
				if (TryParseUInt32(element, out uint appId) && appId > 0)
				{
					result.Add(appId);
				}
			}
		}
		else if (raw is System.Collections.IEnumerable list and not string)
		{
			foreach (var item in list)
			{
				if (item != null && TryParseUInt32(item, out uint appId) && appId > 0)
				{
					result.Add(appId);
				}
			}
		}
		else if (TryParseUInt32(raw, out uint single) && single > 0)
		{
			result.Add(single);
		}

		return result.Distinct().ToList();
	}

	private static bool TryParseUInt32(object? value, out uint parsed)
	{
		switch (value)
		{
			case uint u:
				parsed = u;
				return true;
			case int i when i > 0:
				parsed = (uint)i;
				return true;
			case long l when l > 0 && l <= uint.MaxValue:
				parsed = (uint)l;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } e:
				return e.TryGetUInt32(out parsed);
			case JsonElement { ValueKind: JsonValueKind.String } s:
				return uint.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			case string text:
				return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}

	private static bool TryParsePositiveInt(object? value, out int parsed)
	{
		switch (value)
		{
			case int i:
				parsed = i;
				return true;
			case long l when l is >= 1 and <= int.MaxValue:
				parsed = (int)l;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } e:
				return e.TryGetInt32(out parsed) && parsed >= 1;
			case JsonElement { ValueKind: JsonValueKind.String } s:
				return int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			case string text:
				return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}
}
